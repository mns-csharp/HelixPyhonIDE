namespace HelixPythonNamespace.Python.Emit
{
    public enum InterfaceStyle
    {
        Protocol,          // typing.Protocol
        AbstractBaseClass  // abc.ABC + @abstractmethod
    }

    public sealed class CodegenPolicy
    {
        public InterfaceStyle InterfaceStyle { get; set; } = InterfaceStyle.Protocol;
        public string Indent { get; set; } = "    "; // four spaces
        public string LineEnding { get; set; } = "\n";
        public bool EmitFromFutureAnnotations { get; set; } = false;

        public static CodegenPolicy Default => new CodegenPolicy();
    }
}
