using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixTextEditorNET;

namespace HelixPythonNamespace.UI.HelixTextEditorNET.Tests
{
    /// <summary>
    /// A dwell means "the pointer rested somewhere". With no pointer in the control there is no
    /// such place, so no dwell may be raised.
    ///
    /// Setting <c>MouseDwellTime</c> used to ARM the dwell timer — and the IDE sets it once while
    /// building every editor (PythonEditorControl). The tick then fired with the mouse position
    /// still at its default (0,0), which is a real client point: the first character of the file.
    /// So each launch popped a hover doc for the file's opening token ("from — selective import"),
    /// anchored at the editor's top-left, with no mouse movement left to dismiss it.
    /// </summary>
    [TestClass]
    public class DwellWithoutAPointerTests
    {
        private static void PumpFor(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
        }

        [TestMethod]
        public void SettingMouseDwellTime_DoesNotRaiseADwellTheUserNeverMade()
        {
            using (var editor = new Scintilla { Width = 400, Height = 200 })
            {
                var _ = editor.Handle;
                editor.Text = "from argparse import ArgumentParser\nx = 1\n";

                DwellEventArgs raised = null;
                editor.DwellStart += (s, e) => raised = e;

                editor.MouseDwellTime = 30;      // what the IDE does when it builds an editor
                PumpFor(300);                    // ten times the dwell, with a live message loop

                Assert.IsNull(raised,
                    raised == null ? "" : "a dwell was raised at position " + raised.Position
                    + " without the pointer ever entering the control — that is the phantom "
                    + "tooltip over the file's first token");
            }
        }

        [TestMethod]
        public void OnceThePointerIsInside_TheDwellStillWorks()
        {
            using (var editor = new Scintilla { Width = 400, Height = 200 })
            {
                var _ = editor.Handle;
                editor.Text = "from argparse import ArgumentParser\nx = 1\n";

                DwellEventArgs raised = null;
                editor.DwellStart += (s, e) => raised = e;
                editor.MouseDwellTime = 30;

                // Enter + a real move, exactly as a hovering user produces.
                var t = typeof(Scintilla);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                t.GetMethod("OnMouseEnter", flags).Invoke(editor, new object[] { EventArgs.Empty });
                t.GetMethod("OnMouseMove", flags).Invoke(editor, new object[] {
                    new MouseEventArgs(MouseButtons.None, 0, 120, 20, 0) });
                PumpFor(300);

                Assert.IsNotNull(raised, "hover tooltips must still work for a real pointer");
                Assert.IsTrue(raised.X == 120 && raised.Y == 20,
                    "the dwell must report where the pointer actually rested, got ("
                    + raised.X + "," + raised.Y + ")");
            }
        }
    }
}
