namespace HelixPythonNamespace.Refactoring.Structural
{
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.Refactoring;

    /// <summary>
    /// Emitters for the paradigm family (procedural &lt;-&gt; OOP). Port of the C++
    /// <c>structural::ParadigmEmitter</c>. Each method turns a MatchData carried from
    /// <see cref="ParadigmMatcher"/> into a <see cref="StructuralCandidate"/> whose
    /// <see cref="StructuralCandidate.ReplacementText"/> uses '\n' line endings only (the
    /// facade re-applies the host's line endings).
    /// </summary>
    public static class ParadigmEmitter
    {
        private static string JoinParams(IList<string> fields)
        {
            StringBuilder outSb = new StringBuilder();
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) outSb.Append(", ");
                outSb.Append(fields[i]);
            }
            return outSb.ToString();
        }

        /// <summary>Forward: dict factory function -&gt; data class.</summary>
        public static StructuralCandidate EmitDictToClass(DictClassData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.O2_DictToClass;
            c.Label = "To class: " + d.FuncName + "() -> class " + d.ClassName;

            StringBuilder outSb = new StringBuilder();
            outSb.Append("class ").Append(d.ClassName).Append(":\n");
            outSb.Append("    def __init__(self, ").Append(JoinParams(d.Fields)).Append("):\n");
            foreach (string f in d.Fields)
            {
                outSb.Append("        self.").Append(f).Append(" = ").Append(f).Append("\n");
            }
            string text = outSb.ToString();
            if (text.Length > 0 && text[text.Length - 1] == '\n')
            {
                text = text.Substring(0, text.Length - 1);  // no trailing newline
            }
            c.ReplacementText = text;

            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        /// <summary>Reverse: data class -&gt; dict factory function.</summary>
        public static StructuralCandidate EmitClassToDict(DictClassData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.O2_ClassToDict;
            c.Label = "To dict factory: class " + d.ClassName + " -> " + d.FuncName + "()";

            StringBuilder outSb = new StringBuilder();
            outSb.Append("def ").Append(d.FuncName).Append("(").Append(JoinParams(d.Fields)).Append("):\n");
            outSb.Append("    return {");
            for (int i = 0; i < d.Fields.Count; i++)
            {
                if (i > 0) outSb.Append(", ");
                outSb.Append("\"").Append(d.Fields[i]).Append("\": ").Append(d.Fields[i]);
            }
            outSb.Append("}");
            c.ReplacementText = outSb.ToString();

            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        /// <summary>O1 forward: free functions -&gt; class with methods.</summary>
        public static StructuralCandidate EmitFunctionsToClass(FunctionsClassData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.O1_FunctionsToClass;
            c.Label = "To class: " + d.Prefix + "_*() -> class " + d.ClassName;

            List<string> lines = new List<string>();
            lines.Add("class " + d.ClassName + ":");
            for (int i = 0; i < d.Methods.Count; i++)
            {
                if (i > 0) lines.Add("");  // one blank line between methods
                lines.Add("    def " + d.Methods[i].MethodName + "(" +
                          d.Methods[i].ParamsText + "):");
                foreach (string bl in BodySlicer.Indent(d.Methods[i].BodyLines, 8))
                {
                    lines.Add(bl);
                }
            }
            c.ReplacementText = BodySlicer.Join(lines);
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        /// <summary>O1 reverse: class with methods -&gt; free functions.</summary>
        public static StructuralCandidate EmitClassToFunctions(FunctionsClassData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.O1_ClassToFunctions;
            c.Label = "To functions: class " + d.ClassName + " -> " + d.Prefix + "_*()";

            List<string> lines = new List<string>();
            for (int i = 0; i < d.Methods.Count; i++)
            {
                if (i > 0) lines.Add("");  // one blank line between functions
                lines.Add("def " + d.Methods[i].FuncName + "(" +
                          d.Methods[i].ParamsText + "):");
                foreach (string bl in BodySlicer.Indent(d.Methods[i].BodyLines, 4))
                {
                    lines.Add(bl);
                }
            }
            c.ReplacementText = BodySlicer.Join(lines);
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }
    }
}
