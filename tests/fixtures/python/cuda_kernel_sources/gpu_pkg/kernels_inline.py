"""Style 1: the kernel is a string literal in the Python source."""

import cupy as cp

_ADD_SRC = r"""
extern "C" __global__ void add_vectors(const float* a, const float* b, float* c, int n)
{
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) c[i] = a[i] + b[i];
}
"""


class InlineKernels:
    @staticmethod
    def build():
        return cp.RawKernel(_ADD_SRC, "add_vectors")
