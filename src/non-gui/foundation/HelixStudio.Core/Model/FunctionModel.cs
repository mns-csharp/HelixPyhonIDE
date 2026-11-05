namespace HelixPythonNamespace.Core.Model
{
    /// <summary>
    /// A top-level (module-scope) Python function. Distinct from
    /// <see cref="MemberModel"/>, which represents class methods. Used by the
    /// left project tree to surface free functions for "open &amp; edit" without
    /// forcing the user to navigate via File → Open.
    /// </summary>
    public sealed class FunctionModel
    {
        public string Name { get; set; }
        public string Namespace { get; set; }
        public string SourceFilePath { get; set; }
        public int SourceLineNumber { get; set; }
        public bool IsAsync { get; set; }

        /// <summary>The function's docstring, when it has one. Shown as the Solution Explorer
        /// tooltip and anywhere else a symbol's documentation is surfaced.</summary>
        public string Documentation { get; set; }

        /// <summary>Owning project — back-reference, mirrors <see cref="TypeModel.Project"/>.</summary>
        public ProjectModel Project { get; set; }

        public string FullyQualifiedName
        {
            get
            {
                return string.IsNullOrEmpty(Namespace) ? (Name ?? string.Empty) : Namespace + "." + Name;
            }
        }

        public override string ToString() { return FullyQualifiedName; }
    }
}
