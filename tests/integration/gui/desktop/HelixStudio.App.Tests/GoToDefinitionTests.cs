using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 347 (workflow audit): F12 — the most-used navigation gesture in any IDE — was wired to a
    /// bare-word handler that matched the identifier against CLASS names only and jumped to the
    /// diagram. On a function, a method, or an ambiguous name it did nothing at all, and printed
    /// nothing, so the user could not tell a broken IDE from an unresolvable symbol. It now runs the
    /// caret through the same SymbolResolver the right-click menu uses, lands on the definition in
    /// source, and reports every miss in the status bar.
    ///
    /// These tests drive NavigationCoordinator directly — the editor half (Scintilla) needs the
    /// native control, but the resolution and feedback logic is where the defect lived.
    /// </summary>
    [TestClass]
    public class GoToDefinitionTests
    {
        private string _dir;

        [TestInitialize]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "helix_f12_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        /// <summary>
        /// The regression that motivated the change: a module-level function. The old bare-word
        /// implementation searched types only, so this navigated nowhere.
        /// </summary>
        [TestMethod]
        public void F12OnAFunctionCall_OpensTheFunctionsDefinition()
        {
            string file = Write("app.py",
                "def compute(x):",
                "    return x + 1",
                "",
                "result = compute(3)");

            using (var form = TestMainForm.CreateComposed())
            {
                ProjectModel project = Attach(form, file);
                project.Functions.Add(new FunctionModel
                {
                    Name = "compute",
                    Namespace = "app",
                    SourceFilePath = file,
                    SourceLineNumber = 1,
                    Project = project,
                });

                var args = Invoke(form, file, line: 4, column: 10, token: "compute");

                Assert.IsTrue(args.Handled,
                    "F12 on a call to a module-level function must navigate to its definition");
            }
        }

        [TestMethod]
        public void F12OnAClass_OpensItsSource_NotTheDiagram()
        {
            string file = Write("shapes.py",
                "class Circle:",
                "    pass",
                "",
                "c = Circle()");

            using (var form = TestMainForm.CreateComposed())
            {
                ProjectModel project = Attach(form, file);
                var type = new TypeModel("Circle", "shapes", TypeKind.ConcreteClass, project) { SourceFilePath = file };
                project.Types.Add(type);

                var args = Invoke(form, file, line: 4, column: 5, token: "Circle");

                Assert.IsTrue(args.Handled, "F12 on a class must resolve to its definition");
                StringAssert.Contains(Status(form), "shapes.py",
                    "F12 means 'go to the definition in source', so the status must name the source file");
            }
        }

        [TestMethod]
        public void ModuleVariable_ContextMenuOffersDeclarationAndSolutionRename()
        {
            string file = Write("incidence.py",
                "SENSITIVITY = {}",
                "",
                "print(SENSITIVITY)");

            using (var form = TestMainForm.CreateComposed())
            {
                Attach(form, file);
                var args = new HelixPythonNamespace.Editor.EditorSymbolRequestArgs
                {
                    FilePath = file,
                    Line = 3,
                    Column = 8,
                    Token = "SENSITIVITY",
                };

                form.OnEditorResolveSymbolRequested(null, args);

                CollectionAssert.Contains(args.Items.Select(i => i.Label).ToList(),
                    "Go to variable declaration");
                CollectionAssert.Contains(args.Items.Select(i => i.Label).ToList(),
                    "Rename Symbolâ€¦");
            }
        }

        [TestMethod]
        public void F12OnModuleVariable_UsesItsAssignmentAsDefinition()
        {
            string file = Write("incidence.py",
                "SENSITIVITY = {}",
                "",
                "print(SENSITIVITY)");

            using (var form = TestMainForm.CreateComposed())
            {
                Attach(form, file);
                var args = Invoke(form, file, line: 3, column: 8, token: "SENSITIVITY");
                Assert.IsTrue(args.Handled, "a module variable assignment is a navigable definition");
            }
        }

        [TestMethod]
        public void ImportedModuleAttribute_ContextMenuOffersDeclarationAndRename()
        {
            Write("invariants.py", "GAMMA: tuple = ()");
            string incidence = Write("incidence.py",
                "from . import invariants as _inv",
                "",
                "for inv in _inv.GAMMA:",
                "    pass");

            using (var form = TestMainForm.CreateComposed())
            {
                Attach(form, incidence);
                var args = new HelixPythonNamespace.Editor.EditorSymbolRequestArgs
                {
                    FilePath = incidence,
                    Line = 3,
                    Column = 21,
                    Token = "GAMMA",
                };

                form.OnEditorResolveSymbolRequested(null, args);

                CollectionAssert.Contains(args.Items.Select(i => i.Label).ToList(),
                    "Go to variable declaration");
                CollectionAssert.Contains(args.Items.Select(i => i.Label).ToList(),
                    "Rename Symbolâ€¦");
            }
        }

        [TestMethod]
        public void F12OnImportedModuleAttribute_UsesExportingModulesAssignment()
        {
            Write("invariants.py", "GAMMA: tuple = ()");
            string incidence = Write("incidence.py",
                "from . import invariants as _inv",
                "for inv in _inv.GAMMA:",
                "    pass");

            using (var form = TestMainForm.CreateComposed())
            {
                Attach(form, incidence);
                var args = Invoke(form, incidence, line: 2, column: 19, token: "GAMMA");
                Assert.IsTrue(args.Handled,
                    "a qualified user-defined export must navigate through its import alias");
            }
        }

        /// <summary>
        /// The silent-failure case. Doing nothing is indistinguishable from a hung IDE, so an
        /// unresolvable token has to say so.
        /// </summary>
        [TestMethod]
        public void F12OnAnUnknownSymbol_SaysSoInsteadOfDoingNothing()
        {
            string file = Write("app.py", "value = nowhere_at_all");

            using (var form = TestMainForm.CreateComposed())
            {
                Attach(form, file);

                var args = Invoke(form, file, line: 1, column: 9, token: "nowhere_at_all");

                Assert.IsFalse(args.Handled, "nothing resolves, so nothing should be navigated to");
                StringAssert.Contains(Status(form), "nowhere_at_all",
                    "the user must be told which symbol could not be found");
            }
        }

        [TestMethod]
        public void F12WithNoSolutionOpen_ExplainsWhyRatherThanFailingSilently()
        {
            string file = Write("app.py", "x = 1");

            using (var form = TestMainForm.CreateComposed())
            {
                typeof(MainForm).GetField("_currentSolution", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(form, null);

                var args = Invoke(form, file, line: 1, column: 1, token: "x");

                Assert.IsFalse(args.Handled);
                StringAssert.Contains(Status(form), "solution",
                    "with nothing loaded there is no index to search — say that");
            }
        }

        // ------------------------------------------------------------------ helpers

        private string Write(string name, params string[] lines)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, string.Join(Environment.NewLine, lines));
            return path;
        }

        // Builds a one-project solution rooted at the temp dir and installs it on the form.
        private ProjectModel Attach(MainForm form, string anyFileInProject)
        {
            string dir = Path.GetDirectoryName(anyFileInProject);
            var solution = new SolutionModel(Path.Combine(dir, "pyproject.toml"), "Fixture");
            var project = new ProjectModel(Path.Combine(dir, "pyproject.toml"), "Fixture", solution);
            solution.Projects.Add(project);
            typeof(MainForm).GetField("_currentSolution", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(form, solution);
            return project;
        }

        private static HelixPythonNamespace.Editor.EditorGoToDefinitionArgs Invoke(
            MainForm form, string file, int line, int column, string token)
        {
            var args = new HelixPythonNamespace.Editor.EditorGoToDefinitionArgs
            {
                FilePath = file,
                Line = line,
                Column = column,
                Token = token,
            };
            form.OnEditorGoToDefinitionRequested(null, args);
            return args;
        }

        private static string Status(MainForm form)
        {
            var label = (ToolStripStatusLabel)typeof(MainForm)
                .GetField("_statusLabel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .GetValue(form);
            return label != null ? (label.Text ?? string.Empty) : string.Empty;
        }
    }
}
