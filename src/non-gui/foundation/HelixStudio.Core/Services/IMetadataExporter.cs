namespace HelixPythonNamespace.Core.Services
{
    using HelixPythonNamespace.Core.Model;

    public interface IMetadataExporter
    {
        void ExportJson(Diagram diagram, string filePath);
    }
}
