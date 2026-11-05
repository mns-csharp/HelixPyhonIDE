using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// A per-language completion provider. <see cref="CompletionController"/> selects the engine by
    /// the active file's extension (<c>.py</c> → <see cref="PythonCompletionEngine"/>,
    /// <c>.cu</c>/<c>.cuh</c> → <see cref="CudaCompletionEngine"/>), so the same editor UI drives
    /// completion for both languages.
    /// </summary>
    public interface ICompletionEngine
    {
        CompletionContext Classify(string source, int caretOffset);

        List<CompletionItem> GetCompletions(string source, int caretOffset, string filePath, SolutionModel solution);

        List<CompletionItem> GetCompletions(string source, int caretOffset, string filePath, SolutionModel solution, CompletionContext ctx);
    }
}
