using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PyProjReaderTests
    {
        [TestMethod]
        public void ReadsNameAndCompileItems()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_pyproj_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "PythonApplication1.pyproj");
                File.WriteAllText(path, "<Project><PropertyGroup><ProjectHome>.</ProjectHome></PropertyGroup><ItemGroup><Compile Include=\"PythonApplication1.py\" /></ItemGroup></Project>");
                var info = PyProjReader.Read(path);
                Assert.AreEqual("PythonApplication1", info.Name);
                Assert.IsTrue(info.CompileIncludes.Contains("PythonApplication1.py"));
                Assert.AreEqual(Path.GetFullPath(root), Path.GetFullPath(info.ProjectHome));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
