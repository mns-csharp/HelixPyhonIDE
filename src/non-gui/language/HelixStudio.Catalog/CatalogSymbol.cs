namespace HelixPythonNamespace.Catalog
{
    using System.Collections.Generic;

    public sealed class CatalogParam
    {
        public string Name = "";
        public string TypeAnnotation = "";
        public string DefaultValue = "";
        public string Description = "";
    }

    /// <summary>A catalogued external symbol (e.g. <c>os.path.join</c>). Port of the C++ <c>CatalogSymbol</c>.</summary>
    public sealed class CatalogSymbol
    {
        public string ModuleFqn = "";     // "os.path"
        public string Name = "";          // "join"
        public string Kind = "";          // function | class | method | module | data
        public string Signature = "";
        public string Summary = "";
        public string ReturnSummary = "";
        public List<CatalogParam> Params = new List<CatalogParam>();
        public string FullDoc = "";

        public string FullyQualifiedName()
        {
            if (string.IsNullOrEmpty(ModuleFqn))
            {
                return Name;
            }
            return ModuleFqn + "." + Name;
        }
    }
}
