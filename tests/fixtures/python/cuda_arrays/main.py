"""Array maths and a hand-written kernel in one unmodified CuPy-shaped program.

Prints one `name = value` line per check. The C# test compares them, so the output format is
part of the test: keep it stable.
"""

import cupy as cp

KERNEL = cp.RawKernel(r'''
extern "C" __global__ void scale(float* d, float f, int n)
{
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n)
    {
        d[i] = d[i] * f;
    }
}
''', "scale")


def main():
    n = 10

    # Constructors.
    print("zeros =", cp.zeros(4, dtype="float32").tolist())
    print("ones =", cp.ones(4, dtype="int32").tolist())
    print("full =", cp.full(3, 7, dtype="int32").tolist())
    print("arange =", cp.arange(n, dtype="float32").tolist())
    print("arange3 =", cp.arange(2, 11, 3, dtype="int32").tolist())
    print("asarray =", cp.asarray([3, 1, 4, 1, 5]).tolist())

    # Elementwise arithmetic, array with array and array with scalar.
    a = cp.arange(n, dtype="float32")
    b = cp.ones(n, dtype="float32")
    c = a + b * 2.0
    print("combined =", c.tolist())
    print("minus =", (c - a).tolist())
    print("divided =", (c / 2.0).tolist())
    print("rsub =", (20.0 - a).tolist())

    # A reduction -- shared memory and __syncthreads on the emulator.
    print("sum =", c.sum())
    print("intsum =", cp.asarray([3, 1, 4, 1, 5]).sum())

    # The array goes straight into a hand-written kernel, with cp.float32 pinning the scalar's
    # type the way real CuPy code does.
    KERNEL((1,), (n,), (c, cp.float32(10.0), n))
    print("afterkernel =", c.tolist())

    # Indexing and length.
    print("item =", c[3])
    print("len =", len(c))
    print("shape =", c.shape)

    # The limits are part of the contract: they must fail loudly, not quietly.
    try:
        cp.zeros((2, 3))
        print("twod = NO ERROR")
    except NotImplementedError:
        print("twod = refused")
    try:
        cp.arange(4) + cp.arange(5)
        print("mismatch = NO ERROR")
    except ValueError:
        print("mismatch = refused")


main()
