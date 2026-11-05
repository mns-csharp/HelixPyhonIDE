// Loaded by main.py as a plain string -- the shim never sees this path. The kernel debugger
// still binds a breakpoint set in THIS file, by matching its text inside the compiled module.
extern "C" __global__ void scale(int* data, int factor, int n)
{
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n)
    {
        int value = data[i];
        data[i] = value * factor;
    }
}
