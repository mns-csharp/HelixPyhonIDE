"""Device memory: the CuPy and PyCUDA spellings, both over one emulator buffer."""

import ctypes

from helix_cuda.bridge import bridge


class DeviceBuffer:
    """A block of emulator device memory.

    Deliberately dumb: a handle and a size. It does not know what type the bytes are,
    because neither does ``cudaMalloc`` -- the kernel's own signature decides that, and the
    bridge re-types the pointer per parameter at launch. Guessing a type here would mean a
    buffer that is right for the first kernel and wrong for the second one over the same data.
    """

    def __init__(self, nbytes):
        self.nbytes = int(nbytes)
        self._handle = bridge().malloc(self.nbytes)
        self._freed = False

    # PyCUDA passes device pointers to kernels as the allocation object itself, and CuPy
    # passes .data.ptr. Both end up here.
    @property
    def handle(self):
        if self._freed:
            raise RuntimeError("device buffer already freed")
        return self._handle

    @property
    def ptr(self):
        return self.handle

    def free(self):
        if not self._freed:
            bridge().free(self._handle)
            self._freed = True

    def __len__(self):
        return self.nbytes

    def __int__(self):
        # PyCUDA code does int(allocation) to get an address. There is no address here, so
        # this is a stable per-buffer number for printing -- never arithmetic.
        return id(self)

    def __repr__(self):
        state = "freed" if self._freed else self._handle
        return "<DeviceBuffer %s, %d bytes>" % (state, self.nbytes)


def mem_alloc(nbytes):
    """pycuda.driver.mem_alloc."""
    return DeviceBuffer(nbytes)


def _as_bytes(source):
    """Raw bytes of a buffer-protocol object (numpy array, bytearray, ctypes array)."""
    if isinstance(source, (bytes, bytearray, memoryview)):
        return bytes(source)
    tobytes = getattr(source, "tobytes", None)
    if callable(tobytes):
        return tobytes()
    if isinstance(source, ctypes.Array):
        return bytes(memoryview(source).cast("B"))
    raise TypeError(
        "cannot read bytes from %r -- pass a numpy array, bytes, bytearray or ctypes array"
        % type(source).__name__)


def memcpy_htod(destination, source):
    """pycuda.driver.memcpy_htod: host buffer -> device."""
    data = _as_bytes(source)
    handle = destination.handle if isinstance(destination, DeviceBuffer) else destination
    bridge().upload(handle, data)
    return len(data)


def memcpy_dtoh(destination, source):
    """pycuda.driver.memcpy_dtoh: device -> host buffer, filled in place.

    In place, because that is what the real one does and what the calling code expects: it
    already has the array it wants the answer in. Returning a new object instead would leave
    the caller's array full of zeroes with no error.
    """
    handle = source.handle if isinstance(source, DeviceBuffer) else source

    view = memoryview(destination)
    if view.readonly:
        raise TypeError("memcpy_dtoh needs a writable destination")
    raw = view.cast("B")

    # memoryview.nbytes, never len(). For every typed buffer -- array.array('i', ...),
    # a numpy array, a ctypes array -- len() is the number of ELEMENTS, so an 8-int
    # destination asked for 8 bytes and came back with two values filled and six zeroes.
    # numpy happens to expose .nbytes as well; array.array does not, and the fallback to
    # len() is what made the bug look like a kernel that only ran two lanes.
    data = bridge().download(handle, raw.nbytes)
    raw[:len(data)] = data
    return len(data)
