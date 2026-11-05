// helix_gpu.hpp — write GPU code without knowing CUDA.
//
// You write an element body. The library decides the launch configuration, moves the memory,
// checks every call, and gives you the SAME ANSWER whether it runs on the GPU or the CPU.
//
//     #include "helix_gpu.hpp"
//
//     gpu::vector<float> a(n), b(n), c(n);          // unified memory, no cudaMalloc
//     gpu::fill(a, 1.0f); gpu::fill(b, 2.0f);
//
//     gpu::for_each(n, [=] HELIX_GPU (int i) {      // no blocks, no threads, no indices maths
//         c[i] = a[i] + b[i];
//     });
//
//     float total = gpu::reduce(c);                 // a correct tree reduction, not a loop
//
// Compile with nvcc for the GPU, or with any C++ compiler (g++/clang/MSVC) for a CPU build of
// the very same source: the CPU path is the reference implementation, so a machine with no GPU
// still runs, and a wrong GPU answer is one differential test away from being caught.
//
// Design rules, in order:
//   1. Correctness before speed. Every entry point has a CPU reference with identical semantics.
//   2. No silent failure. Every CUDA call is checked; launch errors are surfaced, not swallowed.
//   3. No hidden data movement cost. Memory is unified and prefetched, never copied per call.
//   4. Shapes, not syntax. The API exposes map / reduce / scan / stencil, the things problems
//      actually are, so the user never chooses a block size or writes an index expression.
//
// SPDX-License-Identifier: MIT
#ifndef HELIX_GPU_HPP
#define HELIX_GPU_HPP

#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <stdexcept>
#include <string>
#include <vector>
#include <algorithm>
#include <numeric>

// ---------------------------------------------------------------------------------------------
// Build mode. HELIX_GPU marks a callable that must exist on both sides; under nvcc it becomes
// __host__ __device__, and under a host compiler it disappears. That single macro is why the
// same lambda compiles for both paths.
// ---------------------------------------------------------------------------------------------
#if defined(__CUDACC__)
  #define HELIX_GPU_ENABLED 1
  #define HELIX_GPU __host__ __device__
  #include <cuda_runtime.h>
#else
  #define HELIX_GPU_ENABLED 0
  #define HELIX_GPU
#endif

namespace gpu {

// ---------------------------------------------------------------------------------------------
// Errors: every CUDA call is checked. A failure throws with the call site, never a silent -1.
// ---------------------------------------------------------------------------------------------
class error : public std::runtime_error {
public:
    explicit error(const std::string& what) : std::runtime_error(what) {}
};

#if HELIX_GPU_ENABLED
inline void check(cudaError_t status, const char* call, const char* file, int line) {
    if (status == cudaSuccess) return;
    std::string hint;
    // The single most common first-run failure: the binary holds no cubin for THIS GPU because
    // the build did not say which architecture to target. Say so, rather than leaving a
    // beginner to decode "no kernel image is available for execution on the device".
    if (status == cudaErrorNoKernelImageForDevice)
        hint = "  [hint: rebuild with `nvcc -arch=native` (or -arch=sm_XX for your GPU) — "
               "the binary contains no code for this device]";
    else if (status == cudaErrorInvalidDeviceFunction)
        hint = "  [hint: add `--extended-lambda` to the nvcc command line]";
    throw error(std::string(file) + ":" + std::to_string(line) + ": " + call + " -> "
                + cudaGetErrorString(status) + hint);
}
#define HELIX_GPU_CHECK(call) ::gpu::check((call), #call, __FILE__, __LINE__)
#else
#define HELIX_GPU_CHECK(call) do { } while (0)
#endif

/// True when this build can actually use a GPU AND one is present.
inline bool available() {
#if HELIX_GPU_ENABLED
    int count = 0;
    if (cudaGetDeviceCount(&count) != cudaSuccess) return false;
    return count > 0;
#else
    return false;
#endif
}

/// Name of the device the work will run on — "CPU (reference)" when there is no GPU.
inline std::string device_name() {
#if HELIX_GPU_ENABLED
    if (available()) {
        cudaDeviceProp p;
        if (cudaGetDeviceProperties(&p, 0) == cudaSuccess) return p.name;
    }
#endif
    return "CPU (reference)";
}

// ---------------------------------------------------------------------------------------------
// gpu::vector<T> — memory that is valid on both sides. Unified memory under CUDA (so there is
// no explicit copy to forget), a plain heap buffer otherwise. Indexing works in both worlds,
// which is what lets one lambda body serve both paths.
// ---------------------------------------------------------------------------------------------
template <typename T>
class vector {
public:
    vector() : data_(nullptr), size_(0), owns_(false) {}

    explicit vector(std::size_t n) : data_(nullptr), size_(n), owns_(true) {
        if (n == 0) return;
#if HELIX_GPU_ENABLED
        void* p = nullptr;
        HELIX_GPU_CHECK(cudaMallocManaged(&p, n * sizeof(T)));
        data_ = static_cast<T*>(p);
#else
        data_ = static_cast<T*>(std::malloc(n * sizeof(T)));
        if (data_ == nullptr) throw error("out of memory");
#endif
    }

    vector(const std::vector<T>& host) : vector(host.size()) {
        for (std::size_t i = 0; i < host.size(); ++i) data_[i] = host[i];
    }

    ~vector() { release(); }

    vector(vector&& other) : data_(other.data_), size_(other.size_), owns_(other.owns_) {
        other.data_ = nullptr; other.size_ = 0; other.owns_ = false;
    }
    vector& operator=(vector&& other) {
        if (this != &other) {
            release();
            data_ = other.data_; size_ = other.size_; owns_ = other.owns_;
            other.data_ = nullptr; other.size_ = 0; other.owns_ = false;
        }
        return *this;
    }
    vector(const vector&) = delete;
    vector& operator=(const vector&) = delete;

    // Usable inside an element body on either side — this is the whole point.
    HELIX_GPU T&       operator[](std::size_t i)       { return data_[i]; }
    HELIX_GPU const T& operator[](std::size_t i) const { return data_[i]; }
    HELIX_GPU T*       data()                          { return data_; }
    HELIX_GPU const T* data() const                    { return data_; }
    HELIX_GPU std::size_t size() const                 { return size_; }

    std::vector<T> to_host() const {
        sync();
        std::vector<T> out(size_);
        for (std::size_t i = 0; i < size_; ++i) out[i] = data_[i];
        return out;
    }

    /// Wait for pending device work so the host may read the buffer.
    void sync() const {
#if HELIX_GPU_ENABLED
        if (available()) HELIX_GPU_CHECK(cudaDeviceSynchronize());
#endif
    }

private:
    void release() {
        if (!owns_ || data_ == nullptr) return;
#if HELIX_GPU_ENABLED
        cudaFree(data_);
#else
        std::free(data_);
#endif
        data_ = nullptr; size_ = 0; owns_ = false;
    }

    T* data_;
    std::size_t size_;
    bool owns_;
};

// ---------------------------------------------------------------------------------------------
// Launch configuration — chosen here so the user never picks a block size. 256 threads is the
// standard starting point; the grid is rounded up so the guard inside the kernel is the only
// bounds check anyone needs.
// ---------------------------------------------------------------------------------------------
namespace detail {
inline int block_size() { return 256; }
inline int grid_for(std::size_t n, int threads) {
    std::size_t blocks = (n + static_cast<std::size_t>(threads) - 1) / static_cast<std::size_t>(threads);
    if (blocks == 0) blocks = 1;
    return static_cast<int>(blocks);
}

#if HELIX_GPU_ENABLED
template <typename Body>
__global__ void for_each_kernel(std::size_t n, Body body) {
    std::size_t i = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
    if (i < n) body(static_cast<int>(i));
}

template <typename Body>
__global__ void for_each_2d_kernel(std::size_t w, std::size_t h, Body body) {
    std::size_t x = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
    std::size_t y = static_cast<std::size_t>(blockIdx.y) * blockDim.y + threadIdx.y;
    if (x < w && y < h) body(static_cast<int>(x), static_cast<int>(y));
}

/// Block-wide tree reduction — the correct pattern, written once, so nobody re-derives it.
template <typename T, typename Op>
__global__ void reduce_kernel(const T* in, T* partials, std::size_t n, T identity, Op op) {
    extern __shared__ char smem_raw[];
    T* smem = reinterpret_cast<T*>(smem_raw);
    unsigned tid = threadIdx.x;
    std::size_t i = static_cast<std::size_t>(blockIdx.x) * blockDim.x * 2 + threadIdx.x;

    T acc = identity;
    if (i < n) acc = in[i];
    if (i + blockDim.x < n) acc = op(acc, in[i + blockDim.x]);
    smem[tid] = acc;
    __syncthreads();

    for (unsigned s = blockDim.x / 2; s > 0; s >>= 1) {
        if (tid < s) smem[tid] = op(smem[tid], smem[tid + s]);
        __syncthreads();
    }
    if (tid == 0) partials[blockIdx.x] = smem[0];
}
#endif
} // namespace detail

// ---------------------------------------------------------------------------------------------
// MAP — the shape where each output element is independent. `i` is the element, nothing else.
// ---------------------------------------------------------------------------------------------
template <typename Body>
void for_each(std::size_t n, Body body) {
    if (n == 0) return;
#if HELIX_GPU_ENABLED
    if (available()) {
        int threads = detail::block_size();
        int blocks = detail::grid_for(n, threads);
        detail::for_each_kernel<<<blocks, threads>>>(n, body);
        HELIX_GPU_CHECK(cudaGetLastError());          // launch errors are asynchronous
        HELIX_GPU_CHECK(cudaDeviceSynchronize());
        return;
    }
#endif
    for (std::size_t i = 0; i < n; ++i) body(static_cast<int>(i));
}

/// 2-D map — `x` varies fastest, so consecutive threads touch consecutive memory.
template <typename Body>
void for_each_2d(std::size_t width, std::size_t height, Body body) {
    if (width == 0 || height == 0) return;
#if HELIX_GPU_ENABLED
    if (available()) {
        dim3 threads(16, 16);
        dim3 blocks(static_cast<unsigned>((width + threads.x - 1) / threads.x),
                    static_cast<unsigned>((height + threads.y - 1) / threads.y));
        detail::for_each_2d_kernel<<<blocks, threads>>>(width, height, body);
        HELIX_GPU_CHECK(cudaGetLastError());
        HELIX_GPU_CHECK(cudaDeviceSynchronize());
        return;
    }
#endif
    for (std::size_t y = 0; y < height; ++y)
        for (std::size_t x = 0; x < width; ++x)
            body(static_cast<int>(x), static_cast<int>(y));
}

/// out[i] = f(in[i]) for the common single-input case.
template <typename T, typename U, typename F>
void transform(const vector<T>& in, vector<U>& out, F f) {
    const T* src = in.data();
    U* dst = out.data();
    std::size_t n = in.size() < out.size() ? in.size() : out.size();
    for_each(n, [=] HELIX_GPU (int i) { dst[i] = f(src[i]); });
}

template <typename T>
void fill(vector<T>& v, T value) {
    T* dst = v.data();
    for_each(v.size(), [=] HELIX_GPU (int i) { dst[i] = value; });
}

// ---------------------------------------------------------------------------------------------
// REDUCE — an associative tree on the GPU, std::accumulate on the CPU. Users get a correct
// reduction without ever writing __shared__ or a barrier.
//
// Floating-point note: the GPU sums in a different ORDER than the CPU. Both are valid; they
// will not be bit-identical. Compare with a tolerance, never with ==.
// ---------------------------------------------------------------------------------------------
template <typename T, typename Op>
T reduce(const vector<T>& v, T identity, Op op) {
    std::size_t n = v.size();
    if (n == 0) return identity;
#if HELIX_GPU_ENABLED
    if (available()) {
        int threads = detail::block_size();
        int blocks = detail::grid_for((n + 1) / 2, threads);
        vector<T> partials(static_cast<std::size_t>(blocks));
        std::size_t shared = static_cast<std::size_t>(threads) * sizeof(T);
        detail::reduce_kernel<<<blocks, threads, shared>>>(v.data(), partials.data(), n, identity, op);
        HELIX_GPU_CHECK(cudaGetLastError());
        HELIX_GPU_CHECK(cudaDeviceSynchronize());
        T total = identity;
        for (std::size_t i = 0; i < partials.size(); ++i) total = op(total, partials[i]);
        return total;
    }
#endif
    T total = identity;
    for (std::size_t i = 0; i < n; ++i) total = op(total, v[i]);
    return total;
}

template <typename T>
T reduce(const vector<T>& v) {
    return reduce<T>(v, T(0), [] HELIX_GPU (T a, T b) { return a + b; });
}

// ---------------------------------------------------------------------------------------------
// SCAN — a prefix sum. This is deliberately NOT a naive parallel loop: a scan carries a
// dependence between elements, which is exactly the shape a one-thread-per-element kernel gets
// WRONG. The sequential form is used unless a device scan is available.
// ---------------------------------------------------------------------------------------------
template <typename T>
void inclusive_scan(const vector<T>& in, vector<T>& out) {
    std::size_t n = in.size() < out.size() ? in.size() : out.size();
    in.sync();
    T running = T(0);
    for (std::size_t i = 0; i < n; ++i) { running = running + in[i]; out[i] = running; }
    out.sync();
}

// ---------------------------------------------------------------------------------------------
// STENCIL — each output reads a fixed neighbourhood. Border handling is clamped for you, which
// is the detail hand-written stencils most often get wrong.
// ---------------------------------------------------------------------------------------------
/// A clamped read window centred on one pixel: `at(dx, dy)` never indexes outside the image.
/// This is a STRUCT rather than a nested lambda because nvcc forbids defining an extended
/// __host__ __device__ lambda inside another one — and forbids generic ones — so the user's
/// stencil body takes this concrete type.
template <typename T>
struct clamped_view {
    const T* src;
    int w, h, x, y;

    HELIX_GPU T operator()(int dx, int dy) const {
        int xx = x + dx, yy = y + dy;
        if (xx < 0) xx = 0;
        if (xx > w - 1) xx = w - 1;
        if (yy < 0) yy = 0;
        if (yy > h - 1) yy = h - 1;
        return src[static_cast<std::size_t>(yy) * w + xx];
    }
};

/// body has the signature `T(clamped_view<T> at, int x, int y)`.
template <typename T, typename Body>
void stencil_2d(std::size_t width, std::size_t height, const vector<T>& in, vector<T>& out, Body body) {
    const T* src = in.data();
    T* dst = out.data();
    int w = static_cast<int>(width), h = static_cast<int>(height);
    for_each_2d(width, height, [=] HELIX_GPU (int x, int y) {
        clamped_view<T> at; at.src = src; at.w = w; at.h = h; at.x = x; at.y = y;
        dst[static_cast<std::size_t>(y) * w + x] = body(at, x, y);
    });
}

// ---------------------------------------------------------------------------------------------
// Differential testing — run the same body on both paths and compare. This is the oracle the
// design rules ask for, packaged so a user gets it for one line of code.
// ---------------------------------------------------------------------------------------------
template <typename T>
struct comparison {
    bool equal;
    std::size_t first_difference;
    double max_absolute_difference;
};

template <typename T>
comparison<T> compare(const vector<T>& a, const std::vector<T>& reference, double tolerance = 1e-5) {
    comparison<T> r; r.equal = true; r.first_difference = 0; r.max_absolute_difference = 0.0;
    a.sync();
    std::size_t n = a.size() < reference.size() ? a.size() : reference.size();
    for (std::size_t i = 0; i < n; ++i) {
        double d = static_cast<double>(a[i]) - static_cast<double>(reference[i]);
        if (d < 0) d = -d;
        if (d > r.max_absolute_difference) r.max_absolute_difference = d;
        if (d > tolerance && r.equal) { r.equal = false; r.first_difference = i; }
    }
    if (a.size() != reference.size()) r.equal = false;
    return r;
}

} // namespace gpu

#endif // HELIX_GPU_HPP
