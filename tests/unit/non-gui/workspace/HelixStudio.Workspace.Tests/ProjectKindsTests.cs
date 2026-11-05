using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// <see cref="ProjectKinds"/> has no prior dedicated coverage even though the compliance
    /// pipeline (<see cref="HelixPythonNamespace.Workspace.Python.PackageComplianceValidator"/>) depends
    /// entirely on it: a mis-parsed <c>[tool.helixstudio].kind</c> token silently degrades a project
    /// to <see cref="ProjectKind.ClassLibrary"/> rather than failing loudly, so the token table and
    /// the three predicate methods need their own truth-table tests independent of the validator.
    /// </summary>
    [TestClass]
    public class ProjectKindsTests
    {
        // ---- ToToken / FromToken round-trip ----

        [DataTestMethod]
        [DataRow(ProjectKind.App)]
        [DataRow(ProjectKind.FlaskWebApp)]
        [DataRow(ProjectKind.GuiApp)]
        [DataRow(ProjectKind.CudaExecutable)]
        [DataRow(ProjectKind.CudaLibrary)]
        [DataRow(ProjectKind.ClassLibrary)]
        public void ToToken_ThenFromToken_RoundTrips(ProjectKind kind)
        {
            string token = ProjectKinds.ToToken(kind);
            Assert.AreEqual(kind, ProjectKinds.FromToken(token), "token was: " + token);
        }

        // ---- FromToken: every documented alias ----

        [DataTestMethod]
        [DataRow("app", ProjectKind.App)]
        [DataRow("flask", ProjectKind.FlaskWebApp)]
        [DataRow("flask-web", ProjectKind.FlaskWebApp)]
        [DataRow("flaskwebapp", ProjectKind.FlaskWebApp)]
        [DataRow("gui", ProjectKind.GuiApp)]
        [DataRow("gui-app", ProjectKind.GuiApp)]
        [DataRow("guiapp", ProjectKind.GuiApp)]
        [DataRow("cuda-exe", ProjectKind.CudaExecutable)]
        [DataRow("cuda-executable", ProjectKind.CudaExecutable)]
        [DataRow("cudaexecutable", ProjectKind.CudaExecutable)]
        [DataRow("cuda-lib", ProjectKind.CudaLibrary)]
        [DataRow("cuda-library", ProjectKind.CudaLibrary)]
        [DataRow("cudalibrary", ProjectKind.CudaLibrary)]
        [DataRow("library", ProjectKind.ClassLibrary)]
        public void FromToken_RecognizesEveryDocumentedAlias(string token, ProjectKind expected)
        {
            Assert.AreEqual(expected, ProjectKinds.FromToken(token));
        }

        // ---- FromToken: case-insensitive, whitespace-tolerant ----

        [DataTestMethod]
        [DataRow(" App ", ProjectKind.App)]
        [DataRow("APP", ProjectKind.App)]
        [DataRow("Gui-App", ProjectKind.GuiApp)]
        [DataRow("CUDA-EXE", ProjectKind.CudaExecutable)]
        [DataRow("Library", ProjectKind.ClassLibrary)]
        // Connector layer: the generic runnable kind for the manifest languages
        // (Rust bin / C# Exe / Java app / Julia entry script).
        [DataRow("exe", ProjectKind.Executable)]
        [DataRow("Executable", ProjectKind.Executable)]
        public void FromToken_IsCaseInsensitiveAndTrimsWhitespace(string token, ProjectKind expected)
        {
            Assert.AreEqual(expected, ProjectKinds.FromToken(token));
        }

        // ---- FromToken: unrecognized/missing tokens silently default to ClassLibrary ----
        //
        // This silent fallback (rather than a thrown exception or a distinct "Unknown" kind) is
        // exactly what makes a misspelled `kind = "aap"` in pyproject.toml dangerous: the project
        // quietly becomes a non-executable library instead of failing to load. Pin the behavior.

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("aap")]
        [DataRow("script")]
        [DataRow("webapp")]
        public void FromToken_UnrecognizedOrMissingToken_DefaultsToClassLibrary(string token)
        {
            Assert.AreEqual(ProjectKind.ClassLibrary, ProjectKinds.FromToken(token));
        }

        // ---- IsNative: true only for the two CUDA/C++ kinds ----

        [DataTestMethod]
        [DataRow(ProjectKind.ClassLibrary, false)]
        [DataRow(ProjectKind.App, false)]
        [DataRow(ProjectKind.FlaskWebApp, false)]
        [DataRow(ProjectKind.GuiApp, false)]
        [DataRow(ProjectKind.CudaExecutable, true)]
        [DataRow(ProjectKind.CudaLibrary, true)]
        public void IsNative_TrueOnlyForCudaKinds(ProjectKind kind, bool expected)
        {
            Assert.AreEqual(expected, ProjectKinds.IsNative(kind), kind.ToString());
        }

        // ---- IsExecutable: the Run/Debug-eligible kinds ----

        [DataTestMethod]
        [DataRow(ProjectKind.ClassLibrary, false)]
        [DataRow(ProjectKind.App, true)]
        [DataRow(ProjectKind.FlaskWebApp, true)]
        [DataRow(ProjectKind.GuiApp, true)]
        [DataRow(ProjectKind.CudaExecutable, true)]
        [DataRow(ProjectKind.CudaLibrary, false)]
        public void IsExecutable_TrueForRunnableKinds(ProjectKind kind, bool expected)
        {
            Assert.AreEqual(expected, ProjectKinds.IsExecutable(kind), kind.ToString());
        }

        // ---- RequiresMainModule: only App/GuiApp are subject to the def main() rules ----
        //
        // FlaskWebApp is executable (IsExecutable == true) but must NOT require a main module --
        // its entry point is the WSGI Flask instance. This is the one kind where IsExecutable and
        // RequiresMainModule disagree, so it is the case most worth pinning explicitly.

        [DataTestMethod]
        [DataRow(ProjectKind.ClassLibrary, false)]
        [DataRow(ProjectKind.App, true)]
        [DataRow(ProjectKind.FlaskWebApp, false)]
        [DataRow(ProjectKind.GuiApp, true)]
        [DataRow(ProjectKind.CudaExecutable, false)]
        [DataRow(ProjectKind.CudaLibrary, false)]
        public void RequiresMainModule_TrueOnlyForAppAndGuiApp(ProjectKind kind, bool expected)
        {
            Assert.AreEqual(expected, ProjectKinds.RequiresMainModule(kind), kind.ToString());
        }

        [TestMethod]
        public void FlaskWebApp_IsExecutableButDoesNotRequireMainModule()
        {
            Assert.IsTrue(ProjectKinds.IsExecutable(ProjectKind.FlaskWebApp));
            Assert.IsFalse(ProjectKinds.RequiresMainModule(ProjectKind.FlaskWebApp));
        }
    }
}
