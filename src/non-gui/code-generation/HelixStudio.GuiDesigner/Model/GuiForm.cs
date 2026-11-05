namespace HelixPythonNamespace.GuiDesigner.Model
{
    using System.Collections.Generic;

    public enum GuiFramework { Tkinter, PyQt6, PySide6, WxPython, CppWin32 }

    public static class GuiFrameworks
    {
        public static string ToToken(GuiFramework framework)
        {
            switch (framework)
            {
                case GuiFramework.Tkinter: return "tkinter";
                case GuiFramework.PyQt6: return "pyqt6";
                case GuiFramework.PySide6: return "pyside6";
                case GuiFramework.WxPython: return "wxpython";
                case GuiFramework.CppWin32: return "cpp-win32";
            }
            return "tkinter";
        }

        public static GuiFramework FromToken(string token)
        {
            token = (token ?? "").Trim().ToLowerInvariant();
            if (token == "pyqt6") return GuiFramework.PyQt6;
            if (token == "pyside6") return GuiFramework.PySide6;
            if (token == "wxpython") return GuiFramework.WxPython;
            if (token == "cpp-win32" || token == "win32") return GuiFramework.CppWin32;
            return GuiFramework.Tkinter;
        }
    }

    /// <summary>Per-form viewport state persisted to .designer.json. Port of the C++ <c>GuiViewport</c>.</summary>
    public sealed class GuiViewport
    {
        public double Zoom = 1.0;
        public int PanX;
        public int PanY;
        public int GridSize = 8;
        public bool SnapToGrid;
    }

    public sealed class GuiForm
    {
        public string FormClassName = "MainForm";
        public string Title = "Form";
        public int Width = 640;
        public int Height = 480;
        public GuiFramework Framework = GuiFramework.Tkinter;
        public List<GuiControl> Controls = new List<GuiControl>();
        public int SchemaVersion = 1;
        public GuiViewport Viewport = new GuiViewport();
    }
}
