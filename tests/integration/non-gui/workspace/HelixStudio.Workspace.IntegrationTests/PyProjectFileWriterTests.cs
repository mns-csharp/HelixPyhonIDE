using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PyProjectFileWriterTests
    {
        private static XNamespace Ns => PyProjectFileWriter.MsBuildNamespace;

        private static string TempPackageDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pyprojfwt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [TestMethod]
        public void EmitsWellFormedXmlWithExpectedSchema()
        {
            string dir = TempPackageDir();
            PackageDescriptor pkg = new PackageDescriptor("sample", dir)
            {
                ProjectGuid = Guid.NewGuid(),
                PyProjFilePath = Path.Combine(dir, "sample.pyproj")
            };
            pkg.PyFiles.AddRange(new[] { "__init__.py", "module_a.py" });
            pkg.SubDirectories.Add("sub");

            PyProjectFileWriter.Write(pkg);

            Assert.IsTrue(File.Exists(pkg.PyProjFilePath));
            XDocument doc = XDocument.Load(pkg.PyProjFilePath);
            Assert.AreEqual("Project", doc.Root.Name.LocalName);
            Assert.AreEqual(PyProjectFileWriter.MsBuildNamespace, doc.Root.Name.NamespaceName);
            Assert.AreEqual("Build", (string)doc.Root.Attribute("DefaultTargets"));
        }

        [TestMethod]
        public void WritesProjectGuidAndSchemaProperties()
        {
            string dir = TempPackageDir();
            Guid guid = Guid.NewGuid();
            PackageDescriptor pkg = new PackageDescriptor("pkg", dir)
            {
                ProjectGuid = guid,
                PyProjFilePath = Path.Combine(dir, "pkg.pyproj")
            };
            pkg.PyFiles.Add("__init__.py");

            PyProjectFileWriter.Write(pkg);

            XDocument doc = XDocument.Load(pkg.PyProjFilePath);
            var propGroup = doc.Root.Element(Ns + "PropertyGroup");
            Assert.IsNotNull(propGroup);
            Assert.AreEqual("2.0", (string)propGroup.Element(Ns + "SchemaVersion"));
            string formattedGuid = "{" + guid.ToString("D").ToUpperInvariant() + "}";
            Assert.AreEqual(formattedGuid, (string)propGroup.Element(Ns + "ProjectGuid"));
            Assert.AreEqual("__init__.py", (string)propGroup.Element(Ns + "StartupFile"));
            Assert.AreEqual("pkg", (string)propGroup.Element(Ns + "Name"));
            Assert.AreEqual("pkg", (string)propGroup.Element(Ns + "RootNamespace"));
        }

        [TestMethod]
        public void WritesCompileItemsForEveryPyFile()
        {
            string dir = TempPackageDir();
            PackageDescriptor pkg = new PackageDescriptor("x", dir)
            {
                ProjectGuid = Guid.NewGuid(),
                PyProjFilePath = Path.Combine(dir, "x.pyproj")
            };
            pkg.PyFiles.AddRange(new[] { "__init__.py", "a.py", "b.py", "nested.pyi" });

            PyProjectFileWriter.Write(pkg);

            XDocument doc = XDocument.Load(pkg.PyProjFilePath);
            var compiles = doc.Root.Descendants(Ns + "Compile")
                .Select(e => (string)e.Attribute("Include"))
                .ToList();
            CollectionAssert.AreEquivalent(new[] { "__init__.py", "a.py", "b.py", "nested.pyi" }, compiles);
        }

        [TestMethod]
        public void WritesFolderItemsWithTrailingBackslash()
        {
            string dir = TempPackageDir();
            PackageDescriptor pkg = new PackageDescriptor("x", dir)
            {
                ProjectGuid = Guid.NewGuid(),
                PyProjFilePath = Path.Combine(dir, "x.pyproj")
            };
            pkg.PyFiles.Add("__init__.py");
            pkg.SubDirectories.Add("subpkg");

            PyProjectFileWriter.Write(pkg);

            XDocument doc = XDocument.Load(pkg.PyProjFilePath);
            var folders = doc.Root.Descendants(Ns + "Folder")
                .Select(e => (string)e.Attribute("Include"))
                .ToList();
            Assert.AreEqual(1, folders.Count);
            StringAssert.EndsWith(folders[0], "\\");
        }

        [TestMethod]
        public void ImportsPtvsTargets()
        {
            string dir = TempPackageDir();
            PackageDescriptor pkg = new PackageDescriptor("x", dir)
            {
                ProjectGuid = Guid.NewGuid(),
                PyProjFilePath = Path.Combine(dir, "x.pyproj")
            };
            pkg.PyFiles.Add("__init__.py");

            PyProjectFileWriter.Write(pkg);

            XDocument doc = XDocument.Load(pkg.PyProjFilePath);
            var import = doc.Root.Element(Ns + "Import");
            Assert.IsNotNull(import);
            StringAssert.Contains((string)import.Attribute("Project"), "Microsoft.PythonTools.targets");
        }

        [TestMethod]
        public void StartupFilePrefersInit()
        {
            string dir = TempPackageDir();
            PackageDescriptor pkg = new PackageDescriptor("x", dir)
            {
                ProjectGuid = Guid.NewGuid(),
                PyProjFilePath = Path.Combine(dir, "x.pyproj")
            };
            // Intentionally list __init__.py last to confirm selection logic.
            pkg.PyFiles.AddRange(new[] { "a.py", "b.py", "__init__.py" });

            PyProjectFileWriter.Write(pkg);

            XDocument doc = XDocument.Load(pkg.PyProjFilePath);
            Assert.AreEqual("__init__.py",
                (string)doc.Root.Element(Ns + "PropertyGroup").Element(Ns + "StartupFile"));
        }
    }
}
