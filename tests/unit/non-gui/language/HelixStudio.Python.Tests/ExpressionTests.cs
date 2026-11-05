using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python.Tests
{
    [TestClass]
    public class ExpressionTests
    {
        private static ExpressionSyntax ParseExpr(string src)
        {
            ParseResult r = Python.ParseModule(src + "\n");
            Assert.IsFalse(r.HasErrors, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
            ExpressionStatement s = (ExpressionStatement)r.Module.Statements.Single();
            return s.Expression;
        }

        // `yield` is only legal inside a function, so yield-expression tests parse the
        // expression as the body of a generator and hand back that inner expression.
        private static ExpressionSyntax ParseExprInFunction(string src)
        {
            ParseResult r = Python.ParseModule("def g():\n    " + src + "\n");
            Assert.IsFalse(r.HasErrors, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
            FunctionDefSyntax fn = r.Module.Statements.OfType<FunctionDefSyntax>().Single();
            ExpressionStatement s = (ExpressionStatement)fn.Body.Statements.Single();
            return s.Expression;
        }

        // --------- Literals and atoms ---------

        [TestMethod] public void ParsesIntLiteral() { Assert.IsInstanceOfType(ParseExpr("42"), typeof(LiteralExpression)); }
        [TestMethod] public void ParsesFloatLiteral() { Assert.IsInstanceOfType(ParseExpr("3.14"), typeof(LiteralExpression)); }
        [TestMethod] public void ParsesStringLiteral() { Assert.IsInstanceOfType(ParseExpr("'hello'"), typeof(LiteralExpression)); }
        [TestMethod] public void ParsesTrue() { Assert.IsInstanceOfType(ParseExpr("True"), typeof(LiteralExpression)); }
        [TestMethod] public void ParsesFalse() { Assert.IsInstanceOfType(ParseExpr("False"), typeof(LiteralExpression)); }
        [TestMethod] public void ParsesNone() { Assert.IsInstanceOfType(ParseExpr("None"), typeof(LiteralExpression)); }
        [TestMethod] public void ParsesEllipsis() { Assert.IsInstanceOfType(ParseExpr("..."), typeof(EllipsisExpression)); }
        [TestMethod] public void ParsesName() { Assert.AreEqual("foo", ((NameExpression)ParseExpr("foo")).Name); }

        // --------- PEP 448: starred unpacking inside a set display ---------
        // Regression: {a, *b} / {*a, *b} failed to parse ("Unexpected token: Star") — set displays
        // rejected starred elements although list/tuple/dict-** accepted them (black linegen.py:1105,
        // `{id(rhs.closing_bracket), *omit}`). iter 374.

        [TestMethod]
        public void ParsesSetWithTrailingStar()
        {
            SetExpression s = (SetExpression)ParseExpr("{3, *a}");
            Assert.AreEqual(2, s.Elements.Count);
            Assert.IsInstanceOfType(s.Elements[1], typeof(StarredExpression));
        }

        [TestMethod]
        public void ParsesSetWithLeadingStar()
        {
            SetExpression s = (SetExpression)ParseExpr("{*a, *b}");
            Assert.AreEqual(2, s.Elements.Count);
            Assert.IsInstanceOfType(s.Elements[0], typeof(StarredExpression));
            Assert.IsInstanceOfType(s.Elements[1], typeof(StarredExpression));
        }

        [TestMethod]
        public void PlainSetStillParses()
        {
            SetExpression s = (SetExpression)ParseExpr("{1, 2, 3}");
            Assert.AreEqual(3, s.Elements.Count);
            Assert.IsFalse(s.Elements.Any(e => e is StarredExpression));
        }

        // --------- f-strings: conversion, format spec, implicit concatenation ---------
        // Regression for the virtual compiler flagging 814 phantom errors on a real project:
        // valid, extremely common f-string syntax was rejected by the lexer/parser.

        [TestMethod] public void ParsesFStringSimple() { Assert.IsInstanceOfType(ParseExpr("f\"{x}\""), typeof(FStringExpression)); }

        // {value!r} / !s / !a — was "Unexpected character '!'" (config.py:16 in the sample workspace fixture).
        [TestMethod] public void ParsesFStringConversionRepr() { Assert.IsInstanceOfType(ParseExpr("f\"{x!r}\""), typeof(FStringExpression)); }
        [TestMethod] public void ParsesFStringConversionStr() { Assert.IsInstanceOfType(ParseExpr("f\"{x!s}\""), typeof(FStringExpression)); }
        [TestMethod] public void ParsesFStringConversionAscii() { Assert.IsInstanceOfType(ParseExpr("f\"got {self.mode!r}\""), typeof(FStringExpression)); }

        // Format specs {x:8.3f} / {x:<3s} / {x:5d} + conversion — the ATOM-record idiom (cli.py:62).
        [TestMethod] public void ParsesFStringFormatSpec() { Assert.IsInstanceOfType(ParseExpr("f\"{x:8.3f}\""), typeof(FStringExpression)); }
        [TestMethod] public void ParsesFStringFormatSpecAlign() { Assert.IsInstanceOfType(ParseExpr("f\"{name:<3s}{res:>4s}\""), typeof(FStringExpression)); }
        [TestMethod] public void ParsesFStringConversionThenFormatSpec() { Assert.IsInstanceOfType(ParseExpr("f\"{x!r:>10}\""), typeof(FStringExpression)); }
        // Scientific-notation format spec {p:.2e} — the format-spec text was lexed as tokens and
        // `2e` (exponent, no digits) tripped "Invalid numeric literal" (headtohead_sidechain.py:316).
        [TestMethod] public void ParsesFStringScientificFormatSpec() { Assert.IsInstanceOfType(ParseExpr("f\"p={p:.2e}\""), typeof(FStringExpression)); }
        [TestMethod] public void ParsesFStringFormatSpecWithNestedExprAndTernary() { Assert.IsInstanceOfType(ParseExpr("f\"{err:.2e} ({'sig' if p < 0.05 else 'n.s.'})\""), typeof(FStringExpression)); }
        // A ':' inside a slice within an f-string field is NOT a format spec.
        [TestMethod] public void ParsesFStringWithSliceNotFormatSpec() { Assert.IsInstanceOfType(ParseExpr("f\"{a[1:2]}\""), typeof(FStringExpression)); }

        // Implicit concatenation of adjacent (f-)string literals — was
        // "Expected RightParen, got FStringStart" (cli.py:62, data_shards.py:109 in the fixture).
        [TestMethod] public void ParsesAdjacentFStringConcatenation() { Assert.IsInstanceOfType(ParseExpr("f\"a{x}\" f\"b{y}\""), typeof(FStringExpression)); }
        [TestMethod] public void ParsesMixedStringFStringConcatenation() { Assert.IsInstanceOfType(ParseExpr("\"a\" f\"b{x}\""), typeof(FStringExpression)); }
        [TestMethod] public void ParsesFStringThenPlainStringConcatenation() { Assert.IsInstanceOfType(ParseExpr("f\"a{x}\" \"b\""), typeof(FStringExpression)); }
        [TestMethod] public void ParsesPlainAdjacentStringsStillLiteral() { Assert.IsInstanceOfType(ParseExpr("\"a\" \"b\""), typeof(LiteralExpression)); }

        // The real wrapped-write / wrapped-print idiom from the sample workspace fixture — a call whose args
        // are two adjacent f-strings with format specs and conversions; must parse with no errors.
        [TestMethod] public void ParsesWrappedWriteIdiom()
        {
            ParseExpr("fh.write(f\"ATOM  {serial:5d}  {name:<3s}\" f\"{x:8.3f}{y:8.3f}\\n\")");
            ParseExpr("print(f\"[shards] done: {len(chains)} chains \" f\"(excluded {n_excl} PDBs)\", flush=True)");
        }

        // --------- f-strings: conversion/format-spec content is actually captured ---------
        // The parser used to recognize this syntax but discard the !conversion / :spec text
        // entirely ("skip for now") — the checks above only assert the node type, not the
        // content. The Python->C++ translator (and anything else needing real fidelity) needs
        // the actual conversion letter and spec text, which the lexer already tokenizes
        // correctly; these confirm the parser now preserves it.

        [TestMethod]
        public void FStringConversion_IsCapturedOnTheField()
        {
            FStringExpression fs = (FStringExpression)ParseExpr("f\"{x!r}\"");
            int fieldIndex = fs.Parts.ToList().FindIndex(p => p is NameExpression);
            Assert.AreEqual('r', fs.Conversions[fieldIndex]);
            Assert.IsNull(fs.FormatSpecs[fieldIndex]);
        }

        [TestMethod]
        public void FStringFormatSpec_IsCapturedWithoutTheLeadingColon()
        {
            FStringExpression fs = (FStringExpression)ParseExpr("f\"{x:8.3f}\"");
            int fieldIndex = fs.Parts.ToList().FindIndex(p => p is NameExpression);
            Assert.AreEqual("8.3f", fs.FormatSpecs[fieldIndex]);
            Assert.IsNull(fs.Conversions[fieldIndex]);
        }

        [TestMethod]
        public void FStringConversionAndFormatSpec_BothCapturedTogether()
        {
            FStringExpression fs = (FStringExpression)ParseExpr("f\"{x!r:>10}\"");
            int fieldIndex = fs.Parts.ToList().FindIndex(p => p is NameExpression);
            Assert.AreEqual('r', fs.Conversions[fieldIndex]);
            Assert.AreEqual(">10", fs.FormatSpecs[fieldIndex]);
        }

        [TestMethod]
        public void FStringWithoutConversionOrSpec_BothNull()
        {
            FStringExpression fs = (FStringExpression)ParseExpr("f\"{x}\"");
            int fieldIndex = fs.Parts.ToList().FindIndex(p => p is NameExpression);
            Assert.IsNull(fs.Conversions[fieldIndex]);
            Assert.IsNull(fs.FormatSpecs[fieldIndex]);
        }

        [TestMethod]
        public void FStringConcatenation_PreservesConversionsAndSpecsAcrossFlattening()
        {
            // ParseStringConcatenation flattens two f-strings' Parts into one — the parallel
            // Conversions/FormatSpecs lists must stay index-aligned through that flatten.
            FStringExpression fs = (FStringExpression)ParseExpr("f\"{x!r}\" f\"{y:.2f}\"");
            Assert.AreEqual(fs.Parts.Count, fs.Conversions.Count);
            Assert.AreEqual(fs.Parts.Count, fs.FormatSpecs.Count);
            var names = fs.Parts.Select((p, i) => (p, i)).Where(t => t.p is NameExpression).ToList();
            Assert.AreEqual(2, names.Count);
            Assert.AreEqual('r', fs.Conversions[names[0].i]);
            Assert.AreEqual(".2f", fs.FormatSpecs[names[1].i]);
        }

        // --------- Arithmetic ---------

        [TestMethod]
        public void ParsesAddition()
        {
            BinaryExpression e = (BinaryExpression)ParseExpr("1 + 2");
            Assert.AreEqual(BinaryOperator.Add, e.Operator);
        }

        [TestMethod]
        public void ArithmeticPrecedence_MultBindsTighter()
        {
            // 1 + 2 * 3 → 1 + (2*3)
            BinaryExpression e = (BinaryExpression)ParseExpr("1 + 2 * 3");
            Assert.AreEqual(BinaryOperator.Add, e.Operator);
            BinaryExpression right = (BinaryExpression)e.Right;
            Assert.AreEqual(BinaryOperator.Multiply, right.Operator);
        }

        [TestMethod]
        public void PowerIsRightAssociative()
        {
            // 2 ** 3 ** 2 → 2 ** (3 ** 2)
            BinaryExpression e = (BinaryExpression)ParseExpr("2 ** 3 ** 2");
            Assert.AreEqual(BinaryOperator.Power, e.Operator);
            BinaryExpression right = (BinaryExpression)e.Right;
            Assert.AreEqual(BinaryOperator.Power, right.Operator);
        }

        [TestMethod]
        public void ParsesUnaryMinus()
        {
            UnaryExpression e = (UnaryExpression)ParseExpr("-x");
            Assert.AreEqual(UnaryOperator.Minus, e.Operator);
        }

        [TestMethod]
        public void ParsesFloorDivAndMatMul()
        {
            BinaryExpression fd = (BinaryExpression)ParseExpr("a // b");
            Assert.AreEqual(BinaryOperator.FloorDivide, fd.Operator);
            BinaryExpression mm = (BinaryExpression)ParseExpr("a @ b");
            Assert.AreEqual(BinaryOperator.MatMul, mm.Operator);
        }

        // --------- Boolean ---------

        [TestMethod] public void ParsesAnd() { Assert.AreEqual(BinaryOperator.And, ((BinaryExpression)ParseExpr("a and b")).Operator); }
        [TestMethod] public void ParsesOr() { Assert.AreEqual(BinaryOperator.Or, ((BinaryExpression)ParseExpr("a or b")).Operator); }
        [TestMethod] public void ParsesNot() { Assert.AreEqual(UnaryOperator.Not, ((UnaryExpression)ParseExpr("not x")).Operator); }

        // --------- Comparison ---------

        [TestMethod]
        public void ParsesSimpleComparison()
        {
            ComparisonExpression c = (ComparisonExpression)ParseExpr("a < b");
            Assert.AreEqual(1, c.Operators.Count);
            Assert.AreEqual(ComparisonOperator.Less, c.Operators[0]);
        }

        [TestMethod]
        public void ParsesChainedComparison()
        {
            ComparisonExpression c = (ComparisonExpression)ParseExpr("1 < x < 10");
            Assert.AreEqual(2, c.Operators.Count);
        }

        [TestMethod]
        public void ParsesAllComparisonOps()
        {
            Assert.AreEqual(ComparisonOperator.Eq, ((ComparisonExpression)ParseExpr("a == b")).Operators[0]);
            Assert.AreEqual(ComparisonOperator.NotEq, ((ComparisonExpression)ParseExpr("a != b")).Operators[0]);
            Assert.AreEqual(ComparisonOperator.LessEq, ((ComparisonExpression)ParseExpr("a <= b")).Operators[0]);
            Assert.AreEqual(ComparisonOperator.GreaterEq, ((ComparisonExpression)ParseExpr("a >= b")).Operators[0]);
            Assert.AreEqual(ComparisonOperator.In, ((ComparisonExpression)ParseExpr("a in b")).Operators[0]);
            Assert.AreEqual(ComparisonOperator.NotIn, ((ComparisonExpression)ParseExpr("a not in b")).Operators[0]);
            Assert.AreEqual(ComparisonOperator.Is, ((ComparisonExpression)ParseExpr("a is b")).Operators[0]);
            Assert.AreEqual(ComparisonOperator.IsNot, ((ComparisonExpression)ParseExpr("a is not b")).Operators[0]);
        }

        // --------- Ternary & walrus ---------

        [TestMethod]
        public void ParsesTernary()
        {
            ConditionalExpression e = (ConditionalExpression)ParseExpr("a if b else c");
            Assert.IsNotNull(e.IfTrue);
            Assert.IsNotNull(e.Condition);
            Assert.IsNotNull(e.IfFalse);
        }

        [TestMethod]
        public void ParsesWalrus()
        {
            NamedExpression e = (NamedExpression)ParseExpr("(n := 10)").Children.OfType<NamedExpression>().First();
            Assert.AreEqual("n", e.Target.Name);
        }

        // --------- Lambda ---------

        [TestMethod]
        public void ParsesLambdaNoArgs()
        {
            LambdaExpression e = (LambdaExpression)ParseExpr("lambda: 42");
            Assert.AreEqual(0, e.Parameters.Count);
        }

        [TestMethod]
        public void ParsesLambdaWithArgs()
        {
            LambdaExpression e = (LambdaExpression)ParseExpr("lambda x, y=1: x + y");
            Assert.AreEqual(2, e.Parameters.Count);
            Assert.IsNotNull(e.Parameters[1].DefaultValue);
        }

        // --------- Calls / subscript / attribute ---------

        [TestMethod]
        public void ParsesCallNoArgs()
        {
            CallExpression c = (CallExpression)ParseExpr("f()");
            Assert.AreEqual(0, c.Arguments.Count);
        }

        [TestMethod]
        public void ParsesCallPositionalAndKeyword()
        {
            CallExpression c = (CallExpression)ParseExpr("f(1, x=2, *args, **kw)");
            Assert.AreEqual(4, c.Arguments.Count);
            Assert.AreEqual(CallArgument.ArgKind.Positional, c.Arguments[0].Kind);
            Assert.AreEqual(CallArgument.ArgKind.Keyword, c.Arguments[1].Kind);
            Assert.AreEqual(CallArgument.ArgKind.Star, c.Arguments[2].Kind);
            Assert.AreEqual(CallArgument.ArgKind.DoubleStar, c.Arguments[3].Kind);
        }

        [TestMethod]
        public void ParsesChainedCallAndAttribute()
        {
            // obj.method().other → Attribute(Call(Attr(obj, method)), other)
            AttributeExpression outer = (AttributeExpression)ParseExpr("obj.method().other");
            Assert.AreEqual("other", outer.Name);
            Assert.IsInstanceOfType(outer.Target, typeof(CallExpression));
        }

        [TestMethod]
        public void ParsesSubscript()
        {
            SubscriptExpression s = (SubscriptExpression)ParseExpr("a[0]");
            Assert.IsInstanceOfType(s.Index, typeof(LiteralExpression));
        }

        [TestMethod]
        public void ParsesSlice()
        {
            SubscriptExpression s = (SubscriptExpression)ParseExpr("a[1:10:2]");
            Assert.IsInstanceOfType(s.Index, typeof(SliceExpression));
        }

        [TestMethod]
        public void ParsesAttribute()
        {
            AttributeExpression a = (AttributeExpression)ParseExpr("obj.field");
            Assert.AreEqual("field", a.Name);
        }

        // --------- Collections ---------

        [TestMethod]
        public void ParsesEmptyList() { Assert.AreEqual(0, ((ListExpression)ParseExpr("[]")).Elements.Count); }
        [TestMethod] public void ParsesListLiteral() { Assert.AreEqual(3, ((ListExpression)ParseExpr("[1, 2, 3]")).Elements.Count); }
        [TestMethod] public void ParsesTupleLiteral() { Assert.IsInstanceOfType(ParseExpr("1, 2, 3"), typeof(TupleExpression)); }
        [TestMethod] public void ParsesEmptyDict() { Assert.AreEqual(0, ((DictExpression)ParseExpr("{}")).Entries.Count); }
        [TestMethod] public void ParsesDictLiteral() { Assert.AreEqual(2, ((DictExpression)ParseExpr("{1: 'a', 2: 'b'}")).Entries.Count); }
        [TestMethod] public void ParsesSetLiteral() { Assert.AreEqual(3, ((SetExpression)ParseExpr("{1, 2, 3}")).Elements.Count); }

        // --------- Comprehensions ---------

        [TestMethod]
        public void ParsesListComprehension()
        {
            ComprehensionExpression c = (ComprehensionExpression)ParseExpr("[x*2 for x in range(10)]");
            Assert.AreEqual(ComprehensionKind.List, c.Kind);
            Assert.AreEqual(1, c.Clauses.Count);
        }

        [TestMethod]
        public void ParsesDictComprehension()
        {
            ComprehensionExpression c = (ComprehensionExpression)ParseExpr("{k: v for k, v in pairs}");
            Assert.AreEqual(ComprehensionKind.Dict, c.Kind);
            Assert.IsNotNull(c.ValueElement);
        }

        // --------- PEP 530: async comprehensions ---------

        [TestMethod]
        public void ParsesAsyncListComprehension()
        {
            // Django shortcuts.py:165 — `[obj async for obj in queryset.filter(...)]`.
            ComprehensionExpression c = (ComprehensionExpression)ParseExpr("[obj async for obj in queryset]");
            Assert.AreEqual(ComprehensionKind.List, c.Kind);
            Assert.AreEqual(1, c.Clauses.Count);
            Assert.IsTrue(c.Clauses[0].IsAsync, "the clause should be marked async");
        }

        [TestMethod]
        public void ParsesAsyncSetComprehension()
        {
            ComprehensionExpression c = (ComprehensionExpression)ParseExpr("{n async for n in perms}");
            Assert.AreEqual(ComprehensionKind.Set, c.Kind);
            Assert.IsTrue(c.Clauses[0].IsAsync);
        }

        [TestMethod]
        public void ParsesAsyncDictComprehension()
        {
            ComprehensionExpression c = (ComprehensionExpression)ParseExpr("{k: v async for k, v in items}");
            Assert.AreEqual(ComprehensionKind.Dict, c.Kind);
            Assert.IsTrue(c.Clauses[0].IsAsync);
        }

        [TestMethod]
        public void ParsesAsyncGeneratorExpression()
        {
            ComprehensionExpression c = (ComprehensionExpression)ParseExpr("(x async for x in aiter)");
            Assert.AreEqual(ComprehensionKind.Generator, c.Kind);
            Assert.IsTrue(c.Clauses[0].IsAsync);
        }

        [TestMethod]
        public void ParsesMixedSyncThenAsyncClauses()
        {
            // A sync outer clause followed by an async inner clause.
            ComprehensionExpression c = (ComprehensionExpression)ParseExpr("[y for x in xs async for y in agen(x)]");
            Assert.AreEqual(2, c.Clauses.Count);
            Assert.IsFalse(c.Clauses[0].IsAsync);
            Assert.IsTrue(c.Clauses[1].IsAsync);
        }

        [TestMethod]
        public void ParsesSetComprehension()
        {
            ComprehensionExpression c = (ComprehensionExpression)ParseExpr("{x for x in items}");
            Assert.AreEqual(ComprehensionKind.Set, c.Kind);
        }

        [TestMethod]
        public void ParsesGeneratorComprehension()
        {
            ComprehensionExpression c = (ComprehensionExpression)ParseExpr("(x for x in items)");
            Assert.AreEqual(ComprehensionKind.Generator, c.Kind);
        }

        [TestMethod]
        public void ParsesComprehensionWithMultipleFor()
        {
            ComprehensionExpression c = (ComprehensionExpression)ParseExpr("[x+y for x in xs for y in ys]");
            Assert.AreEqual(2, c.Clauses.Count);
        }

        [TestMethod]
        public void ParsesComprehensionWithIf()
        {
            ComprehensionExpression c = (ComprehensionExpression)ParseExpr("[x for x in xs if x > 0]");
            Assert.AreEqual(1, c.Clauses[0].Ifs.Count);
        }

        // --------- Yield ---------

        [TestMethod]
        public void ParsesYieldValue()
        {
            YieldExpression y = ParseExprInFunction("(yield 42)").Children.OfType<YieldExpression>().First();
            Assert.IsFalse(y.IsFrom);
        }

        [TestMethod]
        public void ParsesYieldFrom()
        {
            YieldExpression y = ParseExprInFunction("(yield from other)").Children.OfType<YieldExpression>().First();
            Assert.IsTrue(y.IsFrom);
        }

        // --------- F-strings ---------

        [TestMethod]
        public void ParsesFStringLiteralOnly()
        {
            FStringExpression f = (FStringExpression)ParseExpr("f'hello'");
            Assert.IsNotNull(f);
        }

        [TestMethod]
        public void ParsesFStringWithExpression()
        {
            FStringExpression f = (FStringExpression)ParseExpr("f'hello {name}!'");
            Assert.IsTrue(f.Parts.Count >= 2, "f-string should have at least one literal and one expression piece.");
        }

        // --------- Bitwise / shift ---------

        [TestMethod] public void ParsesBitwiseOr() { Assert.AreEqual(BinaryOperator.BitOr, ((BinaryExpression)ParseExpr("a | b")).Operator); }
        [TestMethod] public void ParsesBitwiseAnd() { Assert.AreEqual(BinaryOperator.BitAnd, ((BinaryExpression)ParseExpr("a & b")).Operator); }
        [TestMethod] public void ParsesBitwiseXor() { Assert.AreEqual(BinaryOperator.BitXor, ((BinaryExpression)ParseExpr("a ^ b")).Operator); }
        [TestMethod] public void ParsesLeftShift() { Assert.AreEqual(BinaryOperator.LShift, ((BinaryExpression)ParseExpr("a << b")).Operator); }
        [TestMethod] public void ParsesRightShift() { Assert.AreEqual(BinaryOperator.RShift, ((BinaryExpression)ParseExpr("a >> b")).Operator); }

        // --------- Starred / unpacking ---------

        [TestMethod]
        public void ParsesStarredInList()
        {
            ListExpression lx = (ListExpression)ParseExpr("[1, *rest, 2]");
            Assert.IsInstanceOfType(lx.Elements[1], typeof(StarredExpression));
        }
    }
}
