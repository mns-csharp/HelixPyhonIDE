namespace HelixPythonNamespace.AddIns.GuiDesigner
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.GuiDesigner.Model;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// What the GUI Designer Canvas knows about the solution it was opened on.
    ///
    /// <para>Before this it knew nothing: opened from the Tools menu with no active document it
    /// fabricated an empty form called after nothing, while the solution's real windows sat in the
    /// tree needing a context menu the user had to know about. The designer is a view of the
    /// solution, so it opens on the solution.</para>
    ///
    /// <para>A solution can contain several windows, such as a main window, a settings dialog, and
    /// an about box. The designer lets the user choose which form to open.</para>
    /// </summary>
    public sealed partial class GuiDesignerAddIn
    {
        /// <summary>
        /// Open the GUI Designer Canvas on whatever the loaded solution actually contains.
        ///
        /// <list type="bullet">
        /// <item>no solution, or a solution with no form — a starting point, so the canvas is never
        /// an empty room;</item>
        /// <item>one form — that form;</item>
        /// <item>several — the user picks, because several windows is what an application looks
        /// like.</item>
        /// </list>
        /// </summary>
        internal void ShowGuiDesignerOnSolution()
        {
            if (_host.SolutionTypes() == null)
            {
                OpenGuiDesignerDocument(GuiDesignerForm.BuildSampleForm(), null);
                _host.SetStatus("No solution open — starting from the sample form.", Color.FromArgb(70, 90, 130));
                return;
            }

            List<TypeModel> forms = GuiFormsInSolution();
            if (forms.Count == 0)
            {
                OpenGuiDesignerDocument(GuiDesignerForm.BuildSampleForm(), null);
                _host.SetStatus("No form in this solution — here is a starting point to build one from.", Color.FromArgb(70, 90, 130));
                return;
            }

            TypeModel chosen = forms.Count == 1 ? forms[0] : AskWhichForm(forms);
            if (chosen == null) return;                       // the user cancelled; nothing is opened
            OpenGuiForm(chosen, forms.Count);
        }

        /// <summary>
        /// Every window the solution defines.
        ///
        /// Recognised through <see cref="GuiFormBridge.IsGuiFormType"/> — the same test the tree's
        /// own "Open in GUI Designer Canvas" uses — so the designer and the tree cannot disagree
        /// about what counts as a form.
        /// </summary>
        internal List<TypeModel> GuiFormsInSolution()
        {
            var forms = new List<TypeModel>();
            IReadOnlyList<TypeModel> types = _host.SolutionTypes();
            if (types == null) return forms;
            foreach (TypeModel type in types)
                if (GuiFormBridge.IsGuiFormType(type)) forms.Add(type);

            forms.Sort(delegate(TypeModel a, TypeModel b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return forms;
        }

        private void OpenGuiForm(TypeModel type, int outOf)
        {
            GuiForm form = GuiFormBridge.BuildFormFor(type);
            OpenGuiDesignerDocument(form, type.SourceFilePath);
            _host.SetStatus(outOf > 1
                ? type.Name + " — one of " + outOf + " forms in this solution"
                : type.Name + " — the solution's only form", Color.FromArgb(70, 90, 130));
        }

        private TypeModel AskWhichForm(List<TypeModel> forms)
        {
            // A headless host must not be stopped by a modal; the first form is opened, which is
            // the same outcome a user who pressed Enter would get.
            if (_host.IsNonInteractive) return forms[0];

            using (var dialog = new GuiFormChoiceDialog(forms))
                return dialog.ShowDialog(_host.Owner) == DialogResult.OK ? dialog.Chosen : null;
        }
    }

    /// <summary>
    /// Which of the solution's windows to open.
    ///
    /// Not a warning: several windows is what an application looks like. It names each form and the
    /// project it lives in, because two windows called <c>MainWindow</c> in two projects is an
    /// ordinary thing for a solution to contain and the name alone would not tell them apart.
    /// </summary>
    internal sealed class GuiFormChoiceDialog : LunaForm
    {
        private readonly ListBox _list = new ListBox();
        private readonly List<TypeModel> _forms;

        internal TypeModel Chosen
        {
            get
            {
                int i = _list.SelectedIndex;
                return i >= 0 && i < _forms.Count ? _forms[i] : null;
            }
        }

        internal GuiFormChoiceDialog(List<TypeModel> forms)
        {
            _forms = forms;
            Text = "Open a form";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 300);

            var heading = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(12, 11, 12, 0),
                Text = "This solution has " + forms.Count + " forms. Which one?",
            };

            _list.Dock = DockStyle.Fill;
            _list.IntegralHeight = false;
            foreach (TypeModel form in forms)
                _list.Items.Add(form.Project != null
                    ? form.Name + "   —   " + form.Project.Name
                    : form.Name);
            _list.SelectedIndex = 0;
            _list.DoubleClick += delegate { DialogResult = DialogResult.OK; };

            // Flowed from the right rather than placed at ClientSize.Width - N.
            //
            // A hand-placed, right-anchored button measures its anchor against the size its PARENT
            // had when it was added — and a Panel that has just been constructed is 200px wide,
            // whatever it is docked to become. Every such button was therefore shifted right by the
            // difference when the strip took its real width, and left the dialog entirely: the
            // choice dialogs rendered a list and an empty band where their buttons should be, so
            // Enter, Esc, a double-click or the close box were the only ways out and none of them
            // was written anywhere on the dialog. Flow layout owes nothing to a size at construction
            // time. Cancel is added first because the flow runs right to left.
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 46,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 10, 12, 0),
            };
            var ok = new Button { Text = "Open", DialogResult = DialogResult.OK, Width = 90, Height = 26 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 26 };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
            body.Controls.Add(_list);

            Controls.Add(body);
            Controls.Add(buttons);
            Controls.Add(heading);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
