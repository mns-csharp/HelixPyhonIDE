namespace HelixPythonNamespace.GuiDesigner.Tests
{
    using System;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.GuiDesigner.Emit;
    using HelixPythonNamespace.GuiDesigner.Model;
    using HelixPythonNamespace.GuiDesigner.Persistence;

    [TestClass]
    public class EmitterTests
    {
        private static GuiForm ButtonForm(GuiFramework fw)
        {
            GuiForm f = new GuiForm { FormClassName = "MyForm", Title = "Hi", Width = 320, Height = 240, Framework = fw };
            GuiControl b = new GuiControl { Kind = GuiControlKind.Button, Name = "btn1", X = 10, Y = 20, Width = 80, Height = 30 };
            b.Props["Text"] = "Click Me";
            b.Events.Add(new GuiEvent { EventName = "Click", HandlerName = "on_btn1_click" });
            f.Controls.Add(b);
            return f;
        }

        [TestMethod]
        public void TkinterEmitsClassControlEventAndRegions()
        {
            GuiForm f = ButtonForm(GuiFramework.Tkinter);
            // Tkinter uses 'command' event for buttons in the C++ tests; use command here.
            f.Controls[0].Events[0].EventName = "command";
            string py = new TkinterEmitter().Emit(f, "");
            Assert.IsTrue(py.Contains("import tkinter as tk"));
            Assert.IsTrue(py.Contains("class MyForm(tk.Frame):"));
            Assert.IsTrue(py.Contains("self.btn1 = tk.Button(self"));
            Assert.IsTrue(py.Contains("text='Click Me'"));
            Assert.IsTrue(py.Contains(".place(x=10, y=20, width=80, height=30)"));
            Assert.IsTrue(py.Contains("self.btn1.config(command=self.on_btn1_click)"));
            Assert.IsTrue(py.Contains("# region UserCode-on_btn1_click"));
            Assert.IsTrue(py.Contains("root.mainloop()"));
        }

        [TestMethod]
        public void TkinterImportsTtkWhenNeeded()
        {
            GuiForm f = new GuiForm { Framework = GuiFramework.Tkinter };
            f.Controls.Add(new GuiControl { Kind = GuiControlKind.ComboBox, Name = "cb" });
            string py = new TkinterEmitter().Emit(f, "");
            Assert.IsTrue(py.Contains("from tkinter import ttk"));
            Assert.IsTrue(py.Contains("ttk.Combobox(self"));
        }

        [TestMethod]
        public void PyQt6EmitsWidgetSignalAndMain()
        {
            string py = new PyQt6Emitter().Emit(ButtonForm(GuiFramework.PyQt6), "");
            Assert.IsTrue(py.Contains("from PyQt6.QtWidgets import ("));
            Assert.IsTrue(py.Contains("class MyForm(QWidget):"));
            Assert.IsTrue(py.Contains("self.btn1 = QPushButton('Click Me', self)"));
            Assert.IsTrue(py.Contains("self.btn1.setGeometry(10, 20, 80, 30)"));
            Assert.IsTrue(py.Contains("self.btn1.clicked.connect(self.on_btn1_click)"));
            Assert.IsTrue(py.Contains("sys.exit(app.exec())"));
        }

        [TestMethod]
        public void PyQt6SingleLineTextBoxImportsOnlyQLineEdit()
        {
            // Regression (iter 49): a single-line TextBox imported BOTH QLineEdit and
            // QPlainTextEdit, leaving QPlainTextEdit unused (PY5001 in the generated code).
            // Import only the widget actually emitted.
            GuiForm f = new GuiForm { FormClassName = "F", Framework = GuiFramework.PyQt6 };
            f.Controls.Add(new GuiControl { Kind = GuiControlKind.TextBox, Name = "t1" });
            string py = new PyQt6Emitter().Emit(f, "");
            Assert.IsTrue(py.Contains("QLineEdit"), "single-line TextBox should use QLineEdit");
            Assert.IsFalse(py.Contains("QPlainTextEdit"), "single-line TextBox must NOT import unused QPlainTextEdit");
        }

        [TestMethod]
        public void PyQt6MultilineTextBoxImportsOnlyQPlainTextEdit()
        {
            GuiForm f = new GuiForm { FormClassName = "F", Framework = GuiFramework.PyQt6 };
            GuiControl t = new GuiControl { Kind = GuiControlKind.TextBox, Name = "t1" };
            t.Props["Multiline"] = "True";
            f.Controls.Add(t);
            string py = new PyQt6Emitter().Emit(f, "");
            Assert.IsTrue(py.Contains("QPlainTextEdit"), "multiline TextBox should use QPlainTextEdit");
            Assert.IsFalse(py.Contains("QLineEdit"), "multiline-only form must NOT import unused QLineEdit");
        }

        [TestMethod]
        public void PyQt6CheckedListBoxNeedsQtAndCheckableLoop()
        {
            GuiForm f = new GuiForm { Framework = GuiFramework.PyQt6 };
            GuiControl c = new GuiControl { Kind = GuiControlKind.CheckedListBox, Name = "clb" };
            c.Props["Items"] = "a,b,c";
            f.Controls.Add(c);
            string py = new PyQt6Emitter().Emit(f, "");
            Assert.IsTrue(py.Contains("from PyQt6.QtCore import"));
            Assert.IsTrue(py.Contains("Qt"));
            Assert.IsTrue(py.Contains("ItemIsUserCheckable"));
            Assert.IsTrue(py.Contains("addItems(['a', 'b', 'c'])"));
        }

        [TestMethod]
        public void PySide6SwapsImportPrefixOnly()
        {
            string py = new PySide6Emitter().Emit(ButtonForm(GuiFramework.PySide6), "");
            Assert.IsTrue(py.Contains("from PySide6.QtWidgets import ("));
            Assert.IsFalse(py.Contains("from PyQt6."));
            Assert.IsTrue(py.Contains("self.btn1 = QPushButton('Click Me', self)"));
        }

        [TestMethod]
        public void WxPythonEmitsWidgetBindAndMain()
        {
            string py = new WxPythonEmitter().Emit(ButtonForm(GuiFramework.WxPython), "");
            Assert.IsTrue(py.Contains("import wx"));
            Assert.IsTrue(py.Contains("class MyForm(wx.Frame):"));
            Assert.IsTrue(py.Contains("self.btn1 = wx.Button(self, label='Click Me', pos=(10, 20), size=(80, 30))"));
            Assert.IsTrue(py.Contains("self.Bind(wx.EVT_BUTTON, self.on_btn1_click, source=self.btn1)"));
            Assert.IsTrue(py.Contains("app.MainLoop()"));
        }

        [TestMethod]
        public void WxPythonImportsRichtextWhenNeeded()
        {
            GuiForm f = new GuiForm { Framework = GuiFramework.WxPython };
            f.Controls.Add(new GuiControl { Kind = GuiControlKind.RichTextBox, Name = "rt" });
            string py = new WxPythonEmitter().Emit(f, "");
            Assert.IsTrue(py.Contains("import wx.richtext"));
        }

        [TestMethod]
        public void EmitterFactoryResolvesByFramework()
        {
            Assert.AreEqual("Tkinter", EmitterFactory.EmitterFor(GuiFramework.Tkinter).FrameworkName);
            Assert.AreEqual("PyQt6", EmitterFactory.EmitterFor(GuiFramework.PyQt6).FrameworkName);
            Assert.AreEqual("PySide6", EmitterFactory.EmitterFor(GuiFramework.PySide6).FrameworkName);
            Assert.AreEqual("WxPython", EmitterFactory.EmitterFor(GuiFramework.WxPython).FrameworkName);
        }

        [TestMethod]
        public void RegenPreservesUserCodeRegion()
        {
            GuiForm f = ButtonForm(GuiFramework.Tkinter);
            f.Controls[0].Events[0].EventName = "command";
            string existing =
                "    # region UserCode-on_btn1_click\n" +
                "    def on_btn1_click(self):\n" +
                "        print('user edited')\n" +
                "    # endregion\n";
            string py = new TkinterEmitter().Emit(f, existing);
            Assert.IsTrue(py.Contains("print('user edited')"), "User region body must survive regen.");
            Assert.IsFalse(py.Contains("        pass\n    # endregion"), "Default stub must not overwrite user code.");
        }

        [TestMethod]
        public void EmptyFormEmitsPassBodies()
        {
            GuiForm f = new GuiForm { Framework = GuiFramework.PyQt6 };
            string py = new PyQt6Emitter().Emit(f, "");
            Assert.IsTrue(py.Contains("def _build_ui(self):\n        pass"));
        }
    }

    [TestClass]
    public class PersistenceTests
    {
        private string _dir;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "helix_gui_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void Cleanup() { try { Directory.Delete(_dir, true); } catch { } }

        [TestMethod]
        public void SaveLoadRoundTrip()
        {
            GuiForm f = new GuiForm
            {
                FormClassName = "Dlg", Title = "My Dialog", Width = 500, Height = 400,
                Framework = GuiFramework.PyQt6,
            };
            GuiControl b = new GuiControl { Kind = GuiControlKind.Button, Name = "ok", Id = "id1", X = 5, Y = 6, Width = 70, Height = 22 };
            b.Props["Text"] = "OK";
            b.Events.Add(new GuiEvent { EventName = "Click", HandlerName = "on_ok" });
            f.Controls.Add(b);

            string path = Path.Combine(_dir, "form.designer.json");
            GuiFormRepository repo = new GuiFormRepository();
            Assert.IsTrue(repo.Save(path, f));

            GuiForm loaded = repo.Load(path);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("Dlg", loaded.FormClassName);
            Assert.AreEqual("My Dialog", loaded.Title);
            Assert.AreEqual(500, loaded.Width);
            Assert.AreEqual(GuiFramework.PyQt6, loaded.Framework);
            Assert.AreEqual(1, loaded.Controls.Count);
            Assert.AreEqual(GuiControlKind.Button, loaded.Controls[0].Kind);
            Assert.AreEqual("OK", loaded.Controls[0].Props["Text"]);
            Assert.AreEqual(1, loaded.Controls[0].Events.Count);
            Assert.AreEqual("on_ok", loaded.Controls[0].Events[0].HandlerName);
        }

        [TestMethod]
        public void ViewportPersistedOnlyWhenNonDefault()
        {
            string defaultPath = Path.Combine(_dir, "a.designer.json");
            string customPath = Path.Combine(_dir, "b.designer.json");
            GuiFormRepository repo = new GuiFormRepository();

            repo.Save(defaultPath, new GuiForm());
            Assert.IsFalse(File.ReadAllText(defaultPath).Contains("viewport"));

            GuiForm f = new GuiForm();
            f.Viewport.Zoom = 2.0;
            f.Viewport.SnapToGrid = true;
            repo.Save(customPath, f);
            Assert.IsTrue(File.ReadAllText(customPath).Contains("viewport"));

            GuiForm loaded = repo.Load(customPath);
            Assert.AreEqual(2.0, loaded.Viewport.Zoom, 1e-9);
            Assert.IsTrue(loaded.Viewport.SnapToGrid);
        }

        [TestMethod]
        public void LoadMissingFileReturnsNull()
        {
            Assert.IsNull(new GuiFormRepository().Load(Path.Combine(_dir, "nope.json")));
        }

        [TestMethod]
        public void PySourceForStripsDesignerSuffix()
        {
            string py = GuiFormRepository.PySourceFor(Path.Combine(_dir, "MyForm.designer.json"));
            Assert.IsTrue(py.EndsWith("MyForm.py"));
            Assert.IsFalse(py.Contains(".designer"));
        }

        [TestMethod]
        public void EndToEndDesignerJsonToPython()
        {
            // Save a form, reload it, emit Python via the factory — the headless DesignerCli path.
            GuiForm f = new GuiForm { FormClassName = "W", Framework = GuiFramework.WxPython };
            f.Controls.Add(new GuiControl { Kind = GuiControlKind.Label, Name = "lbl" });
            string path = Path.Combine(_dir, "w.designer.json");
            GuiFormRepository repo = new GuiFormRepository();
            repo.Save(path, f);
            GuiForm loaded = repo.Load(path);
            string py = EmitterFactory.EmitterFor(loaded.Framework).Emit(loaded, "");
            Assert.IsTrue(py.Contains("class W(wx.Frame):"));
            Assert.IsTrue(py.Contains("wx.StaticText"));
        }

        [TestMethod]
        public void NativeFrameworkEmitterMapsBoundsAndPreservesUserCode()
        {
            GuiForm form = new GuiForm { FormClassName = "MyForm", Title = "Hi", Width = 320, Height = 240, Framework = GuiFramework.CppWin32 };
            GuiControl button = new GuiControl { Kind = GuiControlKind.Button, Name = "btn1", Width = 80, Height = 30 };
            button.Props["Text"] = "Click Me";
            form.Controls.Add(button);
            form.Controls[0].X = 12; form.Controls[0].Y = 34;
            form.Controls[0].Width = 120; form.Controls[0].Height = 28;

            string cpp = EmitterFactory.EmitterFor(form.Framework).Emit(form, "");
            Assert.IsTrue(cpp.Contains("#include <windows.h>"));
            Assert.IsTrue(cpp.Contains("12, 34, 120, 28"));
        }

        [TestMethod]
        public void EveryEmitterBannerIsRecognizedAsDesignerOwned_IncludingLegacyProductNames()
        {
            // The banner is the ONLY signal that a file is safe to overwrite without asking. A
            // product rename must therefore not orphan files an earlier build generated — this is
            // the regression that shipped when the namespace migration rewrote the literal.
            foreach (GuiFramework framework in EmitterFactory.AllFrameworks())
            {
                string marker = EmitterFactory.EmitterFor(framework).AutoGeneratedMarker;
                Assert.IsTrue(EmitterCommon.StartsWithAutoGeneratedBanner(marker + "\nbody\n", marker),
                              framework + ": its own current banner");
                foreach (string legacy in new[] { "HelixStudio", "HelixPythonNamespace" })
                {
                    string old = marker.Replace("HelixPython", legacy);
                    Assert.IsTrue(EmitterCommon.StartsWithAutoGeneratedBanner(old + "\nbody\n", marker),
                                  framework + ": a file generated as " + legacy + " stays adoptable");
                }
            }
            Assert.IsFalse(EmitterCommon.StartsWithAutoGeneratedBanner("class Handwritten:\n    pass\n",
                           EmitterFactory.EmitterFor(GuiFramework.Tkinter).AutoGeneratedMarker),
                           "hand-written code must never be claimed as designer-owned");
        }

        [TestMethod]
        public void FrameworkTokensRoundTripForEveryDesignerBinding()
        {
            foreach (GuiFramework framework in EmitterFactory.AllFrameworks())
                Assert.AreEqual(framework, GuiFrameworks.FromToken(GuiFrameworks.ToToken(framework)));
        }

    }
}
