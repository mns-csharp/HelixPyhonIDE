using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.CudaVirtualCompiler;
using Cvc = HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The include-aware undefined-name pipeline ("your parser is not catching this new
    /// erroneous statement"): quoted #includes are followed through the fixture-standard
    /// &lt;pkg&gt;/include/&lt;pkg&gt;/ layout, their symbols merge into the table, and — when every
    /// quoted include resolves — an undefined identifier becomes an ERROR (nvcc parity)
    /// instead of an unmarkable warning. Per the fixture-first testing doctrine, these
    /// tests build a real on-disk workspace-shaped tree, not just inline strings.
    /// </summary>
    [TestClass]
    public class IncludeResolutionTests
    {
        private static CudaCompileOptions EditorOptions()
        {
            return new CudaCompileOptions
            {
                EnableAstValidator = false,
                EnableQualifierSync = false,
                EnableLaunchConfig = false,
                EnableMemorySpace = false,
                EnableLint = false,
                EnableLanguageStandard = false,
                EnableIncludeResolution = true,
            };
        }

        private static int Errors(CudaCompileResult r)
        {
            return r.Diagnostics.Count(d => d.Severity == global::HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error);
        }

        private static string BuildWorkspace()
        {
            string root = Path.Combine(Path.GetTempPath(), "hx_incres_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string libInc = Path.Combine(root, "lib_core", "include", "lib_core");
            string appInc = Path.Combine(root, "app_cli", "include", "app_cli");
            Directory.CreateDirectory(libInc);
            Directory.CreateDirectory(appInc);
            File.WriteAllText(Path.Combine(libInc, "shapes.cuh"),
                "#pragma once\n#include <memory>\n" +
                "namespace lib_core::shapes {\n" +
                "struct Point3D { double x = 0.0; double y = 0.0; };\n" +
                "class Polygon { public: double area() const { return 0.0; } };\n" +
                "double total_area(int n);\n" +
                "}\n");
            return root;
        }

        [TestMethod]
        public void QuotedIncludes_ResolveAcrossSiblingPackages_AndUsingsIntroduceNames()
        {
            string root = BuildWorkspace();
            string file = Path.Combine(root, "app_cli", "include", "app_cli", "commands.cuh");
            try
            {
                File.WriteAllText(file,
                    "#pragma once\n#include <string>\n#include \"lib_core/shapes.cuh\"\n" +
                    "using lib_core::shapes::Polygon;\n" +
                    "using namespace lib_core::shapes;\n" +
                    "namespace app_cli::commands {\n" +
                    "double demo() {\n" +
                    "    Polygon p;\n" +                     // via using-declaration
                    "    Point3D q;\n" +                     // via using-directive
                    "    return total_area(3) + p.area();\n" +  // header function
                    "}\n}\n");
                var r = Cvc.CompileFile(file, EditorOptions());
                Assert.AreEqual(0, Errors(r), string.Join(" | ",
                    r.Diagnostics.Where(d => d.Severity == global::HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error)
                                 .Select(d => d.Message)));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void UndefinedName_WithResolvedIncludes_IsAnError_TheUsersRepro()
        {
            string root = BuildWorkspace();
            string file = Path.Combine(root, "app_cli", "include", "app_cli", "commands.cuh");
            try
            {
                // The exact user scenario: a junk-but-parseable expression whose identifiers
                // exist nowhere. With system includes present it used to be silently legal.
                File.WriteAllText(file,
                    "#pragma once\n#include <string>\n#include \"lib_core/shapes.cuh\"\n" +
                    "namespace app_cli::commands {\n" +
                    "int demo() {\n" +
                    "    1-garbage-text;\n" +
                    "    return 0;\n" +
                    "}\n}\n");
                var r = Cvc.CompileFile(file, EditorOptions());
                var errs = r.Diagnostics.Where(d =>
                    d.Severity == global::HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error).ToList();
                Assert.IsTrue(errs.Any(d => d.Message.Contains("'garbage'")),
                    "undefined 'garbage' must be an ERROR when the include graph resolved");
                Assert.IsTrue(errs.Any(d => d.Message.Contains("'text'")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void UnresolvedQuotedInclude_DowngradesToNonStrict()
        {
            string root = BuildWorkspace();
            string file = Path.Combine(root, "app_cli", "include", "app_cli", "commands.cuh");
            try
            {
                File.WriteAllText(file,
                    "#include \"lib_core/does_not_exist.cuh\"\n#include <string>\n" +
                    "int demo() { return mystery_name; }\n");
                var r = Cvc.CompileFile(file, EditorOptions());
                Assert.AreEqual(0, Errors(r),
                    "with an unresolved QUOTED include, absence is unprovable — no undefined-name errors");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void SelfContainedCompiles_KeepWarningSeverity()
        {
            // No include resolution (the default options): existing behavior preserved —
            // CU2101 stays a Warning on include-free snippets.
            var r = Cvc.CompileSource("int f() { return not_defined_anywhere; }\n", "t.cu");
            Assert.AreEqual(0, Errors(r));
            Assert.IsTrue(r.Diagnostics.Any(d =>
                d.Code == CudaDiagnosticCodes.SemUndefinedName &&
                d.Severity == global::HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Warning));
        }
    }
}
