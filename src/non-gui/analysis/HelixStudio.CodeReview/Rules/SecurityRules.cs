namespace HelixPythonNamespace.CodeReview.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>
    /// The findings a reviewer is expected to catch because no test will: each one is code that
    /// works perfectly on the inputs the author tried.
    /// </summary>
    internal static class SecurityRuleSet
    {
        public static IEnumerable<IReviewRule> All()
        {
            yield return new DynamicCodeExecutionRule();
            yield return new ShellInjectionRule();
            yield return new UnsafeDeserializationRule();
            yield return new HardcodedSecretRule();
            yield return new UnsafeProcessAndTempApiRule();
            yield return new WeakRandomnessForSecretsRule();
            yield return new DisabledCertificateValidationRule();
            yield return new SqlBuiltByStringFormattingRule();
            yield return new WeakHashRule();
            yield return new AssertUsedForValidationRule();
        }

        /// <summary>Names that read as a credential wherever they appear.</summary>
        internal static readonly string[] SecretWords =
        {
            "password", "passwd", "pwd", "secret", "token", "api_key", "apikey", "access_key",
            "secret_key", "private_key", "credential", "session_key", "nonce", "salt",
            // Deliberately NOT a bare "auth": it matches `auth_url`, `auth_backend` and
            // `authenticate`, none of which hold a credential. `auth_token` is caught by "token".
        };

        internal static bool LooksLikeSecretName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            foreach (string word in SecretWords)
                if (lower.IndexOf(word, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>The keyword argument with this name, or null.</summary>
        internal static CallArgument KeywordArgument(CallExpression call, string name)
        {
            if (call == null) return null;
            foreach (CallArgument argument in call.Arguments)
                if (argument.Kind == CallArgument.ArgKind.Keyword &&
                    string.Equals(argument.Name, name, StringComparison.Ordinal)) return argument;
            return null;
        }

        internal static ExpressionSyntax FirstPositional(CallExpression call)
        {
            if (call == null) return null;
            foreach (CallArgument argument in call.Arguments)
                if (argument.Kind == CallArgument.ArgKind.Positional) return argument.Value;
            return null;
        }
    }

    /// <summary>CR-SEC-001 — `eval`/`exec` on something that is not a literal.</summary>
    internal sealed class DynamicCodeExecutionRule : IReviewRule
    {
        public string RuleId { get { return "CR-SEC-001"; } }
        public string Title { get { return "Dynamic code execution on a non-literal argument"; } }
        public ReviewCategory Category { get { return ReviewCategory.Security; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (CallExpression call in ReviewHelpers.Calls(context.Module))
            {
                string callee = ReviewHelpers.CalleeName(call);
                if (callee != "eval" && callee != "exec") continue;

                ExpressionSyntax argument = SecurityRuleSet.FirstPositional(call);
                if (argument != null && ReviewHelpers.IsConstantLiteral(argument)) continue;

                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Blocker, ReviewConfidence.High,
                    "`" + callee + "(...)` runs code built at runtime.",
                    "Whatever reaches this argument becomes executable code with the full privileges of the " +
                    "process. Any path from configuration, a request, a filename or a data file to this call " +
                    "is arbitrary code execution, and there is no sandbox that makes it safe.",
                    "Replace it with an explicit dispatch table (`{\"add\": self._add}[name]()`), " +
                    "`ast.literal_eval` for data, or `json.loads` for structured input.",
                    ReviewEffort.Moderate));
            }
        }
    }

    /// <summary>CR-SEC-002 — `shell=True`.</summary>
    internal sealed class ShellInjectionRule : IReviewRule
    {
        public string RuleId { get { return "CR-SEC-002"; } }
        public string Title { get { return "Subprocess launched through a shell"; } }
        public ReviewCategory Category { get { return ReviewCategory.Security; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (CallExpression call in ReviewHelpers.Calls(context.Module))
            {
                CallArgument shell = SecurityRuleSet.KeywordArgument(call, "shell");
                if (shell == null) continue;
                LiteralExpression literal = ReviewHelpers.Unwrap(shell.Value) as LiteralExpression;
                if (literal == null || literal.Token == null || literal.Token.Kind != TokenKind.KwTrue) continue;

                ExpressionSyntax command = SecurityRuleSet.FirstPositional(call);
                bool literalCommand = command != null && ReviewHelpers.IsConstantLiteral(command);

                output.Add(context.Report(call, RuleId, Title, Category,
                    literalCommand ? ReviewSeverity.Major : ReviewSeverity.Critical,
                    literalCommand ? ReviewConfidence.Medium : ReviewConfidence.High,
                    "`shell=True` hands the command line to the system shell" +
                    (literalCommand ? " (the command is a literal here, so the exposure is smaller)." : "."),
                    "The shell re-parses the string, so a semicolon, backtick or `$(...)` anywhere in an " +
                    "interpolated value runs as a second command. Quoting the value is not a fix — the " +
                    "quoting rules differ per platform and per shell.",
                    "Pass an argument LIST and drop `shell=True`: `subprocess.run([\"git\", \"log\", ref])`. " +
                    "The arguments then reach the program untouched, whatever they contain.",
                    ReviewEffort.Small));
            }
        }
    }

    /// <summary>CR-SEC-003 — pickle/yaml/marshal on untrusted bytes.</summary>
    internal sealed class UnsafeDeserializationRule : IReviewRule
    {
        public string RuleId { get { return "CR-SEC-003"; } }
        public string Title { get { return "Deserialization format that can execute code"; } }
        public ReviewCategory Category { get { return ReviewCategory.Security; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (CallExpression call in ReviewHelpers.Calls(context.Module))
            {
                string callee = ReviewHelpers.CalleeName(call);
                if (callee == null) continue;

                string format = null;
                if (callee == "pickle.load" || callee == "pickle.loads" ||
                    callee == "cPickle.load" || callee == "cPickle.loads") format = "pickle";
                else if (callee == "marshal.load" || callee == "marshal.loads") format = "marshal";
                else if (callee == "yaml.load")
                {
                    // `yaml.load(text, Loader=SafeLoader)` is the safe form; only the bare call is a finding.
                    if (SecurityRuleSet.KeywordArgument(call, "Loader") != null) continue;
                    format = "yaml";
                }
                if (format == null) continue;

                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Critical, ReviewConfidence.High,
                    "`" + callee + "(...)` deserializes data in a format that can construct arbitrary objects.",
                    format == "yaml"
                        ? "`yaml.load` without an explicit Loader instantiates any Python type the document " +
                          "names, so a crafted document runs code during parsing."
                        : "The " + format + " format encodes constructor calls, not just data. Loading a " +
                          "crafted payload executes them — this is remote code execution wherever the bytes " +
                          "can be influenced, including a cache file another process can write.",
                    format == "yaml"
                        ? "Use `yaml.safe_load(...)`, or pass `Loader=yaml.SafeLoader` explicitly."
                        : "Use JSON (or another data-only format) for anything that crosses a trust boundary; " +
                          "keep " + format + " for data this process wrote and can verify.",
                    ReviewEffort.Moderate));
            }
        }
    }

    /// <summary>CR-SEC-004 — a credential written into the source.</summary>
    internal sealed class HardcodedSecretRule : IReviewRule
    {
        public string RuleId { get { return "CR-SEC-004"; } }
        public string Title { get { return "Credential hardcoded in source"; } }
        public ReviewCategory Category { get { return ReviewCategory.Security; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                AssignmentStatement assignment = node as AssignmentStatement;
                if (assignment == null || assignment.Targets == null || assignment.Targets.Count == 0) continue;

                string value = ReviewHelpers.StringLiteralValue(assignment.Value);
                if (value == null || value.Length < 4) continue;          // "" and "x" are placeholders, not secrets
                if (value.IndexOf("${", StringComparison.Ordinal) >= 0) continue;  // a template, filled in elsewhere

                foreach (ExpressionSyntax target in assignment.Targets)
                {
                    string name = ReviewHelpers.DottedNameOf(target);
                    if (name == null || !SecurityRuleSet.LooksLikeSecretName(ReviewHelpers.LastSegment(name))) continue;

                    output.Add(context.Report(assignment, RuleId, Title, Category,
                        ReviewSeverity.Blocker, ReviewConfidence.Medium,
                        "`" + name + "` is assigned a string literal.",
                        "A secret in source is a secret in every clone, every branch, every build artefact and " +
                        "the whole of history — rotating it later does not remove it from the commits that " +
                        "already exist. Reviewers are the last check before that becomes permanent.",
                        "Read it from the environment or a secret store at call time: " +
                        "`os.environ[\"" + ReviewHelpers.LastSegment(name).ToUpperInvariant() + "\"]`, " +
                        "and rotate the value that was committed.",
                        ReviewEffort.Small));
                    break;
                }
            }
        }
    }

    /// <summary>CR-SEC-005 — `os.system`, `os.popen`, `tempfile.mktemp`.</summary>
    internal sealed class UnsafeProcessAndTempApiRule : IReviewRule
    {
        public string RuleId { get { return "CR-SEC-005"; } }
        public string Title { get { return "Superseded API with a known unsafe contract"; } }
        public ReviewCategory Category { get { return ReviewCategory.Security; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (CallExpression call in ReviewHelpers.Calls(context.Module))
            {
                string callee = ReviewHelpers.CalleeName(call);
                if (callee == null) continue;

                string why, fix;
                switch (callee)
                {
                    case "os.system":
                    case "os.popen":
                        why = "`" + callee + "` always goes through the system shell and offers no way to pass " +
                              "arguments separately, so every interpolated value is shell syntax.";
                        fix = "Use `subprocess.run([...])` with an argument list.";
                        break;
                    case "tempfile.mktemp":
                        why = "`mktemp` returns a NAME and leaves creating the file to you. Between the two, " +
                              "another process can create that path — the classic symlink race.";
                        fix = "Use `tempfile.NamedTemporaryFile()` or `tempfile.mkstemp()`, which create the " +
                              "file atomically and hand back an open handle.";
                        break;
                    default:
                        continue;
                }

                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Major, ReviewConfidence.High,
                    "`" + callee + "(...)` has a safe replacement in the standard library.", why, fix,
                    ReviewEffort.Small));
            }
        }
    }

    /// <summary>CR-SEC-006 — `random` used where the value is a secret.</summary>
    internal sealed class WeakRandomnessForSecretsRule : IReviewRule
    {
        public string RuleId { get { return "CR-SEC-006"; } }
        public string Title { get { return "Predictable randomness used for a secret value"; } }
        public ReviewCategory Category { get { return ReviewCategory.Security; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                AssignmentStatement assignment = node as AssignmentStatement;
                if (assignment == null || assignment.Value == null) continue;

                string targetName = assignment.Targets != null && assignment.Targets.Count > 0
                    ? ReviewHelpers.DottedNameOf(assignment.Targets[0]) : null;
                if (!SecurityRuleSet.LooksLikeSecretName(ReviewHelpers.LastSegment(targetName))) continue;

                foreach (CallExpression call in ReviewHelpers.Calls(assignment.Value))
                {
                    string callee = ReviewHelpers.CalleeName(call);
                    if (callee == null || ReviewHelpers.RootSegment(callee) != "random") continue;

                    output.Add(context.Report(call, RuleId, Title, Category,
                        ReviewSeverity.Major, ReviewConfidence.Medium,
                        "`" + targetName + "` is generated with `" + callee + "`.",
                        "The `random` module is a Mersenne Twister: fast, reproducible, and fully recoverable " +
                        "from a few hundred observed outputs. Anything that is meant to be unguessable — a " +
                        "token, a password reset link, a session id — must not come from it.",
                        "Use `secrets.token_urlsafe()` / `secrets.token_hex()` / `secrets.choice()`, which draw " +
                        "from the OS entropy source.",
                        ReviewEffort.Trivial));
                    break;
                }
            }
        }
    }

    /// <summary>CR-SEC-007 — TLS verification turned off.</summary>
    internal sealed class DisabledCertificateValidationRule : IReviewRule
    {
        public string RuleId { get { return "CR-SEC-007"; } }
        public string Title { get { return "Certificate validation disabled"; } }
        public ReviewCategory Category { get { return ReviewCategory.Security; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                CallExpression call = node as CallExpression;
                if (call != null)
                {
                    string callee = ReviewHelpers.CalleeName(call);
                    if (callee == "ssl._create_unverified_context")
                    {
                        output.Add(Finding(context, call, "`ssl._create_unverified_context()` builds a context that accepts any certificate."));
                        continue;
                    }
                    CallArgument verify = SecurityRuleSet.KeywordArgument(call, "verify");
                    if (verify != null)
                    {
                        LiteralExpression literal = ReviewHelpers.Unwrap(verify.Value) as LiteralExpression;
                        if (literal != null && literal.Token != null && literal.Token.Kind == TokenKind.KwFalse)
                            output.Add(Finding(context, call, "`verify=False` disables certificate checking for this request."));
                    }
                    continue;
                }

                AssignmentStatement assignment = node as AssignmentStatement;
                if (assignment == null || assignment.Targets == null || assignment.Targets.Count == 0) continue;
                LiteralExpression assigned = ReviewHelpers.Unwrap(assignment.Value) as LiteralExpression;
                if (assigned == null || assigned.Token == null || assigned.Token.Kind != TokenKind.KwFalse) continue;
                string name = ReviewHelpers.LastSegment(ReviewHelpers.DottedNameOf(assignment.Targets[0]));
                if (name != "check_hostname" && name != "verify_mode") continue;

                output.Add(Finding(context, assignment, "`" + name + " = False` turns off part of the TLS handshake check."));
            }
        }

        private ReviewFinding Finding(ReviewContext context, SyntaxNode node, string message)
        {
            return context.Report(node, RuleId, Title, Category,
                ReviewSeverity.Critical, ReviewConfidence.High, message,
                "Without validation the connection is still encrypted, but to whoever answered — which is " +
                "exactly what an interception attack provides. The encryption then protects the attacker's " +
                "channel rather than yours, and nothing in the client's behaviour changes.",
                "Keep validation on and fix the underlying trust problem: point the client at the right CA " +
                "bundle (`verify=\"/path/ca.pem\"`), or install the internal root certificate on the host.",
                ReviewEffort.Moderate);
        }
    }

    /// <summary>CR-SEC-008 — SQL assembled by string formatting.</summary>
    internal sealed class SqlBuiltByStringFormattingRule : IReviewRule
    {
        public string RuleId { get { return "CR-SEC-008"; } }
        public string Title { get { return "SQL statement built by string formatting"; } }
        public ReviewCategory Category { get { return ReviewCategory.Security; } }

        private static readonly HashSet<string> Executors = new HashSet<string>(StringComparer.Ordinal)
        {
            "execute", "executemany", "executescript", "raw", "execute_sql",
        };

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (CallExpression call in ReviewHelpers.Calls(context.Module))
            {
                AttributeExpression member = ReviewHelpers.Unwrap(call.Callee) as AttributeExpression;
                if (member == null || !Executors.Contains(member.Name)) continue;

                ExpressionSyntax statement = ReviewHelpers.Unwrap(SecurityRuleSet.FirstPositional(call));
                if (statement == null) continue;

                string how = HowItIsBuilt(statement);
                if (how == null) continue;

                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Blocker, ReviewConfidence.Medium,
                    "The statement passed to `" + member.Name + "()` is built with " + how + ".",
                    "Values spliced into SQL text are parsed as SQL. A quote character in the value ends the " +
                    "literal and everything after it is executed — which is why this is still the most " +
                    "damaging defect a review can catch, and why it never shows up in tests using tidy inputs.",
                    "Pass parameters, never text: `cursor.execute(\"SELECT * FROM t WHERE id = ?\", (id,))`. " +
                    "The driver then sends the value out of band and it can never be read as syntax.",
                    ReviewEffort.Small));
            }
        }

        // Only shapes that INTERPOLATE count. A plain literal, or a name holding a constant, is fine.
        private static string HowItIsBuilt(ExpressionSyntax statement)
        {
            FStringExpression fstring = statement as FStringExpression;
            if (fstring != null)
            {
                foreach (SyntaxNode part in fstring.Parts)
                    if (part is ExpressionSyntax && !(part is LiteralExpression)) return "an f-string";
                return null;
            }

            BinaryExpression binary = statement as BinaryExpression;
            if (binary != null)
            {
                if (binary.Operator == BinaryOperator.Modulo &&
                    ReviewHelpers.StringLiteralValue(binary.Left) != null) return "`%` formatting";
                if (binary.Operator == BinaryOperator.Add &&
                    (ReviewHelpers.StringLiteralValue(binary.Left) != null ||
                     ReviewHelpers.StringLiteralValue(binary.Right) != null)) return "string concatenation";
                return null;
            }

            CallExpression call = statement as CallExpression;
            if (call != null)
            {
                AttributeExpression member = ReviewHelpers.Unwrap(call.Callee) as AttributeExpression;
                if (member != null && member.Name == "format" &&
                    ReviewHelpers.StringLiteralValue(member.Target) != null) return "`str.format`";
            }
            return null;
        }
    }

    /// <summary>CR-SEC-009 — MD5/SHA-1 where a digest is being trusted.</summary>
    internal sealed class WeakHashRule : IReviewRule
    {
        public string RuleId { get { return "CR-SEC-009"; } }
        public string Title { get { return "Broken hash algorithm"; } }
        public ReviewCategory Category { get { return ReviewCategory.Security; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (CallExpression call in ReviewHelpers.Calls(context.Module))
            {
                string callee = ReviewHelpers.CalleeName(call);
                if (callee != "hashlib.md5" && callee != "hashlib.sha1") continue;
                // Python 3.9+ lets the author state that the digest is not a security control.
                if (SecurityRuleSet.KeywordArgument(call, "usedforsecurity") != null) continue;

                string algorithm = ReviewHelpers.LastSegment(callee).ToUpperInvariant();
                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Major, ReviewConfidence.Medium,
                    "`" + callee + "()` — " + algorithm + " has practical collision attacks.",
                    "Two different inputs can be produced with the same digest, so anything that treats the " +
                    "digest as an identity (a signature, a de-duplication key, a cache of trusted content) can " +
                    "be made to confuse one input for another. Note the flip side: as a non-security checksum " +
                    "this is fine, which is what the `usedforsecurity=False` flag is for.",
                    "Use `hashlib.sha256(...)`; if the digest is only a cache key, pass " +
                    "`usedforsecurity=False` to say so in the code.",
                    ReviewEffort.Small));
            }
        }
    }

    /// <summary>CR-SEC-010 — `assert` doing validation outside tests.</summary>
    internal sealed class AssertUsedForValidationRule : IReviewRule
    {
        public string RuleId { get { return "CR-SEC-010"; } }
        public string Title { get { return "`assert` used to validate input"; } }
        public ReviewCategory Category { get { return ReviewCategory.Security; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            if (context.IsTestFile) return;   // asserting IS the job of a test

            foreach (ReviewFunction function in context.Functions)
            {
                if (function.Node.Body == null) continue;
                if (ReviewHelpers.IsTestFunction(function.Node)) continue;

                // Only parameters matter: an assert about a local invariant is a debugging aid, but
                // an assert about an argument is a contract the caller can violate.
                var parameters = new HashSet<string>(StringComparer.Ordinal);
                foreach (ParameterSyntax p in function.Node.Parameters) parameters.Add(p.Name);
                if (parameters.Count == 0) continue;

                foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(function.Node.Body))
                {
                    AssertStatement assertion = node as AssertStatement;
                    if (assertion == null || assertion.Test == null) continue;

                    string checkedName = null;
                    foreach (string name in ReviewHelpers.NamesIn(assertion.Test))
                        if (parameters.Contains(name)) { checkedName = name; break; }
                    if (checkedName == null) continue;

                    output.Add(context.Report(assertion, RuleId, Title, Category,
                        ReviewSeverity.Major, ReviewConfidence.Medium,
                        "This `assert` validates the argument `" + checkedName + "`.",
                        "`python -O` removes every assert statement from the compiled code. The check then " +
                        "disappears in exactly the configuration people run in production, and the invalid " +
                        "value flows on into code written on the assumption that it was rejected.",
                        "Raise explicitly: `if not <condition>: raise ValueError(\"" + checkedName + " must ...\")`. " +
                        "Keep `assert` for invariants that cannot be false unless this module itself is broken.",
                        ReviewEffort.Small));
                    break;   // one comment per function
                }
            }
        }
    }
}
