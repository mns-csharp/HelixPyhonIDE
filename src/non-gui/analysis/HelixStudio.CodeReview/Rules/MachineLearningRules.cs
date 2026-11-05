namespace HelixPythonNamespace.CodeReview.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>Review checks whose contracts come from tensor/autograd and neural-network APIs.</summary>
    internal static class MachineLearningRuleSet
    {
        public static IEnumerable<IReviewRule> All()
        {
            yield return new ModuleBaseInitializerRule();
            yield return new LayerCreatedDuringForwardRule();
            yield return new AutogradDataEscapeRule();
            yield return new BackwardWithoutGradientResetRule();
            yield return new FunctionalDropoutIgnoresModeRule();
            yield return new UnsafeTensorNumpyConversionRule();
        }
    }

    internal sealed class ModuleBaseInitializerRule : IReviewRule
    {
        public string RuleId { get { return "CR-ML-001"; } }
        public string Title { get { return "Neural-network module skips base initialization"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewClass cls in context.Classes)
            {
                if (!IsModule(cls.Node)) continue;
                FunctionDefSyntax init = null;
                foreach (FunctionDefSyntax method in cls.Methods) if (method.Name == "__init__") { init = method; break; }
                if (init == null || CallsModuleInitializer(init.Body)) continue;
                output.Add(context.Report(cls.Node, RuleId, Title, Category,
                    ReviewSeverity.Critical, ReviewConfidence.High,
                    "`" + cls.Name + "` derives from `nn.Module` but its constructor does not initialize the base class.",
                    "PyTorch creates the parameter, buffer and child-module registries in `Module.__init__`. Assigning layers before that call can fail immediately or leave parameters invisible to optimizers and serialization.",
                    "Call `super().__init__()` as the first statement of `__init__` before assigning layers or parameters.",
                    ReviewEffort.Trivial));
            }
        }

        private static bool IsModule(ClassDefSyntax cls)
        {
            if (cls == null) return false;
            foreach (ExpressionSyntax b in cls.BaseExpressions)
            {
                string name = ReviewHelpers.DottedNameOf(b);
                if (name == "nn.Module" || name == "torch.nn.Module" || name == "Module") return true;
            }
            return false;
        }

        private static bool CallsModuleInitializer(SuiteSyntax body)
        {
            if (body == null) return false;
            foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(body))
            {
                var call = node as CallExpression;
                var attr = call != null ? call.Callee as AttributeExpression : null;
                if (attr == null || attr.Name != "__init__") continue;
                var targetCall = attr.Target as CallExpression;
                var superName = targetCall != null ? targetCall.Callee as NameExpression : null;
                if (superName != null && superName.Name == "super") return true;
                string target = ReviewHelpers.DottedNameOf(attr.Target);
                if (target == "nn.Module" || target == "torch.nn.Module") return true;
            }
            return false;
        }
    }

    internal sealed class LayerCreatedDuringForwardRule : IReviewRule
    {
        public string RuleId { get { return "CR-ML-002"; } }
        public string Title { get { return "Trainable layer is created during forward"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }
        private static readonly HashSet<string> Layers = new HashSet<string>(StringComparer.Ordinal)
        { "Linear", "Conv1d", "Conv2d", "Conv3d", "Embedding", "LSTM", "GRU", "BatchNorm1d", "BatchNorm2d", "MultiheadAttention" };

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                if (!function.IsMethod || function.Name != "forward" || function.Node.Body == null) continue;
                foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(function.Node.Body))
                {
                    var call = node as CallExpression;
                    string callee = call != null ? ReviewHelpers.CalleeName(call) : null;
                    string leaf = ReviewHelpers.LastSegment(callee);
                    if (leaf == null || !Layers.Contains(leaf)) continue;
                    string root = ReviewHelpers.RootSegment(callee);
                    if (root != "nn" && root != "torch") continue;
                    output.Add(context.Report(call, RuleId, Title, Category,
                        ReviewSeverity.Critical, ReviewConfidence.High,
                        "`" + callee + "(...)` constructs a trainable layer inside `forward`.",
                        "A fresh layer and fresh parameters are created for every batch. They are not registered when the optimizer is built, so training cannot update the weights that produced the output.",
                        "Construct the layer once in `__init__`, assign it to `self`, and call that registered layer from `forward`.",
                        ReviewEffort.Small));
                }
            }
        }
    }

    internal sealed class AutogradDataEscapeRule : IReviewRule
    {
        public string RuleId { get { return "CR-ML-003"; } }
        public string Title { get { return "Tensor data bypasses autograd"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }
        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            if (!context.ImportedNames.Contains("torch") && !context.ImportedNames.Contains("nn")) return;
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                var attribute = node as AttributeExpression;
                if (attribute == null || attribute.Name != "data") continue;
                output.Add(context.Report(attribute, RuleId, Title, Category,
                    ReviewSeverity.Major, ReviewConfidence.Medium,
                    "`.data` accesses tensor storage outside autograd's safety checks.",
                    "Mutating or reusing that view can silently produce gradients for values that no longer match the forward pass, which is harder to diagnose than an explicit runtime failure.",
                    "Use `.detach()` for a non-gradient view, or perform intentional parameter updates inside `torch.no_grad()`.",
                    ReviewEffort.Small));
            }
        }
    }

    internal sealed class BackwardWithoutGradientResetRule : IReviewRule
    {
        public string RuleId { get { return "CR-ML-004"; } }
        public string Title { get { return "Backward pass may accumulate stale gradients"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }
        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                if (function.Node.Body == null || IsIntentionalAccumulation(function.Name)) continue;
                int resetLine = int.MaxValue;
                var backwards = new List<CallExpression>();
                foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(function.Node.Body))
                {
                    var call = node as CallExpression;
                    string leaf = call != null ? ReviewHelpers.LastSegment(ReviewHelpers.CalleeName(call)) : null;
                    if (leaf == "zero_grad") resetLine = Math.Min(resetLine, call.Span.Start.Line);
                    else if (leaf == "backward") backwards.Add(call);
                }
                foreach (CallExpression backward in backwards)
                {
                    if (resetLine < backward.Span.Start.Line) continue;
                    output.Add(context.Report(backward, RuleId, Title, Category,
                        ReviewSeverity.Major, ReviewConfidence.Medium,
                        "`backward()` runs without an earlier `zero_grad()` in `" + function.QualifiedName + "`.",
                        "PyTorch accumulates gradients by default. Unless accumulation is deliberate, each step includes gradients left by earlier batches and changes the effective optimization algorithm.",
                        "Call `optimizer.zero_grad(set_to_none=True)` before the forward/backward pass, or rename/document this function as an intentional gradient-accumulation step.",
                        ReviewEffort.Trivial));
                }
            }
        }
        private static bool IsIntentionalAccumulation(string name)
        {
            name = (name ?? string.Empty).ToLowerInvariant();
            return name.Contains("accumul") || name.Contains("microbatch");
        }
    }

    internal sealed class FunctionalDropoutIgnoresModeRule : IReviewRule
    {
        public string RuleId { get { return "CR-ML-005"; } }
        public string Title { get { return "Functional dropout ignores evaluation mode"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }
        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                var call = node as CallExpression;
                string callee = call != null ? ReviewHelpers.CalleeName(call) : null;
                if (callee != "F.dropout" && callee != "torch.nn.functional.dropout") continue;
                bool specifiesTraining = false;
                foreach (CallArgument argument in call.Arguments)
                    if (argument.Kind == CallArgument.ArgKind.Keyword && argument.Name == "training")
                        specifiesTraining = true;
                if (specifiesTraining) continue;
                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Critical, ReviewConfidence.High,
                    "`" + callee + "(...)` does not pass the module's training state.",
                    "Functional dropout defaults to `training=True`, so predictions remain random after `model.eval()` and validation no longer measures the deployed network.",
                    "Pass `training=self.training`, or construct `nn.Dropout` once in `__init__` and call the registered module.",
                    ReviewEffort.Trivial));
            }
        }
    }

    internal sealed class UnsafeTensorNumpyConversionRule : IReviewRule
    {
        public string RuleId { get { return "CR-ML-006"; } }
        public string Title { get { return "Tensor-to-NumPy conversion is not detached and moved to CPU"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }
        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            if (!context.ImportedNames.Contains("torch") && !context.ImportedNames.Contains("nn")) return;
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                var call = node as CallExpression;
                var numpy = call != null ? call.Callee as AttributeExpression : null;
                if (numpy == null || numpy.Name != "numpy") continue;
                bool detached = ContainsMethod(numpy.Target, "detach");
                bool cpu = ContainsMethod(numpy.Target, "cpu");
                if (detached || cpu) continue;
                var parameter = numpy.Target as NameExpression;
                if (parameter == null || !IsFunctionParameter(context, call.Span.Start.Line, parameter.Name)) continue;
                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.Medium,
                    "Parameter `" + parameter.Name + "` crosses directly into NumPy with no explicit device or autograd boundary.",
                    "If callers pass a CUDA tensor or one that requires gradients, `.numpy()` raises at runtime. The function signature does not prove either precondition, so CPU-only samples can hide the failure.",
                    "Make the boundary explicit with `tensor.detach().cpu().numpy()`, or validate and document that this API accepts CPU tensors without gradients only.",
                    ReviewEffort.Trivial));
            }
        }
        private static bool IsFunctionParameter(ReviewContext context, int line, string name)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                if (function.Node == null || line < function.Node.Span.Start.Line || line > function.Node.Span.End.Line) continue;
                foreach (ParameterSyntax parameter in function.Node.Parameters)
                    if (parameter.Name == name) return true;
            }
            return false;
        }
        private static bool ContainsMethod(ExpressionSyntax expression, string method)
        {
            ExpressionSyntax current = expression;
            while (current != null)
            {
                var call = current as CallExpression;
                if (call != null)
                {
                    var callee = call.Callee as AttributeExpression;
                    if (callee == null) return false;
                    if (callee.Name == method) return true;
                    current = callee.Target;
                    continue;
                }
                var attribute = current as AttributeExpression;
                if (attribute != null) { current = attribute.Target; continue; }
                var parenthesized = current as ParenthesizedExpression;
                if (parenthesized != null) { current = parenthesized.Inner; continue; }
                break;
            }
            return false;
        }
    }
}
