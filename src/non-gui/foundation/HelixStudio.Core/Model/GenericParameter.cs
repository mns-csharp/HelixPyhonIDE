using System.Collections.Generic;

namespace HelixPythonNamespace.Core.Model
{
    public class GenericParameter
    {
        public GenericParameter(string name, List<string> constraints)
        {
            Name = name;
            Constraints = constraints ?? new List<string>();
        }

        public GenericParameter(string name)
            : this(name, null)
        {
        }

        public string Name { get; private set; }
        public List<string> Constraints { get; private set; }
    }
}
