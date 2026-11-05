namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// One suggestion in the completion popup. <see cref="Label"/> is what the user
    /// sees; <see cref="InsertText"/> is what actually gets typed when committed
    /// (usually equal to Label, but can include a snippet, trailing <c>(</c>, etc.).
    /// <see cref="Documentation"/> feeds the side doc panel.
    /// </summary>
    public sealed class CompletionItem
    {
        public string Label { get; }
        public CompletionKind Kind { get; }
        public string InsertText { get; }
        public string Detail { get; }          // one-line signature / summary
        public string Documentation { get; }   // longer description, may be null

        public CompletionItem(string label, CompletionKind kind, string insertText = null,
            string detail = null, string documentation = null)
        {
            Label = label ?? string.Empty;
            Kind = kind;
            InsertText = insertText ?? Label;
            Detail = detail;
            Documentation = documentation;
        }

        public override string ToString() => Kind + " " + Label;
    }
}
