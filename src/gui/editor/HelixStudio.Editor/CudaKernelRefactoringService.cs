using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.Editor
{
    public sealed class CudaRefactoringResult
    {
        public bool Succeeded { get; internal set; }
        public string Source { get; internal set; }
        public string Message { get; internal set; }
    }

    /// <summary>
    /// Conservative, reversible CPU/CUDA refactoring for the common one-element-per-thread shape.
    /// It deliberately refuses loops whose independence cannot be established syntactically.
    /// </summary>
    public sealed class CudaKernelRefactoringService
    {
        private const string BeginMarker = "// PYOPTOOL-CUDA-BEGIN:";
        private const string EndMarker = "// PYOPTOOL-CUDA-END";

        private static readonly Regex ForLoop = new Regex(
            @"for\s*\(\s*(?:int|size_t|unsigned(?:\s+int)?)\s+(?<i>[A-Za-z_]\w*)\s*=\s*0\s*;\s*\k<i>\s*<\s*(?<n>[A-Za-z_]\w*)\s*;\s*(?:\+\+\k<i>|\k<i>\+\+)\s*\)\s*\{(?<body>[\s\S]*?)\}",
            RegexOptions.Compiled);

        public CudaRefactoringResult ConvertToCuda(string source, int selectionStart, int selectionLength)
        {
            if (source == null) return Fail(source, "There is no source to convert.");
            if (source.Contains(BeginMarker)) return Fail(source, "This code already contains a HelixStudio CUDA conversion.");

            int start = selectionLength > 0 ? selectionStart : 0;
            int length = selectionLength > 0 ? selectionLength : source.Length;
            if (start < 0 || length < 0 || start + length > source.Length)
                return Fail(source, "The selected source range is invalid.");

            string selected = source.Substring(start, length);
            Match loop = ForLoop.Match(selected);
            if (!loop.Success)
                return Fail(source, "Select a canonical independent loop: for (int i = 0; i < count; ++i) { ... }.");

            string index = loop.Groups["i"].Value;
            string count = loop.Groups["n"].Value;
            string body = loop.Groups["body"].Value.Trim();
            string hazard = FindHazard(body, index);
            if (hazard != null) return Fail(source, hazard);

            string indent = LeadingWhitespaceAt(selected, loop.Index);
            int replaceStart = loop.Index - indent.Length;
            string originalText = selected.Substring(replaceStart, indent.Length + loop.Length);
            string id = "kernel_" + StableHash(loop.Value).ToString("x8");
            string original = Convert.ToBase64String(Encoding.UTF8.GetBytes(originalText));
            string bodyIndent = indent + "    ";
            string kernelBody = Reindent(body, bodyIndent);

            var replacement = new StringBuilder();
            replacement.Append(indent).Append(BeginMarker).Append(original).AppendLine();
            replacement.Append(indent).AppendLine("#if defined(__CUDACC__)");
            replacement.Append(indent).Append("auto ").Append(id).AppendLine(" = [=] __device__ (int " + index + ") mutable {");
            replacement.Append(kernelBody).AppendLine();
            replacement.Append(indent).AppendLine("};");
            replacement.Append(indent).Append("thrust::for_each_n(thrust::device, thrust::counting_iterator<int>(0), ")
                .Append(count).Append(", ").Append(id).AppendLine(");");
            replacement.Append(indent).AppendLine("#else");
            replacement.Append(Reindent(loop.Value.Trim(), indent)).AppendLine();
            replacement.Append(indent).AppendLine("#endif");
            replacement.Append(indent).Append(EndMarker);

            string convertedSelection = selected.Substring(0, replaceStart) + replacement + selected.Substring(loop.Index + loop.Length);
            string converted = source.Substring(0, start) + convertedSelection + source.Substring(start + length);
            return Success(converted, "Converted an independent map loop to a CUDA device operation with an MSVC/GCC CPU fallback.");
        }

        public CudaRefactoringResult ConvertToCpu(string source)
        {
            if (source == null) return Fail(source, "There is no source to convert.");
            int begin = source.IndexOf(BeginMarker, StringComparison.Ordinal);
            if (begin < 0) return Fail(source, "No reversible HelixStudio CUDA conversion was found.");
            int blockStart = begin;
            while (blockStart > 0 && (source[blockStart - 1] == ' ' || source[blockStart - 1] == '\t')) blockStart--;
            int payloadStart = begin + BeginMarker.Length;
            int lineEnd = source.IndexOf('\n', payloadStart);
            int end = source.IndexOf(EndMarker, lineEnd < 0 ? payloadStart : lineEnd, StringComparison.Ordinal);
            if (lineEnd < 0 || end < 0) return Fail(source, "The CUDA provenance markers are incomplete.");

            string payload = source.Substring(payloadStart, lineEnd - payloadStart).Trim();
            string original;
            try { original = Encoding.UTF8.GetString(Convert.FromBase64String(payload)); }
            catch (FormatException) { return Fail(source, "The CUDA provenance payload is damaged; no source was changed."); }

            int markerEnd = end + EndMarker.Length;
            string restored = source.Substring(0, blockStart) + original + source.Substring(markerEnd);
            return Success(restored, "Restored the exact original CPU loop.");
        }

        private static string FindHazard(string body, string index)
        {
            if (Regex.IsMatch(body, @"\b(break|continue|goto|return)\b"))
                return "The loop changes control flow and cannot be converted safely.";
            if (Regex.IsMatch(body, @"(\+\+|--)\s*[A-Za-z_]\w*|[A-Za-z_]\w*\s*(\+\+|--)"))
                return "The loop updates shared scalar state; use a reduction before converting.";
            if (Regex.IsMatch(body, @"\b[A-Za-z_]\w*\s*(?:\+=|-=|\*=|/=|%=|&=|\|=|\^=)"))
                return "The loop contains an accumulation or shared update; use a reduction before converting.";

            foreach (Match assignment in Regex.Matches(body, @"(?<lhs>[A-Za-z_]\w*(?:\s*\[[^\]]+\])?)\s*=(?!=)"))
            {
                string lhs = assignment.Groups["lhs"].Value;
                if (!lhs.Contains("[") || !Regex.IsMatch(lhs, @"\[\s*" + Regex.Escape(index) + @"\s*\]"))
                    return "Every iteration must write only to its own [" + index + "] output element.";
            }
            return null;
        }

        private static string LeadingWhitespaceAt(string text, int position)
        {
            int line = position;
            while (line > 0 && text[line - 1] != '\n') line--;
            int p = line;
            while (p < position && (text[p] == ' ' || text[p] == '\t')) p++;
            return text.Substring(line, p - line);
        }

        private static string Reindent(string text, string indent)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            int min = lines.Where(x => x.Trim().Length > 0).Select(CountIndent).DefaultIfEmpty(0).Min();
            return string.Join(Environment.NewLine, lines.Select(x => x.Trim().Length == 0 ? "" : indent + x.Substring(Math.Min(min, x.Length))));
        }

        private static int CountIndent(string value)
        {
            int i = 0; while (i < value.Length && (value[i] == ' ' || value[i] == '\t')) i++; return i;
        }

        private static uint StableHash(string value)
        {
            unchecked { uint hash = 2166136261; foreach (char c in value) { hash ^= c; hash *= 16777619; } return hash; }
        }

        private static CudaRefactoringResult Success(string source, string message)
        {
            return new CudaRefactoringResult { Succeeded = true, Source = source, Message = message };
        }

        private static CudaRefactoringResult Fail(string source, string message)
        {
            return new CudaRefactoringResult { Succeeded = false, Source = source, Message = message };
        }
    }
}
