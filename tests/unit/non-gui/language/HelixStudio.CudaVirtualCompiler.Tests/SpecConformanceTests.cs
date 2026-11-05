using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.CudaVirtualCompiler;
using HelixPythonNamespace.Python.Diagnostics;

namespace HelixPythonNamespace.CudaVirtualCompiler.Tests
{
    /// <summary>
    /// Spec-conformance corpus for C++23 (ISO N4950) and CUDA, derived from
    /// language conformance corpus. Each entry is valid source code
    /// that the engine must accept with zero errors — a failure here is a coverage gap, not a
    /// style issue. The companion Python corpus lives in HelixPythonNamespace.Python.Tests.
    /// </summary>
    [TestClass]
    public class SpecConformanceTests
    {
        private static int Errors(string source)
        {
            var opts = new CudaCompileOptions
            {
                EnableLexParse = true,
                EnableAstValidator = false,
                EnableSymtablePass = true,
                EnableQualifierSync = false,
                EnableLaunchConfig = false,
                EnableMemorySpace = false,
                EnableLint = false,
                EnableLanguageStandard = false,
                EnableNameResolution = true,
                EnableIncludeResolution = false,
            };
            var r = CudaVirtualCompiler.CompileSource(source, "spec.cu", opts);
            if (r == null || r.Diagnostics == null) return 0;
            return r.Diagnostics.Count(d => d.Severity == ParseSeverity.Error);
        }

        private static void AllClean(Dictionary<string, string> cases)
        {
            var failures = new List<string>();
            foreach (var c in cases)
                if (Errors(c.Value) > 0) failures.Add(c.Key);
            Assert.AreEqual(0, failures.Count,
                "spec features the engine no longer accepts: " + string.Join(", ", failures));
        }

        [TestMethod]
        public void Cpp23_CoreLanguage_Parses()
        {
            AllClean(new Dictionary<string, string>
            {
                { "if/switch init-statement", "void f() { if (int n = g(); n > 0) { } switch (int k = h(); k) { case 1: break; } }\n" },
                { "range-for with init", "void f() { for (int i = 0; auto x : v) { } }\n" },
                { "structured bindings", "void f() { auto [a, b] = p; for (auto& [k, val] : m) { } }\n" },
                { "designated initializers", "struct S { int x, y; }; void f() { S s = {.x = 1, .y = 2}; }\n" },
                { "fold expressions", "template<class... Ts> auto s(Ts... x) { return (x + ...); }\ntemplate<class... Ts> auto r(Ts... x) { return (... + x); }\ntemplate<class... Ts> void c(Ts... x) { (f(x), ...); }\n" },
                { "sizeof... pack", "template<class... Ts> int n() { return sizeof...(Ts); }\n" },
                { "concepts + requires", "template<class T> concept A = requires(T a) { a + a; }; template<A T> void f(T); template<class T> requires A<T> void g(T);\n" },
                { "requires-expression value", "template<class T> constexpr bool ok = requires { typename T::type; };\n" },
                { "three-way comparison", "struct S { auto operator<=>(const S&) const = default; }; void f(S a, S b) { auto c = a <=> b; }\n" },
                { "coroutines", "task f() { co_await g(); co_yield 1; co_return 2; }\n" },
                { "namespace alias", "namespace a { namespace b { int x; } } namespace ab = a::b;\n" },
                { "template disambiguators", "template<class T> void f(T t) { t.template get<int>(); T::template rebind<int> y; }\n" },
                { "out-of-line member template", "template<class T> struct A { void g(); };\ntemplate<class T> void A<T>::g() {}\n" },
                { "hex float + binary literals", "double a = 0x1p3; double b = 0x1.8p-4; int c = 0b1010; int d = 1'000'000;\n" },
                { "deducing this (C++23)", "struct S { int f(this S& self) { return 1; } template<class Self> auto g(this Self&& self) { return 2; } };\n" },
                { "multidimensional subscript (C++23)", "struct M { int operator[](int a, int b) { return a + b; } }; void f(M m) { auto v = m[1, 2]; }\n" },
                { "static operator() / [] (C++23)", "struct F { static int operator()(int x) { return x; } }; struct G { static int operator[](int x) { return x; } };\n" },
                { "auto(x) decay-copy (C++23)", "void f(auto v) { auto c = auto(v); auto d = auto{v}; }\n" },
                { "if consteval (C++23)", "constexpr int f() { if consteval { return 1; } else { return 2; } }\n" },
                { "size_t literal uz (C++23)", "auto a = 1uz; auto b = 3UZ;\n" },
                { "label at end of block (C++23)", "void f() { { goto done; done: } }\n" },
                { "lambdas", "void f() { auto a = [](int x) { return x; }; auto b = []<class T>(T t) { return t; }; auto c = [x = 1]() { return x; }; }\n" },
                { "explicit(bool) + constinit", "struct S { explicit(true) S(int); }; constinit int v = 1;\n" },
            });
        }

        [TestMethod]
        public void Cuda_LanguageExtensions_Parse()
        {
            AllClean(new Dictionary<string, string>
            {
                { "execution/space qualifiers", "__global__ void k() {} __device__ int d() { return 1; } __host__ __device__ int hd() { return 3; }\n" },
                { "launch bounds + grid constant", "__global__ void __launch_bounds__(256, 4) k() {} __global__ void k2(__grid_constant__ const int p) {}\n" },
                { "cluster dims (CC 9.0)", "__global__ void __cluster_dims__(2, 1, 1) k() {}\n" },
                { "namespace alias in body", "void f() { namespace cg = cooperative_groups; }\n" },
                { "launch configurations", "__global__ void k(int); void h() { k<<<1, 1>>>(0); k<<<g, b, 1024, s>>>(0); }\n" },
                { "warp intrinsics", "__global__ void k(int v) { unsigned m = __activemask(); int s = __shfl_down_sync(m, v, 1); unsigned b = __ballot_sync(m, v); }\n" },
                { "PTX inline assembly", "__device__ int f(int x) { int y; asm volatile(\"mov.s32 %0, %1;\" : \"=r\"(y) : \"r\"(x)); return y; }\n" },
                { "device lambda", "__global__ void k() { auto f = [] __device__ (int x) { return x + 1; }; }\n" },
                { "wmma fragments", "__global__ void k(float* c) { wmma::fragment<wmma::accumulator, 16, 16, 16, float> acc; wmma::fill_fragment(acc, 0.0f); }\n" },
            });
        }

        [TestMethod]
        public void MissingSemicolonAfterDeclaration_IsAnError()
        {
            // nvcc rejects this; the static compiler used to miss it (see NvccVerifierTests).
            Assert.IsTrue(Errors("__global__ void k(int* a) {\n    int i = threadIdx.x\n    a[i] = 1;\n}\n") > 0,
                "a declaration without its ';' must error");
            Assert.AreEqual(0, Errors("__global__ void k(int* a) {\n    int i = threadIdx.x;\n    a[i] = 1;\n}\n"),
                "the well-formed twin must stay clean");
        }

        [TestMethod]
        public void SpacedLaunchSyntax_AsVisualStudioFormatsIt_Parses()
        {
            // VS reformats `k<<<g, b>>>(x)` into `k << <g, b >> > (x)`. nvcc accepts it, and so
            // does most real-world Windows CUDA — cuda_lab/all_kernels.cu is written this way.
            Assert.AreEqual(0, Errors(
                "__global__ void k(int* a, int n);\n" +
                "void h(int* a, int n) {\n" +
                "    dim3 gridSize(1), blockSize(256);\n" +
                "    k << <gridSize, blockSize >> > (a, n);\n" +
                "}\n"), "the spaced launch syntax must parse");

            // The compact spelling must keep working, and a genuine shift must stay a shift.
            Assert.AreEqual(0, Errors(
                "__global__ void k(int* a, int n);\n" +
                "void h(int* a, int n) { k<<<1, 256>>>(a, n); int s = n >> 2; (void)s; }\n"));
        }
    }
}
