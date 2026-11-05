namespace HelixPythonNamespace.Core.Model.Enums
{
    /// <summary>
    /// GUI toolkit chosen when scaffolding a <see cref="ProjectKind.GuiApp"/>.
    /// Persisted in <c>pyproject.toml [tool.helixstudio].gui-framework</c> so the
    /// scaffolder's choice survives reload.
    /// </summary>
    public enum GuiFramework
    {
        /// <summary>Python standard-library Tkinter — no external dependency.</summary>
        Tkinter = 0,

        /// <summary>Qt-for-Python (official Qt bindings, LGPL).</summary>
        PySide6 = 1,

        /// <summary>Riverbank's PyQt bindings.</summary>
        PyQt6 = 2,

        /// <summary>wxPython — native cross-platform widgets.</summary>
        WxPython = 3,
    }

    public static class GuiFrameworks
    {
        public static string ToToken(GuiFramework framework)
        {
            switch (framework)
            {
                case GuiFramework.PySide6: return "pyside6";
                case GuiFramework.PyQt6: return "pyqt6";
                case GuiFramework.WxPython: return "wxpython";
                case GuiFramework.Tkinter: return "tkinter";
                default: return "tkinter";
            }
        }

        public static GuiFramework FromToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return GuiFramework.Tkinter;
            string t = token.Trim().ToLowerInvariant();
            if (t == "pyside6" || t == "pyside" || t == "qt-for-python") return GuiFramework.PySide6;
            if (t == "pyqt6" || t == "pyqt") return GuiFramework.PyQt6;
            if (t == "wxpython" || t == "wx") return GuiFramework.WxPython;
            return GuiFramework.Tkinter;
        }
    }
}
