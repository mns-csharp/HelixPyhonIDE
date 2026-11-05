"""
Python installation introspection for HelixStudio's CatalogScanner.

Run by the IDE as `python introspect.py [module ...]` (see
HelixStudio.Catalog.CatalogScanner.StartScan). Emits one JSON object per line on
stdout, matching the event protocol the scanner parses:

  {"event":"progress","package":<name>,"done":<int>,"total":<int>}
  {"event":"symbols","symbols":[ {moduleFqn,name,kind,signature,summary,
                                  returnSummary,fullDoc,params:[{name,type,
                                  default,description}]} ... ]}
  {"event":"error","package":<name>,"message":<str>}
  {"event":"done"}

Symbols come straight from the live interpreter via `inspect`, so the catalog
reflects exactly what THIS Python installation actually exposes (real-tool parity).

What a module contributes: its public functions, classes, SUBMODULES and DATA
attributes, plus one shallow level of class methods. "Public" follows the same rule
the rest of HelixPython uses (Core `SemanticModel.Visibility`): `_x` is protected and
`__x` is private, but **`__dunder__` is PUBLIC** — `numpy.__version__` is API, and a
scan that drops it makes the IDE look like it does not know the package.

Data attributes are a separate branch because they are neither callable nor classes:
without one, `np.pi`, `np.e`, `np.inf`, `np.newaxis` and `np.__version__` were all
absent from completion while `np.arange` was there. Measured on numpy 2.2.6, the
function/class-only scan produced 166 symbols for the whole package.
"""
import sys
import json
import inspect
import importlib

# A curated, import-safe stdlib set. Callers may override via argv.
DEFAULT_MODULES = [
    "os", "os.path", "sys", "json", "math", "re", "collections", "itertools",
    "functools", "datetime", "random", "string", "io", "typing", "textwrap",
]


def emit(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def _sig(obj):
    try:
        return str(inspect.signature(obj))
    except (ValueError, TypeError):
        return ""


def _params(obj):
    out = []
    try:
        for p in inspect.signature(obj).parameters.values():
            out.append({
                "name": p.name,
                "type": "" if p.annotation is inspect.Parameter.empty else str(p.annotation),
                "default": "" if p.default is inspect.Parameter.empty else repr(p.default),
                "description": "",
            })
    except (ValueError, TypeError):
        pass
    return out


def _summary(obj):
    doc = inspect.getdoc(obj) or ""
    first = doc.strip().split("\n", 1)[0] if doc else ""
    return first[:200]


def _is_public(name):
    """HelixPython's own visibility rule, from Core `SemanticModel.Visibility`.

    `__dunder__` is public — it is how a package publishes `__version__`, `__all__` and
    `__doc__`. `__x` is private and `_x` is protected, and neither belongs in a
    completion list for somebody else's package.
    """
    if name.startswith("__") and name.endswith("__") and len(name) >= 4:
        return True
    return not name.startswith("_")


# A value small and immutable enough that showing it IS the documentation: `__version__`
# is worth nothing without "2.2.6" beside it. Anything else is described by its type,
# because repr() of a large array or a live connection is neither useful nor cheap.
_SHOWABLE = (bool, int, float, complex, str, bytes, type(None))


def _data_summary(obj):
    """One line describing a data attribute: its value when that is small, else its type."""
    try:
        if isinstance(obj, _SHOWABLE):
            text = repr(obj)
            return text if len(text) <= 120 else text[:117] + "..."
        if (isinstance(obj, (tuple, frozenset)) and len(obj) <= 8
                and all(isinstance(x, _SHOWABLE) for x in obj)):
            text = repr(obj)
            if len(text) <= 120:
                return text
    except BaseException:
        pass
    try:
        return type(obj).__name__
    except BaseException:
        return ""


def _data_symbol(module_fqn, name, obj):
    """A module-level constant or variable. Kind "data" maps to CompletionKind.Variable."""
    summary = _data_summary(obj)
    return {
        "moduleFqn": module_fqn,
        "name": name,
        "kind": "data",
        "signature": "",
        "summary": summary,
        "returnSummary": "",
        "fullDoc": summary,
        "params": [],
    }


def _symbol(module_fqn, name, kind, obj):
    is_callable = kind in ("function", "method", "class")
    return {
        "moduleFqn": module_fqn,
        "name": name,
        "kind": kind,
        "signature": _sig(obj) if is_callable else "",
        "summary": _summary(obj),
        "returnSummary": "",
        "fullDoc": (inspect.getdoc(obj) or "")[:2000],
        "params": _params(obj) if kind in ("function", "method") else [],
    }


def _module_symbol(modname, mod):
    """The module's OWN entry, so Quick Info / hover on `import asyncio` has a docstring.

    Stored under the module's parent so CatalogSymbol.FullyQualifiedName() round-trips to
    `modname` itself: "asyncio" -> moduleFqn "", name "asyncio"; "os.path" -> moduleFqn
    "os", name "path".
    """
    parent, _, leaf = modname.rpartition(".")
    return {
        "moduleFqn": parent,
        "name": leaf,
        "kind": "module",
        "signature": "",
        "summary": _summary(mod),
        "returnSummary": "",
        "fullDoc": (inspect.getdoc(mod) or "")[:2000],
        "params": [],
    }


def _scan_module(modname):
    mod = importlib.import_module(modname)
    syms = [_module_symbol(modname, mod)]
    for name, obj in inspect.getmembers(mod):
        if not _is_public(name):
            continue
        try:
            if inspect.isfunction(obj) or inspect.isbuiltin(obj):
                syms.append(_symbol(modname, name, "function", obj))
            elif inspect.isclass(obj):
                syms.append(_symbol(modname, name, "class", obj))
                for mname, mobj in inspect.getmembers(obj):
                    # Class members keep the stricter rule deliberately: adding every
                    # dunder METHOD to every class multiplies the catalog by the ~30
                    # `__eq__`/`__hash__`/... that object gives all of them, for a list
                    # nobody scrolls. Module-level dunders are the ones that carry API.
                    if mname.startswith("_"):
                        continue
                    if inspect.isfunction(mobj) or inspect.ismethod(mobj):
                        syms.append(_symbol(modname + "." + name, mname, "method", mobj))
            elif inspect.ismodule(obj):
                # `np.linalg`, `np.random`, `np.fft` — reachable through the parent and
                # previously invisible, because a submodule is neither function nor class.
                syms.append(_symbol(modname, name, "module", obj))
            elif not inspect.isroutine(obj):
                # Everything left that is not code: constants and module variables.
                syms.append(_data_symbol(modname, name, obj))
            else:
                # A routine inspect does not classify as function/builtin (a method
                # descriptor, a ufunc, a C callable). It is still callable, so it is a
                # function to the user.
                syms.append(_symbol(modname, name, "function", obj))
        except Exception:
            # A pathological member (descriptor that raises, etc.) — skip it.
            continue
    return syms


def main(argv):
    modules = argv[1:] if len(argv) > 1 else DEFAULT_MODULES
    total = len(modules)
    for i, modname in enumerate(modules, start=1):
        emit({"event": "progress", "package": modname, "done": i, "total": total})
        try:
            syms = _scan_module(modname)
        except Exception as e:  # noqa: BLE001 — report and continue
            emit({"event": "error", "package": modname, "message": str(e)})
            continue
        if syms:
            emit({"event": "symbols", "symbols": syms})
    emit({"event": "done"})
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
