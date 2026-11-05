using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python.Tests
{
    [TestClass]
    public class StatementTests
    {
        private static ModuleSyntax Parse(string src)
        {
            ParseResult r = Python.ParseModule(src);
            Assert.IsFalse(r.HasErrors, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
            return r.Module;
        }

        // --------- Simple ---------

        [TestMethod]
        public void ParsesPassBreakContinue()
        {
            // break/continue must sit inside a loop — bare at module level they are a
            // SyntaxError, which the parser now reports (see ErrorCatalogTests).
            ModuleSyntax m = Parse("pass\nwhile x:\n    break\n    continue\n");
            Assert.AreEqual(2, m.Statements.Count);
            Assert.IsInstanceOfType(m.Statements[0], typeof(PassStatement));
            WhileStatement loop = (WhileStatement)m.Statements[1];
            Assert.IsInstanceOfType(loop.Body.Statements[0], typeof(BreakStatement));
            Assert.IsInstanceOfType(loop.Body.Statements[1], typeof(ContinueStatement));
        }

        [TestMethod]
        public void ParsesReturnWithValue()
        {
            ModuleSyntax m = Parse("def f():\n    return 42\n");
            FunctionDefSyntax fn = m.Statements.OfType<FunctionDefSyntax>().Single();
            ReturnStatement r = fn.Body.Statements.OfType<ReturnStatement>().Single();
            Assert.IsNotNull(r.Value);
        }

        [TestMethod]
        public void ParsesReturnBare()
        {
            ModuleSyntax m = Parse("def f():\n    return\n");
            FunctionDefSyntax fn = m.Statements.OfType<FunctionDefSyntax>().Single();
            ReturnStatement r = fn.Body.Statements.OfType<ReturnStatement>().Single();
            Assert.IsNull(r.Value);
        }

        [TestMethod]
        public void ParsesRaiseWithCause()
        {
            ModuleSyntax m = Parse("def f():\n    raise ValueError('x') from err\n");
            FunctionDefSyntax fn = m.Statements.OfType<FunctionDefSyntax>().Single();
            RaiseStatement r = fn.Body.Statements.OfType<RaiseStatement>().Single();
            Assert.IsNotNull(r.Exception);
            Assert.IsNotNull(r.Cause);
        }

        [TestMethod]
        public void ParsesAssertMessage()
        {
            ModuleSyntax m = Parse("assert x > 0, 'must be positive'\n");
            AssertStatement a = m.Statements.OfType<AssertStatement>().Single();
            Assert.IsNotNull(a.Test);
            Assert.IsNotNull(a.Message);
        }

        [TestMethod]
        public void ParsesGlobalAndNonlocal()
        {
            ModuleSyntax m = Parse("def f():\n    global a, b\n    nonlocal c\n");
            FunctionDefSyntax fn = m.Statements.OfType<FunctionDefSyntax>().Single();
            GlobalStatement g = fn.Body.Statements.OfType<GlobalStatement>().Single();
            NonlocalStatement n = fn.Body.Statements.OfType<NonlocalStatement>().Single();
            CollectionAssert.AreEqual(new[] { "a", "b" }, g.Names.ToArray());
            CollectionAssert.AreEqual(new[] { "c" }, n.Names.ToArray());
        }

        [TestMethod]
        public void ParsesDelete()
        {
            ModuleSyntax m = Parse("del x, y\n");
            DelStatement d = m.Statements.OfType<DelStatement>().Single();
            Assert.AreEqual(2, d.Targets.Count);
        }

        [TestMethod]
        public void ParsesYieldStatement()
        {
            ModuleSyntax m = Parse("def g():\n    yield 1\n    yield from other\n");
            FunctionDefSyntax fn = m.Statements.OfType<FunctionDefSyntax>().Single();
            var yields = fn.Body.Statements.OfType<YieldStatement>().ToList();
            Assert.AreEqual(2, yields.Count);
            Assert.IsFalse(yields[0].Expression.IsFrom);
            Assert.IsTrue(yields[1].Expression.IsFrom);
        }

        // --------- if / while / for ---------

        [TestMethod]
        public void ParsesIfElifElse()
        {
            string src = "if x:\n    a\nelif y:\n    b\nelse:\n    c\n";
            ModuleSyntax m = Parse(src);
            IfStatement s = m.Statements.OfType<IfStatement>().Single();
            Assert.AreEqual(1, s.Elifs.Count);
            Assert.IsNotNull(s.Else);
        }

        [TestMethod]
        public void ParsesWhileLoop()
        {
            ModuleSyntax m = Parse("while x:\n    foo()\n");
            Assert.IsInstanceOfType(m.Statements.Single(), typeof(WhileStatement));
        }

        [TestMethod]
        public void ParsesWhileElse()
        {
            ModuleSyntax m = Parse("while x:\n    pass\nelse:\n    pass\n");
            WhileStatement w = (WhileStatement)m.Statements.Single();
            Assert.IsNotNull(w.Else);
        }

        [TestMethod]
        public void ParsesForInList()
        {
            ModuleSyntax m = Parse("for i in range(10):\n    print(i)\n");
            Assert.IsInstanceOfType(m.Statements.Single(), typeof(ForStatement));
        }

        [TestMethod]
        public void ParsesForUnpackTarget()
        {
            ModuleSyntax m = Parse("for k, v in d.items():\n    pass\n");
            ForStatement f = (ForStatement)m.Statements.Single();
            Assert.IsInstanceOfType(f.Target, typeof(TupleExpression));
        }

        [TestMethod]
        public void ParsesAsyncFor()
        {
            ModuleSyntax m = Parse("async def f():\n    async for x in it:\n        pass\n");
            FunctionDefSyntax fn = m.Statements.OfType<FunctionDefSyntax>().Single();
            ForStatement f = fn.Body.Statements.OfType<ForStatement>().Single();
            Assert.IsTrue(f.IsAsync);
        }

        // --------- try / with ---------

        [TestMethod]
        public void ParsesTryExceptFinally()
        {
            string src = "try:\n    a\nexcept Ex as e:\n    b\nexcept:\n    c\nfinally:\n    d\n";
            ModuleSyntax m = Parse(src);
            TryStatement t = (TryStatement)m.Statements.Single();
            Assert.AreEqual(2, t.Handlers.Count);
            Assert.AreEqual("e", t.Handlers[0].Name);
            Assert.IsNull(t.Handlers[1].ExceptionType);
            Assert.IsNotNull(t.Finally);
        }

        [TestMethod]
        public void ParsesTryElseFinally()
        {
            ModuleSyntax m = Parse("try:\n    a\nexcept:\n    b\nelse:\n    c\nfinally:\n    d\n");
            TryStatement t = (TryStatement)m.Statements.Single();
            Assert.IsNotNull(t.Else);
            Assert.IsNotNull(t.Finally);
        }

        [TestMethod]
        public void ParsesExceptStar_Pep654()
        {
            ModuleSyntax m = Parse("try:\n    x\nexcept* ValueError as e:\n    pass\n");
            TryStatement t = (TryStatement)m.Statements.Single();
            Assert.IsTrue(t.Handlers[0].IsStar);
        }

        [TestMethod]
        public void ParsesWithSingle()
        {
            ModuleSyntax m = Parse("with open('f') as f:\n    read(f)\n");
            WithStatement w = (WithStatement)m.Statements.Single();
            Assert.AreEqual(1, w.Items.Count);
            Assert.IsNotNull(w.Items[0].OptionalVars);
        }

        [TestMethod]
        public void ParsesWithMultiple()
        {
            ModuleSyntax m = Parse("with a, b as c, d:\n    pass\n");
            WithStatement w = (WithStatement)m.Statements.Single();
            Assert.AreEqual(3, w.Items.Count);
        }

        [TestMethod]
        public void ParsesWithParenthesized()
        {
            ModuleSyntax m = Parse("with (a, b as c):\n    pass\n");
            WithStatement w = (WithStatement)m.Statements.Single();
            Assert.AreEqual(2, w.Items.Count);
        }

        [TestMethod]
        public void ParsesAsyncWith()
        {
            ModuleSyntax m = Parse("async def f():\n    async with lock:\n        pass\n");
            FunctionDefSyntax fn = m.Statements.OfType<FunctionDefSyntax>().Single();
            WithStatement w = fn.Body.Statements.OfType<WithStatement>().Single();
            Assert.IsTrue(w.IsAsync);
        }

        [TestMethod]
        public void ParsesWithParenExprContextManager()
        {
            // A parenthesized EXPRESSION as the context manager, followed by a trailer —
            // NOT a PEP 617 parenthesized with-items group. The old parser committed to the
            // group form and failed at the '.' after ')' ("Expected Colon, got Dot").
            // (_rouse_dynamics_evidence.py:477 — `with (out / "f").open("w") as f:`.)
            ModuleSyntax m = Parse("with (out / \"f\").open(\"w\") as f:\n    pass\n");
            WithStatement w = (WithStatement)m.Statements.Single();
            Assert.AreEqual(1, w.Items.Count);
            Assert.IsNotNull(w.Items[0].OptionalVars);
        }

        [TestMethod]
        public void ParsesWithParenExprNoAs()
        {
            // Same parenthesized-expression context manager but without `as`.
            ModuleSyntax m = Parse("with (a / b).open(\"w\"):\n    pass\n");
            WithStatement w = (WithStatement)m.Statements.Single();
            Assert.AreEqual(1, w.Items.Count);
            Assert.IsNull(w.Items[0].OptionalVars);
        }

        [TestMethod]
        public void ParsesWithParenSingleThenAs()
        {
            // `with (a) as f:` — a parenthesized single expression with the `as` OUTSIDE the
            // parens; must backtrack out of the group interpretation.
            ModuleSyntax m = Parse("with (a) as f:\n    pass\n");
            WithStatement w = (WithStatement)m.Statements.Single();
            Assert.AreEqual(1, w.Items.Count);
            Assert.IsNotNull(w.Items[0].OptionalVars);
        }

        // --------- Assignment ---------

        [TestMethod]
        public void ParsesSimpleAssignment()
        {
            ModuleSyntax m = Parse("x = 1\n");
            AssignmentStatement a = (AssignmentStatement)m.Statements.Single();
            Assert.AreEqual(1, a.Targets.Count);
        }

        [TestMethod]
        public void ParsesChainedAssignment()
        {
            ModuleSyntax m = Parse("x = y = z = 1\n");
            AssignmentStatement a = (AssignmentStatement)m.Statements.Single();
            Assert.AreEqual(3, a.Targets.Count);
        }

        [TestMethod]
        public void ParsesAugmentedAssignment()
        {
            ModuleSyntax m = Parse("x += 1\n");
            AugmentedAssignmentStatement a = (AugmentedAssignmentStatement)m.Statements.Single();
            Assert.AreEqual(BinaryOperator.Add, a.Operator);
        }

        [TestMethod]
        public void ParsesAllAugmentedOps()
        {
            string src = "a += 1\nb -= 1\nc *= 1\nd /= 1\ne //= 1\nf %= 1\ng @= m\nh **= 1\ni |= 1\nj &= 1\nk ^= 1\nl <<= 1\nn >>= 1\n";
            ModuleSyntax m = Parse(src);
            Assert.AreEqual(13, m.Statements.OfType<AugmentedAssignmentStatement>().Count());
        }

        [TestMethod]
        public void ParsesAnnotatedAssignment()
        {
            ModuleSyntax m = Parse("x: int = 42\n");
            AssignmentStatement a = (AssignmentStatement)m.Statements.Single();
            Assert.IsNotNull(a.Annotation);
            Assert.IsNotNull(a.Value);
        }

        [TestMethod]
        public void ParsesAnnotatedAssignmentNoValue()
        {
            ModuleSyntax m = Parse("x: int\n");
            AssignmentStatement a = (AssignmentStatement)m.Statements.Single();
            Assert.IsNotNull(a.Annotation);
            Assert.IsNull(a.Value);
        }

        [TestMethod]
        public void ParsesTupleTargetAssignment()
        {
            ModuleSyntax m = Parse("a, b = 1, 2\n");
            AssignmentStatement a2 = (AssignmentStatement)m.Statements.Single();
            Assert.IsInstanceOfType(a2.Targets[0], typeof(TupleExpression));
        }

        // --------- Match/case ---------

        [TestMethod]
        public void ParsesMatchBasic()
        {
            string src = "match x:\n    case 1:\n        pass\n    case _:\n        pass\n";
            ModuleSyntax m = Parse(src);
            MatchStatement ms = m.Statements.OfType<MatchStatement>().Single();
            Assert.AreEqual(2, ms.Cases.Count);
            Assert.IsInstanceOfType(ms.Cases[0].Pattern, typeof(LiteralPattern));
            Assert.IsInstanceOfType(ms.Cases[1].Pattern, typeof(WildcardPattern));
        }

        [TestMethod]
        public void ParsesMatchWithGuard()
        {
            string src = "match x:\n    case n if n > 0:\n        pass\n";
            ModuleSyntax m = Parse(src);
            MatchStatement ms = m.Statements.OfType<MatchStatement>().Single();
            Assert.IsNotNull(ms.Cases[0].Guard);
        }

        [TestMethod]
        public void ParsesMatchOrPattern()
        {
            string src = "match x:\n    case 1 | 2 | 3:\n        pass\n";
            ModuleSyntax m = Parse(src);
            MatchStatement ms = m.Statements.OfType<MatchStatement>().Single();
            Assert.IsInstanceOfType(ms.Cases[0].Pattern, typeof(OrPattern));
        }

        [TestMethod]
        public void ParsesMatchClassPattern()
        {
            string src = "match p:\n    case Point(x=0, y=0):\n        pass\n";
            ModuleSyntax m = Parse(src);
            MatchStatement ms = m.Statements.OfType<MatchStatement>().Single();
            Assert.IsInstanceOfType(ms.Cases[0].Pattern, typeof(ClassPattern));
        }

        [TestMethod]
        public void ParsesMatchSequencePattern()
        {
            string src = "match p:\n    case [1, 2, *rest]:\n        pass\n";
            ModuleSyntax m = Parse(src);
            MatchStatement ms = m.Statements.OfType<MatchStatement>().Single();
            Assert.IsInstanceOfType(ms.Cases[0].Pattern, typeof(SequencePattern));
        }

        [TestMethod]
        public void ParsesMatchMappingPattern()
        {
            string src = "match p:\n    case {'x': 1, **rest}:\n        pass\n";
            ModuleSyntax m = Parse(src);
            MatchStatement ms = m.Statements.OfType<MatchStatement>().Single();
            MappingPattern mp = (MappingPattern)ms.Cases[0].Pattern;
            Assert.AreEqual(1, mp.Entries.Count);
            Assert.AreEqual("rest", mp.DoubleStarName);
        }

        [TestMethod]
        public void ParsesMatchAsPattern()
        {
            string src = "match p:\n    case [1, 2] as pair:\n        pass\n";
            ModuleSyntax m = Parse(src);
            MatchStatement ms = m.Statements.OfType<MatchStatement>().Single();
            Assert.IsInstanceOfType(ms.Cases[0].Pattern, typeof(AsPattern));
        }

        [TestMethod]
        public void MatchIsSoftKeyword()
        {
            // 'match' as a variable name is still valid Python.
            ModuleSyntax m = Parse("match = 42\n");
            AssignmentStatement a = (AssignmentStatement)m.Statements.Single();
            Assert.AreEqual("match", ((NameExpression)a.Targets[0]).Name);
        }

        // --------- Module-level mix ---------

        [TestMethod]
        public void ParsesRealishFunction()
        {
            string src =
                "def process(items: list[int]) -> int:\n" +
                "    total = 0\n" +
                "    for i in items:\n" +
                "        if i > 0:\n" +
                "            total += i\n" +
                "        elif i == 0:\n" +
                "            continue\n" +
                "        else:\n" +
                "            break\n" +
                "    return total\n";
            ModuleSyntax m = Parse(src);
            FunctionDefSyntax fn = m.Statements.OfType<FunctionDefSyntax>().Single();
            Assert.AreEqual("process", fn.Name);
            Assert.IsInstanceOfType(fn.Body.Statements[0], typeof(AssignmentStatement));
            Assert.IsInstanceOfType(fn.Body.Statements[1], typeof(ForStatement));
            Assert.IsInstanceOfType(fn.Body.Statements[2], typeof(ReturnStatement));
        }
    }
}
