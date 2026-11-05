using HelixPythonNamespace.AddIns.GuiDesigner;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using HelixPythonNamespace.GuiDesigner.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Taking a hand-written file over is a question, not a button.
    ///
    /// <para>The designer used to put a "Take Over &amp; Start Syncing" button on the bar above the
    /// canvas. It asked the user to decide something about a file before they had done anything to
    /// it, and it sat one stray click away from the overwrite the confirmation behind it exists to
    /// prevent. The button is gone; the confirmation is not. It is asked at the first edit that
    /// would be written to the file, and offered again from File ▸ "Start Syncing to This
    /// File..." to anyone who said no.</para>
    ///
    /// <para>What must stay true is the safety property, which these tests are really about: a file
    /// this designer did not write is never written to until somebody has said yes.</para>
    /// </summary>
    [TestClass]
    public class GuiDesignerTakeOverTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly string HandWritten =
            "import tkinter as tk" + Environment.NewLine + Environment.NewLine +
            "class MainWindow(tk.Tk):" + Environment.NewLine +
            "    def __init__(self):" + Environment.NewLine +
            "        super().__init__()" + Environment.NewLine +
            "        self.title('mine')" + Environment.NewLine;

        private static string NewHandWrittenFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-takeover-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "main.py");
            File.WriteAllText(path, HandWritten);
            return path;
        }

        private static void Set(object owner, string field, object value)
        { owner.GetType().GetField(field, Private).SetValue(owner, value); }

        private static object Get(object owner, string field)
        { return owner.GetType().GetField(field, Private).GetValue(owner); }

        private static void Regenerate(GuiDesignerForm designer, bool fromEdit)
        {
            typeof(GuiDesignerForm)
                .GetMethod("GenerateCode", Private, null, new[] { typeof(bool) }, null)
                .Invoke(designer, new object[] { fromEdit });
        }

        private static IEnumerable<Control> Tree(Control root)
        {
            yield return root;
            foreach (Control child in root.Controls)
                foreach (Control found in Tree(child))
                    yield return found;
        }

        private static GuiForm SomethingToWrite()
        {
            var model = new GuiForm { FormClassName = "MainWindow", Width = 300, Height = 200 };
            model.Controls.Add(new GuiControl
            {
                Kind = GuiControlKind.Button, Name = "goButton",
                X = 10, Y = 10, Width = 80, Height = 24,
            });
            return model;
        }

        /// <summary>The bar above the canvas carries no button at all — only what it is doing.</summary>
        [TestMethod]
        public void TheCanvasCarriesNoTakeOverButton()
        {
            string path = NewHandWrittenFile();
            try
            {
                using (var designer = new GuiDesignerForm(SomethingToWrite(), path))
                {
                    foreach (Control control in Tree(designer))
                    {
                        string text = control.Text ?? string.Empty;
                        Assert.IsFalse(text.IndexOf("Take Over", StringComparison.OrdinalIgnoreCase) >= 0,
                            "a " + control.GetType().Name + " still offers to take the file over: " + text);
                    }
                }
            }
            finally { Cleanup(path); }
        }

        /// <summary>
        /// A hand-written file is not touched by opening the designer on it, which is what the old
        /// button's confirmation bought and what removing it must not have cost.
        /// </summary>
        [TestMethod]
        public void OpeningOnAHandWrittenFileWritesNothing()
        {
            string path = NewHandWrittenFile();
            try
            {
                using (var designer = new GuiDesignerForm(SomethingToWrite(), path))
                {
                    Assert.IsFalse((bool)Get(designer, "_adopted"), "a file nobody adopted was adopted");
                    Assert.AreEqual(HandWritten, File.ReadAllText(path), "the file was rewritten on open");
                }
            }
            finally { Cleanup(path); }
        }

        /// <summary>
        /// Having said no once, the user is not asked again — and the edits they go on making stay
        /// on the canvas instead of reaching the file. (No modal can open here: a test that had to
        /// dismiss one would be a test that hangs.)
        /// </summary>
        [TestMethod]
        public void AfterDecliningTheFileIsLeftAloneAndTheQuestionIsNotRepeated()
        {
            string path = NewHandWrittenFile();
            try
            {
                using (var designer = new GuiDesignerForm(SomethingToWrite(), path))
                {
                    Set(designer, "_adoptDeclined", true);
                    Regenerate(designer, true);
                    Regenerate(designer, true);

                    Assert.AreEqual(HandWritten, File.ReadAllText(path), "a declined take-over still wrote");
                    Assert.IsFalse((bool)Get(designer, "_adopted"));
                }
            }
            finally { Cleanup(path); }
        }

        /// <summary>Once adopted, an edit goes straight to the file — that is what saying yes meant.</summary>
        [TestMethod]
        public void OnceAdoptedAnEditReachesTheFile()
        {
            string path = NewHandWrittenFile();
            try
            {
                using (var designer = new GuiDesignerForm(SomethingToWrite(), path))
                {
                    Set(designer, "_adopted", true);
                    Regenerate(designer, true);

                    string written = File.ReadAllText(path);
                    Assert.AreNotEqual(HandWritten, written, "the adopted file was not written");
                    StringAssert.Contains(written, "goButton", written);
                }
            }
            finally { Cleanup(path); }
        }

        private static void Cleanup(string path)
        {
            try { Directory.Delete(Path.GetDirectoryName(path), true); } catch { }
        }
    }
}
