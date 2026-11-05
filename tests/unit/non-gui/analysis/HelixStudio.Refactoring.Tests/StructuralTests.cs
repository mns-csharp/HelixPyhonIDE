namespace HelixPythonNamespace.Refactoring.Tests
{
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Refactoring;
    using HelixPythonNamespace.VirtualCompiler;
    using Kind = HelixPythonNamespace.Refactoring.StructuralCandidate.Kind;

    /// <summary>
    /// Port of the C++ GTest suite <c>tests/refactoring_tests/structural_tests.cpp</c>
    /// (65 cases). The expected strings are reproduced verbatim from the C++ source: the
    /// emitters are formatting-exact, so any byte-level divergence is a defect.
    /// </summary>
    [TestClass]
    public class StructuralTests
    {
        private static StructuralCandidate GetKind(List<StructuralCandidate> cs, Kind k)
        {
            foreach (StructuralCandidate c in cs) if (c.PatternKind == k) return c;
            return null;
        }

        private static bool HasCaveat(StructuralCandidate c, string code)
        {
            foreach (CandidateIssue iss in c.Caveats) if (iss.Code == code) return true;
            return false;
        }

        // Syntactic validity: ignore semantic (undefined-name/type) diagnostics; a
        // transform fragment's free names are bound in the surrounding module.
        private static bool ParsesCleanly(string src)
        {
            CompileResult result = VirtualCompiler.CompileSource(src);
            foreach (CompileDiagnostic d in result.Diagnostics)
            {
                if (d.Severity == ParseSeverity.Fatal) return false;
                if (d.Code != null && d.Code.StartsWith("PY1")) return false;
            }
            return true;
        }

        // Canonical plain (un-patterned) factory function.
        private const string kPlain =
            "def create_animal(kind):\n"
            + "    if kind == \"dog\":\n"
            + "        return Dog()\n"
            + "    elif kind == \"cat\":\n"
            + "        return Cat()\n"
            + "    raise ValueError(kind)";

        // Canonical registry factory class (the forward transform's output).
        private const string kFactory =
            "class AnimalFactory:\n"
            + "    _registry = {\n"
            + "        \"dog\": Dog,\n"
            + "        \"cat\": Cat,\n"
            + "    }\n"
            + "\n"
            + "    @classmethod\n"
            + "    def create(cls, kind):\n"
            + "        if kind not in cls._registry:\n"
            + "            raise ValueError(kind)\n"
            + "        return cls._registry[kind]()";

        // =====================================================================
        // P1 Factory — forward (introduce)
        // =====================================================================

        [TestMethod]
        public void P1Factory_ForwardMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kPlain);
            StructuralCandidate m = GetKind(cs, Kind.P1_FactoryIntroduce);
            Assert.IsNotNull(m);
            Assert.AreEqual(kFactory, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
        }

        [TestMethod]
        public void P1Factory_ForwardRejectsMultiStatementBranch()
        {
            // A branch that does more than return C() is not a pure factory.
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "def create_animal(kind):\n"
                + "    if kind == \"dog\":\n"
                + "        log(kind)\n"
                + "        return Dog()\n"
                + "    raise ValueError(kind)");
            Assert.IsNull(GetKind(cs, Kind.P1_FactoryIntroduce));
        }

        [TestMethod]
        public void P1Factory_ForwardRejectsConstructorWithArgs()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "def create_animal(kind):\n"
                + "    if kind == \"dog\":\n"
                + "        return Dog(kind)\n"
                + "    raise ValueError(kind)");
            Assert.IsNull(GetKind(cs, Kind.P1_FactoryIntroduce));
        }

        // =====================================================================
        // P1 Factory — reverse (remove)
        // =====================================================================

        [TestMethod]
        public void P1Factory_ReverseMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kFactory);
            StructuralCandidate m = GetKind(cs, Kind.P1_FactoryRemove);
            Assert.IsNotNull(m);
            Assert.AreEqual(kPlain, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
        }

        [TestMethod]
        public void P1Factory_ReverseRejectsNonFactoryClass()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "class Animal:\n"
                + "    def speak(self):\n"
                + "        return \"...\"");
            Assert.IsNull(GetKind(cs, Kind.P1_FactoryRemove));
        }

        // =====================================================================
        // Round-trip — exact equality both directions
        // =====================================================================

        [TestMethod]
        public void P6Facade_RoundTripPreservesReturnAnnotationAndTupleSubscript()
        {
            // Regression (iter 44): facade introduce->remove dropped the '-> T' return
            // annotation, and PyExprPrint rendered a tuple subscript Tuple[int, str] as
            // Tuple[(int, str)]. Both must survive an exact round-trip.
            string plain =
                "def orchestrate(a: int, b: str) -> Tuple[int, str]:\n" +
                "    step_one(a)\n" +
                "    step_two(b)\n" +
                "    return combine(a, b)\n";
            string fwd = StructuralRefactor.Apply(plain, Kind.P6_FacadeIntroduce);
            Assert.IsNotNull(fwd, "facade introduce should match the orchestration function");
            StringAssert.Contains(fwd, "-> Tuple[int, str]:",
                "the return annotation (with a bare tuple subscript, no extra parens) must survive the forward transform");
            string back = StructuralRefactor.Apply(fwd, Kind.P6_FacadeRemove);
            Assert.AreEqual(plain, back, "facade round-trip must reproduce the original exactly");
        }

        [TestMethod]
        public void P1Factory_RoundTripPlainToFactoryToPlain()
        {
            string fwd = StructuralRefactor.Apply(kPlain, Kind.P1_FactoryIntroduce);
            Assert.IsNotNull(fwd);
            Assert.AreEqual(kFactory, fwd);
            string back = StructuralRefactor.Apply(fwd, Kind.P1_FactoryRemove);
            Assert.IsNotNull(back);
            Assert.AreEqual(kPlain, back);
        }

        [TestMethod]
        public void P1Factory_RoundTripFactoryToPlainToFactory()
        {
            string rev = StructuralRefactor.Apply(kFactory, Kind.P1_FactoryRemove);
            Assert.IsNotNull(rev);
            Assert.AreEqual(kPlain, rev);
            string back = StructuralRefactor.Apply(rev, Kind.P1_FactoryIntroduce);
            Assert.IsNotNull(back);
            Assert.AreEqual(kFactory, back);
        }

        [TestMethod]
        public void P1Factory_ApplySplicesWithinSurroundingCode()
        {
            // The factory function is embedded between other top-level statements;
            // Apply must replace only its line range and keep the rest intact.
            string src = "import zoo\n\n" + kPlain + "\n\nx = 1\n";
            string applied = StructuralRefactor.Apply(src, Kind.P1_FactoryIntroduce);
            Assert.IsNotNull(applied);
            StringAssert.Contains(applied, "import zoo");
            StringAssert.Contains(applied, "x = 1");
            StringAssert.Contains(applied, "class AnimalFactory:");
            Assert.IsFalse(applied.Contains("def create_animal"));
        }

        [TestMethod]
        public void P1Factory_MakePrefixAlsoMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "def make_widget(kind):\n"
                + "    if kind == \"button\":\n"
                + "        return Button()\n"
                + "    raise ValueError(kind)");
            StructuralCandidate m = GetKind(cs, Kind.P1_FactoryIntroduce);
            Assert.IsNotNull(m);
            StringAssert.Contains(m.ReplacementText, "class WidgetFactory:");
        }

        // =====================================================================
        // O2 dict <-> class
        // =====================================================================

        private const string kDictFn =
            "def make_point(x, y):\n"
            + "    return {\"x\": x, \"y\": y}";

        private const string kDataClass =
            "class Point:\n"
            + "    def __init__(self, x, y):\n"
            + "        self.x = x\n"
            + "        self.y = y";

        [TestMethod]
        public void O2DictClass_ForwardMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kDictFn);
            StructuralCandidate m = GetKind(cs, Kind.O2_DictToClass);
            Assert.IsNotNull(m);
            Assert.AreEqual(kDataClass, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
        }

        [TestMethod]
        public void O2DictClass_ReverseMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kDataClass);
            StructuralCandidate m = GetKind(cs, Kind.O2_ClassToDict);
            Assert.IsNotNull(m);
            Assert.AreEqual(kDictFn, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
        }

        [TestMethod]
        public void O2DictClass_RoundTripDictToClassToDict()
        {
            string fwd = StructuralRefactor.Apply(kDictFn, Kind.O2_DictToClass);
            Assert.IsNotNull(fwd);
            Assert.AreEqual(kDataClass, fwd);
            string back = StructuralRefactor.Apply(fwd, Kind.O2_ClassToDict);
            Assert.IsNotNull(back);
            Assert.AreEqual(kDictFn, back);
        }

        [TestMethod]
        public void O2DictClass_RoundTripClassToDictToClass()
        {
            string rev = StructuralRefactor.Apply(kDataClass, Kind.O2_ClassToDict);
            Assert.IsNotNull(rev);
            Assert.AreEqual(kDictFn, rev);
            string back = StructuralRefactor.Apply(rev, Kind.O2_DictToClass);
            Assert.IsNotNull(back);
            Assert.AreEqual(kDataClass, back);
        }

        [TestMethod]
        public void O2DictClass_ReverseRejectsFactoryClass()
        {
            // A *Factory class is owned by P1, not O2.
            List<StructuralCandidate> cs = StructuralRefactor.Match(kFactory);
            Assert.IsNull(GetKind(cs, Kind.O2_ClassToDict));
        }

        [TestMethod]
        public void O2DictClass_ForwardRejectsMismatchedKey()
        {
            // {"a": x} where key != value name is not a clean data record.
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "def make_point(x, y):\n"
                + "    return {\"a\": x, \"y\": y}");
            Assert.IsNull(GetKind(cs, Kind.O2_DictToClass));
        }

        // =====================================================================
        // O1 free functions <-> class (verbatim body slicing)
        // =====================================================================

        private const string kFuncs =
            "def counter_increment(self):\n"
            + "    self.count += 1\n"
            + "\n"
            + "def counter_reset(self):\n"
            + "    self.count = 0";

        private const string kMethods =
            "class Counter:\n"
            + "    def increment(self):\n"
            + "        self.count += 1\n"
            + "\n"
            + "    def reset(self):\n"
            + "        self.count = 0";

        [TestMethod]
        public void O1FunctionsClass_ForwardMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kFuncs);
            StructuralCandidate m = GetKind(cs, Kind.O1_FunctionsToClass);
            Assert.IsNotNull(m);
            Assert.AreEqual(kMethods, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
        }

        [TestMethod]
        public void O1FunctionsClass_ReverseMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kMethods);
            StructuralCandidate m = GetKind(cs, Kind.O1_ClassToFunctions);
            Assert.IsNotNull(m);
            Assert.AreEqual(kFuncs, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
        }

        [TestMethod]
        public void O1FunctionsClass_RoundTripFuncsToClassToFuncs()
        {
            string fwd = StructuralRefactor.Apply(kFuncs, Kind.O1_FunctionsToClass);
            Assert.IsNotNull(fwd);
            Assert.AreEqual(kMethods, fwd);
            string back = StructuralRefactor.Apply(fwd, Kind.O1_ClassToFunctions);
            Assert.IsNotNull(back);
            Assert.AreEqual(kFuncs, back);
        }

        [TestMethod]
        public void O1FunctionsClass_RoundTripClassToFuncsToClass()
        {
            string rev = StructuralRefactor.Apply(kMethods, Kind.O1_ClassToFunctions);
            Assert.IsNotNull(rev);
            Assert.AreEqual(kFuncs, rev);
            string back = StructuralRefactor.Apply(rev, Kind.O1_FunctionsToClass);
            Assert.IsNotNull(back);
            Assert.AreEqual(kMethods, back);
        }

        [TestMethod]
        public void O1FunctionsClass_NestedMultiLineBodyRoundTrips()
        {
            // A multi-statement, nested body must survive verbatim through both legs.
            const string funcs =
                "def stack_push(self, x):\n"
                + "    if x is not None:\n"
                + "        self.items.append(x)\n"
                + "        self.size += 1\n"
                + "\n"
                + "def stack_pop(self):\n"
                + "    return self.items.pop()";
            string fwd = StructuralRefactor.Apply(funcs, Kind.O1_FunctionsToClass);
            Assert.IsNotNull(fwd);
            StringAssert.Contains(fwd, "class Stack:");
            StringAssert.Contains(fwd, "            self.size += 1");
            string back = StructuralRefactor.Apply(fwd, Kind.O1_ClassToFunctions);
            Assert.IsNotNull(back);
            Assert.AreEqual(funcs, back);
        }

        [TestMethod]
        public void O1FunctionsClass_ForwardRejectsSingleFunction()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "def counter_increment(self):\n"
                + "    self.count += 1");
            Assert.IsNull(GetKind(cs, Kind.O1_FunctionsToClass));
        }

        [TestMethod]
        public void O1FunctionsClass_ForwardRejectsNonSelfFirstParam()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "def counter_increment(c):\n"
                + "    c.count += 1\n"
                + "\n"
                + "def counter_reset(c):\n"
                + "    c.count = 0");
            Assert.IsNull(GetKind(cs, Kind.O1_FunctionsToClass));
        }

        [TestMethod]
        public void O1FunctionsClass_ForwardRejectsMixedPrefix()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "def counter_increment(self):\n"
                + "    self.count += 1\n"
                + "\n"
                + "def widget_draw(self):\n"
                + "    self.paint()");
            Assert.IsNull(GetKind(cs, Kind.O1_FunctionsToClass));
        }

        [TestMethod]
        public void O1FunctionsClass_ReverseRejectsFactoryClass()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kFactory);
            Assert.IsNull(GetKind(cs, Kind.O1_ClassToFunctions));
        }

        [TestMethod]
        public void O1FunctionsClass_ReverseRejectsDataClass()
        {
            // Single-__init__ data class is owned by O2, not O1.
            List<StructuralCandidate> cs = StructuralRefactor.Match(kDataClass);
            Assert.IsNull(GetKind(cs, Kind.O1_ClassToFunctions));
        }

        // =====================================================================
        // S2 DIP — introduce / inline abstraction (verbatim class body)
        // =====================================================================

        private const string kPlainClass =
            "class PaymentService:\n"
            + "    def charge(self, amount):\n"
            + "        self.total += amount\n"
            + "\n"
            + "    def refund(self, amount):\n"
            + "        self.total -= amount";

        private const string kAbstracted =
            "class PaymentServiceProtocol(Protocol):\n"
            + "    def charge(self, amount): ...\n"
            + "    def refund(self, amount): ...\n"
            + "\n"
            + "\n"
            + "class PaymentService(PaymentServiceProtocol):\n"
            + "    def charge(self, amount):\n"
            + "        self.total += amount\n"
            + "\n"
            + "    def refund(self, amount):\n"
            + "        self.total -= amount";

        [TestMethod]
        public void S2Dip_ForwardMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kPlainClass);
            StructuralCandidate m = GetKind(cs, Kind.S2_DipIntroduce);
            Assert.IsNotNull(m);
            Assert.AreEqual(kAbstracted, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
            // The Protocol import is surfaced, not inlined into the replacement.
            Assert.AreNotEqual(0, m.RequiredImports.Count);
            Assert.AreEqual("from typing import Protocol", m.RequiredImports[0]);
        }

        [TestMethod]
        public void S2Dip_ReverseMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kAbstracted);
            StructuralCandidate m = GetKind(cs, Kind.S2_DipInline);
            Assert.IsNotNull(m);
            Assert.AreEqual(kPlainClass, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
        }

        [TestMethod]
        public void S2Dip_RoundTripIntroduceInline()
        {
            string fwd = StructuralRefactor.Apply(kPlainClass, Kind.S2_DipIntroduce);
            Assert.IsNotNull(fwd);
            Assert.AreEqual(kAbstracted, fwd);
            string back = StructuralRefactor.Apply(fwd, Kind.S2_DipInline);
            Assert.IsNotNull(back);
            Assert.AreEqual(kPlainClass, back);
        }

        [TestMethod]
        public void S2Dip_RoundTripInlineIntroduce()
        {
            string rev = StructuralRefactor.Apply(kAbstracted, Kind.S2_DipInline);
            Assert.IsNotNull(rev);
            Assert.AreEqual(kPlainClass, rev);
            string back = StructuralRefactor.Apply(rev, Kind.S2_DipIntroduce);
            Assert.IsNotNull(back);
            Assert.AreEqual(kAbstracted, back);
        }

        [TestMethod]
        public void S2Dip_ForwardRejectsProtocolAndFactory()
        {
            Assert.IsNull(GetKind(StructuralRefactor.Match(kFactory), Kind.S2_DipIntroduce));
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "class FooProtocol:\n"
                + "    def run(self): ...");
            Assert.IsNull(GetKind(cs, Kind.S2_DipIntroduce));
        }

        [TestMethod]
        public void S2Dip_ReverseRejectsMismatchedNames()
        {
            // impl base must be exactly <implName>Protocol.
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "class FooProtocol(Protocol):\n"
                + "    def run(self): ...\n"
                + "\n"
                + "\n"
                + "class Bar(FooProtocol):\n"
                + "    def run(self):\n"
                + "        return 1");
            Assert.IsNull(GetKind(cs, Kind.S2_DipInline));
        }

        // =====================================================================
        // Detection bridge — analysis rule IDs -> forward structural families
        // =====================================================================

        private static void AssertSingleKind(Kind expected, List<Kind> actual)
        {
            Assert.AreEqual(1, actual.Count);
            Assert.AreEqual(expected, actual[0]);
        }

        [TestMethod]
        public void DetectionBridge_MapsPatternRulesToForwardKinds()
        {
            AssertSingleKind(Kind.P1_FactoryIntroduce, DetectionBridge.KindsForRule("PAT001"));
            AssertSingleKind(Kind.P2_StrategyIntroduce, DetectionBridge.KindsForRule("PAT002"));
            AssertSingleKind(Kind.P3_ObserverIntroduce, DetectionBridge.KindsForRule("PAT003"));
            AssertSingleKind(Kind.P4_DecoratorIntroduce, DetectionBridge.KindsForRule("PAT004"));
            AssertSingleKind(Kind.P5_TemplateIntroduce, DetectionBridge.KindsForRule("PAT005"));
            AssertSingleKind(Kind.P6_FacadeIntroduce, DetectionBridge.KindsForRule("PAT006"));
            AssertSingleKind(Kind.S1_SrpSplit, DetectionBridge.KindsForRule("SRP001"));
            AssertSingleKind(Kind.S2_DipIntroduce, DetectionBridge.KindsForRule("DIP001"));
            AssertSingleKind(Kind.S3_IspSegregate, DetectionBridge.KindsForRule("ISP001"));
            Assert.AreEqual(0, DetectionBridge.KindsForRule("ZZZ999").Count);
        }

        [TestMethod]
        public void DetectionBridge_GateKeepsOnlyHintedForwardCandidates()
        {
            // A god class yields several candidate families; SRP001 should gate to the
            // SRP split only.
            const string god =
                "class Account:\n"
                + "    def deposit(self, amount):\n"
                + "        self.balance += amount\n"
                + "\n"
                + "    def log_in(self, user):\n"
                + "        self.user = user";
            List<StructuralCandidate> cands = StructuralRefactor.Match(god);
            Assert.AreNotEqual(0, cands.Count);

            List<StructuralCandidate> gated = DetectionBridge.Gate(new string[] { "SRP001" }, cands);
            Assert.AreEqual(1, gated.Count);
            Assert.AreEqual(Kind.S1_SrpSplit, gated[0].PatternKind);

            // No hints -> nothing offered.
            Assert.AreEqual(0, DetectionBridge.Gate(new string[0], cands).Count);
        }

        [TestMethod]
        public void DetectionBridge_ReverseKindsAreNeverHinted()
        {
            Assert.IsFalse(DetectionBridge.IsHinted(Kind.P6_FacadeRemove,
                new string[] { "PAT006", "SRP001", "DIP001" }));
            Assert.IsFalse(DetectionBridge.IsHinted(Kind.S1_SrpMerge, new string[] { "SRP001" }));
            Assert.IsTrue(DetectionBridge.IsHinted(Kind.S1_SrpSplit, new string[] { "SRP001" }));
        }

        // =====================================================================
        // S3 ISP — segregate / collapse (role protocols, lossy + caveat)
        // =====================================================================

        private const string kFatProtocol =
            "class Repository(Protocol):\n"
            + "    def fetch(self, key): ...\n"
            + "    def save(self, key, value): ...";

        [TestMethod]
        public void S3Isp_ForwardSegregatesPerMethod()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kFatProtocol);
            StructuralCandidate m = GetKind(cs, Kind.S3_IspSegregate);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Lossy);
            Assert.IsTrue(HasCaveat(m, "STR-ISP-LOSSY"));
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
            StringAssert.Contains(m.ReplacementText, "class RepositoryFetch(Protocol):");
            StringAssert.Contains(m.ReplacementText, "class RepositorySave(Protocol):");
        }

        [TestMethod]
        public void S3Isp_RoundTripSegregateCollapse()
        {
            string fwd = StructuralRefactor.Apply(kFatProtocol, Kind.S3_IspSegregate);
            Assert.IsNotNull(fwd);
            Assert.IsTrue(ParsesCleanly(fwd));
            string back = StructuralRefactor.Apply(fwd, Kind.S3_IspCollapse);
            Assert.IsNotNull(back);
            Assert.IsTrue(ParsesCleanly(back));
            Assert.AreEqual(kFatProtocol, back);  // byte-exact for canonical shape
        }

        [TestMethod]
        public void S3Isp_ForwardRejectsSingleMethodProtocol()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "class Reader(Protocol):\n"
                + "    def read(self): ...");
            Assert.IsNull(GetKind(cs, Kind.S3_IspSegregate));
        }

        // =====================================================================
        // S1 SRP — split / merge (cohesion clustering, lossy + caveat)
        // =====================================================================

        private const string kGodClass =
            "class Account:\n"
            + "    def deposit(self, amount):\n"
            + "        self.balance += amount\n"
            + "\n"
            + "    def withdraw(self, amount):\n"
            + "        self.balance -= amount\n"
            + "\n"
            + "    def log_in(self, user):\n"
            + "        self.user = user\n"
            + "\n"
            + "    def log_out(self):\n"
            + "        self.user = None";

        [TestMethod]
        public void S1Srp_ForwardSplitsByCohesion()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kGodClass);
            StructuralCandidate m = GetKind(cs, Kind.S1_SrpSplit);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Lossy);
            Assert.IsTrue(HasCaveat(m, "STR-SRP-LOSSY"));
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
            StringAssert.Contains(m.ReplacementText, "class AccountBalance:");
            StringAssert.Contains(m.ReplacementText, "class AccountUser:");
        }

        [TestMethod]
        public void S1Srp_RoundTripSplitMerge()
        {
            string fwd = StructuralRefactor.Apply(kGodClass, Kind.S1_SrpSplit);
            Assert.IsNotNull(fwd);
            Assert.IsTrue(ParsesCleanly(fwd));
            string back = StructuralRefactor.Apply(fwd, Kind.S1_SrpMerge);
            Assert.IsNotNull(back);
            Assert.IsTrue(ParsesCleanly(back));
            // Cohesion clusters partition cleanly here, so the merge recovers the god
            // class byte-exact (methods carried verbatim, original order preserved).
            Assert.AreEqual(kGodClass, back);
        }

        [TestMethod]
        public void S1Srp_ForwardRejectsSingleResponsibility()
        {
            // All methods touch the same field -> one cluster -> not splittable.
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "class Counter:\n"
                + "    def inc(self):\n"
                + "        self.count += 1\n"
                + "\n"
                + "    def reset(self):\n"
                + "        self.count = 0");
            Assert.IsNull(GetKind(cs, Kind.S1_SrpSplit));
        }

        [TestMethod]
        public void S1Srp_MergeRejectsUnrelatedPrefixes()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "class Alpha:\n"
                + "    def a(self):\n"
                + "        return self.x\n"
                + "\n"
                + "\n"
                + "class Beta:\n"
                + "    def b(self):\n"
                + "        return self.y");
            Assert.IsNull(GetKind(cs, Kind.S1_SrpMerge));
        }

        // =====================================================================
        // P6 Facade — introduce / remove (near-identity, verbatim body)
        // =====================================================================

        private const string kFacadeFn =
            "def checkout(cart):\n"
            + "    validate(cart)\n"
            + "    charge(cart)\n"
            + "    return ship(cart)";

        private const string kFacadeClass =
            "class CheckoutFacade:\n"
            + "    def checkout(self, cart):\n"
            + "        validate(cart)\n"
            + "        charge(cart)\n"
            + "        return ship(cart)";

        [TestMethod]
        public void P6Facade_ForwardMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kFacadeFn);
            StructuralCandidate m = GetKind(cs, Kind.P6_FacadeIntroduce);
            Assert.IsNotNull(m);
            Assert.AreEqual(kFacadeClass, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
        }

        [TestMethod]
        public void P6Facade_ReverseMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kFacadeClass);
            StructuralCandidate m = GetKind(cs, Kind.P6_FacadeRemove);
            Assert.IsNotNull(m);
            Assert.AreEqual(kFacadeFn, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
        }

        [TestMethod]
        public void P6Facade_RoundTripIntroduceRemove()
        {
            string fwd = StructuralRefactor.Apply(kFacadeFn, Kind.P6_FacadeIntroduce);
            Assert.IsNotNull(fwd);
            Assert.AreEqual(kFacadeClass, fwd);
            string back = StructuralRefactor.Apply(fwd, Kind.P6_FacadeRemove);
            Assert.IsNotNull(back);
            Assert.AreEqual(kFacadeFn, back);
        }

        [TestMethod]
        public void P6Facade_RoundTripRemoveIntroduce()
        {
            string rev = StructuralRefactor.Apply(kFacadeClass, Kind.P6_FacadeRemove);
            Assert.IsNotNull(rev);
            Assert.AreEqual(kFacadeFn, rev);
            string back = StructuralRefactor.Apply(rev, Kind.P6_FacadeIntroduce);
            Assert.IsNotNull(back);
            Assert.AreEqual(kFacadeClass, back);
        }

        [TestMethod]
        public void P6Facade_ForwardRejectsSingleCallBody()
        {
            // A facade must delegate to >= 2 subsystem calls.
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "def checkout(cart):\n"
                + "    return ship(cart)");
            Assert.IsNull(GetKind(cs, Kind.P6_FacadeIntroduce));
        }

        [TestMethod]
        public void P6Facade_ForwardRejectsMethodWithSelf()
        {
            // Already a method (self first param) is not a free orchestration fn.
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "def checkout(self, cart):\n"
                + "    validate(cart)\n"
                + "    charge(cart)");
            Assert.IsNull(GetKind(cs, Kind.P6_FacadeIntroduce));
        }

        [TestMethod]
        public void P6Facade_NoArgRoundTrips()
        {
            const string fn =
                "def boot():\n"
                + "    init()\n"
                + "    start()";
            string fwd = StructuralRefactor.Apply(fn, Kind.P6_FacadeIntroduce);
            Assert.IsNotNull(fwd);
            StringAssert.Contains(fwd, "class BootFacade:");
            StringAssert.Contains(fwd, "def boot(self):");
            string back = StructuralRefactor.Apply(fwd, Kind.P6_FacadeRemove);
            Assert.IsNotNull(back);
            Assert.AreEqual(fn, back);
        }

        // =====================================================================
        // P2 Strategy — introduce / remove (function-table strategy, byte-exact)
        // =====================================================================

        private const string kStrategyPlain =
            "def execute(mode, x):\n"
            + "    if mode == \"fast\":\n"
            + "        return run_fast(x)\n"
            + "    elif mode == \"slow\":\n"
            + "        return run_slow(x)\n"
            + "    raise ValueError(mode)";

        private const string kStrategyTable =
            "_execute_strategies = {\n"
            + "    \"fast\": run_fast,\n"
            + "    \"slow\": run_slow,\n"
            + "}\n"
            + "\n"
            + "\n"
            + "def execute(mode, x):\n"
            + "    if mode not in _execute_strategies:\n"
            + "        raise ValueError(mode)\n"
            + "    return _execute_strategies[mode](x)";

        [TestMethod]
        public void P2Strategy_ForwardMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kStrategyPlain);
            StructuralCandidate m = GetKind(cs, Kind.P2_StrategyIntroduce);
            Assert.IsNotNull(m);
            Assert.AreEqual(kStrategyTable, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
        }

        [TestMethod]
        public void P2Strategy_ReverseMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kStrategyTable);
            StructuralCandidate m = GetKind(cs, Kind.P2_StrategyRemove);
            Assert.IsNotNull(m);
            Assert.AreEqual(kStrategyPlain, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
        }

        [TestMethod]
        public void P2Strategy_RoundTripIntroduceRemove()
        {
            string fwd = StructuralRefactor.Apply(kStrategyPlain, Kind.P2_StrategyIntroduce);
            Assert.IsNotNull(fwd);
            Assert.AreEqual(kStrategyTable, fwd);
            string back = StructuralRefactor.Apply(fwd, Kind.P2_StrategyRemove);
            Assert.IsNotNull(back);
            Assert.AreEqual(kStrategyPlain, back);
        }

        [TestMethod]
        public void P2Strategy_RoundTripRemoveIntroduce()
        {
            string rev = StructuralRefactor.Apply(kStrategyTable, Kind.P2_StrategyRemove);
            Assert.IsNotNull(rev);
            Assert.AreEqual(kStrategyPlain, rev);
            string back = StructuralRefactor.Apply(rev, Kind.P2_StrategyIntroduce);
            Assert.IsNotNull(back);
            Assert.AreEqual(kStrategyTable, back);
        }

        [TestMethod]
        public void P2Strategy_ForwardRejectsZeroArgFactory()
        {
            // A zero-arg constructor dispatch is P1 Factory, not P2 Strategy.
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "def create_animal(kind):\n"
                + "    if kind == \"dog\":\n"
                + "        return Dog()\n"
                + "    raise ValueError(kind)");
            Assert.IsNull(GetKind(cs, Kind.P2_StrategyIntroduce));
        }

        [TestMethod]
        public void P2Strategy_ForwardRejectsMismatchedArgs()
        {
            // Branch must pass exactly the params after the selector.
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "def execute(mode, x):\n"
                + "    if mode == \"fast\":\n"
                + "        return run_fast(y)\n"
                + "    raise ValueError(mode)");
            Assert.IsNull(GetKind(cs, Kind.P2_StrategyIntroduce));
        }

        // =====================================================================
        // P3 Observer — introduce / remove (inject infra, lossy + caveat)
        // =====================================================================

        private const string kSubjectPlain =
            "class Stock:\n"
            + "    def set_price(self, price):\n"
            + "        self.price = price\n"
            + "\n"
            + "    def get_price(self):\n"
            + "        return self.price";

        [TestMethod]
        public void P3Observer_ForwardInjectsInfraWithCaveat()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kSubjectPlain);
            StructuralCandidate m = GetKind(cs, Kind.P3_ObserverIntroduce);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Lossy);
            Assert.IsTrue(HasCaveat(m, "STR-OBSERVER-LOSSY"));
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
            StringAssert.Contains(m.ReplacementText, "def subscribe(self, observer):");
            StringAssert.Contains(m.ReplacementText, "def set_price(self, price):");
        }

        [TestMethod]
        public void P3Observer_RoundTripRecoversSubject()
        {
            string fwd = StructuralRefactor.Apply(kSubjectPlain, Kind.P3_ObserverIntroduce);
            Assert.IsNotNull(fwd);
            Assert.IsTrue(ParsesCleanly(fwd));
            string back = StructuralRefactor.Apply(fwd, Kind.P3_ObserverRemove);
            Assert.IsNotNull(back);
            Assert.IsTrue(ParsesCleanly(back));
            // Byte-exact for the canonical shape (infra block dropped verbatim).
            Assert.AreEqual(kSubjectPlain, back);
        }

        [TestMethod]
        public void P3Observer_ForwardRejectsExistingObserver()
        {
            // A class already carrying observer infra must not be re-wrapped.
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "class Stock:\n"
                + "    def subscribe(self, observer):\n"
                + "        pass");
            Assert.IsNull(GetKind(cs, Kind.P3_ObserverIntroduce));
        }

        // =====================================================================
        // P4 Decorator — introduce / remove (delegating wrapper, lossy + caveat)
        // =====================================================================

        private const string kComponent =
            "class Service:\n"
            + "    def run(self, x):\n"
            + "        return x + 1\n"
            + "\n"
            + "    def name(self):\n"
            + "        return \"svc\"";

        [TestMethod]
        public void P4Decorator_ForwardAddsDelegatingWrapper()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kComponent);
            StructuralCandidate m = GetKind(cs, Kind.P4_DecoratorIntroduce);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Lossy);
            Assert.IsTrue(HasCaveat(m, "STR-DECORATOR-LOSSY"));
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
            StringAssert.Contains(m.ReplacementText, "class ServiceDecorator:");
            StringAssert.Contains(m.ReplacementText, "return self._wrapped.run(x)");
            StringAssert.Contains(m.ReplacementText, "return self._wrapped.name()");
        }

        [TestMethod]
        public void P4Decorator_RoundTripRecoversComponent()
        {
            string fwd = StructuralRefactor.Apply(kComponent, Kind.P4_DecoratorIntroduce);
            Assert.IsNotNull(fwd);
            Assert.IsTrue(ParsesCleanly(fwd));
            string back = StructuralRefactor.Apply(fwd, Kind.P4_DecoratorRemove);
            Assert.IsNotNull(back);
            Assert.IsTrue(ParsesCleanly(back));
            Assert.AreEqual(kComponent, back);  // component carried verbatim
        }

        [TestMethod]
        public void P4Decorator_ReverseRejectsUnrelatedPair()
        {
            // Second class must be exactly <Name>Decorator.
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "class Service:\n"
                + "    def run(self):\n"
                + "        return 1\n"
                + "\n"
                + "\n"
                + "class Other:\n"
                + "    def run(self):\n"
                + "        return 2");
            Assert.IsNull(GetKind(cs, Kind.P4_DecoratorRemove));
        }

        // =====================================================================
        // P5 Template Method — introduce / remove (verbatim skeleton, byte-exact)
        // =====================================================================

        private const string kTemplatePlain =
            "class ReportBuilder:\n"
            + "    def build(self):\n"
            + "        self.fetch()\n"
            + "        self.format()\n"
            + "        self.save()";

        private const string kTemplateAbc =
            "class ReportBuilder(ABC):\n"
            + "    def build(self):\n"
            + "        self.fetch()\n"
            + "        self.format()\n"
            + "        self.save()\n"
            + "\n"
            + "    @abstractmethod\n"
            + "    def fetch(self): ...\n"
            + "\n"
            + "    @abstractmethod\n"
            + "    def format(self): ...\n"
            + "\n"
            + "    @abstractmethod\n"
            + "    def save(self): ...";

        [TestMethod]
        public void P5Template_ForwardMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kTemplatePlain);
            StructuralCandidate m = GetKind(cs, Kind.P5_TemplateIntroduce);
            Assert.IsNotNull(m);
            Assert.AreEqual(kTemplateAbc, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
            Assert.AreNotEqual(0, m.RequiredImports.Count);
            Assert.AreEqual("from abc import ABC, abstractmethod", m.RequiredImports[0]);
        }

        [TestMethod]
        public void P5Template_ReverseMatches()
        {
            List<StructuralCandidate> cs = StructuralRefactor.Match(kTemplateAbc);
            StructuralCandidate m = GetKind(cs, Kind.P5_TemplateRemove);
            Assert.IsNotNull(m);
            Assert.AreEqual(kTemplatePlain, m.ReplacementText);
            Assert.IsTrue(ParsesCleanly(m.ReplacementText));
        }

        [TestMethod]
        public void P5Template_RoundTripIntroduceRemove()
        {
            string fwd = StructuralRefactor.Apply(kTemplatePlain, Kind.P5_TemplateIntroduce);
            Assert.IsNotNull(fwd);
            Assert.AreEqual(kTemplateAbc, fwd);
            string back = StructuralRefactor.Apply(fwd, Kind.P5_TemplateRemove);
            Assert.IsNotNull(back);
            Assert.AreEqual(kTemplatePlain, back);
        }

        [TestMethod]
        public void P5Template_RoundTripRemoveIntroduce()
        {
            string rev = StructuralRefactor.Apply(kTemplateAbc, Kind.P5_TemplateRemove);
            Assert.IsNotNull(rev);
            Assert.AreEqual(kTemplatePlain, rev);
            string back = StructuralRefactor.Apply(rev, Kind.P5_TemplateIntroduce);
            Assert.IsNotNull(back);
            Assert.AreEqual(kTemplateAbc, back);
        }

        [TestMethod]
        public void P5Template_ForwardRejectsNonCallBody()
        {
            // Body must be pure self.<step>() calls.
            List<StructuralCandidate> cs = StructuralRefactor.Match(
                "class C:\n"
                + "    def run(self):\n"
                + "        self.x = 1\n"
                + "        self.fetch()");
            Assert.IsNull(GetKind(cs, Kind.P5_TemplateIntroduce));
        }
    }
}
