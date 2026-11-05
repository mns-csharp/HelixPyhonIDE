__host__ __device__ int square(int value)
{
    return value * value;
}

__global__ void square_values(int* values)
{
    int i = threadIdx.x;
    values[i] = square(i + 1);
}
