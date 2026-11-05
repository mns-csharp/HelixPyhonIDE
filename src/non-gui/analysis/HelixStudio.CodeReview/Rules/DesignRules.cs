namespace HelixPythonNamespace.CodeReview.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>
    /// Shape of the code rather than its behaviour: the comments a reviewer makes about a design
    /// that works today and will be expensive to change. Thresholds come from
    /// <see cref="ReviewOptions"/> so a team can move them without editing rules.
    /// </summary>
    internal static class DesignRuleSet
    {
        public static IEnumerable<IReviewRule> All()
        {
            yield return new GodClassRule();
            yield return new LongParameterListRule();
            yield return new LongFunctionRule();
            yield return new DeepNestingRule();
            yield return new BooleanFlagParameterRule();
            yield return new LawOfDemeterRule();
            yield return new MutableClassAttributeRule();
            yield return new ConstructorDoesRealWorkRule();
            yield return new ModuleLevelMutableStateRule();
            yield return new TooManyReturnPointsRule();
            yield return new FeatureEnvyRule();
            yield return new AbandonedStubRule();
        }
    }

    /// <summary>CR-DES-001 — a class that has grown into a module.</summary>
    internal sealed class GodClassRule : IReviewRule
    {
        public string RuleId { get { return "CR-DES-001"; } }
        public string Title { get { return "Class has too many responsibilities"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewClass cls in context.Classes)
            {
                int methods = cls.Methods.Count;
                int lines = cls.Node.Span.End.Line - cls.Node.Span.Start.Line + 1;
                bool tooManyMethods = methods > context.Options.MaxClassMethods;
                bool tooLong = lines > context.Options.MaxClassLines;
                if (!tooManyMethods && !tooLong) continue;

                string measure = tooManyMethods && tooLong
                    ? methods + " methods across " + lines + " lines"
                    : tooManyMethods ? methods + " methods" : lines + " lines";

                output.Add(context.Report(cls.Node, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.Medium,
                    "`" + cls.Name + "` has " + measure + " (limits: " + context.Options.MaxClassMethods +
                    " methods / " + context.Options.MaxClassLines + " lines).",
                    "A class this size almost never has one reason to change, so every unrelated feature edits " +
                    "the same file and the same tests. It also means nothing in it can be reused without " +
                    "taking all of it — the practical cost of a missing seam.",
                    "Look for a group of methods that share a subset of the attributes; that subset is usually " +
                    "a collaborator waiting to be extracted. Split by responsibility, not by line count.",
                    ReviewEffort.Large));
            }
        }
    }

    /// <summary>CR-DES-002 — too many parameters.</summary>
    internal sealed class LongParameterListRule : IReviewRule
    {
        public string RuleId { get { return "CR-DES-002"; } }
        public string Title { get { return "Long parameter list"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                List<ParameterSyntax> parameters = ReviewHelpers.DeclaredParameters(function.Node, function.IsMethod);
                int count = 0;
                foreach (ParameterSyntax p in parameters)
                    if (p.Kind != ParameterKind.VarPositional && p.Kind != ParameterKind.VarKeyword) count++;
                if (count <= context.Options.MaxFunctionParameters) continue;

                output.Add(context.Report(function.Node, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.High,
                    "`" + function.QualifiedName + "` takes " + count + " parameters (limit " +
                    context.Options.MaxFunctionParameters + ").",
                    "Long lists are hard to call correctly: adjacent parameters of the same type get swapped, " +
                    "and every new caller has to decide about arguments it does not care about. It usually " +
                    "means several of them travel together and belong to one object.",
                    "Group the ones that always appear together into a small class or dataclass, or make the " +
                    "optional tail keyword-only (`def f(a, b, *, verbose=False, retries=3)`) so callers must " +
                    "name them.",
                    ReviewEffort.Moderate));
            }
        }
    }

    /// <summary>CR-DES-003 — a function that does not fit on a screen.</summary>
    internal sealed class LongFunctionRule : IReviewRule
    {
        public string RuleId { get { return "CR-DES-003"; } }
        public string Title { get { return "Function is too long to review as a unit"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                int statements = ReviewHelpers.StatementCount(function.Node.Body);
                if (statements <= context.Options.MaxFunctionStatements) continue;

                output.Add(context.Report(function.Node, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.High,
                    "`" + function.QualifiedName + "` contains " + statements + " statements (limit " +
                    context.Options.MaxFunctionStatements + ").",
                    "Nobody can hold this much state in their head at once, so review of it is skimming rather " +
                    "than reading — which is precisely how the defects in a function this size get through. It " +
                    "also cannot be tested in parts: every test has to drive the whole thing.",
                    "Extract the labelled sections (they are usually already marked by blank lines and " +
                    "comments) into named helpers. The comment that introduces a block is normally the name of " +
                    "the function it wants to be.",
                    ReviewEffort.Large));
            }
        }
    }

    /// <summary>CR-DES-004 — deeply nested control flow.</summary>
    internal sealed class DeepNestingRule : IReviewRule
    {
        public string RuleId { get { return "CR-DES-004"; } }
        public string Title { get { return "Deeply nested control flow"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                int depth = ReviewHelpers.MaxNestingDepth(function.Node.Body);
                if (depth <= context.Options.MaxNestingDepth) continue;

                output.Add(context.Report(function.Node, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.High,
                    "`" + function.QualifiedName + "` nests " + depth + " blocks deep (limit " +
                    context.Options.MaxNestingDepth + ").",
                    "At this depth the conditions that reach a line are no longer visible from it, so a reader " +
                    "has to reconstruct them by scrolling up. That reconstruction is where wrong assumptions " +
                    "about which branch is active come from.",
                    "Invert the guards and return early (`if not ok: return`), which flattens the common path " +
                    "to the left margin, or extract the inner block into its own function.",
                    ReviewEffort.Moderate));
            }
        }
    }

    /// <summary>CR-DES-005 — a boolean parameter that selects behaviour.</summary>
    internal sealed class BooleanFlagParameterRule : IReviewRule
    {
        public string RuleId { get { return "CR-DES-005"; } }
        public string Title { get { return "Boolean flag parameter selects behaviour"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                if (function.Node.Body == null) continue;

                foreach (ParameterSyntax parameter in ReviewHelpers.DeclaredParameters(function.Node, function.IsMethod))
                {
                    if (!ReviewHelpers.IsBooleanLiteral(parameter.DefaultValue)) continue;
                    // Keyword-only flags are already unambiguous at the call site — the readability
                    // half of this finding does not apply, so only branching on it is left.
                    if (parameter.Kind == ParameterKind.KeywordOnly) continue;
                    if (!BranchesOn(function.Node.Body, parameter.Name)) continue;

                    output.Add(context.Report(parameter, RuleId, Title, Category,
                        ReviewSeverity.Info, ReviewConfidence.Medium,
                        "`" + parameter.Name + "` is a boolean that the body branches on, so this function has two modes.",
                        "At the call site it reads as `process(data, True)` — which tells the reader nothing, " +
                        "and is easy to pass in the wrong position. Two behaviours behind one name also means " +
                        "every future change has to consider both.",
                        "Make it keyword-only so callers must write `" + parameter.Name + "=True`, or split the " +
                        "function into the two operations it already contains.",
                        ReviewEffort.Small));
                }
            }
        }

        private static bool BranchesOn(SuiteSyntax body, string name)
        {
            foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(body))
            {
                IfStatement ifStatement = node as IfStatement;
                if (ifStatement != null && ReviewHelpers.NamesIn(ifStatement.Test).Contains(name)) return true;
                ConditionalExpression conditional = node as ConditionalExpression;
                if (conditional != null && ReviewHelpers.NamesIn(conditional.Condition).Contains(name)) return true;
            }
            return false;
        }
    }

    /// <summary>CR-DES-006 — `a.b.c.d()`.</summary>
    internal sealed class LawOfDemeterRule : IReviewRule
    {
        public string RuleId { get { return "CR-DES-006"; } }
        public string Title { get { return "Reaches through several objects to do its work"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        private const int MaxHops = 3;

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                CallExpression call = node as CallExpression;
                if (call == null) continue;
                int hops = ReviewHelpers.AttributeChainDepth(call.Callee);
                if (hops < MaxHops) continue;

                string chain = ReviewHelpers.DottedNameOf(call.Callee);
                if (chain == null) continue;
                // A module path is not an object graph: `os.path.sep.join(...)` is three hops and
                // entirely fine. The file's own imports say which roots are modules.
                if (IsModulePath(context, chain)) continue;

                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Info, ReviewConfidence.Low,
                    "`" + chain + "(...)` navigates " + hops + " objects to reach the method it calls.",
                    "This code now depends on the internal structure of everything along the chain, not just " +
                    "on its immediate collaborator. Any of those types can break it by changing a field it " +
                    "never asked for — and the coupling is invisible from their side.",
                    "Ask the neighbour to do the work (`order.total()` rather than " +
                    "`order.customer.account.balance()`), or take the object you actually need as a parameter.",
                    ReviewEffort.Moderate));
            }
        }

        private static bool IsModulePath(ReviewContext context, string chain)
        {
            string root = ReviewHelpers.RootSegment(chain);
            if (root == "self" || root == "cls") return false;   // reaching through OWN state still counts
            return context.ImportedNames.Contains(root);
        }
    }

    /// <summary>CR-DES-007 — a mutable default shared by every instance.</summary>
    internal sealed class MutableClassAttributeRule : IReviewRule
    {
        public string RuleId { get { return "CR-DES-007"; } }
        public string Title { get { return "Mutable class attribute shared by every instance"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewClass cls in context.Classes)
            {
                foreach (StatementSyntax statement in ReviewHelpers.StatementsOf(cls.Node.Body))
                {
                    AssignmentStatement assignment = statement as AssignmentStatement;
                    if (assignment == null || assignment.Targets == null || assignment.Targets.Count != 1) continue;

                    ExpressionSyntax value = ReviewHelpers.Unwrap(assignment.Value);
                    string kind = value is ListExpression ? "list"
                        : value is DictExpression ? "dict"
                        : value is SetExpression ? "set" : null;
                    if (kind == null) continue;

                    string name = ReviewHelpers.DottedNameOf(assignment.Targets[0]);
                    if (name == null) continue;

                    output.Add(context.Report(assignment, RuleId, Title, Category,
                        ReviewSeverity.Major, ReviewConfidence.High,
                        "`" + cls.Name + "." + name + "` is a class-level " + kind + ", so all instances share one object.",
                        "The " + kind + " is created once when the class body runs. Every instance sees the same " +
                        "one, so `self." + name + ".append(x)` in one object is visible from every other — " +
                        "including objects created later, and objects in other tests. Instances only get their " +
                        "own copy if something rebinds the name.",
                        "Create it per instance in `__init__`: `self." + name + " = " +
                        (kind == "list" ? "[]" : kind == "dict" ? "{}" : "set()") + "`. Keep class-level " +
                        "attributes for immutable values.",
                        ReviewEffort.Small));
                }
            }
        }
    }

    /// <summary>CR-DES-008 — a constructor that does more than assign.</summary>
    internal sealed class ConstructorDoesRealWorkRule : IReviewRule
    {
        public string RuleId { get { return "CR-DES-008"; } }
        public string Title { get { return "Constructor performs I/O or heavy work"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        private static readonly HashSet<string> SideEffectCalls = new HashSet<string>(StringComparer.Ordinal)
        {
            "open", "connect", "request", "get", "post", "put", "delete", "execute", "urlopen",
            "read_csv", "load", "run", "check_output", "system", "listdir", "walk", "glob", "sleep",
        };

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                if (!function.IsMethod || function.Name != "__init__" || function.Node.Body == null) continue;

                foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(function.Node.Body))
                {
                    CallExpression call = node as CallExpression;
                    if (call == null) continue;
                    string callee = ReviewHelpers.CalleeName(call);
                    string leaf = ReviewHelpers.LastSegment(callee);
                    if (leaf == null || !SideEffectCalls.Contains(leaf)) continue;
                    if (ReviewHelpers.RootSegment(callee) == "self") continue;   // delegating to own helper

                    output.Add(context.Report(call, RuleId, Title, Category,
                        ReviewSeverity.Minor, ReviewConfidence.Low,
                        "`" + function.QualifiedName + "` calls `" + callee + "(...)` while constructing the object.",
                        "Constructing an object then means touching the network, the filesystem or the clock, " +
                        "so the type cannot be created in a test without standing all of that up, and a " +
                        "failure surfaces as \"cannot build the object\" rather than as the operation that " +
                        "failed. It also makes construction cost unpredictable.",
                        "Keep `__init__` to assigning what it was given, and move the work into an explicit " +
                        "method (`connect()`, `load()`) or a classmethod factory the caller opts into.",
                        ReviewEffort.Moderate));
                    break;
                }
            }
        }
    }

    /// <summary>CR-DES-009 — module-level mutable state.</summary>
    internal sealed class ModuleLevelMutableStateRule : IReviewRule
    {
        public string RuleId { get { return "CR-DES-009"; } }
        public string Title { get { return "Module-level mutable state"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            if (context.Module == null) return;

            foreach (StatementSyntax statement in context.Module.Statements)
            {
                AssignmentStatement assignment = statement as AssignmentStatement;
                if (assignment == null || assignment.Targets == null || assignment.Targets.Count != 1) continue;

                ExpressionSyntax value = ReviewHelpers.Unwrap(assignment.Value);
                bool mutable = value is ListExpression || value is DictExpression || value is SetExpression;
                if (!mutable) continue;

                string name = ReviewHelpers.DottedNameOf(assignment.Targets[0]);
                if (name == null) continue;
                // An ALL_CAPS name is the convention for "constant"; the container is still mutable,
                // but the author has stated the intent and readers honour it.
                if (name == name.ToUpperInvariant() && name.Length > 1) continue;
                if (!IsMutatedSomewhere(context, name)) continue;

                output.Add(context.Report(assignment, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.Medium,
                    "`" + name + "` is a module-level container that other code in this file mutates.",
                    "Module globals live for the process, so state accumulated by one caller is visible to the " +
                    "next — across requests, across tests, and across threads with no lock. Test order starts " +
                    "to matter, and the failure appears in whichever test happens to run second.",
                    "Own it explicitly: make it an attribute of a class the caller creates, or pass it in. " +
                    "If a process-wide cache really is intended, name it as one and give it a documented reset.",
                    ReviewEffort.Moderate));
            }
        }

        private static bool IsMutatedSomewhere(ReviewContext context, string name)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                CallExpression call = node as CallExpression;
                if (call == null) continue;
                AttributeExpression member = ReviewHelpers.Unwrap(call.Callee) as AttributeExpression;
                if (member == null) continue;
                if (member.Name != "append" && member.Name != "extend" && member.Name != "update" &&
                    member.Name != "add" && member.Name != "pop" && member.Name != "clear" &&
                    member.Name != "remove" && member.Name != "insert") continue;
                NameExpression receiver = ReviewHelpers.Unwrap(member.Target) as NameExpression;
                if (receiver != null && receiver.Name == name) return true;
            }
            return false;
        }
    }

    /// <summary>CR-DES-010 — too many exits.</summary>
    internal sealed class TooManyReturnPointsRule : IReviewRule
    {
        public string RuleId { get { return "CR-DES-010"; } }
        public string Title { get { return "Function has many exit points"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                if (function.Node.Body == null) continue;

                int returns = 0;
                foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(function.Node.Body))
                    if (node is ReturnStatement) returns++;
                if (returns <= context.Options.MaxReturnPoints) continue;

                output.Add(context.Report(function.Node, RuleId, Title, Category,
                    ReviewSeverity.Info, ReviewConfidence.Medium,
                    "`" + function.QualifiedName + "` returns from " + returns + " places (limit " +
                    context.Options.MaxReturnPoints + ").",
                    "Guard clauses that return early are good style, so this is not automatically wrong — but " +
                    "past about half a dozen it becomes hard to be sure every path sets up the same invariants, " +
                    "and adding a cleanup step later means finding all of them.",
                    "Check whether the exits are guard clauses (fine) or scattered results (worth collecting " +
                    "into one return). If cleanup is involved, a `with` block or `try/finally` handles all " +
                    "paths at once.",
                    ReviewEffort.Moderate));
            }
        }
    }

    /// <summary>CR-DES-011 — a method more interested in another object than its own.</summary>
    internal sealed class FeatureEnvyRule : IReviewRule
    {
        public string RuleId { get { return "CR-DES-011"; } }
        public string Title { get { return "Method uses another object more than its own"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        // Calibrated against a 12,000-line scientific corpus: at four accesses and "more than
        // self", this rule produced 154 of 282 findings — over half the review, from one Low
        // confidence heuristic. Requiring five accesses AND twice the self-usage, and excluding
        // imported names (below), is what makes the remainder worth reading.
        private const int MinimumForeignAccesses = 5;

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                if (!function.IsMethod || function.Node.Body == null) continue;
                if (ReviewHelpers.IsDunder(function.Name)) continue;

                // Only the objects this method was HANDED can be envied. A local accumulator the
                // method built itself (`lines.append(...)` seventeen times) is its own work, and
                // `np.zeros(...)` or `Checklist.DEFAULT` are a module and a class — none of the
                // three is a collaborator a method could be moved onto. Measured on a 12,000-line
                // corpus, those three shapes were most of what this rule used to report.
                var candidates = new HashSet<string>(StringComparer.Ordinal);
                foreach (ParameterSyntax parameter in ReviewHelpers.DeclaredParameters(function.Node, true))
                    if (parameter.Kind != ParameterKind.VarPositional && parameter.Kind != ParameterKind.VarKeyword)
                        candidates.Add(parameter.Name);
                if (candidates.Count == 0) continue;

                int selfUses = 0;
                var foreign = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(function.Node.Body))
                {
                    // A parameter that gets reassigned is no longer the object it was given.
                    AssignmentStatement assignment = node as AssignmentStatement;
                    if (assignment != null && assignment.Targets != null)
                    {
                        foreach (ExpressionSyntax target in assignment.Targets)
                        {
                            NameExpression plain = ReviewHelpers.Unwrap(target) as NameExpression;
                            if (plain != null) candidates.Remove(plain.Name);
                        }
                        continue;
                    }

                    AttributeExpression member = node as AttributeExpression;
                    if (member == null) continue;
                    NameExpression receiver = ReviewHelpers.Unwrap(member.Target) as NameExpression;
                    if (receiver == null) continue;
                    if (receiver.Name == "self" || receiver.Name == "cls") { selfUses++; continue; }
                    int n; foreign.TryGetValue(receiver.Name, out n); foreign[receiver.Name] = n + 1;
                }

                string envied = null; int enviedCount = 0;
                foreach (KeyValuePair<string, int> pair in foreign)
                    if (candidates.Contains(pair.Key) && pair.Value > enviedCount)
                    { envied = pair.Key; enviedCount = pair.Value; }

                if (envied == null || enviedCount < MinimumForeignAccesses) continue;
                if (enviedCount < selfUses * 2) continue;

                output.Add(context.Report(function.Node, RuleId, Title, Category,
                    ReviewSeverity.Info, ReviewConfidence.Low,
                    "`" + function.QualifiedName + "` touches `" + envied + "` " + enviedCount +
                    " times and `self` " + selfUses + " times.",
                    "The logic is about the other object, so it is separated from the data it operates on. " +
                    "Every change to that type's internals has to be made here too, and the two can drift " +
                    "without anything failing to compile.",
                    "Consider moving the method onto `" + envied + "`'s type, or asking it for the result " +
                    "instead of reading its parts. If it genuinely belongs to both, a small function taking " +
                    "them both may be the honest answer.",
                    ReviewEffort.Moderate));
            }
        }
    }

    /// <summary>CR-DES-012 — an empty method left behind.</summary>
    internal sealed class AbandonedStubRule : IReviewRule
    {
        public string RuleId { get { return "CR-DES-012"; } }
        public string Title { get { return "Empty implementation with no explanation"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                IReadOnlyList<StatementSyntax> statements = ReviewHelpers.StatementsOf(function.Node.Body);
                if (statements.Count != 1 || !(statements[0] is PassStatement)) continue;

                // An abstract method, a Protocol member and a documented no-op are all deliberate.
                if (function.Node.Decorators.Count > 0) continue;
                if (Docstrings.Of(function.Node.Body) != null) continue;
                if (ReviewHelpers.IsDunder(function.Name)) continue;
                if (InProtocolOrAbstractClass(function)) continue;

                output.Add(context.Report(function.Node, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.Medium,
                    "`" + function.QualifiedName + "` has an empty body and nothing that says why.",
                    "A caller cannot tell whether this is a deliberate no-op, an interface stub, or work that " +
                    "was never finished — and it silently succeeds either way. If it is unfinished, the first " +
                    "sign will be a feature that quietly does nothing.",
                    "Say which it is: a docstring for a deliberate no-op, `@abstractmethod` for a contract, " +
                    "or `raise NotImplementedError` for work still to do — that one at least fails loudly.",
                    ReviewEffort.Trivial));
            }
        }

        private static bool InProtocolOrAbstractClass(ReviewFunction function)
        {
            if (function.OwnerClass == null) return false;
            foreach (ExpressionSyntax baseExpression in function.OwnerClass.BaseExpressions)
            {
                string name = ReviewHelpers.LastSegment(ReviewHelpers.DottedNameOf(baseExpression));
                if (name == "Protocol" || name == "ABC" || name == "ABCMeta") return true;
            }
            foreach (KeyValuePair<string, ExpressionSyntax> keyword in function.OwnerClass.KeywordArguments)
                if (keyword.Key == "metaclass") return true;
            return false;
        }
    }
}
