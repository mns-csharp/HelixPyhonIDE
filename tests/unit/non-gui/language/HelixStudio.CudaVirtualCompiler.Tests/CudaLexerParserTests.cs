namespace HelixPythonNamespace.CudaVirtualCompiler.Tests
{
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Lexing;
    using HelixPythonNamespace.CudaVirtualCompiler.Parsing;

    [TestClass]
    public class CudaLexerParserTests
    {
        private static List<CudaToken> Lex(string src)
        {
            CudaLexer lexer = new CudaLexer(src, "test.cu");
            return lexer.Tokenize();
        }

        private static List<CudaTokenKind> Kinds(List<CudaToken> toks)
        {
            List<CudaTokenKind> k = new List<CudaTokenKind>();
            foreach (CudaToken t in toks) k.Add(t.Kind);
            return k;
        }

        [TestMethod]
        public void LexerEmitsKernelLaunchChevrons()
        {
            List<CudaTokenKind> k = Kinds(Lex("k<<<1, 2>>>(x);"));
            CollectionAssert.Contains(k, CudaTokenKind.TripleLess);
            CollectionAssert.Contains(k, CudaTokenKind.TripleGreater);
        }

        [TestMethod]
        public void LexerClassifiesCudaQualifierKeywords()
        {
            List<CudaToken> toks = Lex("__global__ __device__ __shared__");
            Assert.AreEqual(CudaTokenKind.KwGlobal, toks[0].Kind);
            Assert.AreEqual(CudaTokenKind.KwDevice, toks[1].Kind);
            Assert.AreEqual(CudaTokenKind.KwShared, toks[2].Kind);
        }

        [TestMethod]
        public void LexerEmitsCommentsAndPreprocessorAsTrivia()
        {
            List<CudaToken> toks = Lex("#define N 256\n// hi\nint x;");
            Assert.AreEqual(CudaTokenKind.PreprocessorDirective, toks[0].Kind);
            Assert.AreEqual(CudaTokenKind.CommentLine, toks[1].Kind);
            Assert.IsTrue(toks[0].IsTrivia);
            Assert.IsTrue(toks[1].IsTrivia);
        }

        [TestMethod]
        public void LexerExpandsObjectLikeMacroToLiteral()
        {
            // `#define N 5` used at `<<<1, N>>>` must expand N -> IntLiteral 5 so the
            // launch analyzer AND the emulator see a concrete bound, not a bare
            // identifier reference. Regression for cuda_lab/pyramid.cu, which faulted
            // "unsupported construct: reference to 'N'" before macro expansion.
            Dictionary<string, string> defines = new Dictionary<string, string> { { "N", "5" } };
            CudaLexer lexer = new CudaLexer("k<<<1, N>>>();", "test.cu", defines);
            List<CudaToken> toks = lexer.Tokenize();
            bool sawBareN = false;
            bool sawFive = false;
            foreach (CudaToken t in toks)
            {
                if (t.Kind == CudaTokenKind.Identifier && t.Text == "N") sawBareN = true;
                if (t.Kind == CudaTokenKind.IntLiteral && t.Text == "5") sawFive = true;
            }
            Assert.IsFalse(sawBareN, "macro N should be expanded, not left as an identifier");
            Assert.IsTrue(sawFive, "macro N should expand to IntLiteral 5");
        }

        [TestMethod]
        public void LexerLeavesIdentifierUnexpandedWithoutDefine()
        {
            // No define table => genuine identifiers (variables) must survive intact.
            List<CudaToken> toks = Lex("k<<<1, N>>>();");
            bool sawN = false;
            foreach (CudaToken t in toks)
                if (t.Kind == CudaTokenKind.Identifier && t.Text == "N") sawN = true;
            Assert.IsTrue(sawN, "without a define, N must remain an identifier");
        }

        [TestMethod]
        public void LexerDoesNotExpandKeywordEvenIfDefined()
        {
            // A macro must never rewrite a keyword token (keywords are not identifiers).
            Dictionary<string, string> defines = new Dictionary<string, string> { { "int", "float" } };
            List<CudaToken> toks = new CudaLexer("int x;", "test.cu", defines).Tokenize();
            Assert.AreEqual(CudaTokenKind.KwInt, toks[0].Kind);
        }

        [TestMethod]
        public void LexerReportsUnterminatedString()
        {
            CudaLexer lexer = new CudaLexer("const char* s = \"oops", "test.cu");
            lexer.Tokenize();
            Assert.IsTrue(lexer.Diagnostics.Count >= 1);
            Assert.AreEqual(CudaDiagnosticCodes.LexUnterminatedString, lexer.Diagnostics[0].Code);
        }

        [TestMethod]
        public void ParserParsesKernelAsGlobalFunction()
        {
            CudaParser parser = new CudaParser(Lex("__global__ void mykernel(float* p, int n) { }"), "test.cu");
            TranslationUnit unit = parser.Parse();
            Assert.AreEqual(1, unit.Decls.Count);
            FunctionDecl fn = unit.Decls[0] as FunctionDecl;
            Assert.IsNotNull(fn);
            Assert.AreEqual("mykernel", fn.Name);
            Assert.IsTrue(fn.IsKernel);
            Assert.AreEqual(FuncSpace.Global, fn.Space);
            Assert.AreEqual(2, fn.Params.Count);
            Assert.IsTrue(fn.Params[0].Type.IsPointer());
        }

        [TestMethod]
        public void ParserParsesKernelLaunchExpression()
        {
            CudaParser parser = new CudaParser(Lex("void run() { foo<<<8, 256>>>(a, b); }"), "test.cu");
            TranslationUnit unit = parser.Parse();
            FunctionDecl fn = unit.Decls[0] as FunctionDecl;
            Assert.IsNotNull(fn);
            KernelLaunchExpr launch = FindLaunch(fn.Body);
            Assert.IsNotNull(launch);
            Assert.AreEqual(2, launch.Args.Count);
            NameExpr callee = launch.Callee as NameExpr;
            Assert.IsNotNull(callee);
            Assert.AreEqual("foo", callee.Id);
        }

        private static KernelLaunchExpr FindLaunch(Stmt s)
        {
            KernelLaunchExpr[] found = { null };
            HelixPythonNamespace.CudaVirtualCompiler.Analyze.AstWalk.ForEachExpr(s, delegate (Expr e)
            {
                KernelLaunchExpr k = e as KernelLaunchExpr;
                if (k != null) found[0] = k;
            });
            return found[0];
        }
    }
}
