// Deliberately broken CUDA: a missing ';' and an undeclared identifier.
__global__ void k(int* a) {
    int i = threadIdx.x   // <-- missing semicolon
    a[i] = undeclared_var; // <-- undeclared identifier
}
