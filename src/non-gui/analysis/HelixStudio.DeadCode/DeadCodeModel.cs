namespace HelixPythonNamespace.DeadCode
{
    using System.Collections.Generic;

    public enum DeadCodeKind
    {
        UnusedFunction,
        UnusedClass,
        UnusedVariable,
        UnusedImport,
        UnusedMethod,
        UnreachableCode,
    }

    public sealed class DeadCodeFinding
    {
        public DeadCodeKind Kind = DeadCodeKind.UnusedFunction;
        public string FilePath = "";
        public int Line;
        public int Column;
        public string SymbolName = "";
        public string Message = "";
    }

    public sealed class DeadCodeReport
    {
        public List<DeadCodeFinding> Findings = new List<DeadCodeFinding>();
        public int FilesAnalyzed;
        public bool LinkSkipped;
    }

    public static class DeadCodeKindLabels
    {
        public static string Label(DeadCodeKind kind)
        {
            switch (kind)
            {
                case DeadCodeKind.UnusedFunction: return "Unused function";
                case DeadCodeKind.UnusedClass: return "Unused class";
                case DeadCodeKind.UnusedVariable: return "Unused variable";
                case DeadCodeKind.UnusedImport: return "Unused import";
                case DeadCodeKind.UnusedMethod: return "Unused method";
                case DeadCodeKind.UnreachableCode: return "Unreachable code";
            }
            return "Dead";
        }
    }
}
