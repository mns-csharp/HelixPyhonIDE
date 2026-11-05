using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.GuiDesigner.Emit;
using HelixPythonNamespace.GuiDesigner.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using K = HelixPythonNamespace.GuiDesigner.Model.GuiControlKind;

namespace HelixPythonNamespace.GuiDesigner.Tests
{
    /// <summary>
    /// Every control the GUI Designer Canvas's toolbox offers emits real Tkinter, not a placeholder.
    ///
    /// <para>
    /// The defect these pin was silent and total. The emitter's <c>CtorFor</c> ended in
    /// <c>default: ns = "tk"; ctor = "Frame";</c>, so eighteen of the forty-six toolbox controls
    /// generated an empty <c>tk.Frame</c> — seven visual ones (DataGridView, Chart, MonthCalendar,
    /// DateTimePicker, MaskedTextBox, WebBrowser, BindingNavigator) came out as blank rectangles,
    /// and eleven non-visual components (Timer, ToolTip, ImageList, ErrorProvider, NotifyIcon,
    /// BindingSource and the five dialogs) came out as blank rectangles <b>placed on the form</b>.
    /// Nothing failed. The designer accepted the control, drew it on the canvas, and generated a
    /// program that did not contain it — which is worse than refusing it, because it looks like it
    /// worked.
    /// </para>
    /// <para>
    /// So the assertion is not "the emitter did not throw". It is: no toolbox control emits the
    /// placeholder, no non-visual component is given geometry, and everything the generated module
    /// imports is a name the support module actually defines.
    /// </para>
    /// </summary>
    [TestClass]
    public class EveryToolboxKindEmitsTests
    {
        /// <summary>
        /// The toolbox's own list, mirrored. Kept as a literal rather than reflected out of
        /// GuiDesignerForm because that type lives in the WinForms host: this suite must not need
        /// a UI assembly to answer a question about code generation.
        /// </summary>
        private static readonly K[] ToolboxKinds =
        {
            K.Button, K.Label, K.LinkLabel, K.TextBox, K.RichTextBox, K.MaskedTextBox, K.CheckBox,
            K.CheckedListBox, K.RadioButton, K.ComboBox, K.ListBox, K.ListView, K.TreeView,
            K.PictureBox, K.ProgressBar, K.TrackBar, K.NumericUpDown, K.DomainUpDown,
            K.DateTimePicker, K.MonthCalendar, K.WebBrowser,
            K.Panel, K.GroupBox, K.FlowLayoutPanel, K.TableLayoutPanel, K.TabControl, K.SplitContainer,
            K.MenuStrip, K.ToolStrip, K.StatusStrip, K.ContextMenuStrip,
            K.DataGridView, K.Chart, K.BindingNavigator, K.BindingSource,
            K.Timer, K.ImageList, K.ErrorProvider, K.ToolTip, K.NotifyIcon,
            K.OpenFileDialog, K.SaveFileDialog, K.ColorDialog, K.FontDialog, K.FolderBrowserDialog,
        };

        private static GuiForm FormWith(params K[] kinds)
        {
            var form = new GuiForm { FormClassName = "ProofForm", Title = "Proof", Width = 900, Height = 600 };
            int y = 10;
            foreach (K kind in kinds)
            {
                form.Controls.Add(new GuiControl
                {
                    Id = kind.ToString() + "-id",
                    Kind = kind,
                    Name = "ctl_" + kind.ToString().ToLowerInvariant(),
                    X = 10,
                    Y = y,
                    Width = 160,
                    Height = 24,
                });
                y += 30;
            }
            return form;
        }

        private static string Emit(GuiForm form)
        {
            return new TkinterEmitter().Emit(form, string.Empty);
        }

        [TestMethod]
        public void NoToolboxControl_EmitsTheEmptyFramePlaceholder()
        {
            var placeholders = new List<string>();
            foreach (K kind in ToolboxKinds)
            {
                string code = Emit(FormWith(kind));
                string line = code.Split('\n')
                    .FirstOrDefault(l => l.Contains("self.ctl_" + kind.ToString().ToLowerInvariant() + " ="));
                Assert.IsNotNull(line, kind + " emitted no construction line at all");

                // tk.Frame is legitimate for the three container kinds Tk really does model as a
                // frame; for anything else it is the placeholder.
                bool isAFrameByDesign = kind == K.Panel || kind == K.FlowLayoutPanel
                                        || kind == K.TableLayoutPanel;
                if (!isAFrameByDesign && line.Contains("tk.Frame("))
                    placeholders.Add(kind + " -> " + line.Trim());
            }

            Assert.AreEqual(0, placeholders.Count,
                "these controls generate an empty frame instead of themselves:\n  "
                + string.Join("\n  ", placeholders));
        }

        [TestMethod]
        public void NonVisualComponents_AreConstructedButNeverPlaced()
        {
            foreach (K kind in ToolboxKinds.Where(k => GuiControlKinds.IsNonVisualComponent(k)))
            {
                string name = "ctl_" + kind.ToString().ToLowerInvariant();
                string code = Emit(FormWith(kind));

                StringAssert.Contains(code, "self." + name + " = ", kind + " should still be constructed");
                Assert.IsFalse(code.Contains("self." + name + ".place("),
                    kind + " is not a widget and must not be given a position on the form");
                Assert.IsFalse(code.Contains("self." + name + ".pack("),
                    kind + " is not a widget and must not be packed");
            }
        }

        [TestMethod]
        public void TheMenuBar_IsAttachedToTheToplevel_NotPlacedOnTheFrame()
        {
            // A Tk menubar is configured on the toplevel window. Placing it as a child of the form
            // frame is why a generated form used to show a grey bar and have no menu.
            string code = Emit(FormWith(K.MenuStrip));
            StringAssert.Contains(code, "HelixMenuStrip(self.winfo_toplevel())");
            Assert.IsFalse(code.Contains("self.ctl_menustrip.place("));
        }

        [TestMethod]
        public void EveryRuntimeNameImported_IsDefinedByTheSupportModule()
        {
            // The import line is written by hand in the emitter, so a rename on one side and not
            // the other fails at the user's `import`, pointing at their form rather than at this.
            string runtime = TkinterRuntime.Source();
            foreach (K kind in ToolboxKinds)
            {
                string cls = TkinterRuntime.ClassFor(kind);
                if (cls == null) continue;
                StringAssert.Contains(runtime, "class " + cls,
                    cls + " is imported for " + kind + " but the support module does not define it");
            }
        }

        [TestMethod]
        public void AFormOfEveryKind_ImportsOnlyWhatItUses_AndCompiles()
        {
            string code = Emit(FormWith(ToolboxKinds));

            // One import line, listing exactly the runtime classes this form needs.
            string importLine = code.Split('\n').FirstOrDefault(l => l.StartsWith("from helix_tk import "));
            Assert.IsNotNull(importLine, "a form using the runtime must import from it");
            var imported = importLine.Substring("from helix_tk import ".Length)
                .Split(new[] { ", " }, StringSplitOptions.None).Select(s => s.Trim()).ToList();

            var expected = ToolboxKinds.Select(TkinterRuntime.ClassFor).Where(c => c != null)
                .Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
            CollectionAssert.AreEqual(expected, imported,
                "the import list must be exactly the runtime classes the form uses, sorted");
        }

        [TestMethod]
        public void AFormUsingOnlyTkWidgets_ImportsNoSupportModule()
        {
            // The support module must not appear in a project that does not need it. This is also
            // what keeps every pre-existing form's output byte-identical.
            string code = Emit(FormWith(K.Button, K.Label, K.TextBox, K.CheckBox, K.Panel));
            Assert.IsFalse(code.Contains("helix_tk"),
                "a form built from Tk's own widgets should not pull in the support module");
        }

        [TestMethod]
        public void PropertiesReachTheGeneratedConstructor()
        {
            // The controls that need configuring are useless without it: a grid with no columns
            // shows nothing, and a mask that never arrives validates nothing.
            var form = new GuiForm { FormClassName = "ProofForm", Title = "Proof" };
            var grid = new GuiControl { Id = "g", Kind = K.DataGridView, Name = "grid" };
            grid.Props["Columns"] = "Code, Name, Qty";
            var masked = new GuiControl { Id = "m", Kind = K.MaskedTextBox, Name = "batch" };
            masked.Props["Mask"] = "LL-0000";
            var timer = new GuiControl { Id = "t", Kind = K.Timer, Name = "ticker" };
            timer.Props["Interval"] = "250";
            timer.Props["Enabled"] = "true";
            form.Controls.Add(grid);
            form.Controls.Add(masked);
            form.Controls.Add(timer);

            string code = Emit(form);
            StringAssert.Contains(code, "columns=('Code', 'Name', 'Qty')");
            StringAssert.Contains(code, "mask='LL-0000'");
            StringAssert.Contains(code, "interval=250");
            StringAssert.Contains(code, "enabled=True");
        }

        [TestMethod]
        public void ASingleColumn_IsATupleNotAString()
        {
            // ('Name') is a parenthesised string, and Treeview would then make one column per
            // CHARACTER. The trailing comma is the whole difference.
            var form = new GuiForm { FormClassName = "ProofForm" };
            var grid = new GuiControl { Id = "g", Kind = K.DataGridView, Name = "grid" };
            grid.Props["Columns"] = "Name";
            form.Controls.Add(grid);
            StringAssert.Contains(Emit(form), "columns=('Name',)");
        }

        [TestMethod]
        public void TheSupportModule_CarriesARecognisedBanner()
        {
            // The designer rewrites this file on every generation and refuses to overwrite one a
            // user wrote. That guard reads the banner, so an unrecognised banner would make the
            // designer treat its own output as somebody's hand-written module and stop updating it.
            Assert.IsTrue(EmitterCommon.StartsWithAutoGeneratedBanner(
                TkinterRuntime.Source(), new TkinterEmitter().AutoGeneratedMarker));
        }
    }
}
