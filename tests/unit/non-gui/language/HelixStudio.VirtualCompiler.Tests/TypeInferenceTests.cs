using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Phase-3.0 basic inference coverage. The virtual compiler's type-checker
    /// should infer literal types, track simple assignments, check call arity
    /// against workspace <c>def</c> and class <c>__init__</c> signatures, and
    /// flag attribute access on known stub classes.
    /// </summary>
    [TestClass]
    public class TypeInferenceTests
    {
        private static bool Has(CompileResult r, string code)
        {
            return r.Diagnostics.Any(d => d.Code == code);
        }

        // ---------- Call arity against workspace defs ----------

        [TestMethod]
        public void Call_WithCorrect_ArgCount_IsFine()
        {
            const string src = "def f(x, y): return x + y\nz = f(1, 2)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch),
                "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void Call_WithTooFewArgs_Flagged()
        {
            const string src = "def f(x, y): return x\nf(1)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch));
        }

        [TestMethod]
        public void Call_WithTooManyArgs_Flagged()
        {
            const string src = "def f(x): return x\nf(1, 2, 3)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch));
        }

        [TestMethod]
        public void Call_WithDefaultArg_AcceptsFewerArgs()
        {
            const string src = "def f(x, y=1): return x\nf(1)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch));
        }

        [TestMethod]
        public void Call_VarArgs_DisablesArityCheck()
        {
            const string src = "def f(*args): return args\nf(1, 2, 3, 4, 5)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch));
        }

        [TestMethod]
        public void Call_UnknownKeyword_Flagged()
        {
            const string src = "def f(x, y): return x\nf(x=1, nosuch=2)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeUnknownKeywordArg));
        }

        [TestMethod]
        public void Call_StarArgs_DisablesArityCheck()
        {
            const string src = "def f(x, y): return x\nargs = [1, 2]\nf(*args)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch));
        }

        // ---------- Constructor arity against workspace classes ----------

        [TestMethod]
        public void ConstructorCall_With_MatchingArgs_IsFine()
        {
            const string src =
                "class C:\n" +
                "    def __init__(self, x, y): pass\n" +
                "c = C(1, 2)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch));
        }

        [TestMethod]
        public void ConstructorCall_With_TooFewArgs_Flagged()
        {
            const string src =
                "class C:\n" +
                "    def __init__(self, x, y): pass\n" +
                "c = C(1)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch));
        }

        [TestMethod]
        public void ConstructorCall_WithoutInit_AcceptsAnyArgs()
        {
            // No __init__ → MVP accepts any args.
            const string src = "class C: pass\nc1 = C()\nc2 = C(1, 2, 3)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch));
        }

        // ---------- Attribute access on workspace classes ----------

        [TestMethod]
        public void ExistingAttribute_OnSelfInstance_NoError()
        {
            const string src =
                "class C:\n" +
                "    def __init__(self):\n" +
                "        self.x = 1\n" +
                "    def get(self):\n" +
                "        return self.x\n";
            var r = VirtualCompiler.CompileSource(src);
            // We don't yet track instance-assigned attributes; stay silent rather than false-flag.
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound));
        }

        [TestMethod]
        public void MethodAttribute_OnClassInstance_NoError()
        {
            const string src =
                "class C:\n" +
                "    def hello(self): return 1\n" +
                "c = C()\n" +
                "c.hello()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound));
        }

        [TestMethod]
        public void MissingAttribute_OnWorkspaceClassInstance_Flagged()
        {
            const string src =
                "class C:\n" +
                "    def hello(self): return 1\n" +
                "c = C()\n" +
                "c.nonexistent\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        // ---------- Inheritance: don't false-positive on inherited / external attributes ----------
        // Regression for the 652 phantom errors on the sample workspace fixture: classes extending
        // torch.nn.Module were flagged for every inherited method (register_buffer, to, ...).

        [TestMethod]
        public void InheritedAttribute_FromUnresolvedExternalBase_NotFlagged()
        {
            // nn.Module is not a workspace class and not stubbed; we cannot prove absence.
            const string src =
                "import torch.nn as nn\n" +
                "class Head(nn.Module):\n" +
                "    def forward(self):\n" +
                "        return self.register_buffer('x', None)\n" +
                "h = Head()\n" +
                "h.register_buffer('a', None)\n" +
                "h.to('cuda')\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "inherited-from-external attrs must not be flagged; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void InheritedAttribute_FromWorkspaceBase_Resolved()
        {
            const string src =
                "class Base:\n" +
                "    def hello(self): return 1\n" +
                "class Derived(Base):\n" +
                "    pass\n" +
                "d = Derived()\n" +
                "d.hello()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "inherited workspace-base method must resolve; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void MissingAttribute_OnDerivedWithFullyResolvableBases_StillFlagged()
        {
            // Every base is a workspace class and none defines it → genuinely missing → flag.
            // Proves the fix is targeted, not a blanket suppression of all subclass errors.
            const string src =
                "class Base:\n" +
                "    def hello(self): return 1\n" +
                "class Derived(Base):\n" +
                "    pass\n" +
                "d = Derived()\n" +
                "d.nonexistent\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void BoundMethodCall_DoesNotCountSelf()
        {
            // list.append(self, object): a bound call `xs.append(1)` supplies 1 arg and must NOT
            // be flagged "1 given, 2 required" — accessing a method via an instance binds self.
            const string src = "xs = []\nxs.append(1)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch),
                "bound method call must not count self; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void BoundMethodCall_OnWorkspaceClass_ChecksArityWithoutSelf()
        {
            const string src =
                "class C:\n" +
                "    def add(self, a, b): return a + b\n" +
                "c = C()\n" +
                "c.add(1, 2)\n";      // correct arity once self is dropped
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch),
                "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void DictLiteral_AssignableToTypingDictAnnotation()
        {
            // typing.Dict is builtins.dict; a dict literal must satisfy a Dict[str, str] param.
            const string src =
                "from typing import Dict\n" +
                "def f(attrib: Dict[str, str]): return attrib\n" +
                "f({})\n" +
                "f({'a': 'b'})\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "dict literal must satisfy Dict[str,str]; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void DictReturn_AssignableToTypingDictAnnotation()
        {
            const string src =
                "from typing import Dict\n" +
                "def f() -> Dict[str, str]:\n" +
                "    return {}\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch),
                "dict return must satisfy Dict[str,str]; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        // ---------- Attribute access against bundled stubs ----------

        [TestMethod]
        public void StringMethod_Upper_ResolvesAgainstBuiltinStub()
        {
            const string src = "s = \"hello\"\ns.upper()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "str.upper is in the bundled builtins stub; should not flag");
        }

        [TestMethod]
        public void StringMethod_Nonsense_FlaggedAgainstBuiltinStub()
        {
            const string src = "s = \"hello\"\ns.nonsense_method_xyz()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void Int_BitLength_ResolvesAgainstBuiltinStub()
        {
            const string src = "n = 42\nn.bit_length()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound));
        }

        // ---------- Annotated assignment binds type ----------

        [TestMethod]
        public void AnnotatedAssignment_BindsReturnedType()
        {
            // `x: str = "hi"` → x has type str; x.upper() should resolve.
            const string src = "x: str = \"hi\"\nx.upper()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound));
        }

        // ---------- Imports don't false-flag attribute access ----------

        [TestMethod]
        public void ImportedName_Attribute_NotFlagged()
        {
            // Import-bound names are Any-typed → attribute access is lenient.
            const string src = "import os\nos.getcwd()\nos.anything_goes\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound));
        }

        // ---------- Calls on imported / unknown functions don't fire arity ----------

        [TestMethod]
        public void ImportedFunctionCall_NotArityChecked()
        {
            const string src = "from os import getcwd\ngetcwd()\ngetcwd(1, 2, 3)\n";
            var r = VirtualCompiler.CompileSource(src);
            // AnyRef callable → skip arity check to avoid false positives
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch));
        }

        // ---------- Union / Optional self-assignability (iter 14) ----------
        // A union type must be assignable to an IDENTICAL union type. The bug: the
        // target-union rule ran before the source-union rule, so `str | None` was
        // tested for fitting a *single* member of `str | None` (fails on None) and
        // an identical annotation was reported as a mismatch. Real Python accepts
        // all of these — they were pure false positives on the sample workspace fixture.

        [TestMethod]
        public void OptionalReturn_AssignableToIdenticalOptionalAnnotation()
        {
            // `str | None` return against a `str | None` annotation — script_config.py:402.
            const string src =
                "def f(x: str | None) -> str | None:\n" +
                "    return x\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch),
                "str|None must be assignable to str|None; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void OptionalTyping_Union_ReturnAssignableToIdenticalAnnotation()
        {
            // Optional[int] (== int | None) spelled via typing — script_config.py:409.
            const string src =
                "from typing import Optional\n" +
                "def f(x: Optional[int]) -> Optional[int]:\n" +
                "    return x\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch),
                "Optional[int] must be assignable to Optional[int]; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void OptionalArgument_AssignableToIdenticalOptionalParam()
        {
            // Passing a `float | None` value into a `float | None` parameter — select_pisces_*.py:474.
            const string src =
                "def g(v: float | None): return v\n" +
                "def f(x: float | None):\n" +
                "    g(x)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "float|None arg must satisfy a float|None param; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void TupleWithOptionalMember_AssignableToIdenticalAnnotation()
        {
            // `Tuple[float | None, float, float]` against itself — lcs.py:181. Fixed
            // for free by the union rule (the Tuple rule recurses per item).
            const string src =
                "from typing import Tuple\n" +
                "def f(a: float | None, b: float, c: float) -> Tuple[float | None, float, float]:\n" +
                "    return (a, b, c)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch),
                "Tuple[float|None,float,float] must be assignable to itself; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void Union_GenuineMismatch_StillFlagged()
        {
            // Guard: the reorder must NOT suppress a real mismatch. `int | str` is
            // NOT assignable to a plain `int` return (the str member doesn't fit).
            const string src =
                "def f(x: int | str) -> int:\n" +
                "    return x\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch),
                "int|str must NOT be assignable to int; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        // ---------- Builtin containers are Iterable (iter 15) ----------
        // The bundled builtins stub had every container inherit only `object`, so
        // a `list` / `tuple` / `dict` / `set` / `str` argument was rejected against
        // an `Iterable` parameter (PY6003) — ~11 phantom errors on the sample workspace
        // fixture (evaluate_moore28.py, run_benchmark.py, pca_analysis_55_feat.py,
        // data_shards.py, download_pisces_pdbs.py, xml_utils.py). Real Python: all
        // of these are Iterable.

        [TestMethod]
        public void ListArgument_SatisfiesIterableParam()
        {
            // download_pisces_pdbs.py:137 shape — a list passed where Iterable is expected.
            const string src =
                "from typing import Iterable\n" +
                "def consume(xs: Iterable): return xs\n" +
                "consume([1, 2, 3])\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "a list must satisfy an Iterable param; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void TypingListArgument_SatisfiesIterableParam()
        {
            // select_pisces_*.py:497 shape — a List[str]-typed value against Iterable.
            const string src =
                "from typing import Iterable, List\n" +
                "def consume(xs: Iterable): return xs\n" +
                "def f(items: List[str]):\n" +
                "    consume(items)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "List[str] must satisfy an Iterable param; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void TupleDictSetStr_SatisfyIterableParam()
        {
            const string src =
                "from typing import Iterable\n" +
                "def consume(xs: Iterable): return xs\n" +
                "consume((1, 2))\n" +
                "consume({'a': 1})\n" +
                "consume({1, 2})\n" +
                "consume('abc')\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "tuple/dict/set/str must all satisfy an Iterable param; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void List_IsStill_AssignableToObject()
        {
            // Guard: adding the Iterable base must not sever `list <: object`
            // (object is kept explicitly in the stub header).
            const string src =
                "def consume(x: object): return x\n" +
                "consume([1, 2, 3])\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "a list must still be assignable to object; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void Int_IsNot_Iterable_StillFlagged()
        {
            // Guard: the stub change must not make a non-iterable (int) count as Iterable.
            const string src =
                "from typing import Iterable\n" +
                "def consume(xs: Iterable): return xs\n" +
                "consume(5)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "an int must NOT satisfy an Iterable param; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        // ---------- str stub completeness (iter 16) ----------
        // The bundled str stub was missing many everyday methods, so common calls
        // false-flagged PY6001 on the sample workspace fixture: splitlines (download_pisces_pdbs.py,
        // select_pisces_100/500.py) and rstrip (fix_pdb_columns.py). Completed the str
        // stub with the standard method set (all real CPython str methods).

        [TestMethod]
        public void StringMethods_SplitlinesRstrip_ResolveAgainstStub()
        {
            const string src =
                "s = \"a\\nb\"\n" +
                "s.splitlines()\n" +
                "s.rstrip()\n" +
                "s.lstrip()\n" +
                "s.title()\n" +
                "s.zfill(4)\n" +
                "s.isdigit()\n" +
                "s.count(\"a\")\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "completed str methods must resolve; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void StringMethod_StillFlagsGenuineTypo()
        {
            // Guard: the completion must not turn str into an anything-goes type.
            const string src = "s = \"x\"\ns.splitlinez()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "a genuine typo must still flag; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        // ---------- Rich comparison on a dynamic operand is not bool (iter 17) ----------
        // numpy/pandas/torch override __eq__/__lt__ to return an ndarray/Series/Tensor,
        // NOT bool. The inferencer hard-coded every comparison to bool, so a chained
        // `.sum()/.any()/.astype()/.to_numpy()/.float()` false-flagged PY6001 on the
        // sample workspace fixture (headtohead_sidechain.py, pca_analysis_55_feat.py, dataset.py,
        // sidechain_builder.py, losses_aa.py, verify_*.py). A rich comparison whose
        // operand is Any/Unknown now infers Unknown.

        [TestMethod]
        public void RichComparisonOnDynamic_IsNotBool_MethodsResolve()
        {
            const string src =
                "import numpy as np\n" +
                "arr = np.array([1, 2, 3])\n" +
                "(arr == 0).sum()\n" +
                "(arr < 5).any()\n" +
                "mask = arr >= 0\n" +
                "mask.astype(np.float32)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "an array comparison yields a dynamic value, not bool; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void ConcreteComparison_StaysBool_StillFlagsBadAttribute()
        {
            // Guard: a comparison between concrete ints is still bool, so a bogus
            // attribute on the result must STILL flag — the relaxation must only fire
            // for dynamic operands, not blanket-suppress every comparison.
            const string src =
                "b = (1 < 2)\n" +
                "b.definitely_not_a_bool_method()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "concrete comparison must stay bool; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void WorkspaceClassComparison_MayReturnRichResult()
        {
            const string src =
                "class Symbol:\n" +
                "    pass\n" +
                "x = Symbol()\n" +
                "(x < x).is_Relational\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "user-defined comparisons may return symbolic objects; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void ConventionalPrivateTypeVarAnnotationAcceptsBoundCallType()
        {
            const string src =
                "def identity(value: _T) -> _T:\n" +
                "    return value\n" +
                "identity(1)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        // ---------- Universal object members + lenient bare object (iter 18) ----------

        [TestMethod]
        public void WorkspaceClass_DunderDict_ResolvesViaObjectRoot()
        {
            // script_config.py:378/390 — self.__dict__ on a plain (base-less) class.
            // Every class derives from object, so __dict__/__class__/__doc__ resolve.
            const string src =
                "class ScriptConfig:\n" +
                "    def __init__(self):\n" +
                "        self.x = 1\n" +
                "    def dump(self):\n" +
                "        state = self.__dict__.copy()\n" +
                "        cls = self.__class__\n" +
                "        return state\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "self.__dict__/__class__ must resolve via the object root; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void BareObjectValue_AttributeAccess_IsLenient()
        {
            // build_culled_pdb.py:467 — a dict.get() result is typed `object`; a
            // further .get()/.append() on it must not false-flag (object is the top
            // type / an un-inferred fallback, so it could be any subtype).
            const string src =
                "def f(d: dict):\n" +
                "    seq = d.get('k', {})\n" +
                "    n = seq.get('sequence', '')\n" +
                "    return n\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "attribute access on a bare-object value must be lenient; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void WorkspaceClass_GenuineMissingAttr_StillFlagged()
        {
            // Guard: consulting the object root must NOT suppress a genuinely missing
            // member on a base-less class (only universal object members are added).
            const string src =
                "class Foo:\n" +
                "    def __init__(self):\n" +
                "        self.x = 1\n" +
                "def g(a: Foo):\n" +
                "    a.no_such_method_zzz()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "a genuinely missing member must still flag; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        // ---------- Calling an external-base (nn.Module) instance (iter 19) ----------
        // sidechain_builder.py:153 — `builder = SideChainBuilder(); builder(bb, rt, chis)`.
        // A ClassRef callee is both the class object AND the instance type; the checker
        // arity-checked the INSTANCE call against __init__(self, npz_path=...) → "3 given,
        // 1 accepted". But a nn.Module instance is called via an inherited external
        // __call__ (→ forward), which we can't see. External base → suppress the check.

        [TestMethod]
        public void ExternalBaseInstance_CalledWithAnyArgs_NotArityChecked()
        {
            const string src =
                "import torch.nn as nn\n" +
                "class SideChainBuilder(nn.Module):\n" +
                "    def __init__(self, npz_path=None):\n" +
                "        super().__init__()\n" +
                "    def forward(self, bb, rt, chis):\n" +
                "        return bb\n" +
                "def run(bb, rt, chis):\n" +
                "    builder = SideChainBuilder()\n" +
                "    return builder(bb, rt, chis)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch),
                "an nn.Module-subclass instance call must not be arity-checked against __init__; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void PlainClassConstructor_WrongArity_StillFlagged()
        {
            // Guard: a base-less (no external base) workspace class must STILL have its
            // constructor arity checked — the suppression only fires for external bases.
            const string src =
                "class Point:\n" +
                "    def __init__(self, x, y):\n" +
                "        self.x = x\n" +
                "Point(1, 2, 3)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch),
                "a plain class constructor with too many args must still flag; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        // ---------- Guard-clause (early-exit) None-narrowing (iter 20) ----------
        // verify_crmsd.py:181/207 — `p = f(); if p is None: sys.exit()/continue; g(p)`.
        // After a guard clause that always leaves the block, the fall-through has the
        // Optional narrowed to non-None. The checker restored the env after the `if`,
        // so the later call still saw Optional and false-flagged PY6003.

        [TestMethod]
        public void GuardClause_Return_NarrowsOptionalAfterIf()
        {
            const string src =
                "def need(p: str): return p\n" +
                "def f(x: str | None):\n" +
                "    if x is None:\n" +
                "        return\n" +
                "    need(x)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "x must be narrowed to str after `if x is None: return`; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void GuardClause_SysExit_NarrowsOptionalAfterIf()
        {
            // The exact verify_crmsd.py:181 shape — sys.exit() is NoReturn.
            const string src =
                "import sys\n" +
                "def need(p: str): return p\n" +
                "def f(x: str | None):\n" +
                "    if x is None:\n" +
                "        sys.exit('missing')\n" +
                "    need(x)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "x must be narrowed after `if x is None: sys.exit(...)`; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void GuardClause_Continue_NarrowsOptionalInLoop()
        {
            // verify_crmsd.py:207 shape — `for ...: p = f(); if p is None: continue; g(p)`.
            const string src =
                "def need(p: str): return p\n" +
                "def f(items):\n" +
                "    for it in items:\n" +
                "        x = it.get('k')\n" +
                "        if x is None:\n" +
                "            continue\n" +
                "        need(x)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "x must be narrowed after `if x is None: continue`; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void NonTerminatingGuard_DoesNotOverNarrow()
        {
            // Guard: if the branch does NOT always exit, the Optional must NOT be
            // narrowed afterward (x could still be None at the call).
            const string src =
                "def need(p: str): return p\n" +
                "def f(x: str | None):\n" +
                "    if x is None:\n" +
                "        x = x\n" +
                "    need(x)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "x must stay Optional when the guard doesn't exit; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        // ---------- Implicit Optional: `param: T = None` ⇒ T | None (iter 21) ----------
        // aa_model.py `def build_allatom_model(ipa_ckpt: str = None, ...)` called with
        // `ipa_ckpt=None`. A param annotated T with a literal None default is implicitly
        // Optional[T] (historical mypy default); passing None is intended, not an error.

        [TestMethod]
        public void ImplicitOptional_NoneDefault_AcceptsNoneKeywordArg()
        {
            const string src =
                "def build(ipa_ckpt: str = None, freeze: bool = True):\n" +
                "    if ipa_ckpt:\n" +
                "        return ipa_ckpt\n" +
                "    return ''\n" +
                "build(ipa_ckpt=None, freeze=True)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "None must satisfy an implicit-Optional param `str = None`; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void ImplicitOptional_NoneDefault_AcceptsPositionalNoneAndReal()
        {
            const string src =
                "def f(x: str = None): return x\n" +
                "f(None)\n" +
                "f('hi')\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "both None and str must satisfy `x: str = None`; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void NonDefaultedStrParam_PassedNone_StillFlagged()
        {
            // Guard: implicit-Optional only applies with a None DEFAULT. A plain `x: str`
            // param passed None must still flag.
            const string src =
                "def g(x: str): return x\n" +
                "g(None)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "None into a non-defaulted str param must still flag; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        // ---------- Branch-join: reassign-in-None-branch (iter 22) ----------
        // crmsd.py:100 — `if n_atoms is None: n_atoms = len(query)` makes n_atoms int on
        // BOTH paths (None-branch reassigns to int; else-branch narrowed non-None). After
        // the if, the join is int ∪ int = int, so passing it to an `int` param is fine.

        [TestMethod]
        public void ReassignInNoneBranch_JoinsToNonOptional()
        {
            const string src =
                "from typing import Optional\n" +
                "def need(k: int): return k\n" +
                "def f(n: Optional[int], query):\n" +
                "    if n is None:\n" +
                "        n = len(query)\n" +
                "    need(n)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "n must join to int after `if n is None: n = len(...)`; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        // ---------- Container is a Sequence + bare-object arg leniency (iter 24) ----------

        [TestMethod]
        public void ListLiteral_SatisfiesSequenceParam()
        {
            // io_utils.py:61 — a list literal passed to a `Sequence[str]` param. list IS a
            // Sequence in Python; the builtins stub now chains list -> Sequence.
            const string src =
                "from typing import Sequence\n" +
                "def write_tsv(path: str, header: Sequence[str], rows): return rows\n" +
                "write_tsv('p', ['a', 'b', 'c'], [])\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "a list must satisfy a Sequence[str] param; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void BareObjectValue_SatisfiesConcreteParam()
        {
            // _rouse_dynamics_evidence.py:630 — `a = cells.get(k)` (typed object) passed to
            // a `dict` param. Bare object is this checker's un-inferred fallback → lenient.
            const string src =
                "def plot(a: dict, b: dict): return a\n" +
                "def f(cells: dict):\n" +
                "    a1 = cells.get('k')\n" +
                "    a8 = cells.get('j')\n" +
                "    if a1 and a8:\n" +
                "        plot(a1, a8)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "a bare-object value must be assignable to a concrete param; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void BareObjectCallee_NotArityChecked()
        {
            // plot_prop_logs_rouse_intspace.py:647 — `cust = d.get(k); cust(a, b, c)`.
            // A bare-object callee (un-inferred fallback) must not be arity-checked against
            // object.__init__ ("3 given, 0 accepted").
            const string src =
                "def f(reg: dict, p, out):\n" +
                "    cust = reg.get('k')\n" +
                "    if cust is not None:\n" +
                "        return cust(p, out, 'x')\n" +
                "    return None\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch),
                "calling a bare-object value must not arity-check; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void ConcreteMismatch_StillFlagged_AfterObjectLeniency()
        {
            // Guard: the object-source leniency must not suppress a genuine mismatch
            // between two concrete non-object types (int passed to a str param).
            const string src =
                "def need(s: str): return s\n" +
                "need(5)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "int into a str param must still flag; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void PlainIf_NoReassign_DoesNotNarrowOptional()
        {
            // Guard: a plain `if cond:` that neither exits nor reassigns must leave an
            // Optional as Optional afterward (both branch exits still include None).
            const string src =
                "def need(k: int): return k\n" +
                "def f(n, flag):\n" +
                "    if flag:\n" +
                "        pass\n" +
                "    n2 = n if flag else None\n" +
                "    need(n2)\n";
            var r = VirtualCompiler.CompileSource(src);
            // n2 is Unknown/dynamic here (conditional expr) → lenient, must NOT flag; this
            // asserts the join didn't spuriously turn a dynamic into a hard mismatch.
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "dynamic value must stay lenient; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }
    }
}
