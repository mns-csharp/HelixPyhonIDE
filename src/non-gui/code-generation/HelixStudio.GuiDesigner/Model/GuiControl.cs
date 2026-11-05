namespace HelixPythonNamespace.GuiDesigner.Model
{
    using System.Collections.Generic;

    /// <summary>Full WinForms control taxonomy. Port of the C++ <c>GuiControlKind</c>.</summary>
    public enum GuiControlKind
    {
        Form,
        // Common Controls
        Button, CheckBox, CheckedListBox, ComboBox, DateTimePicker, DomainUpDown,
        Label, LinkLabel, ListBox, ListView, MaskedTextBox, MonthCalendar,
        NotifyIcon, NumericUpDown, PictureBox, ProgressBar, RadioButton,
        RichTextBox, TextBox, ToolTip, TrackBar, TreeView, WebBrowser,
        // Containers
        FlowLayoutPanel, GroupBox, Panel, SplitContainer, TabControl,
        TableLayoutPanel, ToolStripContainer,
        // Menus & Toolbars
        ContextMenuStrip, MenuStrip, StatusStrip, ToolStrip,
        // Data
        BindingNavigator, BindingSource, Chart, DataGridView, DataSet,
        // Components
        BackgroundWorker, ErrorProvider, FileSystemWatcher, HelpProvider,
        ImageList, Process, SerialPort, Timer,
        // Printing
        PageSetupDialog, PrintDialog, PrintDocument, PrintPreviewControl,
        PrintPreviewDialog,
        // Dialogs
        ColorDialog, FolderBrowserDialog, FontDialog, OpenFileDialog, SaveFileDialog,

        /// <summary>
        /// An instance of a locally-defined (non-framework) Python class used as a child widget —
        /// e.g. a hand-written `ShapePanel(ttk.Frame)` composed into a parent form, rather than a
        /// builtin control this designer knows how to draw internals for. Requires
        /// <c>Props["ClassName"]</c> (the class to instantiate) and <c>Props["Module"]</c> (where to
        /// import it from); the canvas renders it as an opaque labeled placeholder — its own internals
        /// are out of scope, the same way a WinForms designer can host a UserControl without knowing
        /// what's inside it.
        /// </summary>
        CustomComponent,
    }

    public sealed class GuiEvent
    {
        public string EventName = "";   // e.g. "Click", "MouseDown", "KeyPress"
        public string HandlerName = ""; // e.g. "on_button1_click"
    }

    public sealed class GuiControl
    {
        public string Id = "";             // stable; uuid-ish
        public GuiControlKind Kind = GuiControlKind.Button;
        public string Name = "";           // python identifier
        public string ParentId = "";       // empty = direct child of the form
        public int X, Y;
        public int Width = 100, Height = 24;
        // Ordinal-sorted to match the C++ std::map iteration order in the emitters.
        public SortedDictionary<string, string> Props = new SortedDictionary<string, string>(System.StringComparer.Ordinal);
        public List<GuiEvent> Events = new List<GuiEvent>();
    }

    /// <summary>Token ↔ kind mapping + non-visual classification. Port of the schema's token tables.</summary>
    public static class GuiControlKinds
    {
        private static readonly Dictionary<GuiControlKind, string> KindToToken = BuildKindToToken();
        private static readonly Dictionary<string, GuiControlKind> TokenToKind = BuildTokenToKind();

        public static string ToToken(GuiControlKind kind)
        {
            string t;
            return KindToToken.TryGetValue(kind, out t) ? t : "Button";
        }

        public static GuiControlKind FromToken(string token)
        {
            GuiControlKind k;
            return token != null && TokenToKind.TryGetValue(token, out k) ? k : GuiControlKind.Button;
        }

        public static bool IsNonVisualComponent(GuiControlKind kind)
        {
            switch (kind)
            {
                case GuiControlKind.Timer:
                case GuiControlKind.BackgroundWorker:
                case GuiControlKind.FileSystemWatcher:
                case GuiControlKind.Process:
                case GuiControlKind.SerialPort:
                case GuiControlKind.ImageList:
                case GuiControlKind.HelpProvider:
                case GuiControlKind.ErrorProvider:
                case GuiControlKind.NotifyIcon:
                case GuiControlKind.ToolTip:
                case GuiControlKind.BindingSource:
                case GuiControlKind.DataSet:
                case GuiControlKind.PageSetupDialog:
                case GuiControlKind.PrintDialog:
                case GuiControlKind.PrintDocument:
                case GuiControlKind.ColorDialog:
                case GuiControlKind.FolderBrowserDialog:
                case GuiControlKind.FontDialog:
                case GuiControlKind.OpenFileDialog:
                case GuiControlKind.SaveFileDialog:
                    return true;
                default:
                    return false;
            }
        }

        private static Dictionary<GuiControlKind, string> BuildKindToToken()
        {
            Dictionary<GuiControlKind, string> m = new Dictionary<GuiControlKind, string>();
            // Canonical WinForms names == enum names for every member.
            foreach (GuiControlKind k in System.Enum.GetValues(typeof(GuiControlKind)))
                m[k] = k.ToString();
            return m;
        }

        private static Dictionary<string, GuiControlKind> BuildTokenToKind()
        {
            Dictionary<string, GuiControlKind> m = new Dictionary<string, GuiControlKind>(System.StringComparer.Ordinal);
            foreach (GuiControlKind k in System.Enum.GetValues(typeof(GuiControlKind)))
                m[k.ToString()] = k;
            // Legacy Tkinter aliases — accepted on input only.
            m["Frame"] = GuiControlKind.Panel;
            m["LabelFrame"] = GuiControlKind.GroupBox;
            m["Entry"] = GuiControlKind.TextBox;
            m["Text"] = GuiControlKind.RichTextBox;
            m["Checkbutton"] = GuiControlKind.CheckBox;
            m["Radiobutton"] = GuiControlKind.RadioButton;
            m["Combobox"] = GuiControlKind.ComboBox;
            m["Listbox"] = GuiControlKind.ListBox;
            m["Scale"] = GuiControlKind.TrackBar;
            m["Spinbox"] = GuiControlKind.NumericUpDown;
            m["Progressbar"] = GuiControlKind.ProgressBar;
            return m;
        }
    }
}
