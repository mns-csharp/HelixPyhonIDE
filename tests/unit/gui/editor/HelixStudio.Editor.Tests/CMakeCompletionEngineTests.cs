using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor.Completion;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CMakeCompletionEngineTests
    {
        private static System.Collections.Generic.List<CompletionItem> Complete(string src, int caret)
        {
            var e = new CMakeCompletionEngine();
            return e.GetCompletions(src, caret, "CMakeLists.txt", null);
        }

        [TestMethod]
        public void OffersCommands_WithSignatures()
        {
            string src = "add_";
            var items = Complete(src, src.Length);
            var addExe = items.FirstOrDefault(i => i.Label == "add_executable");
            Assert.IsNotNull(addExe, "add_executable should be offered");
            Assert.AreEqual(CompletionKind.Function, addExe.Kind);
            StringAssert.Contains(addExe.Detail, "add_executable(", "one-line signature present");
        }

        [TestMethod]
        public void OffersStandardVariables()
        {
            var items = Complete("set(x ${CMAKE_", 14);
            Assert.IsTrue(items.Any(i => i.Label == "CMAKE_SOURCE_DIR"));
            Assert.IsTrue(items.Any(i => i.Label == "CMAKE_CUDA_ARCHITECTURES"));
        }

        [TestMethod]
        public void InsideTargetCommand_OffersVisibilityKeywords()
        {
            // Caret inside target_link_libraries(app <here>
            string src = "target_link_libraries(app ";
            var items = Complete(src, src.Length);
            Assert.IsTrue(items.Any(i => i.Label == "PUBLIC"), "PUBLIC offered inside target_link_libraries");
            Assert.IsTrue(items.Any(i => i.Label == "PRIVATE"));
            Assert.IsTrue(items.Any(i => i.Label == "INTERFACE"));
        }

        [TestMethod]
        public void Commands_CarryCatalogDocumentation()
        {
            var items = Complete("add_", 4);
            var tll = items.FirstOrDefault(i => i.Label == "target_link_libraries");
            Assert.IsNotNull(tll);
            Assert.IsFalse(string.IsNullOrEmpty(tll.Documentation), "target_link_libraries has catalog doc");
            StringAssert.Contains(tll.Documentation, "PUBLIC", "explains the visibility scopes");
        }

        [TestMethod]
        public void OffersProjectDefinedTarget_InsideTargetCommand()
        {
            // add_executable(app ...) then target_include_directories(<here>) should offer 'app'.
            string src = "add_executable(app main.cu)\ntarget_include_directories(";
            var items = Complete(src, src.Length);
            var appTarget = items.FirstOrDefault(i => i.Label == "app");
            Assert.IsNotNull(appTarget, "project target 'app' offered inside a target_* command");
            StringAssert.Contains(appTarget.Detail, "target", "labeled as a project target");
        }

        [TestMethod]
        public void OffersProjectDefinedVariable()
        {
            // set(SRC ...) makes SRC completable.
            string src = "set(SRC main.cu util.cu)\nadd_executable(app ";
            var items = Complete(src, src.Length);
            Assert.IsTrue(items.Any(i => i.Label == "SRC" && i.Detail != null && i.Detail.Contains("this project")),
                "project variable 'SRC' offered");
        }

        [TestMethod]
        public void FileApi_MultiLineTarget_OffersTargetWithKindDetail()
        {
            // FR-PM-05: a target whose source list spans multiple lines is offered (the old line-regex
            // was brittle here), and its detail carries the parsed target Kind.
            string src =
                "add_library(mathkernels STATIC\n" +
                "    ops.cu\n" +
                "    util.cu)\n" +
                "target_link_libraries(app PRIVATE ";
            var items = Complete(src, src.Length);
            var lib = items.FirstOrDefault(i => i.Label == "mathkernels");
            Assert.IsNotNull(lib, "multi-line library target offered inside target_link_libraries");
            StringAssert.Contains(lib.Detail, "StaticLibrary", "detail carries the parsed target kind");
        }

        [TestMethod]
        public void FileApi_Variable_DetailShowsValuePreview()
        {
            // FR-PM-05: variable completion carries a preview of the set() value.
            string src = "set(SRC main.cu kernel.cu)\nadd_executable(app ${";
            var items = Complete(src, src.Length);
            var v = items.FirstOrDefault(i => i.Label == "SRC");
            Assert.IsNotNull(v);
            StringAssert.Contains(v.Detail, "main.cu", "variable detail previews its value");
        }

        [TestMethod]
        public void Catalog_DocumentsAddExecutable()
        {
            string doc = CMakeCatalog.GetDoc("add_executable");
            Assert.IsNotNull(doc);
            StringAssert.Contains(doc, "executable target");
        }

        [TestMethod]
        public void OutsideTargetCommand_NoVisibilityKeywords()
        {
            // At top level, PUBLIC/PRIVATE/INTERFACE should not lead the list.
            string src = "add_executable(app main.cu)\n";
            var items = Complete(src, src.Length);
            Assert.IsFalse(items.Any(i => i.Label == "PUBLIC"),
                "visibility keywords only inside a target_* call");
        }
    }
}
