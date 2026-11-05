namespace HelixPythonNamespace.Core.Services
{
    using System.Threading;
    using HelixPythonNamespace.Core.Model;

    public interface IAnalysisEngine
    {
        AnalysisReport Analyze(Diagram diagram, CancellationToken cancel = default(CancellationToken));
    }
}
