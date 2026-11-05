using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Templates;

namespace HelixPythonNamespace.Workspace.Tests
{
    /// <summary>
    /// The "Analyse a Structure Ensemble" template — the one-click path to something real.
    ///
    /// The claim it has to survive is not "it generates files"; it is that what comes out RUNS. A
    /// template whose output the IDE then refuses is the worst available first impression, so the
    /// assertions here are about the properties that would cause that: the package kind that decides
    /// whether the project is runnable at all, the object-orientation PY8002 enforces, and the flat
    /// layout PY8004 checks the tree against.
    /// </summary>
    [TestClass]
    public class WorkedExampleTemplateTests
    {
        private const string TemplateId = "py.structure-ensemble";

        private readonly List<string> _temporary = new List<string>();

        [TestCleanup]
        public void TearDown()
        {
            foreach (string dir in _temporary)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        private static ProjectTemplate Template()
        {
            ProjectTemplate t = TemplateCatalog.BuiltIn().ById(TemplateId);
            Assert.IsNotNull(t, "the built-in catalog should carry " + TemplateId);
            return t;
        }

        /// <summary>Generate it to a scratch directory and return the project root.</summary>
        private string Generate(string projectName = "ubiquitin_study")
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-tpl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _temporary.Add(root);

            var options = new GenerationOptions { TargetRoot = root };
            options.Variables["ProjectName"] = projectName;
            options.Variables["Description"] = "A worked structural example.";
            options.Variables["PythonVersion"] = "3.11";

            GenerationResult result = ProjectGenerator.Generate(Template(), options);
            Assert.IsTrue(result.Success, result.Error);
            return Path.Combine(root, projectName);
        }

        // ---- it is in the catalog, and findable ---------------------------------------------------

        [TestMethod]
        public void TheTemplateIsOfferedAndDescribesWhatItProduces()
        {
            ProjectTemplate t = Template();

            Assert.AreEqual("Python", t.Language);
            StringAssert.Contains(t.Description, "figure");
            StringAssert.Contains(t.Description, "CSV",
                "the CSV behind the figure is the point, not a footnote");
            CollectionAssert.Contains(t.Tags.ToArray(), "pdb");
            CollectionAssert.Contains(t.Tags.ToArray(), "structural biology");
        }

        // ---- what it generates -----------------------------------------------------------------

        [TestMethod]
        public void ItGeneratesAFlatPackageBesideItsManifest()
        {
            // The canonical layout is flat: <project>/<package>/, never <project>/src/<package>/.
            // A template emitting src/ would be a PY8004 defect on first open.
            string project = Generate();

            Assert.IsTrue(File.Exists(Path.Combine(project, "pyproject.toml")));
            Assert.IsTrue(File.Exists(Path.Combine(project, "main.py")));
            Assert.IsTrue(Directory.Exists(Path.Combine(project, "ubiquitin_study")));
            Assert.IsTrue(File.Exists(Path.Combine(project, "ubiquitin_study", "__init__.py")));
            Assert.IsFalse(Directory.Exists(Path.Combine(project, "src")),
                "src/ is the legacy layout - it is read, never written");
        }

        [TestMethod]
        public void TheManifestDeclaresARunnableApp()
        {
            // ProjectKinds.FromToken defaults a MISSING kind to ClassLibrary, so a manifest without
            // it produces a project that loads as a library and cannot be run at all.
            string toml = File.ReadAllText(Path.Combine(Generate(), "pyproject.toml"));

            StringAssert.Contains(toml, "kind = \"app\"");
            StringAssert.Contains(toml, "startup = true");
            StringAssert.Contains(toml, "name = \"ubiquitin_study\"", "the name is substituted");
            Assert.IsFalse(toml.Contains("{{"), "no placeholder should survive generation");
        }

        [TestMethod]
        public void NoPlaceholderSurvivesInAnyGeneratedFile()
        {
            string project = Generate("my_study");
            foreach (string path in Directory.GetFiles(project, "*", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(path);
                Assert.IsFalse(text.Contains("{{"),
                    Path.GetFileName(path) + " still holds a template placeholder");
            }
        }

        // ---- the rule that would break it on first run ---------------------------------------------

        [TestMethod]
        public void TheGeneratedPythonIsObjectOriented()
        {
            // PY8002 refuses module-level `def` outside the PSM-mandated `def main()`. This is the
            // property that decides whether the template's own output is runnable in the IDE that
            // ships it.
            string project = Generate();

            foreach (string path in Directory.GetFiles(project, "*.py", SearchOption.AllDirectories))
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    if (!line.StartsWith("def ", StringComparison.Ordinal)) continue;
                    Assert.IsTrue(line.StartsWith("def main(", StringComparison.Ordinal),
                        Path.GetFileName(path) + " has a module-level function other than main(): " + line);
                }
            }
        }

        [TestMethod]
        public void TheEntryPointIsAMainFunction()
        {
            string main = File.ReadAllText(Path.Combine(Generate(), "main.py"));

            StringAssert.Contains(main, "def main():");
            StringAssert.Contains(main, "if __name__ == \"__main__\":");
            StringAssert.Contains(main, "EnsembleAnalysis", "main should use the package it ships with");
        }

        [TestMethod]
        public void EveryClassTheEntryPointUsesIsExported()
        {
            // A generated project whose own imports fail is worse than no template.
            string project = Generate();
            string init = File.ReadAllText(Path.Combine(project, "ubiquitin_study", "__init__.py"));

            foreach (string name in new[] { "EnsembleAnalysis", "DistancePlot", "Structure", "StructureSource" })
                StringAssert.Contains(init, name, "__init__ should export " + name);
        }

        // ---- the reproducibility half ---------------------------------------------------------------

        [TestMethod]
        public void ItWritesTheDataBehindItsFigure()
        {
            // A figure a reader cannot check is a picture. The CSV is what makes it a result.
            string plot = File.ReadAllText(Path.Combine(Generate(), "ubiquitin_study", "plot.py"));

            StringAssert.Contains(plot, "def write_csv", "outside a class this would be a rule violation");
            StringAssert.Contains(plot, "chain,resi,resn,distance_angstrom");
        }

        [TestMethod]
        public void AMissingPlottingLibraryIsReportedRatherThanRaised()
        {
            // matplotlib is declared, but its absence must not make the analysis look as if it
            // failed - the numbers are the result and they are already written.
            string plot = File.ReadAllText(Path.Combine(Generate(), "ubiquitin_study", "plot.py"));

            StringAssert.Contains(plot, "except ImportError");
            StringAssert.Contains(plot, "pip install matplotlib");
        }

        [TestMethod]
        public void TheProjectTellsTheReaderHowToReproduceItWithoutTheIde()
        {
            // The point of the command lines being in the project: a reviewer with the archive and
            // no IDE session can still regenerate what the paper shows.
            string reproduce = File.ReadAllText(Path.Combine(Generate(), "REPRODUCE.md"));

            StringAssert.Contains(reproduce, "helix_cli dssp");
            StringAssert.Contains(reproduce, "helix_cli query");
            StringAssert.Contains(reproduce, "--frames");
            StringAssert.Contains(reproduce, "environment.lock.toml",
                "the environment capture is half of the reproducibility claim");
        }

        [TestMethod]
        public void TheDownloadIsCheckedBeforeItIsCached()
        {
            // A captive portal or an error page cached under the accession's name would fail later,
            // somewhere that looks unrelated to the download.
            string structure = File.ReadAllText(Path.Combine(Generate(), "ubiquitin_study", "structure.py"));

            StringAssert.Contains(structure, "ATOM");
            StringAssert.Contains(structure, "did not return a PDB structure");
        }
    }
}
