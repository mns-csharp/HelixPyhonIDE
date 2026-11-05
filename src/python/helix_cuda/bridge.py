"""The pipe to helix_cuda_bridge.exe, and the one process-wide instance of it."""

import atexit
import base64
import json
import os
import subprocess
import sys
import threading


class BridgeError(RuntimeError):
    """The emulator refused something, or the bridge could not be reached.

    Raised at the call site -- inside ``kernel(...)`` or ``mem_alloc(...)`` -- so a failure
    points at the line that caused it rather than at this file.
    """


class CudaBridge:
    """A conversation with one bridge process.

    Started on first use and kept for the life of the Python process, because the emulator's
    device memory lives inside it. Requests are serialised: the protocol is one line of JSON
    in, one line out, and two threads interleaving would each read the other's reply.
    """

    #: Env var an IDE (or a user) can set to point at a specific bridge build.
    EXECUTABLE_VAR = "HELIX_CUDA_BRIDGE"

    def __init__(self, executable=None):
        self._executable = executable or self.find_executable()
        self._process = None
        self._lock = threading.Lock()

    # ---- process ----------------------------------------------------------

    @classmethod
    def find_executable(cls):
        """Locate helix_cuda_bridge.exe: the env var first, then beside this package.

        The env var wins so the IDE can pin the build it just compiled. Without it, the
        search walks up from this file looking for the repository's artifacts folder, which
        is what makes the package work from a source checkout with no install step.
        """
        override = os.environ.get(cls.EXECUTABLE_VAR)
        if override:
            return override

        name = "helix_cuda_bridge.exe" if os.name == "nt" else "helix_cuda_bridge"
        here = os.path.dirname(os.path.abspath(__file__))
        directory = here
        while True:
            candidate = os.path.join(
                directory, "artifacts", "bin", "HelixStudio.CudaBridge", "Debug", "net472", name)
            if os.path.isfile(candidate):
                return candidate
            parent = os.path.dirname(directory)
            if parent == directory:
                break
            directory = parent
        # Last resort: whatever is on PATH. Returning the bare name rather than None keeps the
        # failure at Popen, where the message can say what was looked for.
        return name

    def start(self):
        if self._process is not None and self._process.poll() is None:
            return
        try:
            self._process = subprocess.Popen(
                [self._executable],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                bufsize=1,
            )
        except OSError as error:
            raise BridgeError(
                "could not start the CUDA emulator bridge at %r (%s). Set %s to its path."
                % (self._executable, error, self.EXECUTABLE_VAR))
        atexit.register(self.stop)

    def stop(self):
        process, self._process = self._process, None
        if process is None or process.poll() is not None:
            return
        try:
            process.stdin.write(json.dumps({"op": "quit"}) + "\n")
            process.stdin.flush()
            process.wait(timeout=5)
        except Exception:
            try:
                process.kill()
            except Exception:
                pass

    # ---- protocol ---------------------------------------------------------

    def call(self, **request):
        """Send one request, return its reply as a dict. Raises BridgeError on failure."""
        with self._lock:
            self.start()
            line = json.dumps(request)
            try:
                self._process.stdin.write(line + "\n")
                self._process.stdin.flush()
                reply = self._process.stdout.readline()
            except (BrokenPipeError, ValueError) as error:
                raise BridgeError("the CUDA emulator bridge stopped responding (%s)" % error)

            if not reply:
                stderr = ""
                try:
                    stderr = self._process.stderr.read() or ""
                except Exception:
                    pass
                raise BridgeError(
                    "the CUDA emulator bridge exited without replying." + (
                        "\n" + stderr.strip() if stderr.strip() else ""))

        try:
            parsed = json.loads(reply)
        except ValueError:
            raise BridgeError("unreadable reply from the bridge: %r" % reply[:200])

        if not parsed.get("ok"):
            message = parsed.get("error", "unknown error")
            diagnostics = parsed.get("diagnostics")
            if diagnostics:
                message = message + "\n  " + "\n  ".join(diagnostics)
            raise BridgeError(message)
        return parsed

    # ---- typed operations -------------------------------------------------

    def ping(self):
        return self.call(op="ping").get("emulator", "")

    def compile_module(self, source):
        return self.call(op="module", source=source)["module"]

    def malloc(self, nbytes):
        return self.call(op="malloc", bytes=int(nbytes))["ptr"]

    def free(self, pointer):
        self.call(op="free", ptr=pointer)

    def upload(self, pointer, data):
        self.call(op="upload", ptr=pointer, data=base64.b64encode(bytes(data)).decode("ascii"))

    def download(self, pointer, nbytes):
        reply = self.call(op="download", ptr=pointer, bytes=int(nbytes))
        return base64.b64decode(reply.get("data", ""))

    def launch(self, module, kernel, grid, block, args, shared=0):
        reply = self.call(op="launch", module=module, kernel=kernel,
                          grid=list(grid), block=list(block), args=args, shared=int(shared))
        text = reply.get("output") or ""
        if text:
            # A kernel's printf belongs on the program's stdout, where the user is already
            # looking, not buried in a return value nobody reads.
            sys.stdout.write(text)
        return reply


_BRIDGE = None
_BRIDGE_LOCK = threading.Lock()


def bridge():
    """The process-wide bridge, started on first use."""
    global _BRIDGE
    if _BRIDGE is None:
        with _BRIDGE_LOCK:
            if _BRIDGE is None:
                _BRIDGE = CudaBridge()
    return _BRIDGE
