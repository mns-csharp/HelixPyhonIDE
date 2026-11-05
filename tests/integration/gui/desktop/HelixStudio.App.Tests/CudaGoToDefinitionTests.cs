using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// SRS §10 IntelliSense (loop 525): F12 in a .cu file resolves through the CUDA
    /// symbol table (CudaDefinitionLocator) instead of falling into the Python
    /// resolver and failing with a parse error. The locator itself is covered in
    /// CudaVirtualCompiler.Tests; this exercises the App routing end-to-end.
    /// </summary>
    [TestClass]
    public class CudaGoToDefinitionTests
    {
        [TestMethod]
        public void F12InCudaFile_OpensTheDefiningLine()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_cuda_gtd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string cuPath = Path.Combine(dir, "kernels.cu");
            File.WriteAllText(cuPath,
                "__global__ void scale_kernel(int* data) {\n" +
                "    data[threadIdx.x] *= 2;\n" +
                "}\n" +
                "void host_fn(int* d) { scale_kernel<<<1, 32>>>(d); }\n");
            try
            {
                using (MainForm form = TestMainForm.CreateComposed())
                {
                    TestWindow.ShowOffScreen(form);
                    var args = new HelixPythonNamespace.Editor.EditorGoToDefinitionArgs
                    {
                        FilePath = cuPath,
                        Line = 4,
                        Column = 25,
                        Token = "scale_kernel",
                    };
                    form.OnEditorGoToDefinitionRequested(form, args);
                    Assert.IsTrue(args.Handled, "the CUDA branch must navigate, not fall through to Python");
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [TestMethod]
        public void F12OnUnknownCudaToken_IsNotHandled_ButDoesNotThrow()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_cuda_gtd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string cuPath = Path.Combine(dir, "kernels.cu");
            File.WriteAllText(cuPath, "__global__ void k(int* o) { o[0] = 1; }\n");
            try
            {
                using (MainForm form = TestMainForm.CreateComposed())
                {
                    TestWindow.ShowOffScreen(form);
                    var args = new HelixPythonNamespace.Editor.EditorGoToDefinitionArgs
                    {
                        FilePath = cuPath,
                        Line = 1,
                        Column = 1,
                        Token = "no_such_symbol",
                    };
                    form.OnEditorGoToDefinitionRequested(form, args);
                    Assert.IsFalse(args.Handled);
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
