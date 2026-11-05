using System;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>A completion engine plus the file predicate that activates it.</summary>
    public sealed class CompletionEngineContribution
    {
        private readonly Func<string, bool> _supportsFile;

        public CompletionEngineContribution(ICompletionEngine engine, Func<string, bool> supportsFile)
        {
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _supportsFile = supportsFile ?? throw new ArgumentNullException(nameof(supportsFile));
        }

        public ICompletionEngine Engine { get; private set; }

        public bool Supports(string filePath)
        {
            return _supportsFile(filePath);
        }
    }
}
