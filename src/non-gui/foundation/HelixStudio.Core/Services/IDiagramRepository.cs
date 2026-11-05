namespace HelixPythonNamespace.Core.Services
{
    using HelixPythonNamespace.Core.Model;

    public interface IDiagramRepository
    {
        Diagram Load(string filePath);
        void Save(Diagram diagram, string filePath);
    }
}
