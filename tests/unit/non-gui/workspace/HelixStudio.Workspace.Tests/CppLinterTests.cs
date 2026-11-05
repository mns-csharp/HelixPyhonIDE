using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Cpp;

namespace HelixPythonNamespace.Workspace.Tests
{
    /// <summary>
    /// FR-LINT (C++): C++ was the one shipped language with no linter of its own. These pin both
    /// directions — the rules fire on the violations they name (rejection), and the constructs that
    /// merely LOOK like violations stay clean (acceptance). The acceptance half matters most here:
    /// the first version of the C-style-cast rule read `if (ok) return;` as "a cast to type ok" and
    /// produced 406 false positives over the 274 C++/CUDA fixture files.
    /// </summary>
    [TestClass]
    public class CppLinterTests
    {
        private static string[] Codes(string src, string path = "x.cpp")
        {
            return CppLinter.Lint(src, path).Select(f => f.Code).ToArray();
        }

        [TestMethod]
        public void CPL001_HeaderWithoutAGuardIsFlagged_AndOneWithEitherFormIsNot()
        {
            CollectionAssert.Contains(Codes("int f();\n", "a.hpp"), "CPL001");
            CollectionAssert.DoesNotContain(Codes("#pragma once\nint f();\n", "a.hpp"), "CPL001");
            CollectionAssert.DoesNotContain(Codes("#ifndef A_H\n#define A_H\nint f();\n#endif\n", "a.hpp"), "CPL001");
            // A .cpp is not included by anyone — the rule must not fire there.
            CollectionAssert.DoesNotContain(Codes("int f() { return 0; }\n", "a.cpp"), "CPL001");
        }

        [TestMethod]
        public void CPL002_UsingNamespaceInAHeaderIsFlagged_ButNotInsideAFunctionOrInACpp()
        {
            CollectionAssert.Contains(Codes("#pragma once\nusing namespace std;\n", "a.hpp"), "CPL002");
            CollectionAssert.DoesNotContain(
                Codes("#pragma once\nvoid f() { using namespace std; }\n", "a.hpp"), "CPL002");
            CollectionAssert.DoesNotContain(Codes("using namespace std;\n", "a.cpp"), "CPL002");
        }

        [TestMethod]
        public void CPL003_FlagsRealCastsOnly()
        {
            CollectionAssert.Contains(Codes("void f(void* p) { char* s = (char*)p; }"), "CPL003");
            CollectionAssert.Contains(Codes("int f(double d) { return (int)d; }"), "CPL003");
            CollectionAssert.Contains(Codes("void f(double d) { auto x = (unsigned int)d; }"), "CPL003");
        }

        /// <summary>The exact shapes that produced the 406-false-positive sweep.</summary>
        [TestMethod]
        public void CPL003_DoesNotFireOnParenthesisedExpressionsOrControlFlow()
        {
            foreach (string src in new[]
            {
                "void f(bool ok) { if (ok) return; }",
                "void f(bool ok) { while (ok) { break; } }",
                "int f(int n) { int m = (n); return m; }",
                "int f(int a, int b) { return (a + b) * 2; }",
                "void f(int n) { switch (n) { default: break; } }",
                "int f() { return sizeof(int); }",
                "void g(); void f() { g(); }",
            })
                CollectionAssert.DoesNotContain(Codes(src), "CPL003", src);
        }

        /// <summary>`(void)x;` is the standard unused-parameter discard, not a cast to rewrite.</summary>
        [TestMethod]
        public void CPL003_LeavesTheVoidDiscardIdiomAlone()
        {
            CollectionAssert.DoesNotContain(Codes("void f(int unused) { (void)unused; }"), "CPL003");
        }

        [TestMethod]
        public void CPL004_FlagsNullMacro_ButNotAnIdentifierContainingNull()
        {
            CollectionAssert.Contains(Codes("void* p = NULL;"), "CPL004");
            CollectionAssert.DoesNotContain(Codes("int NULLABLE_COUNT = 1;"), "CPL004");
            CollectionAssert.DoesNotContain(Codes("void* p = nullptr;"), "CPL004");
        }

        [TestMethod]
        public void CPL005_FlagsMutableGlobals_ButNotConstOrExternOrLocals()
        {
            CollectionAssert.Contains(Codes("int g_counter = 0;"), "CPL005");
            CollectionAssert.DoesNotContain(Codes("const int kFine = 7;"), "CPL005");
            CollectionAssert.DoesNotContain(Codes("constexpr int kAlsoFine = 7;"), "CPL005");
            CollectionAssert.DoesNotContain(Codes("extern int g_elsewhere;"), "CPL005");
            CollectionAssert.DoesNotContain(Codes("void f() { int local = 0; }"), "CPL005");
        }

        /// <summary>
        /// Comments and string literals are blanked before any rule runs, so a rule can never fire
        /// on text that is not code.
        /// </summary>
        [TestMethod]
        public void RulesNeverFireInsideCommentsOrStringLiterals()
        {
            string src =
                "// NULL and (char*)p in a comment\n" +
                "/* using namespace std; and NULL in a block comment */\n" +
                "const char* s = \"NULL (int)x using namespace std;\";\n";
            string[] codes = Codes(src);
            CollectionAssert.DoesNotContain(codes, "CPL003");
            CollectionAssert.DoesNotContain(codes, "CPL004");
            CollectionAssert.DoesNotContain(codes, "CPL002");
        }

        [TestMethod]
        public void IsCppFile_RecognisesTheSourceAndHeaderExtensions()
        {
            foreach (string p in new[] { "a.cpp", "a.cc", "a.cxx", "a.h", "a.hh", "a.hpp", "a.hxx" })
                Assert.IsTrue(CppLinter.IsCppFile(p), p);
            foreach (string p in new[] { "a.cu", "a.cuh", "a.py", "a.rs", "" })
                Assert.IsFalse(CppLinter.IsCppFile(p), p);
        }
    }
}
