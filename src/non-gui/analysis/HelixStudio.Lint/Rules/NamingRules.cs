namespace HelixPythonNamespace.Lint.Rules
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Python.Syntax;
    using HelixPythonNamespace.Python.Text;
    using HelixPythonNamespace.VirtualCompiler.Symtable;

    internal static class NameStyle
    {
        public static bool IsPascalCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (!char.IsUpper(name[0])) return false;
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] == '_') return false;
            }
            return true;
        }

        public static bool IsSnakeCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            int i = 0;
            while (i < name.Length && name[i] == '_') i++;       // leading underscores ok
            int end = name.Length;
            while (end > i && name[end - 1] == '_') end--;        // trailing underscore ok
            for (int k = i; k < end; k++)
            {
                if (char.IsUpper(name[k])) return false;
            }
            return true;
        }
    }

    /// <summary>N001: Class name should be PascalCase.</summary>
    public sealed class ClassPascalCaseRule : ILintRule
    {
        public string Code { get { return "N001"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            LintHelpers.WalkStatements(module.Statements, stmt =>
            {
                ClassDefSyntax cls = stmt as ClassDefSyntax;
                if (cls == null) return;
                if (!NameStyle.IsPascalCase(cls.Name))
                {
                    output.Add(new LintFinding
                    {
                        Code = Code,
                        Severity = AnalysisSeverity.Info,
                        Message = "Class name '" + cls.Name + "' should be PascalCase.",
                        Span = cls.Span,
                    });
                }
            });
        }
    }

    /// <summary>N002: Function/method name should be snake_case.</summary>
    public sealed class FunctionSnakeCaseRule : ILintRule
    {
        public string Code { get { return "N002"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            LintHelpers.WalkStatements(module.Statements, stmt =>
            {
                FunctionDefSyntax fn = stmt as FunctionDefSyntax;
                if (fn == null) return;
                if (!NameStyle.IsSnakeCase(fn.Name))
                {
                    output.Add(new LintFinding
                    {
                        Code = Code,
                        Severity = AnalysisSeverity.Info,
                        Message = "Function name '" + fn.Name + "' should be snake_case.",
                        Span = fn.Span,
                    });
                }
            });
        }
    }

    /// <summary>N003: Name shadows a Python builtin (top-level def/class, parameter, assignment).</summary>
    public sealed class ShadowsBuiltinRule : ILintRule
    {
        public string Code { get { return "N003"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            System.Action<string, SourceSpan> reportIfShadows = (name, span) =>
            {
                if (string.IsNullOrEmpty(name) || name == "_") return;
                if (Builtins.IsBuiltin(name))
                {
                    output.Add(new LintFinding
                    {
                        Code = "N003",
                        Severity = AnalysisSeverity.Warning,
                        Message = "'" + name + "' shadows a Python builtin.",
                        Span = span,
                    });
                }
            };

            // Top-level function/class names. Methods inside class bodies are skipped
            // — `def map(self, fn)` is a common idiom, not a shadow issue.
            foreach (StatementSyntax stmt in module.Statements)
            {
                if (stmt is FunctionDefSyntax fn) reportIfShadows(fn.Name, fn.Span);
                else if (stmt is ClassDefSyntax cls) reportIfShadows(cls.Name, cls.Span);
            }

            LintHelpers.WalkStatements(module.Statements, stmt =>
            {
                if (stmt is FunctionDefSyntax fn)
                {
                    foreach (ParameterSyntax p in fn.Parameters)
                    {
                        if (p.Name == "self" || p.Name == "cls") continue;
                        reportIfShadows(p.Name, p.Span);
                    }
                }
                else if (stmt is AssignmentStatement assign)
                {
                    foreach (ExpressionSyntax target in assign.Targets)
                    {
                        NameExpression name = target as NameExpression;
                        if (name != null) reportIfShadows(name.Name, name.Span);
                    }
                }
            });
        }
    }
}
