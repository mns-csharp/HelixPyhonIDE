"""HelixPython notebook kernel host.

One long-lived Python process that runs notebook cells in a single namespace and reports what each
one produced, as line-delimited JSON on stdout.

Why not the Jupyter kernel protocol
-----------------------------------
Talking ZeroMQ to a real ipykernel would mean pyzmq plus jupyter_client installed in the user's
interpreter before a single cell could run, and a five-socket handshake to debug when it is not.
This host needs nothing but the interpreter itself. When IPython IS importable it is used, and the
user gets what they expect from a notebook: magics, rich display, `_` history, matplotlib figures
captured as PNG. When it is not, cells still run -- plain exec, text output, last-expression value --
and the IDE says which mode it got rather than pretending.

Protocol
--------
Requests (one JSON object per line on stdin):
    {"type": "execute",  "id": "...", "code": "..."}
    {"type": "complete", "id": "...", "code": "...", "cursor": 12}
    {"type": "sys_path", "id": "...", "paths": ["D:\code", ...]}
    {"type": "interrupt"}
    {"type": "shutdown"}

Responses (one JSON object per line on stdout):
    {"type": "ready",          "implementation": "ipython"|"plain", "python": "3.11.0", ...}
    {"type": "stream",         "id": "...", "name": "stdout"|"stderr", "text": "..."}
    {"type": "execute_result", "id": "...", "execution_count": 3, "data": {mime: payload}}
    {"type": "display_data",   "id": "...", "data": {mime: payload}}
    {"type": "error",          "id": "...", "ename": "...", "evalue": "...", "traceback": [...]}
    {"type": "status",         "id": "...", "state": "idle", "execution_count": 3, "ok": true}
    {"type": "completions",    "id": "...", "matches": [...], "cursor_start": 4}
    {"type": "sys_path",       "id": "...", "added": [...], "missing": [...], "shadowed": [...]}

stdin is read on a separate thread so an "interrupt" arriving mid-cell can raise KeyboardInterrupt
in the main thread -- the only way to stop a running cell on Windows without killing the process and
losing the whole namespace with it.
"""

import os
import sys
import json
import importlib
import threading
import traceback
import base64

# --------------------------------------------------------------------------- the numpy preload
#
# Load numpy HERE, at module scope, while this process still has exactly one thread.
#
# Not an optimisation. Without it, `import numpy` inside a CELL deadlocks the kernel: the main
# thread blocks forever inside `create_module` for numpy's `_multiarray_umath` extension, burning
# no CPU, producing no output and never returning a status. Reproduced 3/3 on the shipped host on
# during testing, on `import numpy` and on `import torch` (which imports numpy), and 3/3 fixed by this
# preload -- 1.1 s and 4.2 s respectively. It is very probably the "cell that never finishes"
# reported whose report was a two-line cell reading `import numpy as np` /
# `print(np.__version__)` stuck with no output.
#
# What is established: it is not IPython (the PlainBackend fallback deadlocks too), not the
# StreamForwarder, and not the reader thread's file object (raw `os.read` and a dup of fd 0
# deadlock the same way). What distinguishes the working case is simply WHEN the extension is
# loaded -- at start-up, before the stdin reader thread exists, it is fine; from inside the request
# loop it is not. That is the signature of a Windows loader-lock deadlock in a native extension
# that spawns threads as it initialises, which is what numpy's BLAS backend does.
#
# The exact interaction is NOT fully explained, and this comment says so rather than implying a
# tidier story: a reconstruction of the host's structure could not be made to deadlock, so
# something beyond "a second thread exists" is involved. What IS established is the failure, its
# reproducibility, and that loading numpy while single-threaded removes it every time.
#
# The cost is one numpy import per kernel, ~0.3 s beside IPython's own start-up, paid by notebooks
# that never use numpy. A kernel that reliably runs the cell is worth a third of a second.
try:
    import numpy as _preloaded_numpy
except BaseException:            # not installed, or broken: cells still run, they just may deadlock
    _preloaded_numpy = None

try:                      # Python 3
    import queue
except ImportError:       # pragma: no cover - Python 2 is not supported, but fail loudly not weirdly
    import Queue as queue  # type: ignore

PROTOCOL_VERSION = 1

_write_lock = threading.Lock()
_requests = queue.Queue()
_shutdown = threading.Event()


def emit(message):
    """One JSON object, one line, flushed. The IDE reads this stream line by line."""
    with _write_lock:
        try:
            sys.__stdout__.write(json.dumps(message, ensure_ascii=False, default=str) + "\n")
            sys.__stdout__.flush()
        except (IOError, OSError, ValueError):
            # The IDE closed the pipe: nothing can be reported, and raising here would print a
            # traceback into a stream nobody is reading.
            _shutdown.set()


def reader_thread():
    """Parse stdin forever. Interrupts are acted on HERE so they can land mid-execution."""
    while not _shutdown.is_set():
        line = sys.stdin.readline()
        if not line:
            _shutdown.set()
            _requests.put(None)
            return
        # A byte-order mark can lead the FIRST line: .NET's Process.StandardInput writer emits the
        # encoding preamble on its first write, and json.loads rejects the line it leads. Dropping
        # a request because of an invisible character is a kernel that "just does nothing".
        line = line.strip().lstrip("﻿")
        if not line:
            continue
        try:
            request = json.loads(line)
        except ValueError:
            continue
        kind = request.get("type")
        if kind == "interrupt":
            # Raises KeyboardInterrupt in the MAIN thread, which is where the cell is running.
            # Between cells it is harmless: the main thread is blocked on the queue.
            try:
                import _thread
                _thread.interrupt_main()
            except Exception:
                pass
            continue
        if kind == "shutdown":
            _shutdown.set()
            _requests.put(None)
            return
        _requests.put(request)


# --------------------------------------------------------------------------- output plumbing

class StreamForwarder(object):
    """A file-like that turns writes into `stream` messages, live rather than at cell end.

    Live matters: a training loop that prints every epoch should fill the output area as it runs,
    which is the whole reason anyone prints in a loop.
    """

    def __init__(self, name):
        self.name = name
        self._cell_id = None

    def bind(self, cell_id):
        self._cell_id = cell_id

    def write(self, text):
        if not text:
            return 0
        emit({"type": "stream", "id": self._cell_id, "name": self.name, "text": text})
        return len(text)

    def writelines(self, lines):
        for line in lines:
            self.write(line)

    def flush(self):
        pass

    def isatty(self):
        return False

    @property
    def encoding(self):
        return "utf-8"


def _mime_bundle(data):
    """Normalise a formatter's mime bundle to JSON-safe strings, keeping binary as base64."""
    bundle = {}
    for mime, payload in (data or {}).items():
        if payload is None:
            continue
        if isinstance(payload, bytes):
            bundle[mime] = base64.b64encode(payload).decode("ascii")
        elif isinstance(payload, (dict, list)):
            bundle[mime] = payload
        else:
            bundle[mime] = payload if isinstance(payload, str) else str(payload)
    return bundle


# --------------------------------------------------------------------------- IPython backend

class IPythonBackend(object):
    """The real thing: magics, rich display, execution counts, `_` and `Out[..]`."""

    name = "ipython"

    def __init__(self):
        from IPython.core.interactiveshell import InteractiveShell
        self.shell = InteractiveShell.instance()
        self.shell.display_formatter.active_types = self.shell.display_formatter.format_types
        self._cell_id = None
        # Route display() and figures through us instead of IPython's own publisher.
        self.shell.display_pub.publish = self._publish
        # ...and stop the displayhook PRINTING the result as well. It still records the value on
        # the ExecutionResult, which is what `execute()` reports as an execute_result -- without
        # this the value arrives twice: once as "Out[1]: 42" on stdout and once as a result.
        self.shell.displayhook.write_output_prompt = lambda: None
        self.shell.displayhook.write_format_data = lambda data, md=None: None
        # Same for the traceback: IPython prints its own coloured one to stdout, so without this an
        # exception arrives twice -- once as stream text and once as the error. Taking IPython's
        # formatted traceback and reporting THAT is what Jupyter stores in the file, ANSI and all.
        self._reported_error = False
        self.shell._showtraceback = self._show_traceback

    def _publish(self, data, metadata=None, **kwargs):
        emit({"type": "display_data", "id": self._cell_id, "data": _mime_bundle(data)})

    def _show_traceback(self, etype, evalue, stb):
        self._reported_error = True
        emit({
            "type": "error",
            "id": self._cell_id,
            "ename": getattr(etype, "__name__", str(etype)),
            "evalue": str(evalue),
            "traceback": list(stb or []),
        })

    def execute(self, cell_id, code):
        self._cell_id = cell_id
        self._reported_error = False
        result = self.shell.run_cell(code, store_history=True, silent=False)

        if result.error_before_exec is not None or result.error_in_exec is not None:
            if not self._reported_error:
                emit_error(cell_id, result.error_in_exec or result.error_before_exec)
            return False, self.shell.execution_count - 1

        if result.result is not None:
            data, _ = self.shell.display_formatter.format(result.result)
            emit({
                "type": "execute_result",
                "id": cell_id,
                "execution_count": self.shell.execution_count - 1,
                "data": _mime_bundle(data),
            })
        return True, self.shell.execution_count - 1

    def complete(self, code, cursor):
        text, matches = self.shell.complete(None, code, cursor)
        if not matches and getattr(self.shell.Completer, "use_jedi", False):
            # Measured on IPython 8 + jedi 0.19: jedi returns nothing for `x.bit_` where IPython's
            # own completer returns both bit_count and bit_length. The two disagree about versions
            # more often than they agree, so a jedi miss falls back rather than reporting "no
            # completions" for a name that plainly has some.
            self.shell.Completer.use_jedi = False
            text, matches = self.shell.complete(None, code, cursor)
        return list(matches), cursor - len(text or "")


# --------------------------------------------------------------------------- plain backend

class PlainBackend(object):
    """No IPython installed. Cells still run; magics are reported, not silently ignored."""

    name = "plain"

    def __init__(self):
        self.namespace = {"__name__": "__main__", "__builtins__": __builtins__}
        self.execution_count = 0

    def execute(self, cell_id, code):
        import ast

        self.execution_count += 1
        code = self._strip_magics(cell_id, code)
        try:
            tree = ast.parse(code, filename="<cell>", mode="exec")
        except SyntaxError as error:
            emit_error(cell_id, error)
            return False, self.execution_count

        # Run everything but a trailing bare expression as statements, then evaluate that one so its
        # value can be reported the way a notebook reports it.
        last_expression = None
        if tree.body and isinstance(tree.body[-1], ast.Expr):
            last_expression = ast.Expression(tree.body[-1].value)
            tree.body = tree.body[:-1]
        try:
            if tree.body:
                exec(compile(tree, "<cell>", "exec"), self.namespace)
            if last_expression is not None:
                value = eval(compile(last_expression, "<cell>", "eval"), self.namespace)
                if value is not None:
                    self.namespace["_"] = value
                    emit({
                        "type": "execute_result",
                        "id": cell_id,
                        "execution_count": self.execution_count,
                        "data": {"text/plain": repr(value)},
                    })
        except BaseException as error:      # KeyboardInterrupt included, and it must be reported
            emit_error(cell_id, error)
            return False, self.execution_count
        return True, self.execution_count

    def _strip_magics(self, cell_id, code):
        kept = []
        for line in code.split("\n"):
            stripped = line.strip()
            if stripped.startswith("%") or stripped.startswith("!") or stripped.startswith("?"):
                emit({"type": "stream", "id": cell_id, "name": "stderr",
                      "text": "[helix] skipped `%s`: IPython is not installed in this interpreter, "
                              "so magics and shell escapes cannot run.\n" % stripped})
                kept.append("")          # keep the line count so tracebacks still line up
            else:
                kept.append(line)
        return "\n".join(kept)

    def complete(self, code, cursor):
        import re
        prefix = re.split(r"[^\w.]", code[:cursor])[-1] if cursor else ""
        head, _, tail = prefix.rpartition(".")
        try:
            if head:
                target = eval(head, self.namespace)
                names = dir(target)
            else:
                names = list(self.namespace.keys()) + dir(__builtins__)
        except BaseException:
            names = list(self.namespace.keys())
        matches = sorted(set(n for n in names if n.startswith(tail) and not n.startswith("__")))
        return matches, cursor - len(tail)


def describe_namespace(backend):
    """What the Variables pane shows: the user's own names, with a shape rather than a repr.

    Colab's inspector is useful because it says `DataFrame (1079133, 11)` rather than printing a
    million rows. Anything whose size is expensive or dangerous to compute is reported without it --
    calling len() on a generator consumes it, and repr() on a lazy object can hit the network.
    """
    namespace = getattr(backend, "shell", None)
    namespace = namespace.user_ns if namespace is not None else getattr(backend, "namespace", {})

    hidden = {"In", "Out", "get_ipython", "exit", "quit", "open"}
    variables = []
    for name, value in list(namespace.items()):
        if name.startswith("_") or name in hidden:
            continue
        try:
            kind = type(value).__name__
            if kind in ("module", "function", "builtin_function_or_method", "type", "ABCMeta"):
                continue
            variables.append({
                "name": name,
                "type": kind,
                "shape": _shape_of(value),
                "value": _short_repr(value),
            })
        except BaseException:
            variables.append({"name": name, "type": "?", "shape": "", "value": "(could not inspect)"})
    variables.sort(key=lambda v: v["name"].lower())
    return variables


def _shape_of(value):
    for attribute in ("shape", "size"):
        try:
            shape = getattr(value, attribute, None)
            if shape is not None and not callable(shape):
                return str(tuple(shape)) if hasattr(shape, "__iter__") else str(shape)
        except BaseException:
            pass
    try:
        if isinstance(value, (list, tuple, dict, set, str, bytes)):
            return str(len(value))
    except BaseException:
        pass
    return ""


def _short_repr(value, limit=120):
    try:
        text = repr(value)
    except BaseException:
        return "(repr failed)"
    text = text.replace("\n", " ")
    return text if len(text) <= limit else text[:limit] + "…"


def describe_resources():
    """RAM and disk, the way Colab's resource meter reports them.

    psutil when it is installed, and the interpreter's own numbers when it is not -- a resource
    meter that requires a dependency the user does not have is a meter that never shows anything.
    """
    resources = {"python": sys.version.split()[0]}
    try:
        import psutil, os
        process = psutil.Process(os.getpid())
        virtual = psutil.virtual_memory()
        resources["ram_used_bytes"] = int(process.memory_info().rss)
        resources["ram_total_bytes"] = int(virtual.total)
        resources["system_ram_used_bytes"] = int(virtual.total - virtual.available)
        disk = psutil.disk_usage(os.getcwd())
        resources["disk_used_bytes"] = int(disk.used)
        resources["disk_total_bytes"] = int(disk.total)
        resources["source"] = "psutil"
    except BaseException:
        try:
            import os, shutil, resource  # resource is POSIX-only; guarded by the except below
            resources["ram_used_bytes"] = int(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss) * 1024
        except BaseException:
            pass
        try:
            import os, shutil
            usage = shutil.disk_usage(os.getcwd())
            resources["disk_used_bytes"] = int(usage.used)
            resources["disk_total_bytes"] = int(usage.total)
        except BaseException:
            pass
        resources["source"] = "stdlib"
    return resources


def emit_error(cell_id, error):
    ename = type(error).__name__
    evalue = str(error)
    tb = traceback.format_exception(type(error), error, error.__traceback__)
    # Drop the host's own frames: the user cares about their cell, not about this file.
    tb = [line.rstrip("\n") for line in tb if "helix_kernel_host.py" not in line]
    emit({"type": "error", "id": cell_id, "ename": ename, "evalue": evalue, "traceback": tb})


def apply_sys_path(cell_id, paths):
    """Put the IDE's registered directories at the FRONT of `sys.path`.

    Front, not back: the point of registering a directory is to import the code that is in it, and
    appending would let a same-named package already installed in site-packages win silently.

    Three things are reported back rather than assumed:

    * `missing`  -- a directory that is not there any more. The notebook remembers absolute paths,
      and a drive that is not mounted must say so instead of producing `ModuleNotFoundError` two
      cells later with nothing to connect it to.
    * `shadowed` -- a top-level name that is ALREADY in `sys.modules` from somewhere else. Adding a
      path cannot dislodge a module Python has already imported, so the honest answer is to name it
      and let the user restart the runtime. Silently doing nothing here is the single most
      confusing outcome this feature can produce.
    * `added`    -- what actually went on the path, in the order it was applied.
    """
    added, missing, shadowed = [], [], []

    for path in (paths or []):
        if not isinstance(path, str) or not path.strip():
            continue
        try:
            resolved = os.path.abspath(path)
        except (ValueError, TypeError):
            missing.append(path)
            continue
        if not os.path.isdir(resolved):
            missing.append(path)
            continue

        # Compare case-insensitively on Windows: the same directory reached by two spellings must
        # not be inserted twice, or a restart-heavy session grows sys.path without bound.
        if not any(os.path.normcase(entry) == os.path.normcase(resolved)
                   for entry in sys.path if isinstance(entry, str)):
            sys.path.insert(0, resolved)
        added.append(resolved)

        shadowed.extend(_already_imported_from_elsewhere(resolved))

    # A new sys.path entry gets a fresh finder, but Python caches directory listings per entry, so
    # a directory that gained an __init__.py since it was last looked at would still read as empty.
    importlib.invalidate_caches()

    return {"type": "sys_path", "id": cell_id,
            "added": added, "missing": missing, "shadowed": sorted(set(shadowed))}


def _already_imported_from_elsewhere(root):
    """Top-level names this root provides that Python has ALREADY imported from somewhere else.

    Asked this way round -- over `sys.modules`, not over the directory -- on purpose. A registered
    root can be a repository with a hundred thousand entries, or a folder on a share that answers
    slowly, and listing it to find a handful of collisions would make registering a path cost
    seconds. `sys.modules` is in memory and a few hundred entries long, so the loop is bounded by
    what is loaded rather than by what is on the disk, and it touches only the names that could
    actually collide.
    """
    names = []
    for name in list(sys.modules):
        if "." in name:
            continue                      # submodules follow their top-level package
        module = sys.modules.get(name)
        origin = getattr(module, "__file__", None)
        if not origin:
            continue                      # built-in or namespace package: nothing to compare
        try:
            provided = (os.path.isfile(os.path.join(root, name + ".py"))
                        or os.path.isfile(os.path.join(root, name, "__init__.py")))
            if not provided:
                continue
            here = os.path.normcase(os.path.abspath(origin))
            if not here.startswith(os.path.normcase(root + os.sep)):
                names.append(name)
        except (OSError, IOError, ValueError):
            continue
    return names


def main():
    stdout, stderr = StreamForwarder("stdout"), StreamForwarder("stderr")
    sys.stdout, sys.stderr = stdout, stderr

    try:
        backend = IPythonBackend()
    except BaseException:
        backend = PlainBackend()

    emit({
        "type": "ready",
        "protocol": PROTOCOL_VERSION,
        "implementation": backend.name,
        "python": sys.version.split()[0],
        "executable": sys.executable,
    })

    threading.Thread(target=reader_thread, name="helix-stdin", daemon=True).start()

    while not _shutdown.is_set():
        request = _requests.get()
        if request is None:
            break
        kind = request.get("type")
        cell_id = request.get("id")

        if kind == "execute":
            stdout.bind(cell_id)
            stderr.bind(cell_id)
            ok = False
            count = None
            try:
                ok, count = backend.execute(cell_id, request.get("code") or "")
            except KeyboardInterrupt:
                emit({"type": "stream", "id": cell_id, "name": "stderr",
                      "text": "[helix] interrupted\n"})
            except BaseException as error:
                emit_error(cell_id, error)
            finally:
                stdout.bind(None)
                stderr.bind(None)
                emit({"type": "status", "id": cell_id, "state": "idle",
                      "ok": bool(ok), "execution_count": count})

        elif kind == "complete":
            try:
                matches, start = backend.complete(request.get("code") or "", int(request.get("cursor") or 0))
            except BaseException:
                matches, start = [], 0
            emit({"type": "completions", "id": cell_id, "matches": matches, "cursor_start": start})

        elif kind == "variables":
            emit({"type": "variables", "id": cell_id, "variables": describe_namespace(backend)})

        elif kind == "resources":
            emit({"type": "resources", "id": cell_id, "resources": describe_resources()})

        elif kind == "sys_path":
            emit(apply_sys_path(cell_id, request.get("paths")))

        elif kind == "ping":
            emit({"type": "pong", "id": cell_id})

    emit({"type": "exit"})


if __name__ == "__main__":
    main()
