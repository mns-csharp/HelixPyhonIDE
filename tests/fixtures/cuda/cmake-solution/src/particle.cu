__global__ void advance(float* positions, const float* velocities, int count)
{
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < count) positions[i] += velocities[i];
}
