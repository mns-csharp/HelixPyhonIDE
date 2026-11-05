using System;
using System.IO;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.LanguageServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PackageManagerAdapterRegistryTests
    {
        [TestMethod]
        public void EveryPackageLanguageHasAnEditableAdapter()
        {
            // HelixPython registers the PEP 621 adapter and nothing else: nuget/cargo/maven/
            // gradle/julia-pkg ship in their own products, and C++ never had one (CMake has no
            // package manager this IDE edits). Every ProjectLanguage this product declares is
            // swept, so a language added later without an adapter decision fails here.
            Assert.AreEqual("python-pep621", PackageManagerAdapterRegistry.For(ProjectLanguage.Python).Id);
            foreach (ProjectLanguage lang in (ProjectLanguage[])Enum.GetValues(typeof(ProjectLanguage)))
            {
                if (lang == ProjectLanguage.Python) continue;
                Assert.IsNull(PackageManagerAdapterRegistry.For(lang),
                    lang + ": only Python has a package manager this IDE edits");
            }
        }

    }
}
