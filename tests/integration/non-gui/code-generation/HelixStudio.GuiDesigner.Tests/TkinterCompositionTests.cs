namespace HelixPythonNamespace.GuiDesigner.Tests
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.GuiDesigner.Emit;
    using HelixPythonNamespace.GuiDesigner.Model;

    // Exercises the tree-aware emission added for "MainWindow"-shaped forms: a TabControl (Notebook)
    // holding CustomComponent children (instances of a locally-defined class, e.g. ShapePanel), where
    // children are composed via .add(...) rather than absolute .place(...).
    [TestClass]
    public class TkinterCompositionTests
    {
        private static GuiForm NotebookWithCustomPanels()
        {
            var f = new GuiForm { FormClassName = "MainWindow", Title = "Demo", Width = 420, Height = 320, Framework = GuiFramework.Tkinter };
            var notebook = new GuiControl { Id = "nb1", Kind = GuiControlKind.TabControl, Name = "notebook", Width = 400, Height = 300 };
            f.Controls.Add(notebook);

            var shapePanel = new GuiControl { Id = "sp1", Kind = GuiControlKind.CustomComponent, Name = "shape_panel", ParentId = "nb1" };
            shapePanel.Props["ClassName"] = "ShapePanel";
            shapePanel.Props["Module"] = "app_gui.views";
            shapePanel.Props["TabTitle"] = "Shapes";
            f.Controls.Add(shapePanel);

            var animalPanel = new GuiControl { Id = "ap1", Kind = GuiControlKind.CustomComponent, Name = "animal_panel", ParentId = "nb1" };
            animalPanel.Props["ClassName"] = "AnimalPanel";
            animalPanel.Props["Module"] = "app_gui.views";
            animalPanel.Props["TabTitle"] = "Animals";
            f.Controls.Add(animalPanel);

            return f;
        }

        [TestMethod]
        public void NotebookChildren_AreAddedAsTabs_NotPlaced()
        {
            string py = new TkinterEmitter().Emit(NotebookWithCustomPanels(), "");

            Assert.IsTrue(py.Contains("self.notebook = ttk.Notebook(self)"), py);
            Assert.IsTrue(py.Contains("self.shape_panel = ShapePanel(self.notebook)"), py);
            Assert.IsTrue(py.Contains("self.notebook.add(self.shape_panel, text='Shapes')"), py);
            Assert.IsTrue(py.Contains("self.animal_panel = AnimalPanel(self.notebook)"), py);
            Assert.IsTrue(py.Contains("self.notebook.add(self.animal_panel, text='Animals')"), py);

            // Tab children are composed via .add(), never independently positioned.
            Assert.IsFalse(py.Contains("self.shape_panel.place("), py);
            Assert.IsFalse(py.Contains("self.animal_panel.place("), py);
        }

        [TestMethod]
        public void CustomComponentImports_AreDeduplicatedAndSorted()
        {
            string py = new TkinterEmitter().Emit(NotebookWithCustomPanels(), "");
            Assert.IsTrue(py.Contains("from app_gui.views import AnimalPanel, ShapePanel"), py);
        }

        [TestMethod]
        public void PackLayout_EmitsPackInsteadOfPlace()
        {
            var f = new GuiForm { FormClassName = "F", Framework = GuiFramework.Tkinter };
            var c = new GuiControl { Id = "c1", Kind = GuiControlKind.Panel, Name = "listPanel" };
            c.Props["Layout"] = "Pack";
            c.Props["PackFill"] = "both";
            c.Props["PackExpand"] = "True";
            f.Controls.Add(c);

            string py = new TkinterEmitter().Emit(f, "");
            Assert.IsTrue(py.Contains("self.listPanel.pack(fill='both', expand=True)"), py);
            Assert.IsFalse(py.Contains("self.listPanel.place("), py);
        }

        [TestMethod]
        public void FlatFormWithNoParentIds_EmitsExactlyAsBefore()
        {
            // Backward-compat guard: a form where every control has ParentId == "" (every pre-existing
            // form/test) must produce identical output to the original flat-loop implementation.
            var f = new GuiForm { FormClassName = "Flat", Framework = GuiFramework.Tkinter, Width = 300, Height = 200 };
            f.Controls.Add(new GuiControl { Kind = GuiControlKind.Button, Name = "b1", X = 5, Y = 5, Width = 80, Height = 24 });
            f.Controls.Add(new GuiControl { Kind = GuiControlKind.Label, Name = "l1", X = 5, Y = 40, Width = 80, Height = 20 });

            string py = new TkinterEmitter().Emit(f, "");
            Assert.IsTrue(py.Contains("self.b1 = tk.Button(self)\n        self.b1.place(x=5, y=5, width=80, height=24)"), py);
            Assert.IsTrue(py.Contains("self.l1 = tk.Label(self)\n        self.l1.place(x=5, y=40, width=80, height=20)"), py);
        }
    }
}
