__global__ void unchecked_kernel()
{
}

int main()
{
    cudaSetDevice(0);
    unchecked_kernel<<<1, 1>>>();
    return 0;
}
