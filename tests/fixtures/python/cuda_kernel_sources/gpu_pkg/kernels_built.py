"""Style 4: the kernel text only exists once the program is running.

Nothing static can see this one -- the final string is assembled from pieces at
call time. It is here so a test can prove the analyser does NOT claim to find it.
"""

import cupy as cp

_HEADER = "extern \"C\" __global__ void "
_BODY = "(float* out) { out[threadIdx.x] = %d; }"


class BuiltKernels:
    @staticmethod
    def make(name, value):
        return cp.RawKernel(_HEADER + name + (_BODY % value), name)
