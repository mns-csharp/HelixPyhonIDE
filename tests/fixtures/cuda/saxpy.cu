// SAXPY: y = a*x + y  — demonstrates CUDA syntax highlighting.
#include <cuda_runtime.h>

__global__ void saxpy(int n, float a, const float* x, float* y)
{
    int i = blockIdx.x * blockDim.x + threadIdx.x;  // global thread index
    if (i < n) {
        y[i] = a * x[i] + y[i];
    }
}

int main()
{
    const int N = 1 << 20;
    float *dx, *dy;
    cudaMalloc(&dx, N * sizeof(float));
    cudaMalloc(&dy, N * sizeof(float));

    dim3 block(256);
    dim3 grid((N + block.x - 1) / block.x);
    saxpy<<<grid, block>>>(N, 2.0f, dx, dy);
    cudaDeviceSynchronize();

    cudaFree(dx);
    cudaFree(dy);
    return 0;
}
