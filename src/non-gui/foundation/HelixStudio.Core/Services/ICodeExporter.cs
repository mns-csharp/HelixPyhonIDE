namespace HelixPythonNamespace.Core.Services
{
    using HelixPythonNamespace.Core.Model;

    public interface ICodeExporter
    {
        void ExportCSharp(Diagram diagram, ExportOptions options);
    }
}
