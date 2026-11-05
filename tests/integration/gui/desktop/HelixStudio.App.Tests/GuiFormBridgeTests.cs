using HelixPythonNamespace.AddIns.GuiDesigner;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;
using GuiForm = HelixPythonNamespace.GuiDesigner.Model.GuiForm;
using GuiFramework = HelixPythonNamespace.GuiDesigner.Model.GuiFramework;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class GuiFormBridgeTests
    {
        private static ProjectModel MakeProject()
        {
            var sln = new SolutionModel(@"C:\fake\t.helixsln", "t");
            var proj = new ProjectModel(@"C:\fake\t.toml", "app_gui", sln);
            sln.Projects.Add(proj);
            return proj;
        }

        [TestMethod]
        public void DirectTkinterBase_IsDetectedAsTkinter()
        {
            ProjectModel project = MakeProject();
            var mainWindow = new TypeModel("MainWindow", "app_gui.main", TypeKind.ConcreteClass, project);
            mainWindow.BaseTypes.Add(new TypeReference("tk.Tk", RelationshipKind.Inherits));
            project.Types.Add(mainWindow);

            Assert.IsTrue(GuiFormBridge.IsGuiFormType(mainWindow));
            Assert.AreEqual(GuiFramework.Tkinter, GuiFormBridge.DetectFramework(mainWindow));
        }

        [TestMethod]
        public void PlainDomainClass_IsNotAGuiForm()
        {
            ProjectModel project = MakeProject();
            var notAForm = new TypeModel("Animal", "app_gui.main", TypeKind.ConcreteClass, project);
            project.Types.Add(notAForm);

            Assert.IsFalse(GuiFormBridge.IsGuiFormType(notAForm));
            Assert.IsNull(GuiFormBridge.DetectFramework(notAForm));
        }

        [TestMethod]
        public void TransitiveBase_ThroughAnInSolutionIntermediateClass_IsStillDetected()
        {
            ProjectModel project = MakeProject();
            var baseWindow = new TypeModel("BaseWindow", "app_gui.main", TypeKind.ConcreteClass, project);
            baseWindow.BaseTypes.Add(new TypeReference("tk.Tk", RelationshipKind.Inherits));
            project.Types.Add(baseWindow);

            var mainWindow = new TypeModel("MainWindow", "app_gui.main", TypeKind.ConcreteClass, project);
            mainWindow.BaseTypes.Add(new TypeReference(baseWindow.FullyQualifiedName, RelationshipKind.Inherits));
            project.Types.Add(mainWindow);

            var sln = project.Solution;
            foreach (TypeReference r in mainWindow.BaseTypes) r.Bind(sln);
            foreach (TypeReference r in baseWindow.BaseTypes) r.Bind(sln);

            Assert.IsTrue(GuiFormBridge.IsGuiFormType(mainWindow));
            Assert.AreEqual(GuiFramework.Tkinter, GuiFormBridge.DetectFramework(mainWindow));
        }

        [TestMethod]
        public void BuildFormFor_UsesTheClassNameAndDetectedFramework()
        {
            ProjectModel project = MakeProject();
            var mainWindow = new TypeModel("MainWindow", "app_gui.main", TypeKind.ConcreteClass, project);
            mainWindow.BaseTypes.Add(new TypeReference("tk.Tk", RelationshipKind.Inherits));
            project.Types.Add(mainWindow);

            GuiForm form = GuiFormBridge.BuildFormFor(mainWindow);

            Assert.AreEqual("MainWindow", form.FormClassName);
            Assert.AreEqual(GuiFramework.Tkinter, form.Framework);
            Assert.AreEqual(0, form.Controls.Count);
        }

        [TestMethod]
        public void QtShortName_DefaultsToPyQt6WhenFrameworkIsAmbiguous()
        {
            ProjectModel project = MakeProject();
            var mainWindow = new TypeModel("MainWindow", "app_gui.main", TypeKind.ConcreteClass, project);
            mainWindow.BaseTypes.Add(new TypeReference("QMainWindow", RelationshipKind.Inherits));
            project.Types.Add(mainWindow);

            Assert.AreEqual(GuiFramework.PyQt6, GuiFormBridge.DetectFramework(mainWindow));
        }

        [TestMethod]
        public void BuildFormFor_PrefersTheProjectsExplicitGuiFrameworkSetting_OverBaseClassDetection()
        {
            // A GuiApp project scaffolded for PySide6 whose class happens to subclass the bare
            // "Frame" short name (ambiguous — could read as Tkinter) should still honor the
            // project's own explicit, persisted choice rather than the class-level guess.
            ProjectModel project = MakeProject();
            project.Kind = ProjectKind.GuiApp;
            project.GuiFramework = HelixPythonNamespace.Core.Model.Enums.GuiFramework.PySide6;

            var mainWindow = new TypeModel("MainWindow", "app_gui.main", TypeKind.ConcreteClass, project);
            mainWindow.BaseTypes.Add(new TypeReference("Frame", RelationshipKind.Inherits));
            project.Types.Add(mainWindow);

            GuiForm form = GuiFormBridge.BuildFormFor(mainWindow);

            Assert.AreEqual(GuiFramework.PySide6, form.Framework);
        }

    }
}
