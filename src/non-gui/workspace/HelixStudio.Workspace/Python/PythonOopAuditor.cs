using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>The strict-OOP rule a module-level statement violates.</summary>
    public enum OopViolationKind
    {
        /// <summary>A module-level <c>def</c> that is not the entry module's <c>main()</c>.</summary>
        ModuleLevelFunction = 0,

        /// <summary>Executable module-level code (loops, calls, with/try, ...).</summary>
        ModuleLevelStatement = 1,

        /// <summary>A module-level assignment that is neither a dunder nor an ALL_CAPS constant.</summary>
        NonConstantAssignment = 2,

        /// <summary>The file could not be parsed, so OOP compliance cannot be verified.</summary>
        UnparsableFile = 3,
    }

    /// <summary>One strict-OOP finding in one file.</summary>
    public sealed class OopViolation
    {
        public OopViolation(string filePath, int line, OopViolationKind kind, string name, string message)
        {
            FilePath = filePath ?? string.Empty;
            Line = line;
            Kind = kind;
            Name = name ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public string FilePath { get; }

        /// <summary>1-based line of the offending statement (0 when unknown).</summary>
        public int Line { get; }

        public OopViolationKind Kind { get; }

        /// <summary>The offending symbol name when one exists (function or target name).</summary>
        public string Name { get; }

        public string Message { get; }

        public override string ToString()
        {
            return FilePath + "(" + Line + "): " + Kind + ": " + Message;
        }
    }

    /// <summary>
    /// Audits Python modules against the strict-OOP shape the Import Engine
    /// guarantees: a module may contain only a docstring, imports, class
    /// definitions, dunder / ALL_CAPS constant assignments and <c>pass</c>;
    /// the entry module may additionally contain exactly the PSM entry shape
    /// (one top-level <c>def main()</c> plus the <c>__main__</c> guard). Every
    /// other module-level statement is a violation. Pure and read-only.
    ///
    /// Test infrastructure (<c>test_*.py</c>, <c>*_test.py</c>, <c>conftest.py</c>)
    /// may additionally keep the test-runner protocol at module level — both the
    /// FUNCTIONS (<c>test_*</c>, <c>@pytest.fixture</c>, <c>pytest_*</c> hooks,
    /// <c>setup_module</c>…) and the VARIABLES (<c>collect_ignore</c>,
    /// <c>pytest_plugins</c>, <c>pytestmark</c>…). See
    /// <see cref="HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule"/>, the same
    /// carve-out the IDE's PY8002 pass applies inside a test project. pytest can
    /// only discover these at module scope, so encapsulating them does not produce
    /// OOP test code — it produces a suite whose fixtures silently stop being
    /// applied. An ordinary helper or an ordinary variable in a test file is still
    /// a violation, and <see cref="PythonOopConverter"/> encapsulates it.
    /// </summary>
    public static class PythonOopAuditor
    {
        /// <summary>Audit every Python file under <paramref name="rootDir"/> (venv/artifact dirs excluded).</summary>
        public static List<OopViolation> AuditTree(string rootDir)
        {
            var violations = new List<OopViolation>();
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return violations;
            foreach (string file in PythonSolutionAuditor.EnumeratePythonFiles(rootDir))
                violations.AddRange(AuditFile(file));
            return violations;
        }

        /// <summary>Audit a single file on disk; entry-module allowance follows its file name.</summary>
        public static List<OopViolation> AuditFile(string filePath)
        {
            string source;
            try { source = File.ReadAllText(filePath); }
            catch (Exception ex)
            {
                return new List<OopViolation>
                {
                    new OopViolation(filePath, 0, OopViolationKind.UnparsableFile, null,
                                     "File could not be read: " + ex.Message)
                };
            }
            return AuditSource(source, filePath, PythonSolutionAuditor.IsMainModule(filePath));
        }

        /// <summary>
        /// Audit source text. <paramref name="filePath"/> is used for reporting AND to decide
        /// whether the file is test infrastructure (<c>test_*.py</c>, <c>*_test.py</c>,
        /// <c>conftest.py</c>), because in those modules the test-runner protocol functions are
        /// exempt — see <see cref="HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule"/>.
        /// </summary>
        public static List<OopViolation> AuditSource(string source, string filePath, bool isEntryModule)
        {
            var violations = new List<OopViolation>();
            if (source == null) return violations;

            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source, filePath).Module; }
            catch { module = null; }
            if (module == null)
            {
                violations.Add(new OopViolation(filePath, 0, OopViolationKind.UnparsableFile, null,
                    "File could not be parsed, so strict-OOP compliance cannot be verified."));
                return violations;
            }

            bool isTestModule = HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule
                .IsTestInfrastructureModuleName(SafeModuleName(filePath));

            foreach (StatementSyntax s in module.Statements)
                Classify(s, filePath, isEntryModule, isTestModule, violations);
            return violations;
        }

        private static string SafeModuleName(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            try { return Path.GetFileNameWithoutExtension(filePath); }
            catch { return null; }
        }

        private static void Classify(StatementSyntax s, string filePath, bool isEntryModule,
                                     bool isTestModule, List<OopViolation> violations)
        {
            int line = s.Span.Start.Line;

            if (s is ImportStatement || s is FromImportStatement ||
                s is ClassDefSyntax || s is PassStatement)
            {
                return;
            }

            if (s is ExpressionStatement es)
            {
                if (IsInertLiteral(es.Expression)) return;  // docstring or stub ellipsis

                // pytest APIs whose contract is "runs at import time" — register_assert_rewrite
                // must precede the plugin import, importorskip must skip the module before it is
                // collected. Neither can be deferred into a class. Every OTHER module-level call
                // (sys.path.insert, warnings.filterwarnings, ...) is still executable module-level
                // code and still a violation, in a conftest as much as anywhere else.
                if (isTestModule && es.Expression is CallExpression rc
                    && HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule.IsRunnerModuleCall(
                        DottedDecoratorName(rc)))
                    return;
            }

            if (s is FunctionDefSyntax fn)
            {
                if (isEntryModule && fn.Name == "main") return;  // PSM entry shape

                // In test infrastructure, the functions the runner can only find at module level.
                // A @pytest.fixture encapsulated into a class is not a compile error — it is worse:
                // the suite still runs and the fixture is silently never applied. Without this the
                // guard fails the import of any project that has a conftest.py.
                if (isTestModule && HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule
                        .IsTestProtocolFunction(fn.Name, DottedDecorators(fn))) return;

                violations.Add(new OopViolation(filePath, line, OopViolationKind.ModuleLevelFunction, fn.Name,
                    "Module-level function 'def " + fn.Name + "' — in strict OOP form every function " +
                    "must be a method of a class."));
                return;
            }

            if (s is IfStatement ifs)
            {
                if (isEntryModule && IsDunderMainGuard(ifs)) return;
                if (IsTypeCheckingImportGuard(ifs)) return;
                violations.Add(new OopViolation(filePath, line, OopViolationKind.ModuleLevelStatement, null,
                    isEntryModule
                        ? "Module-level 'if' statement — only the \"if __name__ == '__main__':\" guard is allowed here."
                        : "Module-level 'if' statement — executable module-level code is not OOP."));
                return;
            }

            if (s is AssignmentStatement asn)
            {
                if (AllTargetsAreConstants(asn)) return;

                // pytest reads `collect_ignore`, `pytest_plugins` and `pytestmark` off the module
                // by name, so they cannot be ALL_CAPS or move into a class either. Without this a
                // conftest declaring `collect_ignore = [...]` fails the import for the same reason
                // its fixtures used to.
                if (isTestModule && AllTargetsAreRunnerVariables(asn)) return;

                violations.Add(new OopViolation(filePath, line, OopViolationKind.NonConstantAssignment,
                    FirstTargetName(asn),
                    "Module-level assignment — in strict OOP form module state is limited to dunder " +
                    "names (__all__, __version__, ...) and ALL_CAPS constants; everything else belongs " +
                    "in a class."));
                return;
            }

            violations.Add(new OopViolation(filePath, line, OopViolationKind.ModuleLevelStatement, null,
                "Executable module-level statement — in strict OOP form all behavior lives in classes " +
                "(the entry module's main() is the only exception)."));
        }

        /// <summary>
        /// A function's decorators as dotted names with call syntax stripped, which is the shape
        /// <see cref="HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule"/> takes: <c>@pytest.fixture</c>
        /// and <c>@pytest.fixture(scope="module")</c> both reduce to <c>"pytest.fixture"</c>.
        /// Shared with <see cref="PythonOopConverter"/>, which must make the identical decision
        /// about which functions may stay at module level.
        /// </summary>
        internal static List<string> DottedDecorators(FunctionDefSyntax fn)
        {
            var names = new List<string>();
            if (fn == null || fn.Decorators == null) return names;
            foreach (DecoratorSyntax d in fn.Decorators)
            {
                string dotted = DottedDecoratorName(d?.Expression);
                if (!string.IsNullOrEmpty(dotted)) names.Add(dotted);
            }
            return names;
        }

        private static string DottedDecoratorName(ExpressionSyntax e)
        {
            CallExpression call = e as CallExpression;
            if (call != null) e = call.Callee;

            DottedName dotted = e as DottedName;
            if (dotted != null) return string.Join(".", dotted.Parts);

            var parts = new List<string>();
            AttributeExpression attr = e as AttributeExpression;
            while (attr != null)
            {
                parts.Insert(0, attr.Name);
                e = attr.Target;
                attr = e as AttributeExpression;
            }
            NameExpression name = e as NameExpression;
            if (name == null) return null;            // a subscript/lambda decorator: not a name
            parts.Insert(0, name.Name);
            return string.Join(".", parts);
        }

        /// <summary>String literal (docstring) or bare <c>...</c> (stub body) statement.</summary>
        private static bool IsInertLiteral(ExpressionSyntax e)
        {
            LiteralExpression lit = e as LiteralExpression;
            if (lit == null) return false;
            return lit.Token.Kind == TokenKind.String ||
                   lit.Token.Kind == TokenKind.FStringStart ||
                   lit.Token.Kind == TokenKind.Ellipsis;
        }

        /// <summary>
        /// <c>if __name__ == "__main__":</c> (either operand order) with no elif/else — the strict
        /// entry shape this auditor allows at module level. The condition itself is
        /// <see cref="PythonEntryPointFacts.IsDunderMainTest"/>, shared with the PSM compliance
        /// validator so the two passes cannot drift on what a guard is.
        /// </summary>
        public static bool IsDunderMainGuard(IfStatement ifs)
        {
            if (ifs == null || ifs.Elifs.Count > 0 || ifs.Else != null) return false;
            return PythonEntryPointFacts.IsDunderMainTest(ifs.Test);
        }

        /// <summary><c>if TYPE_CHECKING:</c> (or <c>typing.TYPE_CHECKING</c>) whose body is imports only.</summary>
        private static bool IsTypeCheckingImportGuard(IfStatement ifs)
        {
            if (ifs.Elifs.Count > 0 || ifs.Else != null) return false;
            bool isTypeChecking = IsName(ifs.Test, "TYPE_CHECKING") ||
                (ifs.Test is AttributeExpression attr && attr.Name == "TYPE_CHECKING");
            if (!isTypeChecking) return false;
            if (ifs.Body == null) return false;
            foreach (StatementSyntax bs in ifs.Body.Statements)
            {
                if (!(bs is ImportStatement) && !(bs is FromImportStatement) && !(bs is PassStatement))
                    return false;
            }
            return true;
        }

        private static bool AllTargetsAreConstants(AssignmentStatement asn)
        {
            if (asn.Targets.Count == 0) return false;
            foreach (ExpressionSyntax t in asn.Targets)
            {
                NameExpression name = t as NameExpression;
                if (name == null) return false;
                if (!IsDunderName(name.Name) && !IsAllCapsConstant(name.Name)) return false;
            }
            return true;
        }

        private static bool AllTargetsAreRunnerVariables(AssignmentStatement asn)
        {
            if (asn.Targets.Count == 0) return false;
            foreach (ExpressionSyntax t in asn.Targets)
            {
                NameExpression name = t as NameExpression;
                if (name == null) return false;
                if (!HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule.IsRunnerModuleVariable(name.Name))
                    return false;
            }
            return true;
        }

        private static string FirstTargetName(AssignmentStatement asn)
        {
            foreach (ExpressionSyntax t in asn.Targets)
                if (t is NameExpression name) return name.Name;
            return null;
        }

        private static bool IsDunderName(string s)
        {
            return s != null && s.Length > 4 &&
                   s.StartsWith("__", StringComparison.Ordinal) &&
                   s.EndsWith("__", StringComparison.Ordinal);
        }

        /// <summary><c>MAX_SIZE</c>, <c>T</c>, <c>DEFAULT2</c> — uppercase start, then uppercase/digits/underscores.</summary>
        private static bool IsAllCapsConstant(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!(s[0] >= 'A' && s[0] <= 'Z') && s[0] != '_') return false;
            foreach (char c in s)
            {
                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) return false;
            }
            return true;
        }

        private static bool IsName(ExpressionSyntax e, string name)
        {
            return PythonEntryPointFacts.IsName(e, name);
        }
    }
}
