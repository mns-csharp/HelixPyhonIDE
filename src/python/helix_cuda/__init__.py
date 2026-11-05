"""helix_cuda -- run CUDA kernels with no GPU, by talking to the Helix CPU emulator.

This package is what lets a Python program that expects a GPU run on a machine that has
none. It provides the small part of the CuPy and PyCUDA surfaces that real programs use to
compile and launch a kernel, and sends every allocation, copy and launch to
``helix_cuda_bridge.exe``, which runs them on the emulator.

Two ways in:

``import helix_cuda as cp``
    Explicit. Change the import and nothing else. Good for a program you own.

``helix_cuda.install()``
    Registers ``helix_cuda`` under the names ``cupy`` and ``pycuda`` so an UNCHANGED program
    picks it up. This is what the IDE calls before launching, via ``sitecustomize``.

What this is honest about: it is not CuPy. There is a SMALL array type -- ``zeros``, ``ones``,
``arange``, ``asarray``, ``+ - * /`` and ``sum`` -- and every one of those runs as a real kernel
on the emulator rather than as Python arithmetic behind the API. Past that there is nothing: no
broadcasting, no slicing, no ufuncs, no linear algebra, and one dimension only. A program that
needs more will say so plainly rather than quietly computing something else.
"""

from helix_cuda.bridge import BridgeError, CudaBridge, bridge
from helix_cuda.memory import DeviceBuffer, mem_alloc, memcpy_dtoh, memcpy_htod
from helix_cuda.module import RawKernel, RawModule, SourceModule
from helix_cuda.ndarray import (
    arange, array, asarray, asnumpy, empty, float32, float64, full, int32, int64,
    ndarray, ones, zeros)

__all__ = [
    "BridgeError",
    "CudaBridge",
    "DeviceBuffer",
    "RawKernel",
    "RawModule",
    "SourceModule",
    "arange",
    "array",
    "asarray",
    "asnumpy",
    "bridge",
    "empty",
    "float32",
    "float64",
    "full",
    "install",
    "int32",
    "int64",
    "mem_alloc",
    "memcpy_dtoh",
    "memcpy_htod",
    "ndarray",
    "ones",
    "uninstall",
    "zeros",
]

__version__ = "1.0.0"

_INSTALLED_NAMES = ("cupy", "pycuda", "pycuda.driver", "pycuda.compiler")
_SAVED = {}


def install(names=_INSTALLED_NAMES):
    """Make ``import cupy`` (and pycuda) resolve to this package.

    Registers the module object in ``sys.modules`` rather than putting a fake package on the
    path. Both work, but only this one is reversible inside a live process and only this one
    cannot be shadowed by a real CuPy that happens to be installed -- an entry already in
    ``sys.modules`` is never looked up again.

    Any real module already imported under one of those names is saved, so ``uninstall``
    puts it back.
    """
    import sys

    module = sys.modules[__name__]
    for name in names:
        if name in sys.modules and sys.modules[name] is not module:
            _SAVED[name] = sys.modules[name]
        sys.modules[name] = module
    # pycuda's API is nested: `from pycuda.compiler import SourceModule`, `pycuda.driver.mem_alloc`.
    # Pointing those submodule names at this module makes both spellings resolve.
    return module


def uninstall():
    """Undo :func:`install`, restoring anything it displaced."""
    import sys

    for name in _INSTALLED_NAMES:
        if name in sys.modules and sys.modules[name].__name__ == __name__:
            del sys.modules[name]
    for name, module in _SAVED.items():
        sys.modules[name] = module
    _SAVED.clear()
