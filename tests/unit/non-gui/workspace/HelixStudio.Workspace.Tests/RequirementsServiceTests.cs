using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>FR-PROJ-04: requirements.txt read/write + sync with pyproject [project].dependencies.</summary>
    [TestClass]
    public class RequirementsServiceTests
    {
        [TestMethod]
        public void Parse_ClassifiesLines_AndParsesRequirements()
        {
            const string txt =
                "# app requirements\n" +
                "numpy>=1.24\n" +
                "requests  # http\n" +
                "-r other.txt\n" +
                "-e .\n" +
                "\n" +
                "click>=8.0\n";
            var lines = RequirementsService.Parse(txt);
            Assert.AreEqual(RequirementLine.LineKind.Comment, lines[0].Kind);
            Assert.AreEqual(RequirementLine.LineKind.Requirement, lines[1].Kind);
            Assert.AreEqual("numpy", lines[1].Requirement.CanonicalName);
            Assert.AreEqual(RequirementLine.LineKind.Requirement, lines[2].Kind);
            Assert.AreEqual("requests", lines[2].Requirement.CanonicalName, "inline comment stripped before parse");
            Assert.AreEqual(RequirementLine.LineKind.Option, lines[3].Kind, "-r include");
            Assert.AreEqual(RequirementLine.LineKind.Option, lines[4].Kind, "-e editable");
            Assert.AreEqual(RequirementLine.LineKind.Blank, lines[5].Kind);

            var reqs = RequirementsService.Requirements(lines);
            CollectionAssert.AreEqual(new[] { "numpy", "requests", "click" }, reqs.Select(r => r.CanonicalName).ToArray());
        }

        [TestMethod]
        public void Write_RoundTripsVerbatim()
        {
            const string txt =
                "# header\n" +
                "numpy>=1.24\n" +
                "requests  # http\n" +
                "-e .\n";
            var lines = RequirementsService.Parse(txt);
            // Parse drops the trailing empty element; Write joins with '\n', so re-add the trailing newline.
            Assert.AreEqual(txt, RequirementsService.Write(lines) + "\n");
        }

        [TestMethod]
        public void Diff_ReportsAsymmetricDifferences()
        {
            string dir = NewTempDir();
            string reqs = Path.Combine(dir, "requirements.txt");
            string proj = Path.Combine(dir, "pyproject.toml");
            File.WriteAllText(reqs, "numpy>=1.24\nrequests\npytest==7.4.0\n");
            File.WriteAllText(proj,
                "[project]\nname='d'\nversion='0'\ndependencies=[\"numpy>=1.24\", \"requests\", \"click>=8.0\"]\n");
            var diff = RequirementsService.Diff(reqs, proj);
            Assert.IsFalse(diff.InSync);
            CollectionAssert.AreEqual(new[] { "pytest" }, diff.OnlyInRequirements.ToArray());
            CollectionAssert.AreEqual(new[] { "click" }, diff.OnlyInPyproject.ToArray());
            Assert.AreEqual(0, diff.SpecifierDiffers.Count);
        }

        [TestMethod]
        public void SyncFromPyproject_AppendsMissing_PreservesExisting()
        {
            string dir = NewTempDir();
            string reqs = Path.Combine(dir, "requirements.txt");
            string proj = Path.Combine(dir, "pyproject.toml");
            File.WriteAllText(reqs, "# my deps\nnumpy>=1.24\nrequests  # http\n");
            File.WriteAllText(proj,
                "[project]\nname='d'\nversion='0'\ndependencies=[\"numpy>=1.24\", \"requests\", \"click>=8.0\"]\n");

            int added = RequirementsService.SyncFromPyproject(reqs, proj);
            Assert.AreEqual(1, added);
            string after = File.ReadAllText(reqs);
            StringAssert.Contains(after, "# my deps");            // existing comment preserved
            StringAssert.Contains(after, "requests  # http");     // existing inline comment preserved
            StringAssert.Contains(after, "click>=8.0");           // appended
            // Idempotent: a second sync adds nothing.
            Assert.AreEqual(0, RequirementsService.SyncFromPyproject(reqs, proj));
        }

        private static string NewTempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "helix_reqtest_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }
    }
}
