using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixTextEditorNET;
using HelixTextEditorNET.Core;
using HelixTextEditorNET.Lexing;

namespace HelixTextEditorNET.Tests
{
    [TestClass]
    public class PythonLexerEngineTests
    {
        private static DocumentModel Styled(string code, string keywords = "def class if return import from for while")
        {
            var doc = new DocumentModel();
            doc.SetText(code);
            LexerEngine lx = LexerEngine.Create(2);  // SCLEX_PYTHON
            lx.SetKeywords(0, keywords);
            lx.StyleDocument(doc);
            return doc;
        }

        [TestMethod]
        public void Keywords_Identifiers_Comments_Numbers()
        {
            var doc = Styled("def foo(): # hi\n    return 42\n");
            Assert.AreEqual(Style.Python.Word, doc.StyleAt(0));          // 'd' of def
            Assert.AreEqual(Style.Python.DefName, doc.StyleAt(4));       // 'f' of foo
            Assert.AreEqual(Style.Python.CommentLine, doc.StyleAt(11));  // '#'
            Assert.AreEqual(Style.Python.Word, doc.StyleAt(20));         // 'r' of return
            Assert.AreEqual(Style.Python.Number, doc.StyleAt(27));       // '4'
        }

        [TestMethod]
        public void Strings_SingleDoubleAndTriple()
        {
            var doc = Styled("a = 'x'\nb = \"y\"\nc = '''long\nstring'''\n");
            Assert.AreEqual(Style.Python.Character, doc.StyleAt(4));     // 'x' single-quoted
            Assert.AreEqual(Style.Python.String, doc.StyleAt(12));       // "y" double-quoted
            int tripleStart = "a = 'x'\nb = \"y\"\nc = ".Length;
            Assert.AreEqual(Style.Python.Triple, doc.StyleAt(tripleStart));
            // The style persists across the newline inside the triple.
            Assert.AreEqual(Style.Python.Triple, doc.StyleAt(tripleStart + 10));
        }

        [TestMethod]
        public void EscapedQuote_BeforeTripleClose_DoesNotTerminateEarly()
        {
            // Regression parity with the C++ lexer fix: '\'' just before ''' close.
            string code = "s = '''ab\\'''' + x";
            var doc = Styled(code);
            // The escaped quote is inside the string; the close is the ''' after it.
            int closeEnd = code.IndexOf("' + x");
            Assert.AreEqual(Style.Python.Triple, doc.StyleAt(9));  // escaped quote region
            Assert.AreEqual(Style.Python.Operator, doc.StyleAt(code.IndexOf('+')));
        }

        [TestMethod]
        public void ClassName_And_Decorator()
        {
            var doc = Styled("@property\nclass Cat(Animal):\n    pass\n");
            Assert.AreEqual(Style.Python.Decorator, doc.StyleAt(0));
            int classPos = "@property\n".Length;
            Assert.AreEqual(Style.Python.Word, doc.StyleAt(classPos));
            Assert.AreEqual(Style.Python.ClassName, doc.StyleAt(classPos + 6));  // 'C' of Cat
        }

        [TestMethod]
        public void UnterminatedString_IsStringEol()
        {
            var doc = Styled("a = 'oops\nb = 1\n");
            Assert.AreEqual(Style.Python.StringEol, doc.StyleAt(4));
            // The next line lexes normally.
            Assert.AreEqual(Style.Python.Number, doc.StyleAt(14));
        }
    }

    [TestClass]
    public class CppLexerEngineTests
    {
        private static DocumentModel Styled(string code)
        {
            var doc = new DocumentModel();
            doc.SetText(code);
            LexerEngine lx = LexerEngine.Create(3);  // SCLEX_CPP
            lx.SetKeywords(0, "int return void if else for while");
            lx.SetKeywords(1, "size_t uint32_t");
            lx.StyleDocument(doc);
            return doc;
        }

        [TestMethod]
        public void Comments_Preprocessor_Strings_Keywords()
        {
            string code = "#include <a.h>\n// line\n/* block */\nint x = \"s\";\n";
            var doc = Styled(code);
            Assert.AreEqual(Style.Cpp.Preprocessor, doc.StyleAt(0));
            Assert.AreEqual(Style.Cpp.CommentLine, doc.StyleAt(code.IndexOf("// line")));
            Assert.AreEqual(Style.Cpp.Comment, doc.StyleAt(code.IndexOf("/* block")));
            Assert.AreEqual(Style.Cpp.Word, doc.StyleAt(code.IndexOf("int x")));
            Assert.AreEqual(Style.Cpp.String, doc.StyleAt(code.IndexOf("\"s\"")));
        }

        [TestMethod]
        public void Word2_ForTypeKeywordSet()
        {
            string code = "size_t n = 0;";
            var doc = Styled(code);
            Assert.AreEqual(Style.Cpp.Word2, doc.StyleAt(0));
        }

        [TestMethod]
        public void BraceFold_MarksHeadersAndLevels()
        {
            var doc = new DocumentModel();
            doc.SetText("void f() {\n  int a;\n}\nint b;\n");
            LexerEngine lx = LexerEngine.Create(3);
            lx.StyleDocument(doc);
            lx.FoldDocument(doc);
            Assert.IsTrue((doc.GetLevel(0) & Folding.FoldLevelHeaderFlag) != 0, "'{' line is a header");
            Assert.AreEqual(Folding.FoldLevelBase + 1,
                            doc.GetLevel(1) & Folding.FoldLevelNumberMask, "body is one level deeper");
            Assert.AreEqual(Folding.FoldLevelBase,
                            doc.GetLevel(3) & Folding.FoldLevelNumberMask, "after '}' back to base");
        }
    }

    [TestClass]
    public class RawLexerIdTests
    {
        [TestMethod]
        public void RawIds_ResolveToRealEngines()
        {
            Assert.IsInstanceOfType(LexerEngine.Create(111), typeof(NullLexerEngine), "SCLEX_RUST has no engine in this product");
            Assert.IsInstanceOfType(LexerEngine.Create(9), typeof(PropsLexerEngine));
            Assert.IsInstanceOfType(LexerEngine.Create(80), typeof(CMakeLexerEngine));
            Assert.IsInstanceOfType(LexerEngine.Create(2), typeof(PythonLexerEngine));
            Assert.IsInstanceOfType(LexerEngine.Create(3), typeof(CppLexerEngine));
            Assert.IsInstanceOfType(LexerEngine.Create(999), typeof(NullLexerEngine));
        }

        [TestMethod]
        public void Props_StylesMatchHardcodedConsumerNumbers()
        {
            var doc = new DocumentModel();
            doc.SetText("# c\n[sec]\nkey=value\n");
            LexerEngine lx = LexerEngine.Create(9);
            lx.StyleDocument(doc);
            Assert.AreEqual(1, doc.StyleAt(0));   // SCE_PROPS_COMMENT
            Assert.AreEqual(2, doc.StyleAt(4));   // SCE_PROPS_SECTION
            Assert.AreEqual(5, doc.StyleAt(10));  // SCE_PROPS_KEY
            Assert.AreEqual(3, doc.StyleAt(13));  // SCE_PROPS_ASSIGNMENT
        }

        [TestMethod]
        public void CMake_StylesMatchHardcodedConsumerNumbers()
        {
            var doc = new DocumentModel();
            doc.SetText("# c\nadd_executable(app main.cpp)\nset(V ${X})\n");
            LexerEngine lx = LexerEngine.Create(80);
            lx.StyleDocument(doc);
            Assert.AreEqual(1, doc.StyleAt(0));                                  // COMMENT
            Assert.AreEqual(5, doc.StyleAt(4));                                  // COMMANDS
            int varPos = "# c\nadd_executable(app main.cpp)\nset(V ".Length;
            Assert.AreEqual(7, doc.StyleAt(varPos));                             // VARIABLE ${X}
        }
    }

    [TestClass]
    public class IndentFoldTests
    {
        [TestMethod]
        public void PythonIndentFold_HeaderAndBlankHandling()
        {
            var doc = new DocumentModel();
            doc.SetText("def f():\n    a = 1\n\n    b = 2\ntop = 3\n");
            Folding.FoldByIndent(doc);
            Assert.IsTrue((doc.GetLevel(0) & Folding.FoldLevelHeaderFlag) != 0, "def line is a header");
            Assert.IsTrue((doc.GetLevel(2) & Folding.FoldLevelWhiteFlag) != 0, "blank line is white");
            Assert.AreEqual(Folding.FoldLevelBase + 4,
                            doc.GetLevel(1) & Folding.FoldLevelNumberMask);
            Assert.AreEqual(Folding.FoldLevelBase,
                            doc.GetLevel(4) & Folding.FoldLevelNumberMask);
        }
    }
}
