namespace HelixPythonNamespace.GuiDesigner.Model
{
    /// <summary>Filters the shared toolbox so a binding never promises a control it cannot emit.</summary>
    public static class GuiFrameworkCapabilities
    {
        public static bool Supports(GuiFramework framework, GuiControlKind kind)
        {
            if (framework == GuiFramework.CppWin32)
                return kind == GuiControlKind.Button || kind == GuiControlKind.Label || kind == GuiControlKind.TextBox || kind == GuiControlKind.CheckBox || kind == GuiControlKind.RadioButton || kind == GuiControlKind.ComboBox || kind == GuiControlKind.ListBox;
            return true;
        }
    }
}
