"""A small device array, so a program can do its glue-code maths as well as its kernels.

``helix_cuda`` started as kernels only: compile, allocate, copy, launch. That is the part that
genuinely needs a GPU, and it was the honest place to stop. But real CuPy code is not only
kernels -- it builds its inputs with ``cp.zeros``/``cp.arange``, scales something with ``a * 2``,
and checks the answer with ``.sum()``. A program that does any of that used to stop with a clear
error, which is honest but not much use.

**Everything here runs on the emulator.** ``a + b`` compiles an elementwise CUDA kernel and
launches it through the bridge, exactly as a kernel the user wrote would be. Doing the arithmetic
in Python instead would be far simpler and would produce the same numbers -- and would mean that
"it ran on the emulated GPU" was no longer true of half the program. It would also hide emulator
bugs behind CPython's arithmetic, which is the opposite of what an emulator is for.

What this deliberately is NOT: it is one-dimensional, it has no broadcasting, no slicing, no
linear algebra and no ufuncs beyond the four operators and ``sum``. Those are real limits, and a
program that needs them will say so plainly rather than quietly computing something else.
"""

# Imported under a private name: this module also defines cupy's `array(...)` constructor,
# and the two names would otherwise collide -- silently, because the shadowing only bites
# the first time something calls tolist().
import array as _array

from helix_cuda.bridge import bridge
from helix_cuda.memory import DeviceBuffer

#: name -> (array.array typecode, bytes per element, CUDA type, python converter)
_DTYPES = {
    "int32": ("i", 4, "int", int),
    "float32": ("f", 4, "float", float),
    "float64": ("d", 8, "double", float),
}

_ALIASES = {
    "int": "int32",
    "i4": "int32",
    "int32": "int32",
    "float": "float32",
    "f4": "float32",
    "float32": "float32",
    "double": "float64",
    "f8": "float64",
    "float64": "float64",
}


def _resolve_dtype(dtype):
    """Accept the spellings real code uses: a string, numpy's dtype, or a Python type."""
    if dtype is None:
        return "float32"
    name = getattr(dtype, "name", None)          # numpy dtype
    if isinstance(name, str):
        dtype = name
    elif dtype is int:
        dtype = "int32"
    elif dtype is float:
        dtype = "float64"
    key = str(dtype).strip().lower()
    if key in _ALIASES:
        return _ALIASES[key]
    raise TypeError(
        "helix_cuda supports dtypes %s, not %r"
        % (", ".join(sorted(_DTYPES)), dtype))


_ELEMENTWISE = """\
extern "C" __global__ void helix_ew(const %(t)s* a, const %(t)s* b, %(t)s* out, int n)
{
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n)
    {
        out[i] = a[i] %(op)s b[i];
    }
}
"""

_SCALAR = """\
extern "C" __global__ void helix_scalar(const %(t)s* a, %(t)s s, %(t)s* out, int n)
{
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n)
    {
        out[i] = %(expr)s;
    }
}
"""

_FILL = """\
extern "C" __global__ void helix_fill(%(t)s* out, %(t)s value, %(t)s step, int n)
{
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n)
    {
        out[i] = value + step * i;
    }
}
"""

# One block of 64, looping over the input. 64 rather than 256 because every thread in a block that
# uses __syncthreads becomes a real thread in the emulator, and 64 is plenty to exercise the
# reduction while staying cheap.
_SUM = """\
extern "C" __global__ void helix_sum(const %(t)s* a, %(t)s* out, int n)
{
    __shared__ %(t)s partial[64];
    int t = threadIdx.x;
    %(t)s acc = 0;
    for (int i = t; i < n; i = i + 64)
    {
        acc = acc + a[i];
    }
    partial[t] = acc;
    __syncthreads();
    for (int s = 32; s > 0; s = s / 2)
    {
        if (t < s)
        {
            partial[t] = partial[t] + partial[t + s];
        }
        __syncthreads();
    }
    if (t == 0)
    {
        out[0] = partial[0];
    }
}
"""

_THREADS_PER_BLOCK = 128


class ndarray(object):
    """A one-dimensional array living in emulator device memory."""

    def __init__(self, size, dtype="float32", buffer=None):
        self.dtype = _resolve_dtype(dtype)
        self.size = int(size)
        if self.size < 0:
            raise ValueError("negative array size")
        self.itemsize = _DTYPES[self.dtype][1]
        self.nbytes = self.size * self.itemsize
        self.data = buffer if buffer is not None else DeviceBuffer(max(self.nbytes, 1))

    # ---- shape-ish attributes real code reads ----------------------------

    @property
    def shape(self):
        return (self.size,)

    @property
    def ndim(self):
        return 1

    def __len__(self):
        return self.size

    def __repr__(self):
        return "helix_cuda.ndarray(%r, dtype=%s)" % (self.tolist(), self.dtype)

    # ---- host transfer ---------------------------------------------------

    def tolist(self):
        """The contents as a plain Python list."""
        if self.size == 0:
            return []
        raw = bridge().download(self.data.handle, self.nbytes)
        values = _array.array(_DTYPES[self.dtype][0])
        values.frombytes(raw[:self.nbytes])
        return list(values)

    def get(self):
        """cupy.ndarray.get: bring it back to the host.

        Returns a numpy array when numpy is importable, because that is what calling code does
        arithmetic on afterwards, and a plain list otherwise.
        """
        values = self.tolist()
        try:
            import numpy
        except ImportError:
            return values
        return numpy.array(values, dtype=self.dtype)

    def copy(self):
        result = ndarray(self.size, self.dtype)
        if self.size:
            bridge().upload(result.data.handle,
                            bridge().download(self.data.handle, self.nbytes))
        return result

    def __getitem__(self, index):
        # One element at a time, by download. Slicing is not supported rather than being emulated
        # badly: a slice that silently copied would give callers a view they could not write through.
        if isinstance(index, slice):
            raise TypeError("helix_cuda arrays do not support slicing")
        position = int(index)
        if position < 0:
            position += self.size
        if position < 0 or position >= self.size:
            raise IndexError("index %d is out of bounds for size %d" % (index, self.size))
        return self.tolist()[position]

    # ---- arithmetic ------------------------------------------------------

    def _elementwise(self, other, op, reverse=False):
        cuda_type = _DTYPES[self.dtype][2]

        if isinstance(other, ndarray):
            if other.size != self.size:
                raise ValueError(
                    "shapes (%d,) and (%d,) do not match -- helix_cuda does not broadcast"
                    % (self.size, other.size))
            if other.dtype != self.dtype:
                raise TypeError(
                    "cannot combine %s with %s -- helix_cuda does not promote dtypes"
                    % (self.dtype, other.dtype))
            left, right = (other, self) if reverse else (self, other)
            result = ndarray(self.size, self.dtype)
            if self.size:
                module = bridge().compile_module(_ELEMENTWISE % {"t": cuda_type, "op": op})
                _launch(module, "helix_ew", self.size,
                        [{"ptr": left.data.handle}, {"ptr": right.data.handle},
                         {"ptr": result.data.handle}, {"i": self.size}])
            return result

        if not isinstance(other, (int, float)):
            item = getattr(other, "item", None)   # a numpy scalar
            if not callable(item):
                return NotImplemented
            other = item()

        expression = ("s %s a[i]" % op) if reverse else ("a[i] %s s" % op)
        result = ndarray(self.size, self.dtype)
        if self.size:
            module = bridge().compile_module(
                _SCALAR % {"t": cuda_type, "expr": expression})
            _launch(module, "helix_scalar", self.size,
                    [{"ptr": self.data.handle}, _scalar(self.dtype, other),
                     {"ptr": result.data.handle}, {"i": self.size}])
        return result

    def __add__(self, other): return self._elementwise(other, "+")
    def __sub__(self, other): return self._elementwise(other, "-")
    def __mul__(self, other): return self._elementwise(other, "*")
    def __truediv__(self, other): return self._elementwise(other, "/")

    def __radd__(self, other): return self._elementwise(other, "+", reverse=True)
    def __rsub__(self, other): return self._elementwise(other, "-", reverse=True)
    def __rmul__(self, other): return self._elementwise(other, "*", reverse=True)
    def __rtruediv__(self, other): return self._elementwise(other, "/", reverse=True)

    __div__ = __truediv__          # Python 2 spelling, harmless to keep

    def sum(self):
        """Add every element, on the emulator.

        A real tree reduction rather than a host-side loop over ``tolist()``. The point of running
        it as a kernel is that it exercises shared memory and ``__syncthreads`` -- the two things a
        reduction gets wrong -- instead of testing CPython's ``sum``.
        """
        if self.size == 0:
            return 0 if self.dtype == "int32" else 0.0
        cuda_type = _DTYPES[self.dtype][2]
        result = ndarray(1, self.dtype)
        module = bridge().compile_module(_SUM % {"t": cuda_type})
        bridge().launch(module, "helix_sum", (1, 1, 1), (64, 1, 1),
                        [{"ptr": self.data.handle}, {"ptr": result.data.handle},
                         {"i": self.size}])
        return result.tolist()[0]


def _scalar(dtype, value):
    """One kernel scalar argument, typed to match the array's own element type."""
    if dtype == "int32":
        return {"i": int(value)}
    return {"f": float(value)}


def _launch(module, name, size, arguments):
    blocks = (size + _THREADS_PER_BLOCK - 1) // _THREADS_PER_BLOCK
    bridge().launch(module, name, (blocks, 1, 1), (_THREADS_PER_BLOCK, 1, 1), arguments)


# ---- constructors --------------------------------------------------------

def _filled(size, dtype, start, step):
    result = ndarray(size, dtype)
    if result.size:
        cuda_type = _DTYPES[result.dtype][2]
        module = bridge().compile_module(_FILL % {"t": cuda_type})
        _launch(module, "helix_fill", result.size,
                [{"ptr": result.data.handle}, _scalar(result.dtype, start),
                 _scalar(result.dtype, step), {"i": result.size}])
    return result


def zeros(shape, dtype="float32"):
    return _filled(_size_of(shape), dtype, 0, 0)


def ones(shape, dtype="float32"):
    return _filled(_size_of(shape), dtype, 1, 0)


def full(shape, value, dtype="float32"):
    return _filled(_size_of(shape), dtype, value, 0)


def empty(shape, dtype="float32"):
    # Zeroed rather than genuinely uninitialised. Real CuPy hands back whatever was in that memory;
    # doing the same here would make a program that forgot to fill its array fail differently on
    # every run, which is a miserable thing to debug and teaches nothing about the GPU.
    return zeros(shape, dtype)


def arange(*args, **kwargs):
    """arange(stop) / arange(start, stop) / arange(start, stop, step)."""
    dtype = kwargs.pop("dtype", None)
    if kwargs:
        raise TypeError("unsupported arange keyword(s): " + ", ".join(sorted(kwargs)))
    if len(args) == 1:
        start, stop, step = 0, args[0], 1
    elif len(args) == 2:
        start, stop, step = args[0], args[1], 1
    elif len(args) == 3:
        start, stop, step = args
    else:
        raise TypeError("arange takes 1 to 3 positional arguments")
    if step == 0:
        raise ValueError("arange step cannot be zero")

    if dtype is None:
        every_int = all(isinstance(v, int) for v in (start, stop, step))
        dtype = "int32" if every_int else "float32"
    count = int(max(0, -(-(stop - start) // step))) if isinstance(step, int) and isinstance(start, int) \
        else int(max(0, (float(stop) - float(start)) / float(step)))
    return _filled(count, dtype, start, step)


def asarray(values, dtype=None):
    """Copy host data up to the device."""
    if isinstance(values, ndarray):
        return values if dtype is None or _resolve_dtype(dtype) == values.dtype else values.copy()

    tolist = getattr(values, "tolist", None)     # numpy array
    if callable(tolist):
        if dtype is None:
            dtype = getattr(getattr(values, "dtype", None), "name", None)
        values = tolist()
    values = list(values)

    if dtype is None:
        dtype = "int32" if all(isinstance(v, int) for v in values) else "float32"
    resolved = _resolve_dtype(dtype)

    result = ndarray(len(values), resolved)
    if result.size:
        typecode, _, _, convert = _DTYPES[resolved]
        staged = _array.array(typecode, [convert(v) for v in values])
        bridge().upload(result.data.handle, staged.tobytes())
    return result


def array(values, dtype=None):
    """cupy.array -- a copy of the data on the device."""
    return asarray(values, dtype)


def asnumpy(value):
    """cupy.asnumpy."""
    return value.get() if isinstance(value, ndarray) else value


# ---- dtype scalars -------------------------------------------------------
#
# `kernel(grid, block, (buf, cp.float32(2.0), n))` is how real CuPy code pins a kernel argument's
# type, and it is extremely common -- without these, such a program dies on an AttributeError
# before reaching anything interesting. They return plain Python numbers because that is what the
# bridge's argument encoder already types correctly: an int becomes an integer argument and a float
# becomes a floating-point one, which is the distinction these calls exist to express.

def float32(value):
    """cupy.float32 -- a float argument for a kernel that declares `float`."""
    return float(value)


def float64(value):
    """cupy.float64 -- a float argument for a kernel that declares `double`."""
    return float(value)


def int32(value):
    """cupy.int32 -- an integer argument."""
    return int(value)


def int64(value):
    """cupy.int64 -- an integer argument."""
    return int(value)


def _size_of(shape):
    """Only 1-D. A tuple is accepted so `zeros((n,))` works, but not a 2-D one."""
    if isinstance(shape, int):
        return shape
    dimensions = tuple(shape)
    if len(dimensions) != 1:
        raise NotImplementedError(
            "helix_cuda arrays are one-dimensional; %r has %d dimensions"
            % (dimensions, len(dimensions)))
    return int(dimensions[0])
