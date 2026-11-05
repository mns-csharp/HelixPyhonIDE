namespace HelixPythonNamespace.GuiDesigner.Emit
{
    using HelixPythonNamespace.GuiDesigner.Model;

    /// <summary>Resolves the emitter for a framework. Port of the C++ <c>EmitterFor</c>.</summary>
    public static class EmitterFactory
    {
        public static IGuiCodeEmitter EmitterFor(GuiFramework framework)
        {
            switch (framework)
            {
                case GuiFramework.Tkinter: return new TkinterEmitter();
                case GuiFramework.PyQt6: return new PyQt6Emitter();
                case GuiFramework.PySide6: return new PySide6Emitter();
                case GuiFramework.WxPython: return new WxPythonEmitter();
                case GuiFramework.CppWin32: return new CppWin32Emitter();
            }
            return new TkinterEmitter();
        }

        public static GuiFramework[] AllFrameworks()
        {
            return (GuiFramework[])System.Enum.GetValues(typeof(GuiFramework));
        }
    }
}
