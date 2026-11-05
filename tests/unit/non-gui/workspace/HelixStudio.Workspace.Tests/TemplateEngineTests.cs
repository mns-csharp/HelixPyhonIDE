using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Templates;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// New Project/Solution Wizard — engine-first (SRS §9/§14). Covers the data-driven catalog, the
    /// search/filter layer (substring/acronym/language), and the transactional generator (placeholder
    /// substitution + rollback), all independent of the WinForms shell.
    /// </summary>
    [TestClass]
    public class TemplateEngineTests
    {
        [TestMethod]
        public void Catalog_BuiltIn_HasPerLanguageTemplates_AndFacets()
        {
            var c = TemplateCatalog.BuiltIn();
            Assert.IsTrue(c.Count >= 6, "expected several built-in templates");
            var langs = c.Facet("Language");
            CollectionAssert.Contains(langs, "Python");
            CollectionAssert.Contains(langs, "CUDA C");
            CollectionAssert.Contains(langs, "C++");
            Assert.IsNotNull(c.ById("py.console"));
        }

        [TestMethod]
        public void Search_Cuda_ReturnsCudaTemplates_RankedFirst()
        {
            var c = TemplateCatalog.BuiltIn();
            var hits = TemplateSearchEngine.Search(c, "cuda");
            Assert.IsTrue(hits.Count >= 2);
            foreach (var h in hits) Assert.AreEqual("CUDA C", h.Template.Language, "cuda query should only return CUDA templates");
        }

        [TestMethod]
        public void Search_Acronym_cmd_FindsCudaMolecularDynamics()
        {
            var c = TemplateCatalog.BuiltIn();
            var hits = TemplateSearchEngine.Search(c, "cmd");
            Assert.IsTrue(hits.Count >= 1);
            Assert.AreEqual("cuda.mdsim", hits[0].Template.Id, "acronym 'cmd' should rank CUDA Molecular Dynamics first");
        }

        [TestMethod]
        public void Search_EmptyQuery_ReturnsAll_NameOrdered()
        {
            var c = TemplateCatalog.BuiltIn();
            var hits = TemplateSearchEngine.Search(c, "");
            Assert.AreEqual(c.Count, hits.Count);
        }

        [TestMethod]
        public void Search_LanguageFilter_ConstrainsResults()
        {
            var c = TemplateCatalog.BuiltIn();
            var hits = TemplateSearchEngine.Search(c, "", new TemplateFilter { Language = "C++" });
            Assert.IsTrue(hits.Count >= 1);
            foreach (var h in hits) Assert.AreEqual("C++", h.Template.Language);
        }

        [TestMethod]
        public void Generate_CreatesFiles_AndSubstitutesProjectName()
        {
            var c = TemplateCatalog.BuiltIn();
            string root = TempDir();
            try
            {
                var opt = new GenerationOptions { TargetRoot = root };
                opt.Variables["ProjectName"] = "Widget";
                var r = ProjectGenerator.Generate(c.ById("py.console"), opt);
                Assert.IsTrue(r.Success, "generation should succeed: " + r.Error);

                string main = Path.Combine(root, "Widget", "main.py");
                Assert.IsTrue(File.Exists(main), "main.py should exist under the substituted project dir");
                StringAssert.Contains(File.ReadAllText(main), "Hello from Widget");
                Assert.IsTrue(File.Exists(Path.Combine(root, "Widget", "pyproject.toml")));
            }
            finally { SafeDelete(root); }
        }

        [TestMethod]
        public void Generate_RollsBack_OnConflict_NoPartialProject()
        {
            var c = TemplateCatalog.BuiltIn();
            string root = TempDir();
            try
            {
                // Pre-create a conflicting file so the second file write fails (Overwrite=false).
                Directory.CreateDirectory(Path.Combine(root, "Widget"));
                File.WriteAllText(Path.Combine(root, "Widget", "pyproject.toml"), "PRE-EXISTING");

                var opt = new GenerationOptions { TargetRoot = root };
                opt.Variables["ProjectName"] = "Widget";
                var r = ProjectGenerator.Generate(c.ById("py.console"), opt);

                Assert.IsFalse(r.Success, "generation should fail on the pre-existing file");
                // main.py was written first, then the conflict rolled everything back.
                Assert.IsFalse(File.Exists(Path.Combine(root, "Widget", "main.py")), "rollback must remove the file it created");
                Assert.AreEqual("PRE-EXISTING", File.ReadAllText(Path.Combine(root, "Widget", "pyproject.toml")), "pre-existing file must be untouched");
            }
            finally { SafeDelete(root); }
        }

        [TestMethod]
        public void ConfigPages_Declared_AndOverrideFeedsGeneration()
        {
            var c = TemplateCatalog.BuiltIn();
            var cuda = c.ById("cuda.console");
            Assert.IsTrue(cuda.ConfigPages.Count >= 1, "CUDA template should declare a config page");
            Assert.AreEqual("86", cuda.ConfigDefaults()["CudaArch"], "default GPU arch");

            string root = TempDir();
            try
            {
                var opt = new GenerationOptions { TargetRoot = root };
                opt.Variables["ProjectName"] = "Sim";
                opt.Variables["CudaArch"] = "90";   // user picked sm_90 on the CUDA options page
                var r = ProjectGenerator.Generate(cuda, opt);
                Assert.IsTrue(r.Success, r.Error);
                StringAssert.Contains(File.ReadAllText(Path.Combine(root, "Sim", "CMakeLists.txt")), "CUDA_ARCHITECTURES 90");
            }
            finally { SafeDelete(root); }
        }

        [TestMethod]
        public void ConfigDefault_UsedWhenNotOverridden()
        {
            var c = TemplateCatalog.BuiltIn();
            string root = TempDir();
            try
            {
                var opt = new GenerationOptions { TargetRoot = root };
                opt.Variables["ProjectName"] = "App";   // PythonVersion not supplied -> config default 3.11
                var r = ProjectGenerator.Generate(c.ById("py.console"), opt);
                Assert.IsTrue(r.Success, r.Error);
                StringAssert.Contains(File.ReadAllText(Path.Combine(root, "App", "pyproject.toml")), "requires-python = \">=3.11\"");
            }
            finally { SafeDelete(root); }
        }

        [TestMethod]
        public void Loader_DiscoversOnDiskTemplate_WithConfigPage_AndSourceFile()
        {
            string root = TempDir();
            try
            {
                string tdir = Path.Combine(root, "flask");
                Directory.CreateDirectory(Path.Combine(tdir, "files"));
                File.WriteAllText(Path.Combine(tdir, "files", "app.py"), "# {{ProjectName}} on port {{Port}}\n");
                File.WriteAllText(Path.Combine(tdir, "template.json"),
                    "{\"id\":\"user.flask\",\"name\":\"Flask Web App\",\"language\":\"Python\",\"framework\":\"Flask\"," +
                    "\"projectType\":\"Web\",\"tags\":[\"flask\",\"web\"]," +
                    "\"configPages\":[{\"title\":\"Flask\",\"fields\":[{\"name\":\"Port\",\"label\":\"Port\",\"type\":\"text\",\"default\":\"5000\"}]}]," +
                    "\"files\":[{\"path\":\"{{ProjectName}}/app.py\",\"source\":\"files/app.py\"}]}");

                var c = TemplateCatalog.BuiltIn();
                var errs = c.DiscoverFrom(root, "user");
                Assert.AreEqual(0, errs.Count, "no load errors expected");
                var flask = c.ById("user.flask");
                Assert.IsNotNull(flask, "the on-disk template should be discovered");
                Assert.AreEqual("Flask", flask.Framework);
                Assert.AreEqual("user", flask.InstalledBy);
                Assert.AreEqual(1, flask.ConfigPages.Count);
                Assert.AreEqual("5000", flask.ConfigDefaults()["Port"]);

                // It generates like any built-in template, using the source-file body.
                string outRoot = TempDir();
                try
                {
                    var opt = new GenerationOptions { TargetRoot = outRoot };
                    opt.Variables["ProjectName"] = "Site"; opt.Variables["Port"] = "8080";
                    var r = ProjectGenerator.Generate(flask, opt);
                    Assert.IsTrue(r.Success, r.Error);
                    StringAssert.Contains(File.ReadAllText(Path.Combine(outRoot, "Site", "app.py")), "Site on port 8080");
                }
                finally { SafeDelete(outRoot); }
            }
            finally { SafeDelete(root); }
        }

        [TestMethod]
        public void Loader_SkipsMalformedManifest_WithoutThrowing()
        {
            string root = TempDir();
            try
            {
                string bad = Path.Combine(root, "broken");
                Directory.CreateDirectory(bad);
                File.WriteAllText(Path.Combine(bad, "template.json"), "{ this is not valid json ");
                var loader = new TemplateLoader();
                var found = loader.LoadFrom(root, "user");
                Assert.AreEqual(0, found.Count);
                Assert.AreEqual(1, loader.Errors.Count, "the malformed manifest should be recorded as an error, not thrown");
            }
            finally { SafeDelete(root); }
        }

        [TestMethod]
        public void PostGen_EmitsReadmeGitignoreLicense_ByLanguage()
        {
            string root = TempDir();
            try
            {
                var reqs = new System.Collections.Generic.List<PostGenRequest>
                {
                    new PostGenRequest { Type = PostGenActionType.Readme, ProjectName = "Widget", Description = "A gadget." },
                    new PostGenRequest { Type = PostGenActionType.Gitignore, Language = "Python" },
                    new PostGenRequest { Type = PostGenActionType.License, Author = "Ada", Year = DateTime.UtcNow.Year },
                };
                var rep = PostGenRunner.Run(root, reqs);
                Assert.IsFalse(rep.AnyFailed, "file-emitting actions should all succeed");

                StringAssert.Contains(File.ReadAllText(Path.Combine(root, "README.md")), "# Widget");
                StringAssert.Contains(File.ReadAllText(Path.Combine(root, "README.md")), "A gadget.");
                StringAssert.Contains(File.ReadAllText(Path.Combine(root, ".gitignore")), "__pycache__/");
                string lic = File.ReadAllText(Path.Combine(root, "LICENSE"));
                StringAssert.Contains(lic, "MIT License");
                StringAssert.Contains(lic, DateTime.UtcNow.Year + " Ada");
            }
            finally { SafeDelete(root); }
        }

        [TestMethod]
        public void PostGen_Gitignore_IsLanguageSpecific()
        {
            StringAssert.Contains(PostGenRunner.GitignoreText("Rust"), "/target");
            StringAssert.Contains(PostGenRunner.GitignoreText("C#"), "obj/");
            StringAssert.Contains(PostGenRunner.GitignoreText("CUDA C"), "*.obj");
        }

        [TestMethod]
        public void PostGen_RestoreCommand_IsLanguageSpecific()
        {
            // Only Python has a restore this product runs; cargo/dotnet/Pkg went with their products.
            Assert.AreEqual("pip", PostGenRunner.RestoreCommand("Python")[0]);
            Assert.IsNull(PostGenRunner.RestoreCommand("TOML"), "no restore for a data language");
        }

        [TestMethod]
        public void UsageStore_PersistsUsageAndFavorites_AcrossLoad()
        {
            string path = Path.Combine(TempDir(), "usage.json");
            try
            {
                var s = TemplateUsageStore.Load(path);
                DateTime first = DateTime.UtcNow.AddMinutes(-2);
                DateTime second = first.AddMinutes(1);
                s.RecordUse("py.console", first);
                s.RecordUse("py.console", second);
                s.ToggleFavorite("cpp.console");
                s.Save();

                var s2 = TemplateUsageStore.Load(path);   // fresh load = a new session
                Assert.AreEqual(2, s2.Count("py.console"));
                Assert.AreEqual(second, s2.LastUsed("py.console"));
                Assert.IsTrue(s2.IsFavorite("cpp.console"));
                Assert.IsFalse(s2.IsFavorite("py.console"));
            }
            finally { SafeDelete(Path.GetDirectoryName(path)); }
        }

        [TestMethod]
        public void UsageStore_Recent_OrdersMostRecentFirst()
        {
            var s = TemplateUsageStore.Load(null);   // in-memory only (no path)
            DateTime now = DateTime.UtcNow;
            s.RecordUse("a", now.AddMinutes(-3));
            s.RecordUse("b", now);
            s.RecordUse("c", now.AddMinutes(-1));
            CollectionAssert.AreEqual(new[] { "b", "c", "a" }, s.Recent(10).ToArray());
        }

        [TestMethod]
        public void UsageStore_CorruptFile_LoadsEmpty_NoThrow()
        {
            string path = Path.Combine(TempDir(), "usage.json");
            try
            {
                File.WriteAllText(path, "{ not valid json");
                var s = TemplateUsageStore.Load(path);
                Assert.AreEqual(0, s.Count("anything"));
            }
            finally { SafeDelete(Path.GetDirectoryName(path)); }
        }

        [TestMethod]
        public void Substitute_LeavesUnknownTokensIntact()
        {
            var vars = new Dictionary<string, string> { { "ProjectName", "Foo" } };
            Assert.AreEqual("Foo/{{Unknown}}", ProjectGenerator.Substitute("{{ProjectName}}/{{Unknown}}", vars));
        }

        // Iter 245: the wizard must cover every Python kind the legacy "New Solution…" dialog offered
        // (blank / class library / GUI app / Flask) before that dialog can be retired. Each new template
        // must anchor a pyproject.toml at {{ProjectName}}/pyproject.toml so the wizard opens it.
        [TestMethod]
        public void Catalog_HasPythonKinds_ForNewSolutionParity()
        {
            var c = TemplateCatalog.BuiltIn();
            foreach (string id in new[] { "py.blank", "py.classlib", "py.gui.tk", "py.flask" })
            {
                var t = c.ById(id);
                Assert.IsNotNull(t, id + " template should exist for wizard parity");
                Assert.AreEqual("Python", t.Language, id + " is a Python template");
                bool hasPyproject = false;
                foreach (var f in t.Files)
                    if (f.RelativePath == "{{ProjectName}}/pyproject.toml") { hasPyproject = true; break; }
                Assert.IsTrue(hasPyproject, id + " must anchor a pyproject.toml so the wizard can open it");
            }
        }

        [TestMethod]
        public void Generate_BlankSolution_ProducesOnlyAnOpenablePyproject()
        {
            var c = TemplateCatalog.BuiltIn();
            string root = TempDir();
            try
            {
                var opt = new GenerationOptions { TargetRoot = root };
                opt.Variables["ProjectName"] = "Empty1";   // PythonVersion falls back to config default 3.11
                var r = ProjectGenerator.Generate(c.ById("py.blank"), opt);
                Assert.IsTrue(r.Success, r.Error);

                string proj = Path.Combine(root, "Empty1");
                string pyproject = Path.Combine(proj, "pyproject.toml");
                Assert.IsTrue(File.Exists(pyproject), "blank solution is just a pyproject.toml");
                StringAssert.Contains(File.ReadAllText(pyproject), "name = \"Empty1\"");
                StringAssert.Contains(File.ReadAllText(pyproject), "requires-python = \">=3.11\"");
                Assert.AreEqual(1, Directory.GetFiles(proj).Length, "blank solution writes no source files, only pyproject.toml");
            }
            finally { SafeDelete(root); }
        }

        [TestMethod]
        public void Generate_FlaskAndLibrary_ProduceTheirSignatureFiles()
        {
            var c = TemplateCatalog.BuiltIn();
            string root = TempDir();
            try
            {
                var flaskOpt = new GenerationOptions { TargetRoot = root };
                flaskOpt.Variables["ProjectName"] = "Site";
                Assert.IsTrue(ProjectGenerator.Generate(c.ById("py.flask"), flaskOpt).Success);
                string app = Path.Combine(root, "Site", "app.py");
                Assert.IsTrue(File.Exists(app));
                StringAssert.Contains(File.ReadAllText(app), "from flask import Flask");
                StringAssert.Contains(File.ReadAllText(Path.Combine(root, "Site", "pyproject.toml")), "flask>=3.0");

                var libOpt = new GenerationOptions { TargetRoot = root };
                libOpt.Variables["ProjectName"] = "Kit";
                Assert.IsTrue(ProjectGenerator.Generate(c.ById("py.classlib"), libOpt).Success);
                // The template and Add Class share this direct package root; no parallel
                // src-based package should appear later.
                Assert.IsTrue(File.Exists(Path.Combine(root, "Kit", "Kit", "__init__.py")),
                    "class library ships a flat importable package");
                Assert.IsFalse(Directory.Exists(Path.Combine(root, "Kit", "src")),
                    "new Python projects must not add an extraneous src grouping folder");
            }
            finally { SafeDelete(root); }
        }

        private static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "helix_tmpl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }
        private static void SafeDelete(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
    }
}
