namespace HelixPythonNamespace.Refactoring.Structural
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Refactoring;

    /// <summary>
    /// Emits <see cref="StructuralCandidate"/> replacements for the SOLID structural
    /// family (S1 SRP, S2 DIP, S3 ISP — each forward + reverse). Port of the C++
    /// <c>helix::refactoring::structural::SolidEmitter</c>. All output uses <c>'\n'</c>
    /// line endings; the facade re-applies host line endings.
    /// </summary>
    public static class SolidEmitter
    {
        // S2 DIP forward: plain class -> Protocol + class implementing it.
        public static StructuralCandidate EmitDipIntroduce(DipData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.S2_DipIntroduce;
            c.Label = "Introduce abstraction: class " + d.ClassName + " : " + d.ProtocolName;

            List<string> lines = new List<string>();
            lines.Add("class " + d.ProtocolName + "(Protocol):");
            foreach (string h in d.MethodHeaders) lines.Add(h + " ...");
            lines.Add("");
            lines.Add("");
            lines.Add("class " + d.ClassName + "(" + d.ProtocolName + "):");
            foreach (string bl in d.ClassBodyLines) lines.Add(bl);
            c.ReplacementText = BodySlicer.Join(lines);

            c.RequiredImports.Add("from typing import Protocol");
            c.AuxEdits.Add("from typing import Protocol");
            c.Caveats.Add(new CandidateIssue(
                "STR-DIP-IMPORT",
                "Requires `from typing import Protocol` at module top; the abstraction " +
                "is structural (no runtime base change of behavior)."));
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // S2 DIP reverse: Protocol + impl -> bare class.
        public static StructuralCandidate EmitDipInline(DipData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.S2_DipInline;
            c.Label = "Inline abstraction: " + d.ProtocolName + " + " + d.ClassName +
                      " -> class " + d.ClassName;

            List<string> lines = new List<string>();
            lines.Add("class " + d.ClassName + ":");
            foreach (string bl in d.ClassBodyLines) lines.Add(bl);
            c.ReplacementText = BodySlicer.Join(lines);

            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // S1 SRP forward: god class -> one class per cohesion cluster.
        public static StructuralCandidate EmitSrpSplit(SrpSplitData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.S1_SrpSplit;
            string names = "";
            for (int i = 0; i < d.Parts.Count; i++)
            {
                if (i != 0) names += ", ";
                names += d.Parts[i].ClassName;
            }
            c.Label = "SRP split: cohesion clusters -> " + names;

            List<string> lines = new List<string>();
            for (int i = 0; i < d.Parts.Count; i++)
            {
                if (i != 0) { lines.Add(""); lines.Add(""); }
                lines.Add("class " + d.Parts[i].ClassName + ":");
                foreach (string bl in d.Parts[i].BodyLines) lines.Add(bl);
            }
            c.ReplacementText = BodySlicer.Join(lines);

            c.Lossy = true;
            c.Caveats.Add(new CandidateIssue(
                "STR-SRP-LOSSY",
                "Splits a multi-responsibility class into one class per cohesion cluster " +
                "(grouped by shared self.<field> access); the merge reverse is not a " +
                "byte-exact inverse but preserves the full method surface."));
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // S1 SRP reverse: run of prefix-sharing classes -> one merged class.
        public static StructuralCandidate EmitSrpMerge(SrpMergeData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.S1_SrpMerge;
            c.Label = "SRP merge: prefix-sharing classes -> class " + d.MergedName;

            List<string> lines = new List<string>();
            lines.Add("class " + d.MergedName + ":");
            foreach (string bl in d.BodyLines) lines.Add(bl);
            c.ReplacementText = BodySlicer.Join(lines);

            c.Lossy = true;
            c.Caveats.Add(new CandidateIssue(
                "STR-SRP-LOSSY", "Merges cohesion-cluster classes back into one class."));
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // S3 ISP forward: fat Protocol -> one role protocol per method.
        public static StructuralCandidate EmitIspSegregate(IspSegregateData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.S3_IspSegregate;
            string names = "";
            for (int i = 0; i < d.Roles.Count; i++)
            {
                if (i != 0) names += ", ";
                names += d.Roles[i].ProtocolName;
            }
            c.Label = "ISP segregate: fat Protocol -> " + names;

            List<string> lines = new List<string>();
            for (int i = 0; i < d.Roles.Count; i++)
            {
                if (i != 0) { lines.Add(""); lines.Add(""); }
                lines.Add("class " + d.Roles[i].ProtocolName + "(Protocol):");
                foreach (string l in d.Roles[i].MethodLines) lines.Add(l);
            }
            c.ReplacementText = BodySlicer.Join(lines);

            c.Lossy = true;
            c.Caveats.Add(new CandidateIssue(
                "STR-ISP-LOSSY",
                "Segregates a fat interface into one role protocol per method; the " +
                "collapse reverse is not a byte-exact inverse but preserves the full " +
                "method surface. Implementors should inherit only the roles they need."));
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // S3 ISP reverse: run of prefix-sharing protocols -> one fat protocol.
        public static StructuralCandidate EmitIspCollapse(IspCollapseData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.S3_IspCollapse;
            c.Label = "ISP collapse: role protocols -> class " + d.ProtocolName + "(Protocol)";

            List<string> lines = new List<string>();
            lines.Add("class " + d.ProtocolName + "(Protocol):");
            foreach (string l in d.MethodLines) lines.Add(l);
            c.ReplacementText = BodySlicer.Join(lines);

            c.Lossy = true;
            c.Caveats.Add(new CandidateIssue(
                "STR-ISP-LOSSY", "Collapses role protocols back into one fat protocol."));
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }
    }
}
