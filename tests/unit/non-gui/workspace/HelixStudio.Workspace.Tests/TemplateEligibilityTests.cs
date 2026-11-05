using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Templates;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// "Add New Project" eligibility: per-solution-language template sets, every
    /// referenced id resolvable in the built-in catalog, and the new native
    /// (C++/CUDA library+app) templates actually generate.
    /// </summary>
    [TestClass]
    public class TemplateEligibilityTests
    {
        [TestMethod]
        public void PythonSolution_OffersPythonKindsTestAndCudaLibrary_InOrder()
        {
            CollectionAssert.AreEqual(
                new[] { "py.console", "py.notebook", "py.classlib", "py.test", "cuda.classlib", "py.flask", "py.gui.tk" },
                TemplateEligibility.EligibleTemplateIds("Python").ToArray());
        }

        [TestMethod]
        public void CudaSolution_OffersNativeStaticDllTestAndPythonApp_InOrder()
        {
            CollectionAssert.AreEqual(
                new[] { "cuda.console", "cuda.classlib", "cuda.dll", "cpp.console", "cpp.classlib", "cpp.dll", "cpp.test", "py.console" },
                TemplateEligibility.EligibleTemplateIds("CUDA").ToArray());
        }

        [TestMethod]
        public void CppSolution_MirrorsCuda_WithCppFirst()
        {
            CollectionAssert.AreEqual(
                new[] { "cpp.console", "cpp.classlib", "cpp.dll", "cpp.test", "cuda.console", "cuda.classlib", "cuda.dll", "py.console" },
                TemplateEligibility.EligibleTemplateIds("C++").ToArray());
        }

        [TestMethod]
        public void UnknownLanguage_MeansNoRestriction()
        {
            Assert.IsNull(TemplateEligibility.EligibleTemplateIds(null));
            Assert.IsNull(TemplateEligibility.EligibleTemplateIds(""));
            Assert.IsNull(TemplateEligibility.EligibleTemplateIds("COBOL"));
        }

        [TestMethod]
        public void EveryEligibleId_ResolvesInTheBuiltInCatalog()
        {
            TemplateCatalog catalog = TemplateCatalog.BuiltIn();
            foreach (string lang in new[] { "Python", "CUDA", "C++" })
            {
                foreach (string id in TemplateEligibility.EligibleTemplateIds(lang))
                {
                    Assert.IsNotNull(catalog.ById(id),
                        "eligibility for '" + lang + "' references missing template '" + id + "'");
                }
            }
        }

        [TestMethod]
        public void Catalog_ContainsTheFullTemplateBatch()
        {
            TemplateCatalog c = TemplateCatalog.BuiltIn();
            foreach (string id in new[]
            {
                "cpp.dll", "cuda.dll",
                "cpp.test", "py.test",
            })
            {
                Assert.IsNotNull(c.ById(id), id + " must exist");
            }
            StringAssert.Contains(c.ById("cuda.classlib").Name, "(Static)");
            StringAssert.Contains(c.ById("cpp.classlib").Name, "(Static)");
            Assert.AreEqual("Test", c.ById("py.test").ProjectType, "test templates carry the Test facet for filtering");
            Assert.AreEqual("DLL", c.ById("cpp.dll").ProjectType);
        }

        [TestMethod]
        public void CudaDll_Generates_SharedLibraryCMake()
        {
            string dir = Generate("cuda.dll", "CudaArch", "86");
            try
            {
                string cmake = File.ReadAllText(Path.Combine(dir, "CMakeLists.txt"));
                StringAssert.Contains(cmake, "add_library(NativeProj SHARED kernels.cu)");
                StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "kernels.cu")), "__declspec(dllexport)");
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(dir), true); } catch { } }
        }

        [TestMethod]
        public void PyTest_Generates_AStrictlyOopTestPackage()
        {
            string dir = Generate("py.test", "PythonVersion", "3.11");
            try
            {
                string test = File.ReadAllText(Path.Combine(dir, "test_basic.py"));
                StringAssert.Contains(test, "class TestBasic:", "pytest-collectable class — strict-OOP shape");
                // kind = "test", not "library": solution policy rule 3 (PY8003) allows test files
                // in a test project and nowhere else, so a test template declaring itself a library
                // would generate a project blocked from compiling the instant it was created.
                StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "pyproject.toml")), "kind = \"test\"");
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(dir), true); } catch { } }
        }

        // ---- the new native templates generate real projects ----

        private static string Generate(string templateId, params string[] varPairs)
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_tmpl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            ProjectTemplate t = TemplateCatalog.BuiltIn().ById(templateId);
            Assert.IsNotNull(t, templateId + " must exist");
            var opt = new GenerationOptions { TargetRoot = root };
            opt.Variables["ProjectName"] = "NativeProj";
            opt.Variables["Description"] = "test";
            for (int i = 0; i + 1 < varPairs.Length; i += 2) opt.Variables[varPairs[i]] = varPairs[i + 1];
            var r = ProjectGenerator.Generate(t, opt);
            Assert.IsTrue(r.Success, templateId + ": " + r.Error);
            return Path.Combine(root, "NativeProj");
        }

        [TestMethod]
        public void CppConsole_Generates_CMakeExecutable()
        {
            string dir = Generate("cpp.console", "CxxStandard", "20");
            try
            {
                string cmake = File.ReadAllText(Path.Combine(dir, "CMakeLists.txt"));
                StringAssert.Contains(cmake, "add_executable(NativeProj main.cpp)");
                StringAssert.Contains(cmake, "CMAKE_CXX_STANDARD 20");
                Assert.IsFalse(cmake.Contains("{{"), "all template variables substituted");
                Assert.IsTrue(File.Exists(Path.Combine(dir, "main.cpp")));
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(dir), true); } catch { } }
        }

        [TestMethod]
        public void CppLibrary_Generates_CMakeStaticLibrary()
        {
            string dir = Generate("cpp.classlib", "CxxStandard", "17");
            try
            {
                string cmake = File.ReadAllText(Path.Combine(dir, "CMakeLists.txt"));
                StringAssert.Contains(cmake, "add_library(NativeProj STATIC lib.cpp lib.h)");
                Assert.IsTrue(File.Exists(Path.Combine(dir, "lib.h")));
                Assert.IsTrue(File.Exists(Path.Combine(dir, "lib.cpp")));
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(dir), true); } catch { } }
        }

        [TestMethod]
        public void CudaConsole_Generates_AnOpenableCMakeExecutable()
        {
            string dir = Generate("cuda.console", "CudaArch", "86");
            try
            {
                string cmake = File.ReadAllText(Path.Combine(dir, "CMakeLists.txt"));
                StringAssert.Contains(cmake, "LANGUAGES CUDA CXX");
                StringAssert.Contains(cmake, "add_executable(NativeProj main.cu)");
                StringAssert.Contains(cmake, "CUDA_ARCHITECTURES 86");
                Assert.IsFalse(cmake.Contains("{{"), "all template variables substituted");
                Assert.IsTrue(File.Exists(Path.Combine(dir, "main.cu")));
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(dir), true); } catch { } }
        }

        [TestMethod]
        public void CudaMdsim_Generates_WithACMakeLists_SoItCanOpenAsAWorkspace()
        {
            // Regression: this template used to ship only simulate.cu — the wizard finished
            // but the created project could not be opened as a native workspace at all.
            string dir = Generate("cuda.mdsim", "CudaArch", "86");
            try
            {
                Assert.IsTrue(File.Exists(Path.Combine(dir, "CMakeLists.txt")),
                    "every native template must be openable => must ship a CMakeLists.txt");
                string cmake = File.ReadAllText(Path.Combine(dir, "CMakeLists.txt"));
                StringAssert.Contains(cmake, "add_executable(NativeProj simulate.cu)");
                Assert.IsTrue(File.Exists(Path.Combine(dir, "simulate.cu")));
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(dir), true); } catch { } }
        }

        [TestMethod]
        public void CudaLibrary_Generates_CMakeCudaStaticLibrary()
        {
            string dir = Generate("cuda.classlib", "CudaArch", "86");
            try
            {
                string cmake = File.ReadAllText(Path.Combine(dir, "CMakeLists.txt"));
                StringAssert.Contains(cmake, "LANGUAGES CUDA CXX");
                StringAssert.Contains(cmake, "add_library(NativeProj STATIC kernels.cu)");
                StringAssert.Contains(cmake, "CUDA_ARCHITECTURES 86");
                string cu = File.ReadAllText(Path.Combine(dir, "kernels.cu"));
                StringAssert.Contains(cu, "__global__ void scale_kernel");
                StringAssert.Contains(cu, "extern \"C\" cudaError_t launch_scale");
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(dir), true); } catch { } }
        }
    }
}
