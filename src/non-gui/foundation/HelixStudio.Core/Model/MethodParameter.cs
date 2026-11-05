namespace HelixPythonNamespace.Core.Model
{
    public class MethodParameter
    {
        public MethodParameter(string name, string typeName)
        {
            Name = name;
            TypeName = typeName;
        }

        public string Name { get; private set; }
        public string TypeName { get; private set; }
    }
}
