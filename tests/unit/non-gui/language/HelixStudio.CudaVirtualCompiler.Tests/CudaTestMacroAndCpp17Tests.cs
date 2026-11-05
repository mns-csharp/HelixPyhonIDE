using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.CudaVirtualCompiler;
using HelixPythonNamespace.CudaVirtualCompiler.Ast;
using Cvc = HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Regression pack for "C++ solution opened, lots of red dots": the edit-time
    /// diagnostics ran this virtual compiler over Catch2/GTest test files and modern
    /// C++17 library headers and produced dozens of FALSE Error diagnostics — every one
    /// a red gutter dot + squiggle on perfectly valid code. Each test here captures one
    /// construct from the multi-package-workspace fixture that used to misparse.
    /// </summary>
    [TestClass]
    public class CudaTestMacroAndCpp17Tests
    {
        private static CudaCompileResult Compile(string source)
        {
            return Cvc.CompileSource(source, "k.cu");
        }

        private static int Errors(CudaCompileResult r)
        {
            return r.Diagnostics.Count(d => d.Severity == global::HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error);
        }

        [TestMethod]
        public void TestCaseMacros_ParseAsFunctions_LocalsGetTheirOwnScope()
        {
            // The killer bug: same-named locals in SIBLING tests collided into bogus
            // "Redefinition" errors because the macro shape failed to parse as a function
            // and recovery spilled body locals to file scope.
            var r = Compile(
                "TEST_CASE(\"total_area empty list is zero\", \"[geometry]\") { REQUIRE(total_area({}) == 0.0); }\n" +
                "TEST_CASE(\"total_area sum of rectangles\", \"[geometry]\") {\n" +
                "    Rectangle r1(2, 3);\n" +
                "    Rectangle r2(4, 5);\n" +
                "    std::vector<const IShape*> shapes{&r1, &r2};\n" +
                "    REQUIRE(total_area(shapes) == Approx(2 * 3 + 4 * 5));\n" +
                "}\n" +
                "TEST_CASE(\"total_area mixed shapes\", \"[geometry]\") {\n" +
                "    Circle c(1);\n" +
                "    std::vector<const IShape*> shapes{&c};\n" +
                "    REQUIRE(total_area(shapes) == Approx(3.14));\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r), "sibling test locals must never collide");
            var fns = r.Unit.Decls.OfType<FunctionDecl>().ToList();
            Assert.AreEqual(3, fns.Count, "each TEST_CASE parses as a function");
            Assert.AreEqual("total_area empty list is zero", fns[0].Name);
            Assert.IsTrue(fns[1].HasBody);
        }

        [TestMethod]
        public void GtestMacros_ParseAsFunctions_KeyedSuiteDotName()
        {
            var r = Compile("TEST(MathSuite, Addition) {\n    int x = 1 + 1;\n}\n");
            Assert.AreEqual(0, Errors(r));
            var fn = r.Unit.Decls.OfType<FunctionDecl>().FirstOrDefault();
            Assert.IsNotNull(fn);
            Assert.AreEqual("MathSuite.Addition", fn.Name);
        }

        [TestMethod]
        public void Generate_BracedTemporariesAsCallArguments_ParseClean()
        {
            var r = Compile(
                "TEST_CASE(\"Rectangle area\", \"[shapes]\") {\n" +
                "    struct Case {\n" +
                "        double w, h, expected;\n" +
                "    };\n" +
                "    auto c = GENERATE(Case{2.0, 3.0, 6.0}, Case{5.0, 5.0, 25.0});\n" +
                "    REQUIRE(Rectangle(c.w, c.h).area() == Approx(c.expected));\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r), "Case{...} as a call argument and a local struct must parse");
        }

        [TestMethod]
        public void NestedNamespaceDefinition_Cpp17_ParsesClean()
        {
            var r = Compile(
                "namespace lib_core::animals {\n" +
                "int speak() { return 1; }\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r), "namespace a::b { } must not fail at its first line");
        }

        [TestMethod]
        public void IfConditionDeclaration_WithDynamicCast_ParsesClean()
        {
            var r = Compile(
                "BoundingBox bounding_box(const IShape& shape) {\n" +
                "    if (const auto* c = dynamic_cast<const Circle*>(&shape)) {\n" +
                "        return BoundingBox{-c->radius, -c->radius, c->radius, c->radius};\n" +
                "    }\n" +
                "    return BoundingBox{0, 0, 0, 0};\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r));
        }

        [TestMethod]
        public void PlainBooleanIfCondition_IsNotMistakenForADeclaration()
        {
            // `use_new && q` also matches the `Type&& name` declarator shape — the missing
            // initializer must keep it an expression (a mid-session regression caught by
            // the g1m kernel fixtures).
            var r = Compile(
                "void f(int use_new, int q, int ms, int nm, int* ox) {\n" +
                "    if (use_new && q >= ms && q < ms + nm) {\n" +
                "        *ox = 1;\n" +
                "    }\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r));
        }

        [TestMethod]
        public void StructuredBindingRangeFor_ParsesClean()
        {
            var r = Compile(
                "void f() {\n" +
                "    for (const auto& [k, v] : data_) {\n" +
                "        use(k, v);\n" +
                "    }\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r));
        }

        [TestMethod]
        public void TripleGreater_ClosingNestedTemplateArgs_ParsesClean()
        {
            // '>>>' lexes as the kernel-launch token; the angle matchers must still close
            // three template levels with it.
            var r = Compile(
                "void f(std::vector<std::shared_ptr<Step<int>>> steps) {\n" +
                "    int n = 0;\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r));
        }

        [TestMethod]
        public void TemplateIdAsValueArgument_AndConceptCondition_ParseClean()
        {
            var r = Compile(
                "void f() {\n" +
                "    results.emplace_back(std::in_place_index<0>, task());\n" +
                "    if constexpr (std::is_void_v<decltype(fn(args...))>) {\n" +
                "        fn(1);\n" +
                "    }\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r));
        }

        [TestMethod]
        public void ComparisonInsideCallArgs_KeepsComparisonReading()
        {
            // The template-id heuristic must NOT swallow `a < b, c > d` style comparisons.
            var r = Compile(
                "void f(int a, int b, int c, int d) {\n" +
                "    g(a < b, c > d);\n" +
                "    if (a < b || c > d) { g(1, 2); }\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r));
        }

        [TestMethod]
        public void DirectInit_WithQualifiedCallArgument_IsAVariable_NotAPrototype()
        {
            var r = Compile(
                "void f() {\n" +
                "    TransactionalStorage<std::string, int> txn(std::move(storage));\n" +
                "    MiddlewareChain chain(std::move(router));\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r), "most-vexing-parse split: an argument that is a CALL means direct-init");
        }

        [TestMethod]
        public void PointerToMemberParameter_AndBraceDefaultArg_ParseClean()
        {
            var r = Compile(
                "SubscriptionId subscribe_weak(std::weak_ptr<Listener> obj, void (Listener::*method)(const Payload&)) {\n" +
                "    return 0;\n" +
                "}\n" +
                "inline int default_chain(std::unordered_set<std::string> auth_tokens = {\"dev-token\"},\n" +
                "                         double rate = 100.0) {\n" +
                "    return 1;\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r));
        }

        [TestMethod]
        public void GenericVariadicLambda_WithDecltypeTrailingReturn_ParsesClean()
        {
            var r = Compile(
                "auto retry(int times) {\n" +
                "    return [times](auto&&... args) mutable -> decltype(fn(args...)) {\n" +
                "        return fn(std::forward<decltype(args)>(args)...);\n" +
                "    };\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r), "the trailing return's ')' tokens must not eat the lambda body");
        }

        [TestMethod]
        public void UsingNamespaceInsideFunctionBody_ParsesClean()
        {
            var r = Compile(
                "TEST_CASE(\"callhier\") {\n" +
                "    using namespace pythonsimple::callhier_demo;\n" +
                "    REQUIRE(alpha() == 2);\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r));
        }

        [TestMethod]
        public void ClassMembers_CudaQualifiedCtors_MutableFields_MemberTemplates_ParseClean()
        {
            // Each construct here used to derail the record-member parse, losing every
            // member after it (their uses then read as undefined names).
            var r = Compile(
                "struct Point3D {\n" +
                "    double x = 0.0;\n" +
                "    double y = 0.0;\n" +
                "    __host__ __device__ Point3D() {}\n" +
                "    __host__ __device__ Point3D(double x_, double y_) : x(x_), y(y_) {}\n" +
                "    __host__ __device__ bool same(const Point3D& o) const { return x == o.x && y == o.y; }\n" +
                "};\n" +
                "class Holder {\n" +
                "public:\n" +
                "    explicit Holder(int n) : n_(n) {}\n" +
                "    template <typename T> int apply(T t) { return n_ + t; }\n" +
                "    using Alias = int;\n" +
                "    enum class Mode { A, B };\n" +
                "    int get() const { return n_; }\n" +
                "private:\n" +
                "    mutable int n_;\n" +
                "    void (Holder::*hook_)(int) = nullptr;\n" +   // pointer-to-member field
                "};\n");
            Assert.AreEqual(0, Errors(r));
        }

        [TestMethod]
        public void InheritedBaseMembers_AndTemplatedBaseInitializers_ResolveClean()
        {
            var r = Compile(
                "class Rectangle { public: double width = 0; double height = 0; };\n" +
                "class Square : public Rectangle {\n" +
                "public:\n" +
                "    double side() const { return width; }\n" +   // inherited field
                "};\n" +
                "template <typename T> class Step { public: Step(int n) {} };\n" +
                "template <typename T>\n" +
                "class Filter : public Step<T> {\n" +
                "public:\n" +
                "    Filter(int n) : Step<T>(n), pred_(n) {}\n" + // templated base initializer
                "    int run() const { return pred_; }\n" +
                "private:\n" +
                "    int pred_;\n" +
                "};\n");
            Assert.AreEqual(0, Errors(r));
        }

        [TestMethod]
        public void StaticAssert_AndRequiresClause_ParseClean()
        {
            var r = Compile(
                "struct Command { int id; };\n" +
                "struct AddShape { int id; };\n" +
                "static_assert(std::is_base_of_v<Command, AddShape>);\n" +
                "template <typename S>\n" +
                "    requires Shape<S> && std::derived_from<S, Command>\n" +
                "int total(const S& s) { return s.id; }\n");
            Assert.AreEqual(0, Errors(r));
        }

        [TestMethod]
        public void RealSyntaxErrors_AreStillReported()
        {
            // The fixes must not have degraded the parser into accepting anything.
            var r = Compile("void f() {\n    int x = ;\n}\n");
            Assert.IsTrue(Errors(r) > 0, "a genuinely broken initializer still errors");
        }

        // ---- docs/errors.md bizarre-error catalog: each entry must produce an Error ----

        [TestMethod]
        public void ErrorCatalog_BizarreStatements_AllRejected()
        {
            string[] bad =
            {
                "void f() { auto x; }",                                   // auto without initializer
                "void f() { double dd = 1.2.3; }",                        // two decimal points
                "void f(int v, int y) { v = 5 = y; }",                    // assignment into a literal
                "void f() { int arr[-5]; }",                              // negative array size
                "void f() { goto nowhere; }",                             // undefined label
                "__global__ __host__ void both();",                       // invalid qualifier combo
            };
            for (int i = 0; i < bad.Length; i++)
            {
                var r = Compile(bad[i]);
                Assert.IsTrue(Errors(r) > 0, "must reject: " + bad[i]);
            }
        }

        [TestMethod]
        public void ErrorCatalog_LegalNeighbors_StayClean()
        {
            string[] good =
            {
                "void f() { auto x = 1; }",                               // initialized auto
                "void f(int n) { for (const auto& item : items) { use(item); } }", // range-for auto
                "void f() { double dd = 1.25; }",
                "void f(int v, int y) { v = y = 5; }",                    // chained assignment is legal
                "void f() { int arr[5]; arr[0] = 1; }",
                "void f(int n) {\nretry:\n    if (n > 0) { n--; goto retry; }\n}", // defined label
                "__global__ void kernel_ok(int* p) { p[0] = 1; }",
            };
            for (int i = 0; i < good.Length; i++)
            {
                var r = Compile(good[i]);
                Assert.AreEqual(0, Errors(r), "must accept: " + good[i]);
            }
        }

        [TestMethod]
        public void ExpressionStatement_MissingSemicolon_IsAnError()
        {
            // A junk line in a body (`x1x23444x123` with no ';') is a real syntax error
            // nvcc rejects; the parser used to silently tolerate it, so typing garbage
            // produced NO squiggle at all.
            var r = Compile(
                "struct CommandValue { int kind; };\n" +
                "static CommandValue from_shape(int v) {\n" +
                "    CommandValue c;\n" +
                "    x1x23444x123\n" +
                "    return c;\n" +
                "}\n");
            Assert.IsTrue(Errors(r) > 0, "a statement without its ';' must error");
        }

        [TestMethod]
        public void SectionMacroWithBlock_AndGotoLabel_NoFalseSemicolonErrors()
        {
            var r = Compile(
                "TEST_CASE(\"sections\", \"[g]\") {\n" +
                "    SECTION(\"first\") {\n" +
                "        int a = 1;\n" +
                "    }\n" +
                "    SECTION(\"second\") {\n" +
                "        int a = 2;\n" +
                "    }\n" +
                "}\n" +
                "void retry_demo(int n) {\n" +
                "retry:\n" +
                "    if (n > 0) { n--; goto retry; }\n" +
                "}\n");
            Assert.AreEqual(0, Errors(r),
                "SECTION-with-block and goto labels are not missing-semicolon errors");
        }
    }
}
