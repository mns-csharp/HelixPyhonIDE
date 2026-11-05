using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class VsSolutionWriterTests
    {
        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "slnwriter_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static PackageDescriptor MakePackage(string root, string name)
        {
            string packageDir = Path.Combine(root, name);
            Directory.CreateDirectory(packageDir);
            PackageDescriptor pkg = new PackageDescriptor(name, packageDir)
            {
                ProjectGuid = Guid.NewGuid(),
                PyProjFilePath = Path.Combine(packageDir, name + ".pyproj")
            };
            return pkg;
        }

        [TestMethod]
        public void EmitsSolutionHeader()
        {
            string root = TempDir();
            string slnPath = Path.Combine(root, "x.sln");
            VsSolutionWriter.Write(slnPath, new List<PackageDescriptor> { MakePackage(root, "a") });
            string sln = File.ReadAllText(slnPath);
            StringAssert.Contains(sln, "Microsoft Visual Studio Solution File, Format Version 12.00");
        }

        [TestMethod]
        public void EmitsProjectLineForEachPackage()
        {
            string root = TempDir();
            var packages = new List<PackageDescriptor> { MakePackage(root, "alpha"), MakePackage(root, "beta") };
            string slnPath = Path.Combine(root, "multi.sln");
            VsSolutionWriter.Write(slnPath, packages);
            string sln = File.ReadAllText(slnPath);
            StringAssert.Contains(sln, "\"alpha\"");
            StringAssert.Contains(sln, "\"beta\"");
            StringAssert.Contains(sln, VsSolutionWriter.PtvsProjectTypeGuid);
            // Relative path to the .pyproj should use backslashes.
            StringAssert.Contains(sln, "alpha\\alpha.pyproj");
            StringAssert.Contains(sln, "beta\\beta.pyproj");
        }

        [TestMethod]
        public void EmitsConfigurationPlatformsForEveryProject()
        {
            string root = TempDir();
            var pkg = MakePackage(root, "alpha");
            string slnPath = Path.Combine(root, "x.sln");
            VsSolutionWriter.Write(slnPath, new List<PackageDescriptor> { pkg });
            string sln = File.ReadAllText(slnPath);
            string g = "{" + pkg.ProjectGuid.ToString("D").ToUpperInvariant() + "}";
            StringAssert.Contains(sln, g + ".Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            StringAssert.Contains(sln, g + ".Release|Any CPU.ActiveCfg = Release|Any CPU");
        }

        [TestMethod]
        public void EmitsSolutionGuidInExtensibilityGlobals()
        {
            string root = TempDir();
            string slnPath = Path.Combine(root, "x.sln");
            VsSolutionWriter.Write(slnPath, new List<PackageDescriptor> { MakePackage(root, "a") });
            string sln = File.ReadAllText(slnPath);
            StringAssert.Contains(sln, "SolutionGuid = {");
        }
    }
}
