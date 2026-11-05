namespace HelixPythonNamespace.GuiDesigner.Emit
{
    /// <summary>
    /// PySide6 emitter — reuses the entire PyQt6 implementation and only swaps the import
    /// prefix. The QtWidgets API surface used here is identical between the two bindings.
    /// Port of the C++ <c>PySide6Emitter</c>.
    /// </summary>
    public sealed class PySide6Emitter : PyQt6Emitter
    {
        public override string FrameworkName { get { return "PySide6"; } }
        protected override string ImportPrefix { get { return "PySide6"; } }
    }
}
