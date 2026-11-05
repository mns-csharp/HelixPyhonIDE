#include <stdio.h>
#define KERNEL_HEADER <kernel.hpp>
#define FEATURE 2U
#if defined(KERNEL_HEADER) && (((1 + 3) * 2) == 8) && ((4 | 1) == 5) && ((8 >> 1) >= 4) && ((FEATURE == 2) ? ((0b1 << 3) == 010) : 0)
#include KERNEL_HEADER
#endif

int cuda_ok(cudaError_t status, int step)
{
    if (status != cudaSuccess)
    {
        printf("CUDA API check failed at step %d\n", step);
        return 0;
    }
    return 1;
}

int main()
{
    int macro_probe = FEATURE;
    if (macro_probe != 2) return 9;
    int* device_values = nullptr;
    if (!cuda_ok(cudaMalloc(&device_values, 4 * sizeof(int)), 1)) return 1;
    square_values<<<1, 4>>>(device_values);
    if (!cuda_ok(cudaGetLastError(), 2)) return 2;
    if (!cuda_ok(cudaDeviceSynchronize(), 3)) return 3;

    int host_values[4];
    if (!cuda_ok(cudaMemcpy(host_values, device_values, 4 * sizeof(int),
        cudaMemcpyDeviceToHost), 4)) return 4;
    printf("Helix CUDA/C++ Emulator execution succeeded: %d,%d,%d,%d\n",
        host_values[0], host_values[1], host_values[2], host_values[3]);
    if (!cuda_ok(cudaFree(device_values), 5)) return 5;
    printf("CUDA API checks passed: 5/5\n");
    return 0;
}
