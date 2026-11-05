using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Core.Services;
using HelixPythonNamespace.Python.Emit;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// Python-only compliance writer. Applies downstream fixes when upstream
    /// contracts change. MVP: regenerates the whole file for the target type
    /// via <see cref="PythonSourceEmitter"/>. CST-precise edits with
    /// surrounding-trivia preservation are Phase C work.
    /// </summary>
    public sealed class ComplianceUpdateService
    {
        public void ApplyMissingMemberFix(TypeModel type, MemberModel missingMember)
        {
            if (type == null || missingMember == null) return;
            bool alreadyPresent = false;
            for (int i = 0; i < type.Members.Count; i++)
            {
                if (string.Equals(type.Members[i].Name, missingMember.Name, StringComparison.Ordinal))
                { alreadyPresent = true; break; }
            }
            if (!alreadyPresent)
            {
                MemberModel stub = new MemberModel(missingMember.Name, missingMember.MemberKind);
                stub.ReturnType = missingMember.ReturnType;
                stub.AccessModifier = missingMember.AccessModifier;
                foreach (MethodParameterModel p in missingMember.Parameters)
                {
                    MethodParameterModel copy = new MethodParameterModel(p.Name, p.TypeName);
                    copy.HasDefaultValue = p.HasDefaultValue;
                    copy.DefaultValue = p.DefaultValue;
                    stub.Parameters.Add(copy);
                }
                type.Members.Add(stub);
            }
            Rewrite(type);
        }

        public void ApplyRenameFix(TypeModel type, string oldMemberName, string newMemberName)
        {
            if (type == null || string.IsNullOrEmpty(oldMemberName) || string.IsNullOrEmpty(newMemberName)) return;
            foreach (MemberModel m in type.Members)
            {
                if (string.Equals(m.Name, oldMemberName, StringComparison.Ordinal))
                    m.Name = newMemberName;
            }
            Rewrite(type);
        }

        public void ApplySignatureChangeFix(TypeModel type, MemberModel updatedSignature)
        {
            if (type == null || updatedSignature == null) return;
            for (int i = 0; i < type.Members.Count; i++)
            {
                if (string.Equals(type.Members[i].Name, updatedSignature.Name, StringComparison.Ordinal))
                {
                    type.Members[i].Parameters.Clear();
                    foreach (MethodParameterModel p in updatedSignature.Parameters)
                    {
                        MethodParameterModel copy = new MethodParameterModel(p.Name, p.TypeName);
                        copy.HasDefaultValue = p.HasDefaultValue;
                        copy.DefaultValue = p.DefaultValue;
                        type.Members[i].Parameters.Add(copy);
                    }
                    type.Members[i].ReturnType = updatedSignature.ReturnType;
                }
            }
            Rewrite(type);
        }

        public int ApplyAutoFixes(ComplianceReport report)
        {
            if (report == null) return 0;
            int applied = 0;
            foreach (ComplianceGap gap in report.Gaps)
            {
                if (gap.FixKind != ComplianceFixKind.AutoFix) continue;
                if (gap.DownstreamType == null || gap.UpstreamMember == null) continue;
                switch (gap.ChangeKind)
                {
                    case UpstreamChangeKind.MemberAdded:
                        ApplyMissingMemberFix(gap.DownstreamType, gap.UpstreamMember);
                        gap.IsResolved = true;
                        gap.FixDescription = "Added missing stub for " + gap.UpstreamMember.Name;
                        applied++;
                        break;
                    case UpstreamChangeKind.MemberRenamed:
                        if (gap.DownstreamMember != null)
                        {
                            ApplyRenameFix(gap.DownstreamType, gap.DownstreamMember.Name, gap.UpstreamMember.Name);
                            gap.IsResolved = true;
                            gap.FixDescription = "Renamed " + gap.DownstreamMember.Name + " -> " + gap.UpstreamMember.Name;
                            applied++;
                        }
                        break;
                    case UpstreamChangeKind.SignatureChanged:
                    case UpstreamChangeKind.ReturnTypeChanged:
                        ApplySignatureChangeFix(gap.DownstreamType, gap.UpstreamMember);
                        gap.IsResolved = true;
                        gap.FixDescription = "Updated signature of " + gap.UpstreamMember.Name;
                        applied++;
                        break;
                }
            }
            return applied;
        }

        private static void Rewrite(TypeModel type)
        {
            if (string.IsNullOrEmpty(type.SourceFilePath)) return;
            string body = PythonSourceEmitter.Render(type, CodegenPolicy.Default);
            try { File.WriteAllText(type.SourceFilePath, body); } catch { }
        }
    }
}
