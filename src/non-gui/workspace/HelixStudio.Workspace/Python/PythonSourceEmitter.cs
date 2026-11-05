using System.Collections.Generic;
using System.Linq;
using System.Text;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Python.Emit;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Renders a <see cref="TypeModel"/> to a complete Python module body.
    /// Goes beyond the generic <see cref="PythonWriter"/> by knowing about
    /// <c>Interface / AbstractClass / Record / Enum / Concrete</c> and emitting
    /// the appropriate imports, decorators, and body shape.
    /// </summary>
    public static class PythonSourceEmitter
    {
        public static string Render(TypeModel type, CodegenPolicy policy = null)
        {
            if (type == null) return string.Empty;
            policy = policy ?? CodegenPolicy.Default;
            StringBuilder imports = new StringBuilder();
            StringBuilder body = new StringBuilder();
            HashSet<string> abcImports = new HashSet<string>();
            HashSet<string> typingImports = new HashSet<string>();
            bool needsDataclass = false;
            bool needsEnum = false;

            // Classify and build decorator / base list for this type.
            List<string> decorators = new List<string>();
            List<string> bases = new List<string>();

            foreach (TypeReference r in type.BaseTypes)
            {
                if (!string.IsNullOrEmpty(r.FullyQualifiedName))
                    bases.Add(r.SimpleName);
            }

            switch (type.TypeKind)
            {
                case TypeKind.Interface:
                    if (policy.InterfaceStyle == InterfaceStyle.Protocol)
                    {
                        bases.Insert(0, "Protocol");
                        typingImports.Add("Protocol");
                    }
                    else
                    {
                        bases.Insert(0, "ABC");
                        abcImports.Add("ABC");
                        abcImports.Add("abstractmethod");
                    }
                    break;
                case TypeKind.AbstractClass:
                    bases.Insert(0, "ABC");
                    abcImports.Add("ABC");
                    abcImports.Add("abstractmethod");
                    break;
                case TypeKind.Record:
                    decorators.Add("@dataclass");
                    needsDataclass = true;
                    break;
                case TypeKind.Enum:
                    bases.Insert(0, "Enum");
                    needsEnum = true;
                    break;
            }

            // Emit imports block.
            if (abcImports.Count > 0)
                imports.Append("from abc import ").Append(string.Join(", ", abcImports.OrderBy(x => x))).Append(policy.LineEnding);
            if (typingImports.Count > 0)
                imports.Append("from typing import ").Append(string.Join(", ", typingImports.OrderBy(x => x))).Append(policy.LineEnding);
            if (needsDataclass)
                imports.Append("from dataclasses import dataclass").Append(policy.LineEnding);
            if (needsEnum)
                imports.Append("from enum import Enum").Append(policy.LineEnding);
            if (imports.Length > 0) imports.Append(policy.LineEnding);

            // Emit decorators + class header.
            foreach (string d in decorators) body.Append(d).Append(policy.LineEnding);
            body.Append("class ").Append(type.Name);
            if (type.GenericParameters.Count > 0)
            {
                body.Append('[');
                for (int i = 0; i < type.GenericParameters.Count; i++)
                {
                    if (i > 0) body.Append(", ");
                    body.Append(type.GenericParameters[i].Name);
                }
                body.Append(']');
            }
            if (bases.Count > 0) body.Append('(').Append(string.Join(", ", bases)).Append(')');
            body.Append(':').Append(policy.LineEnding);

            // Emit enum values.
            if (type.TypeKind == TypeKind.Enum && type.EnumValues != null && type.EnumValues.Count > 0)
            {
                foreach (EnumValueModel v in type.EnumValues)
                {
                    body.Append(policy.Indent).Append(v.Name).Append(" = ")
                        .Append(string.IsNullOrEmpty(v.ValueExpression) ? "auto()" : v.ValueExpression)
                        .Append(policy.LineEnding);
                }
                return imports.ToString() + body.ToString();
            }

            // Emit members.
            bool anyMember = false;
            foreach (MemberModel m in type.Members)
            {
                RenderMember(body, type, m, policy, abcImports);
                anyMember = true;
            }
            if (!anyMember)
            {
                body.Append(policy.Indent).Append("pass").Append(policy.LineEnding);
            }

            // Re-emit imports if abstractmethod was needed but not previously added.
            if (abcImports.Contains("abstractmethod"))
            {
                if (imports.ToString().IndexOf("abstractmethod", System.StringComparison.Ordinal) < 0)
                {
                    StringBuilder newImports = new StringBuilder();
                    newImports.Append("from abc import ABC, abstractmethod").Append(policy.LineEnding);
                    newImports.Append(policy.LineEnding);
                    return newImports.ToString() + body.ToString();
                }
            }

            return imports.ToString() + body.ToString();
        }

        private static void RenderMember(StringBuilder body, TypeModel type, MemberModel m, CodegenPolicy policy, HashSet<string> abcImports)
        {
            // Decorators
            if (m.IsStatic && m.MemberKind == MemberKind.Method)
                body.Append(policy.Indent).Append("@staticmethod").Append(policy.LineEnding);
            if (m.MemberKind == MemberKind.Property)
                body.Append(policy.Indent).Append("@property").Append(policy.LineEnding);
            if (m.IsAbstract || type.TypeKind == TypeKind.AbstractClass && m.IsAbstract)
            {
                body.Append(policy.Indent).Append("@abstractmethod").Append(policy.LineEnding);
                abcImports.Add("ABC");
                abcImports.Add("abstractmethod");
            }

            body.Append(policy.Indent);
            if (m.IsAsync) body.Append("async ");
            body.Append("def ").Append(GetPythonMemberName(m)).Append('(');
            // self / cls first parameter
            if (!m.IsStatic) body.Append("self");
            // Method parameters
            foreach (MethodParameterModel p in m.Parameters)
            {
                if (body[body.Length - 1] != '(') body.Append(", ");
                body.Append(p.Name);
                if (!string.IsNullOrEmpty(p.TypeName)) body.Append(": ").Append(p.TypeName);
                if (p.HasDefaultValue && !string.IsNullOrEmpty(p.DefaultValue))
                    body.Append(string.IsNullOrEmpty(p.TypeName) ? "=" : " = ").Append(p.DefaultValue);
            }
            body.Append(')');
            if (!string.IsNullOrEmpty(m.ReturnType)) body.Append(" -> ").Append(m.ReturnType);
            body.Append(':').Append(policy.LineEnding);

            // Body
            body.Append(policy.Indent).Append(policy.Indent);
            if (m.IsAbstract) body.Append("...");
            else body.Append("raise NotImplementedError");
            body.Append(policy.LineEnding);
        }

        private static string GetPythonMemberName(MemberModel m)
        {
            if (m.MemberKind == MemberKind.Constructor) return "__init__";
            string name = m.Name ?? string.Empty;
            switch (m.AccessModifier)
            {
                case AccessModifier.Protected:
                    if (!name.StartsWith("_")) return "_" + name;
                    return name;
                case AccessModifier.Private:
                    if (!name.StartsWith("__")) return "__" + name.TrimStart('_');
                    return name;
                default:
                    return name;
            }
        }
    }
}
