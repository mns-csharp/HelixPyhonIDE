// Self-test for helix_gpu.hpp. Compiles BOTH ways from this one file:
//   nvcc -x cu -std=c++17 --extended-lambda selftest.cpp -o selftest_gpu
//   cl  /std:c++17 /EHsc selftest.cpp                      (or g++ -std=c++17)
// Every check compares the running path against an independently computed reference.
#include "helix_gpu.hpp"
#include <cstdio>
#include <vector>
#include <cmath>

static int failures = 0;

static void check(bool ok, const char* what) {
    std::printf("%-46s %s\n", what, ok ? "ok" : "FAIL");
    if (!ok) ++failures;
}

static int run();

int main() {
    // A GPU failure must arrive as a readable message, not a process abort.
    try {
        return run();
    } catch (const gpu::error& e) {
        std::printf("\nGPU ERROR: %s\n", e.what());
        return 2;
    }
}

static int run() {
    std::printf("helix_gpu selftest — device: %s (gpu build: %d)\n\n",
                gpu::device_name().c_str(), HELIX_GPU_ENABLED);

    const std::size_t n = 100000;

    // ---- map ------------------------------------------------------------------------------
    {
        gpu::vector<float> a(n), b(n), c(n);
        gpu::fill(a, 1.5f);
        gpu::fill(b, 2.25f);
        float* pa = a.data(); float* pb = b.data(); float* pc = c.data();
        gpu::for_each(n, [=] HELIX_GPU (int i) { pc[i] = pa[i] + pb[i]; });

        std::vector<float> reference(n, 3.75f);
        gpu::comparison<float> r = gpu::compare(c, reference);
        check(r.equal, "map: c[i] = a[i] + b[i]");
    }

    // ---- transform ------------------------------------------------------------------------
    {
        gpu::vector<float> in(n), out(n);
        float* p = in.data();
        gpu::for_each(n, [=] HELIX_GPU (int i) { p[i] = static_cast<float>(i % 100); });
        gpu::transform(in, out, [] HELIX_GPU (float v) { return v * 2.0f + 1.0f; });

        std::vector<float> reference(n);
        for (std::size_t i = 0; i < n; ++i) reference[i] = static_cast<float>(i % 100) * 2.0f + 1.0f;
        check(gpu::compare(out, reference).equal, "transform: out[i] = 2*in[i] + 1");
    }

    // ---- reduce ---------------------------------------------------------------------------
    {
        gpu::vector<float> v(n);
        float* p = v.data();
        gpu::for_each(n, [=] HELIX_GPU (int i) { p[i] = 1.0f; });
        float total = gpu::reduce(v);
        check(std::fabs(total - static_cast<float>(n)) < 1e-1f, "reduce: sum of ones == n");

        gpu::vector<int> ints(1000);
        int* q = ints.data();
        gpu::for_each(1000, [=] HELIX_GPU (int i) { q[i] = i; });
        int m = gpu::reduce<int>(ints, 0, [] HELIX_GPU (int x, int y) { return x > y ? x : y; });
        check(m == 999, "reduce: custom max operator");
    }

    // ---- 2-D map --------------------------------------------------------------------------
    {
        const std::size_t w = 320, h = 240;
        gpu::vector<int> img(w * h);
        int* p = img.data();
        int W = static_cast<int>(w);
        gpu::for_each_2d(w, h, [=] HELIX_GPU (int x, int y) { p[static_cast<std::size_t>(y) * W + x] = x + y; });

        std::vector<int> reference(w * h);
        for (std::size_t y = 0; y < h; ++y)
            for (std::size_t x = 0; x < w; ++x) reference[y * w + x] = static_cast<int>(x + y);
        check(gpu::compare(img, reference).equal, "for_each_2d: img[y][x] = x + y");
    }

    // ---- stencil with clamped borders ------------------------------------------------------
    {
        const std::size_t w = 64, h = 48;
        gpu::vector<float> in(w * h), out(w * h);
        float* p = in.data();
        int W = static_cast<int>(w);
        gpu::for_each_2d(w, h, [=] HELIX_GPU (int x, int y) { p[static_cast<std::size_t>(y) * W + x] = static_cast<float>(x); });

        gpu::stencil_2d(w, h, in, out, [] HELIX_GPU (gpu::clamped_view<float> at, int, int) {
            return (at(-1, 0) + at(0, 0) + at(1, 0)) / 3.0f;
        });

        std::vector<float> reference(w * h);
        for (int y = 0; y < static_cast<int>(h); ++y) {
            for (int x = 0; x < W; ++x) {
                int l = x - 1 < 0 ? 0 : x - 1;
                int r = x + 1 > W - 1 ? W - 1 : x + 1;
                reference[static_cast<std::size_t>(y) * w + x] =
                    (static_cast<float>(l) + static_cast<float>(x) + static_cast<float>(r)) / 3.0f;
            }
        }
        check(gpu::compare(out, reference).equal, "stencil_2d: 1x3 blur with clamped borders");
    }

    // ---- scan (a carried dependence, deliberately sequential) --------------------------------
    {
        gpu::vector<int> in(1000), out(1000);
        int* p = in.data();
        gpu::for_each(1000, [=] HELIX_GPU (int i) { p[i] = 1; });
        gpu::inclusive_scan(in, out);
        std::vector<int> reference(1000);
        for (int i = 0; i < 1000; ++i) reference[i] = i + 1;
        check(gpu::compare(out, reference).equal, "inclusive_scan: running total");
    }

    std::printf("\n%s\n", failures == 0 ? "ALL CHECKS PASSED" : "FAILURES PRESENT");
    return failures == 0 ? 0 : 1;
}
