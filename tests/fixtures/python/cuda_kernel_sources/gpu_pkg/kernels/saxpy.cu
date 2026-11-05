// Fact-2: a kernel the Python program reads from a real .cu file at run time.
__device__ float scale(float a, float x) { return a * x; }

extern "C" __global__ void saxpy(float a, const float* x, float* y, int n)
{
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) y[i] = scale(a, x[i]) + y[i];
}
