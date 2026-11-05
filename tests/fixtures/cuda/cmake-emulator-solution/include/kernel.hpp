#pragma once
__host__ __device__ int square(int value);
__global__ void square_values(int* values);
