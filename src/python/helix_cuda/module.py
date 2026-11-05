"""Kernel compilation and launching: RawKernel, RawModule and SourceModule."""

import ctypes

from helix_cuda.bridge import bridge
from helix_cuda.memory import DeviceBuffer


def _argument(value):
    """One kernel argument, in the shape the bridge expects.

    A device buffer becomes a pointer handle; everything else becomes a scalar. The
    int/float split matters: the emulator binds a parameter by the Value it is given, so
    passing 3 where the kernel declares ``float`` would hand it an integer 3 and quietly
    change the arithmetic.
    """
    if isinstance(value, DeviceBuffer):
        return {"ptr": value.handle}

    # A device array passed straight to a kernel -- the ordinary CuPy spelling, where the array
    # IS the argument and the pointer is an implementation detail the caller never sees.
    buffer = getattr(value, "data", None)
    if isinstance(buffer, DeviceBuffer):
        return {"ptr": buffer.handle}

    # numpy scalars (np.int32(8), np.float32(1.5)) are what real CuPy code passes.
    item = getattr(value, "item", None)
    if callable(item) and not isinstance(value, (int, float, bool)):
        try:
            value = item()
        except Exception:
            pass

    if isinstance(value, bool):
        return {"i": int(value)}
    if isinstance(value, int):
        return {"i": int(value)}
    if isinstance(value, float):
        return {"f": float(value)}
    if isinstance(value, (ctypes.c_float, ctypes.c_double)):
        return {"f": float(value.value)}
    if isinstance(value, ctypes._SimpleCData):
        return {"i": int(value.value)}

    raise TypeError(
        "cannot pass %r to a kernel -- expected a device buffer, an int or a float"
        % type(value).__name__)


def _dim(value):
    """CUDA's (x[, y[, z]]) tuple, or a bare int, as a 3-tuple."""
    if value is None:
        return (1, 1, 1)
    if isinstance(value, int):
        return (value, 1, 1)
    items = list(value)
    while len(items) < 3:
        items.append(1)
    return tuple(int(v) for v in items[:3])


class RawKernel:
    """cupy.RawKernel: one named kernel, compiled from source, called like a function.

    ``kernel(grid, block, args)`` -- the CuPy calling convention, where grid and block are
    tuples and the arguments arrive as one tuple.
    """

    def __init__(self, code, name, options=(), backend="nvrtc", **kwargs):
        self.code = code
        self.name = name
        self.options = tuple(options)
        self.backend = backend
        # Compiled eagerly, as CuPy does, so a syntax error in the kernel is raised where the
        # RawKernel is constructed rather than at the first launch in some later loop.
        self._module = bridge().compile_module(code)

    def __call__(self, grid, block, args, shared_mem=0, **kwargs):
        bridge().launch(
            self._module, self.name, _dim(grid), _dim(block),
            [_argument(a) for a in args], shared=shared_mem)

    @property
    def kernel(self):
        return self

    def __repr__(self):
        return "<helix_cuda.RawKernel %s>" % self.name


class RawModule:
    """cupy.RawModule: compile once, then ask for kernels by name."""

    def __init__(self, code=None, options=(), backend="nvrtc", **kwargs):
        if code is None:
            raise ValueError("helix_cuda.RawModule needs source code "
                             "(path= and cubin modules are not emulated)")
        self.code = code
        self._module = bridge().compile_module(code)

    def get_function(self, name):
        return _BoundKernel(self._module, name)


class _BoundKernel:
    """A kernel already resolved inside a compiled module."""

    def __init__(self, module, name):
        self._module = module
        self.name = name

    def __call__(self, grid, block, args, shared_mem=0, **kwargs):
        bridge().launch(self._module, self.name, _dim(grid), _dim(block),
                        [_argument(a) for a in args], shared=shared_mem)

    def __repr__(self):
        return "<helix_cuda kernel %s>" % self.name


class SourceModule:
    """pycuda.compiler.SourceModule.

    Same job as RawModule, different calling convention: PyCUDA kernels take their arguments
    positionally with ``grid=`` and ``block=`` keywords, rather than CuPy's three-tuple form.
    Both are supported because real code in the wild is written against both, and a program
    should not have to be rewritten to run here.
    """

    def __init__(self, source, options=None, **kwargs):
        self.source = source
        self._module = bridge().compile_module(source)

    def get_function(self, name):
        return _PyCudaKernel(self._module, name)


class _PyCudaKernel:
    def __init__(self, module, name):
        self._module = module
        self.name = name

    def __call__(self, *args, **kwargs):
        block = _dim(kwargs.pop("block", None))
        grid = _dim(kwargs.pop("grid", None))
        shared = kwargs.pop("shared", 0)
        kwargs.pop("stream", None)
        if kwargs:
            raise TypeError("unsupported kernel keyword(s): " + ", ".join(sorted(kwargs)))
        bridge().launch(self._module, self.name, grid, block,
                        [_argument(a) for a in args], shared=shared)

    def prepare(self, *args, **kwargs):
        # PyCUDA's prepared-call API is an optimisation over the same launch. Accepting it
        # and doing nothing keeps programs that use it working.
        return self

    def prepared_call(self, grid, block, *args, **kwargs):
        bridge().launch(self._module, self.name, _dim(grid), _dim(block),
                        [_argument(a) for a in args], shared=kwargs.pop("shared_size", 0))

    def __repr__(self):
        return "<helix_cuda pycuda kernel %s>" % self.name
