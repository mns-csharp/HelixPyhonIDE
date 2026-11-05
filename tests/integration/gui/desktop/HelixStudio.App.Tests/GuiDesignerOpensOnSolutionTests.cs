using HelixPythonNamespace.AddIns.GuiDesigner;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The GUI Designer Canvas opens on the SOLUTION, not on a fabricated empty form.
    ///
    /// Before this it knew nothing about what was loaded: from the Tools menu with no active
    /// document it made up a form named after nothing, while the solution's real windows sat in the
    /// tree behind a context menu the user had to know about.
    ///
    /// A solution with several windows is an ordinary application, so the user chooses which form
    /// to open.
    /// </summary>
    [TestClass]
    public class GuiDesignerOpensOnSolutionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static object Get(object owner, string field)
        { return owner.GetType().GetField(field, Private).GetValue(owner); }

        private static ProjectModel GuiProject(SolutionModel solution, string name)
        {
            var project = new ProjectModel(
                Path.Combine(@"C:\ws", name, "pyproject.toml"), name, solution)
            {
                Kind = ProjectKind.GuiApp,
                GuiFramework = HelixPythonNamespace.Core.Model.Enums.GuiFramework.Tkinter,
            };
            solution.Projects.Add(project);
            return project;
        }

        private static ProjectModel Library(SolutionModel solution, string name)
        {
            var project = new ProjectModel(
                Path.Combine(@"C:\ws", name, "pyproject.toml"), name, solution)
            {
                Kind = ProjectKind.ClassLibrary,
            };
            solution.Projects.Add(project);
            return project;
        }

        /// <summary>
        /// A window as the PARSER would leave one: a class that inherits a framework base. Being in
        /// a GUI project is not what makes a class a window — a GUI project also holds its plain
        /// classes — so the base type is what is written here.
        /// </summary>
        private static TypeModel Window(ProjectModel project, string name)
        {
            TypeModel type = PlainClass(project, name);
            type.BaseTypes.Add(new TypeReference("tkinter.Tk", RelationshipKind.Inherits));
            return type;
        }

        private static TypeModel PlainClass(ProjectModel project, string name)
        {
            var type = new TypeModel(name, project.Name, TypeKind.ConcreteClass, project)
            {
                SourceFilePath = Path.Combine(
                    project.ProjectDirectory, name.ToLowerInvariant() + ".py"),
            };
            project.Types.Add(type);
            return type;
        }

        private static MainForm Shell(SolutionModel solution)
        {
            MainForm form = TestMainForm.CreateComposed();
            FixtureSolution.RealizeHeadless(form);
            form._currentSolution = solution;
            return form;
        }

        private static string Describe(MainForm form)
        {
            var status = (ToolStripStatusLabel)Get(form, "_statusLabel");
            return status.Text;
        }

        /// <summary>
        /// The forms the designer finds are the ones the TREE would call forms — one test for what
        /// counts as a window, so the two surfaces cannot disagree about it.
        /// </summary>
        [TestMethod]
        public void TheFormsFoundAreTheOnesTheTreeCallsForms()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            ProjectModel gui = GuiProject(solution, "app_gui");
            TypeModel main = Window(gui, "MainWindow");
            TypeModel settings = Window(gui, "SettingsDialog");

            // A plain class that inherits nothing framework-shaped is not a window.
            TypeModel helper = PlainClass(Library(solution, "lib_core"), "Helper");

            MainForm form = Shell(solution);
            try
            {
                List<TypeModel> found = form.AddIn<GuiDesignerAddIn>().GuiFormsInSolution();
                CollectionAssert.AreEquivalent(new[] { main, settings }, found,
                    "found: " + string.Join(", ", found.Select(t => t.Name).ToArray()));

                foreach (TypeModel type in found)
                    Assert.IsTrue(GuiFormBridge.IsGuiFormType(type),
                        type.Name + " is not what the tree calls a form");
                Assert.IsFalse(found.Contains(helper), "a library class was taken for a window");
            }
            finally { form.Dispose(); }
        }

        /// <summary>A solution with one window opens on that window, and says so.</summary>
        [TestMethod]
        public void OneFormOpensThatForm()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            Window(GuiProject(solution, "app_gui"), "MainWindow");

            MainForm form = Shell(solution);
            try
            {
                form.AddIn<GuiDesignerAddIn>().ShowGuiDesignerOnSolution();
                StringAssert.Contains(Describe(form), "MainWindow", Describe(form));
                StringAssert.Contains(Describe(form), "only form", Describe(form));
            }
            finally { form.Dispose(); }
        }

        /// <summary>
        /// Several windows is an application, not a fault. The status line says which of how many
        /// was opened — and says nothing about a rule, because none was broken.
        /// </summary>
        [TestMethod]
        public void SeveralFormsAreAChoiceAndNotAViolation()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            ProjectModel gui = GuiProject(solution, "app_gui");
            Window(gui, "MainWindow");
            Window(gui, "SettingsDialog");
            Window(gui, "AboutBox");

            MainForm form = Shell(solution);
            try
            {
                form.AddIn<GuiDesignerAddIn>().ShowGuiDesignerOnSolution();          // headless: takes the first, no modal
                string said = Describe(form);
                StringAssert.Contains(said, "one of 3 forms", said);
                foreach (string alarm in new[] { "may contain", "violation", "at most one" })
                    Assert.IsTrue(said.IndexOf(alarm, StringComparison.OrdinalIgnoreCase) < 0,
                        "several windows is an ordinary application and must not be reported as a "
                        + "problem: " + said);
            }
            finally { form.Dispose(); }
        }

        /// <summary>
        /// A solution with no window still opens on something to start from, and says why — an
        /// empty canvas is the one outcome that teaches nothing.
        /// </summary>
        [TestMethod]
        public void NoFormStillOpensOnAStartingPoint()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            PlainClass(Library(solution, "lib_core"), "Helper");

            MainForm form = Shell(solution);
            try
            {
                form.AddIn<GuiDesignerAddIn>().ShowGuiDesignerOnSolution();
                StringAssert.Contains(Describe(form), "No form in this solution", Describe(form));
            }
            finally { form.Dispose(); }
        }

        /// <summary>With no solution loaded it still opens, and says that is why.</summary>
        [TestMethod]
        public void NoSolutionStillOpensOnTheSample()
        {
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                FixtureSolution.RealizeHeadless(form);
                form.AddIn<GuiDesignerAddIn>().ShowGuiDesignerOnSolution();
                StringAssert.Contains(Describe(form), "No solution open", Describe(form));
            }
            finally { form.Dispose(); }
        }
    }
}
