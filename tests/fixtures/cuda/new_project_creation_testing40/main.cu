#include <cstdio>

__global__ void addVec(const float* a, const float* b, float* c, int n) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) c[i] = a[i] + b[i];
}

int main() {
    printf("new_project_creation_testing40: CUDA kernel ready\n");
    return 0;
}
