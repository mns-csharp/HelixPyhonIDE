namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.Debugger;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// Small modal dialog to set a breakpoint's <c>condition</c> (a Python expression that must be
    /// truthy for the breakpoint to fire) and <c>hitCondition</c> (a hit-count rule debugpy
    /// understands, e.g. <c>5</c>, <c>&gt;=3</c>, <c>%2</c>). Leaving both blank clears the
    /// condition. Used from the Breakpoints panel's right-click "Edit Condition…".
    /// </summary>
    internal static class BreakpointConditionDialog
    {
        public static bool Show(IWin32Window owner, string fileName, int line,
            string currentCondition, string currentHit, out string condition, out string hitCondition)
        {
            // Default the out-params to null so a Cancel (early return) leaves them cleared.
            condition = null; hitCondition = null;
            // LunaForm gives the dialog the app's XP "Luna" theming for a consistent look.
            using (Form dlg = new LunaForm())
            {
                // Fixed, non-resizable, centered-on-parent modal — no taskbar entry, no min/max.
                dlg.Text = "Breakpoint Condition — " + fileName + ":" + line;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false; dlg.MaximizeBox = false; dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(440, 168);
                dlg.Font = LunaFonts.Ui;

                // Condition row: a monospace (Consolas) box since it holds a Python expression.
                Label condLbl = new Label { Text = "Break when this expression is true:", AutoSize = true, Location = new Point(12, 12) };
                TextBox condBox = new TextBox { Location = new Point(14, 32), Width = 410, Font = new Font("Consolas", 9f), Text = currentCondition ?? "" };

                // Hit-count row: narrower box for the short count rule (e.g. "5", ">=3", "%2").
                Label hitLbl = new Label { Text = "Hit count (e.g.  5,  >=3,  %2):", AutoSize = true, Location = new Point(12, 66) };
                TextBox hitBox = new TextBox { Location = new Point(14, 86), Width = 160, Font = new Font("Consolas", 9f), Text = currentHit ?? "" };

                // OK does not auto-close: validate the hit-count first so a typo (which debugpy would
                // silently ignore) is caught and reported instead of saved as a dead rule.
                Button ok = new Button { Text = "OK", Location = new Point(268, 128), Width = 75 };
                Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(349, 128), Width = 75 };
                ok.Click += delegate
                {
                    string h = hitBox.Text.Trim();
                    if (!BreakpointHitCondition.IsValid(h))
                    {
                        MessageBox.Show(dlg,
                            "'" + h + "' isn't a valid hit count.\n\nUse a number, or a comparison:\n" +
                            "    5        stop on the 5th hit\n    >=3      on/after the 3rd hit\n" +
                            "    <10      before the 10th hit\n    %2        every 2nd hit",
                            "Breakpoint Hit Count", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        hitBox.Focus(); hitBox.SelectAll();
                        return; // keep the dialog open
                    }
                    dlg.DialogResult = DialogResult.OK;
                };

                // Add controls, then bind Enter->OK and Esc->Cancel via Accept/CancelButton.
                dlg.Controls.Add(condLbl); dlg.Controls.Add(condBox);
                dlg.Controls.Add(hitLbl); dlg.Controls.Add(hitBox);
                dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok; dlg.CancelButton = cancel;

                // Cancel leaves the out-params null (condition cleared); OK returns the
                // trimmed values. OK only closes once the hit-count passed validation above.
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                condition = condBox.Text.Trim();
                hitCondition = hitBox.Text.Trim();
                return true;
            }
        }
    }
}
