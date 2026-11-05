namespace HelixPythonNamespace.Metrics
{
    using System.Collections.Generic;

    /// <summary>An input source file: path + full text.</summary>
    public struct MetricsSource
    {
        public string FilePath;
        public string Text;

        public MetricsSource(string filePath, string text)
        {
            FilePath = filePath;
            Text = text;
        }
    }

    public sealed class FunctionMetric
    {
        public string Name;
        public string FilePath;
        public int Line;
        public int Loc;
        public int StatementCount;
        public int ParameterCount;
        public int CyclomaticComplexity = 1;
        public int MaxNestingDepth;
    }

    public sealed class ClassMetric
    {
        public string Name;
        public string FilePath;
        public int Line;
        public int MethodCount;
        public int Loc;
    }

    public sealed class FileMetric
    {
        public string FilePath;
        public int TotalLines;
        public int CodeLines;
        public int CommentLines;
        public int BlankLines;
        public int FunctionCount;
        public int ClassCount;
    }

    public sealed class MetricsReport
    {
        public List<FileMetric> Files = new List<FileMetric>();
        public List<ClassMetric> Classes = new List<ClassMetric>();
        public List<FunctionMetric> Functions = new List<FunctionMetric>();
    }
}
