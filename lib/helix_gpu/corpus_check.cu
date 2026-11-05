// helix_gpu vs. the real thing.
//
// Every check below runs a kernel taken from the sweep corpora (tests/fixtures/cuda,
// cuda_lab, cudaMMC, pLEM) EXACTLY as its author wrote it — launch configuration, bounds
// guard, index arithmetic and all — and then runs the helix_gpu expression of the same
// computation. The two results are compared element by element. If the library is a real
// substitute for hand-written CUDA, they agree; if it is a toy, they do not.
//
//   nvcc -x cu -std=c++17 --extended-lambda -arch=native -ccbin <MSVC> corpus_check.cu -o corpus_check
//
// This file is CUDA-only on purpose: it exists to compare against hand-written kernels, and
// there are no hand-written kernels on the CPU path. helix_gpu's own CPU/GPU agreement is
// covered by selftest.cpp, which builds both ways.
#include "helix_gpu.hpp"
#include <cstdio>
#include <vector>
#include <cmath>

static int failures = 0;

static void check(bool ok, const char* what) {
    std::printf("%-52s %s\n", what, ok ? "ok" : "FAIL");
    if (!ok) ++failures;
}

// ---------------------------------------------------------------------------------------
// The corpus kernels, verbatim in shape.
// ---------------------------------------------------------------------------------------

// Map workload with a positive bounds guard.
__global__ void brighten(const unsigned char* in, unsigned char* out, int n, int amount) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) {
        int v = in[i] + amount;
        out[i] = v > 255 ? 255 : (unsigned char)v;
    }
}

// 3x3 stencil workload with a negative guard and
// clamped borders. This is the shape that made helix_gpu grow `clamped_view`.
__device__ int clampi(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

__global__ void blur3x3(const unsigned char* in, unsigned char* out, int W, int H) {
    int x = blockIdx.x * blockDim.x + threadIdx.x;
    int y = blockIdx.y * blockDim.y + threadIdx.y;
    if (x >= W || y >= H) return;
    int sum = 0;
    for (int dy = -1; dy <= 1; ++dy)
        for (int dx = -1; dx <= 1; ++dx)
            sum += in[clampi(y + dy, 0, H - 1) * W + clampi(x + dx, 0, W - 1)];
    out[y * W + x] = (unsigned char)(sum / 9);
}

// cuda_lab/matrix.cu — the naive matrix multiply, a 2-D map whose inner loop is a reduction
// over k. Both indices are declared row-first, which is the case that used to come back from
// the round trip with its axes swapped.
__global__ void matmul_naive(const float* a, const float* b, float* c, int n) {
    int row = blockIdx.y * blockDim.y + threadIdx.y;
    int col = blockIdx.x * blockDim.x + threadIdx.x;
    if (row < n && col < n) {
        float sum = 0.0f;
        for (int k = 0; k < n; ++k) sum += a[row * n + k] * b[k * n + col];
        c[row * n + col] = sum;
    }
}

// cudaMMC / pLEM style: a gather through an index array — the shape the translator refuses
// to parallelise automatically, but which the library expresses directly.
__global__ void gather(const float* src, const int* idx, float* dst, int n) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) dst[i] = src[idx[i]];
}

// ---------------------------------------------------------------------------------------

int main() {
    if (!gpu::available()) {
        std::printf("no CUDA device — this check compares against real kernels and needs one\n");
        return 77;
    }
    std::printf("corpus check — device: %s\n\n", gpu::device_name().c_str());

    try {
        // ---- map: brighten --------------------------------------------------------------
        {
            const int n = 100000, amount = 40;
            gpu::vector<unsigned char> in(n), hand(n), lib(n);
            unsigned char* p = in.data();
            gpu::for_each(n, [=] HELIX_GPU (int i) { p[i] = (unsigned char)(i % 256); });

            brighten<<<(n + 255) / 256, 256>>>(in.data(), hand.data(), n, amount);
            HELIX_GPU_CHECK(cudaDeviceSynchronize());

            const unsigned char* src = in.data();
            unsigned char* out = lib.data();
            gpu::for_each(n, [=] HELIX_GPU (int i) {
                int v = src[i] + amount;
                out[i] = v > 255 ? 255 : (unsigned char)v;
            });

            check(gpu::compare(lib, hand.to_host()).equal, "map: gpu::for_each == hand-written brighten");
        }

        // ---- stencil: blur3x3 -----------------------------------------------------------
        {
            const int W = 320, H = 240;
            gpu::vector<unsigned char> in(W * H), hand(W * H), lib(W * H);
            unsigned char* p = in.data();
            gpu::for_each_2d(W, H, [=] HELIX_GPU (int x, int y) { p[y * W + x] = (unsigned char)((x * 7 + y * 3) % 256); });

            dim3 block(16, 16), grid((W + 15) / 16, (H + 15) / 16);
            blur3x3<<<grid, block>>>(in.data(), hand.data(), W, H);
            HELIX_GPU_CHECK(cudaDeviceSynchronize());

            gpu::stencil_2d(W, H, in, lib,
                [] HELIX_GPU (gpu::clamped_view<unsigned char> at, int, int) {
                    int sum = 0;
                    for (int dy = -1; dy <= 1; ++dy)
                        for (int dx = -1; dx <= 1; ++dx) sum += at(dx, dy);
                    return (unsigned char)(sum / 9);
                });

            check(gpu::compare(lib, hand.to_host()).equal, "stencil: gpu::stencil_2d == hand-written blur3x3");
        }

        // ---- 2-D map with an inner reduction: matmul ------------------------------------
        {
            const int n = 128;
            gpu::vector<float> a(n * n), b(n * n), hand(n * n), lib(n * n);
            float* pa = a.data(); float* pb = b.data();
            gpu::for_each(n * n, [=] HELIX_GPU (int i) {
                pa[i] = (float)((i % 13) - 6);
                pb[i] = (float)((i % 7) - 3);
            });

            dim3 block(16, 16), grid((n + 15) / 16, (n + 15) / 16);
            matmul_naive<<<grid, block>>>(a.data(), b.data(), hand.data(), n);
            HELIX_GPU_CHECK(cudaDeviceSynchronize());

            const float* qa = a.data(); const float* qb = b.data(); float* qc = lib.data();
            gpu::for_each_2d(n, n, [=] HELIX_GPU (int col, int row) {
                float sum = 0.0f;
                for (int k = 0; k < n; ++k) sum += qa[row * n + k] * qb[k * n + col];
                qc[row * n + col] = sum;
            });

            check(gpu::compare(lib, hand.to_host()).equal, "matmul: gpu::for_each_2d == hand-written matmul");
        }

        // ---- gather ----------------------------------------------------------------------
        {
            const int n = 50000;
            gpu::vector<float> src(n), hand(n), lib(n);
            gpu::vector<int> idx(n);
            float* ps = src.data(); int* pi = idx.data();
            gpu::for_each(n, [=] HELIX_GPU (int i) {
                ps[i] = (float)i * 0.5f;
                pi[i] = (i * 2654435761u) % n;      // a scrambled, data-dependent read
            });

            gather<<<(n + 255) / 256, 256>>>(src.data(), idx.data(), hand.data(), n);
            HELIX_GPU_CHECK(cudaDeviceSynchronize());

            const float* qs = src.data(); const int* qi = idx.data(); float* qd = lib.data();
            gpu::for_each(n, [=] HELIX_GPU (int i) { qd[i] = qs[qi[i]]; });

            check(gpu::compare(lib, hand.to_host()).equal, "gather: gpu::for_each == hand-written gather");
        }

        // ---- reduce: the library's answer against a host sum -----------------------------
        {
            const int n = 1 << 20;
            gpu::vector<float> v(n);
            float* p = v.data();
            gpu::for_each(n, [=] HELIX_GPU (int i) { p[i] = (float)(i % 17); });
            float got = gpu::reduce(v);

            double want = 0.0;
            for (int i = 0; i < n; ++i) want += (double)(i % 17);
            check(std::fabs(got - (float)want) / want < 1e-4, "reduce: gpu::reduce == host sum");
        }
    } catch (const gpu::error& e) {
        std::printf("\nGPU ERROR: %s\n", e.what());
        return 2;
    }

    std::printf("\n%s\n", failures == 0 ? "ALL CORPUS CHECKS PASSED" : "FAILURES PRESENT");
    return failures == 0 ? 0 : 1;
}
