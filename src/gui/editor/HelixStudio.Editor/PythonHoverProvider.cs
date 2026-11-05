namespace HelixPythonNamespace.Editor
{
    using HelixPythonNamespace.Editor.Completion;

    /// <summary>
    /// Parser-backed fallback used when the completion controller is unavailable. It deliberately
    /// shares PythonCompletionEngine's Quick Info path so hover, F1 and completion documentation
    /// cannot disagree about local symbols.
    /// </summary>
    public sealed class PythonHoverProvider
    {
        public string GetHoverInfo(string sourceCode, int line, int column, string word, string filePath)
        {
            if (string.IsNullOrEmpty(sourceCode) || string.IsNullOrEmpty(word)) return null;
            int offset = 0;
            int currentLine = 1;
            while (offset < sourceCode.Length && currentLine < line)
                if (sourceCode[offset++] == '\n') currentLine++;
            offset = System.Math.Min(sourceCode.Length, offset + System.Math.Max(0, column - 1));
            var quick = new PythonCompletionEngine().GetQuickInfo(sourceCode, offset, filePath, null);
            if (quick == null || quick.Item == null) return null;
            string detail = quick.Item.Detail;
            string doc = quick.Item.Documentation;
            if (string.IsNullOrEmpty(detail)) return doc;
            return string.IsNullOrEmpty(doc) ? detail : detail + "\n\n" + doc;
        }
    }
}
