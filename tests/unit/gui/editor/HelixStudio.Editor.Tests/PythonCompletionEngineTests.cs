using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor.Completion;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PythonCompletionEngineTests
    {
        private sealed class KeyDrivenScintilla : HelixTextEditorNET.Scintilla
        {
            public void PressTab()
            {
                base.OnKeyDown(new System.Windows.Forms.KeyEventArgs(System.Windows.Forms.Keys.Tab));
            }
        }

        [TestMethod]
        public void Classify_General_EmptyPrefix_AtModuleStart()
        {
            var e = new PythonCompletionEngine();
            var ctx = e.Classify("", 0);
            Assert.AreEqual(CompletionContextKind.General, ctx.Kind);
            Assert.AreEqual("", ctx.Prefix);
        }

        [TestMethod]
        public void Classify_General_WithPrefix()
        {
            var e = new PythonCompletionEngine();
            string src = "def f(): ret";
            var ctx = e.Classify(src, src.Length);
            Assert.AreEqual(CompletionContextKind.General, ctx.Kind);
            Assert.AreEqual("ret", ctx.Prefix);
        }

        [TestMethod]
        public void Classify_Member_AfterDot()
        {
            var e = new PythonCompletionEngine();
            string src = "x = obj.na";
            var ctx = e.Classify(src, src.Length);
            Assert.AreEqual(CompletionContextKind.Member, ctx.Kind);
            Assert.AreEqual("obj", ctx.MemberBase);
            Assert.AreEqual("na", ctx.Prefix);
        }

        [TestMethod]
        public void Classify_Import_Line()
        {
            var e = new PythonCompletionEngine();
            string src = "import js";
            var ctx = e.Classify(src, src.Length);
            Assert.AreEqual(CompletionContextKind.Import, ctx.Kind);
            Assert.AreEqual("js", ctx.Prefix);
        }

        [TestMethod]
        public void General_IncludesKeywordsAndBuiltins()
        {
            var e = new PythonCompletionEngine();
            var items = e.GetCompletions("", 0, null, null);
            Assert.IsTrue(items.Any(i => i.Kind == CompletionKind.Snippet && i.Label == "def"));
            Assert.IsTrue(items.Any(i => i.Kind == CompletionKind.Builtin && i.Label == "print"));
            Assert.IsTrue(items.Any(i => i.Kind == CompletionKind.Builtin && i.Label == "range"));
        }

        [TestMethod]
        public void DecoratorContext_OffersOnlyAtPrefixedDecorators()
        {
            var e = new PythonCompletionEngine();
            string src = "class Example:\n    @";

            CompletionContext ctx = e.Classify(src, src.Length);
            var items = e.GetCompletions(src, src.Length, "example.py", null, ctx);

            Assert.AreEqual(CompletionContextKind.Decorator, ctx.Kind);
            Assert.AreEqual("@", ctx.Prefix);
            Assert.AreEqual(src.Length - 1, ctx.PrefixStart);
            Assert.IsTrue(items.Any(i => i.Label == "@staticmethod"));
            Assert.IsTrue(items.Any(i => i.Label == "@classmethod"));
            Assert.IsTrue(items.Any(i => i.Label == "@property"));
            Assert.IsTrue(items.All(i => i.Label.StartsWith("@")),
                "decorator completion must not contain ordinary keywords or builtins");
            Assert.IsFalse(items.Any(i => i.Label == "abs" || i.Label == "class"));
        }

        [TestMethod]
        public void DecoratorContext_PartialPrefixReplacesTheAtSignToo()
        {
            var e = new PythonCompletionEngine();
            string src = "class Example:\n    @sta";
            CompletionContext ctx = e.Classify(src, src.Length);
            var ranked = CompletionRanker.FilterAndRank(
                e.GetCompletions(src, src.Length, "example.py", null, ctx), ctx.Prefix);

            Assert.AreEqual(CompletionContextKind.Decorator, ctx.Kind);
            Assert.AreEqual("@sta", ctx.Prefix);
            Assert.AreEqual("@staticmethod", ranked.First().Label);
        }

        [TestMethod]
        public void General_ExposesModuleLevelDefsAndClasses()
        {
            var e = new PythonCompletionEngine();
            string src =
                "class Foo:\n    pass\n\ndef bar():\n    return 1\n\n";
            // Caret at end.
            var items = e.GetCompletions(src, src.Length, null, null);
            Assert.IsTrue(items.Any(i => i.Label == "Foo" && i.Kind == CompletionKind.Class));
            Assert.IsTrue(items.Any(i => i.Label == "bar" && i.Kind == CompletionKind.Function));
        }

        [TestMethod]
        public void General_ExposesEnclosingFunctionParams()
        {
            var e = new PythonCompletionEngine();
            string src =
                "def fn(alpha, beta):\n    gamma = 1\n    ";
            int caret = src.Length;
            var items = e.GetCompletions(src, caret, null, null);
            Assert.IsTrue(items.Any(i => i.Label == "alpha" && i.Kind == CompletionKind.Parameter));
            Assert.IsTrue(items.Any(i => i.Label == "beta" && i.Kind == CompletionKind.Parameter));
            Assert.IsTrue(items.Any(i => i.Label == "gamma" && i.Kind == CompletionKind.Variable));
        }

        [TestMethod]
        public void Member_OnStringLiteral_OffersStringMethods()
        {
            var e = new PythonCompletionEngine();
            string src = "\"hello\".";
            var items = e.GetCompletions(src, src.Length, null, null);
            Assert.IsTrue(items.Any(i => i.Label == "upper" && i.Kind == CompletionKind.Method));
            Assert.IsTrue(items.Any(i => i.Label == "split" && i.Kind == CompletionKind.Method));
        }

        [TestMethod]
        public void Member_OnSelf_OffersMethodsAndInstanceAttributes()
        {
            // Regression (iter 57): `self.` used to offer only methods, never the instance
            // attributes assigned via self.X — a core autocomplete gap.
            var e = new PythonCompletionEngine();
            string src =
                "class Calculator:\n" +
                "    def __init__(self):\n" +
                "        self.total = 0\n" +
                "        self.history = []\n" +
                "    def add(self, value):\n" +
                "        self.\n";
            int caret = src.LastIndexOf("self.") + "self.".Length;
            var items = e.GetCompletions(src, caret, "calc.py", null);
            Assert.IsTrue(items.Any(i => i.Label == "add" && i.Kind == CompletionKind.Method), "methods still offered");
            Assert.IsTrue(items.Any(i => i.Label == "total" && i.Kind == CompletionKind.Field), "instance attr 'total' must be offered");
            Assert.IsTrue(items.Any(i => i.Label == "history" && i.Kind == CompletionKind.Field), "instance attr 'history' must be offered");
        }

        [TestMethod]
        public void MemberCompletion_HidesLeadingUnderscoreUntilUserRequestsIt()
        {
            var e = new PythonCompletionEngine();
            string src =
                "class Account:\n" +
                "    def __init__(self):\n" +
                "        self.name = 'public'\n" +
                "        self._name = 'hidden'\n" +
                "    def run(self):\n        pass\n" +
                "account = Account()\naccount.";

            var publicItems = e.GetCompletions(src, src.Length, "account.py", null);
            Assert.IsTrue(publicItems.Any(i => i.Label == "name"));
            Assert.IsTrue(publicItems.Any(i => i.Label == "run"));
            Assert.AreEqual("run()", publicItems.First(i => i.Label == "run").InsertText);
            Assert.IsFalse(publicItems.Any(i => i.Label == "_name"));
            Assert.IsFalse(publicItems.Any(i => i.Label == "__init__"));

            string explicitHidden = src + "_";
            var hiddenItems = e.GetCompletions(
                explicitHidden, explicitHidden.Length, "account.py", null);
            Assert.IsTrue(hiddenItems.Any(i => i.Label == "_name"),
                "typing underscore explicitly must reveal implementation members");
            Assert.IsTrue(hiddenItems.Any(i => i.Label == "__init__"));
        }

        [TestMethod]
        public void CallableCompletion_InsertsParenthesesAndPlacesCaretInside()
        {
            using (var editor = new HelixTextEditorNET.Scintilla())
            {
                editor.Text = "account.run";
                var item = new CompletionItem(
                    "run", CompletionKind.Method, "run()", "run(self, value)");

                bool applied = CompletionController.ApplyCallableInsertion(
                    editor, item, "account.".Length);

                Assert.IsTrue(applied);
                Assert.AreEqual("account.run()", editor.Text);
                Assert.AreEqual("account.run(".Length, editor.CurrentPosition);
            }
        }

        [TestMethod]
        public void RealTabCommit_OnInitiallySelectedMethod_InsertsCallAndCaretInside()
        {
            const string source =
                "class Account:\n" +
                "    def run(self):\n        pass\n" +
                "account = Account()\naccount.ru";
            using (var editor = new KeyDrivenScintilla())
            using (var controller = new CompletionController(editor, () => null, () => "account.py"))
            {
                editor.Text = source;
                editor.GotoPosition(editor.TextLength);
                controller.Invoke(force: true);
                Assert.IsTrue(editor.AutoCActive, "completion list must be active before Tab");

                editor.PressTab();

                Assert.IsTrue(editor.Text.EndsWith("account.run()"));
                Assert.AreEqual(editor.TextLength - 1, editor.CurrentPosition,
                    "caret must be between the inserted parentheses");
            }
        }

        [TestMethod]
        public void RealTabCommit_OnDefShortcut_ExpandsFunctionBody()
        {
            const string source = "class Example:\n    def";
            using (var editor = new KeyDrivenScintilla())
            using (var controller = new CompletionController(editor, () => null, () => "example.py"))
            {
                editor.Text = source;
                editor.GotoPosition(editor.TextLength);
                controller.Invoke(force: true);
                Assert.IsTrue(editor.AutoCActive, "the def snippet must be selectable");

                editor.PressTab();

                Assert.AreEqual(
                    "class Example:\n    def function_name(self):\n        pass",
                    editor.Text);
            }
        }

        [TestMethod]
        public void Member_OnListLiteral_OffersListMethods()
        {
            var e = new PythonCompletionEngine();
            string src = "[1,2,3].";
            var items = e.GetCompletions(src, src.Length, null, null);
            Assert.IsTrue(items.Any(i => i.Label == "append" && i.Kind == CompletionKind.Method));
            Assert.IsTrue(items.Any(i => i.Label == "pop" && i.Kind == CompletionKind.Method));
        }

        [TestMethod]
        public void ScientificFactories_ProvideOfflineMemberCompletion()
        {
            var e = new PythonCompletionEngine();
            string numpy = "import numpy as np\nrng = np.random.default_rng(7)\nsamples = rng.normal(size=(2, 3))\nsamples.";
            var numpyItems = e.GetCompletions(numpy, numpy.Length, "science.py", null);
            Assert.IsTrue(numpyItems.Any(i => i.Label == "reshape"), "ndarray members must not require a live catalog");

            string torch = "import torch\nx = torch.zeros(2, 3)\nx.";
            var torchItems = e.GetCompletions(torch, torch.Length, "model.py", null);
            Assert.IsTrue(torchItems.Any(i => i.Label == "detach"), "Tensor members must be available offline");
        }

        [TestMethod]
        public void GenericAnnotations_ResolveTheirRuntimeMemberSurface()
        {
            var e = new PythonCompletionEngine();
            string src = "def consume(values: list[int]):\n    values.";
            var items = e.GetCompletions(src, src.Length, "typed.py", null);
            Assert.IsTrue(items.Any(i => i.Label == "append"));
        }

        [TestMethod]
        public void AnnotatedAssignmentsAndLocalFactoriesDriveMemberCompletion()
        {
            var e = new PythonCompletionEngine();
            string src =
                "class Result:\n" +
                "    def render(self):\n        pass\n" +
                "class Builder:\n" +
                "    def make(self) -> Result:\n        return Result()\n" +
                "    @staticmethod\n" +
                "    def create() -> Result:\n        return Result()\n" +
                "    def run(self):\n" +
                "        typed: Result = Result()\n" +
                "        typed.\n";
            int caret = src.LastIndexOf("typed.") + "typed.".Length;
            var typed = e.GetCompletions(src, caret, "factory.py", null);
            Assert.IsTrue(typed.Any(i => i.Label == "render"));

            string viaSelf = src.Replace("        typed.\n", "        value = self.make()\n        value.\n");
            var selfItems = e.GetCompletions(viaSelf, viaSelf.LastIndexOf("value.") + "value.".Length, "factory.py", null);
            Assert.IsTrue(selfItems.Any(i => i.Label == "render"));

            string viaClass = src.Replace("        typed.\n", "        value = Builder.create()\n        value.\n");
            var classItems = e.GetCompletions(viaClass, viaClass.LastIndexOf("value.") + "value.".Length, "factory.py", null);
            Assert.IsTrue(classItems.Any(i => i.Label == "render"));
        }

        [TestMethod]
        public void ImportedClassConstructorUsesItsDeclaredClassSurfaceWhenSolutionProvidesIt()
        {
            var e = new PythonCompletionEngine();
            string src = "from pathlib import Path\np = Path('x')\np.";
            var items = e.GetCompletions(src, src.Length, "paths.py", null);
            Assert.IsTrue(items.Any(i => i.Label == "read_text"));
            Assert.IsTrue(items.Any(i => i.Label == "resolve"));
        }

        [TestMethod]
        public void Import_LineOffersCommonModules()
        {
            var e = new PythonCompletionEngine();
            string src = "import ";
            var items = e.GetCompletions(src, src.Length, null, null);
            Assert.IsTrue(items.Any(i => i.Label == "os" && i.Kind == CompletionKind.Module));
            Assert.IsTrue(items.Any(i => i.Label == "sys" && i.Kind == CompletionKind.Module));
            // No keywords in import context.
            Assert.IsFalse(items.Any(i => i.Kind == CompletionKind.Keyword));
        }

        // Regression: the RHS doc panel (CompletionDocPanel) hides itself when an item's Detail
        // and Documentation are both empty, so bare `import`/`from` module suggestions showed no
        // doc at all — every entry in the hardcoded commonModules list was built with neither set.
        [TestMethod]
        public void Import_LineModules_HaveADetailDescription()
        {
            var e = new PythonCompletionEngine();
            string src = "import ";
            var items = e.GetCompletions(src, src.Length, null, null);
            CompletionItem os = items.First(i => i.Label == "os" && i.Kind == CompletionKind.Module);
            Assert.IsFalse(string.IsNullOrEmpty(os.Detail), "module suggestions must carry a description for the RHS doc panel");
        }

        // Regression: user-reported bug — `from io import <cursor>` was showing the SAME
        // "list every importable module" completions as bare `import`/`from`, instead of members
        // of the module already named in the from-clause (StringIO, BytesIO, etc.). Classify must
        // recognize this as its own context, distinct from Import, carrying the module name.
        [TestMethod]
        public void Classify_FromImportMember_AfterModuleAndImportKeyword()
        {
            var e = new PythonCompletionEngine();
            string src = "from io import ";
            var ctx = e.Classify(src, src.Length);
            Assert.AreEqual(CompletionContextKind.FromImportMember, ctx.Kind);
            Assert.AreEqual("io", ctx.FromImportModule);
            Assert.AreEqual("", ctx.Prefix);
        }

        [TestMethod]
        public void Classify_FromImportMember_WithPartiallyTypedName()
        {
            var e = new PythonCompletionEngine();
            string src = "from io import Str";
            var ctx = e.Classify(src, src.Length);
            Assert.AreEqual(CompletionContextKind.FromImportMember, ctx.Kind);
            Assert.AreEqual("io", ctx.FromImportModule);
            Assert.AreEqual("Str", ctx.Prefix);
        }

        [TestMethod]
        public void Classify_FromImportMember_WithDottedModuleName()
        {
            var e = new PythonCompletionEngine();
            string src = "from lib_core.models import ";
            var ctx = e.Classify(src, src.Length);
            Assert.AreEqual(CompletionContextKind.FromImportMember, ctx.Kind);
            Assert.AreEqual("lib_core.models", ctx.FromImportModule);
        }

        [TestMethod]
        public void Classify_From_StillTypingModuleName_IsPlainImportNotMember()
        {
            // No "import" keyword reached yet — still suggesting module names, same as before.
            var e = new PythonCompletionEngine();
            string src = "from i";
            var ctx = e.Classify(src, src.Length);
            Assert.AreEqual(CompletionContextKind.Import, ctx.Kind);
        }

        [TestMethod]
        public void FromImportMember_NoLongerOffersTheModuleList()
        {
            // The actual bug: without a Catalog wired up, this must NOT fall back to the
            // "list every module" behavior (that was the reported symptom) — it should offer
            // nothing rather than something misleading, same philosophy as the analogous `X.`
            // import-bound-member case when the Catalog can't resolve the module.
            var e = new PythonCompletionEngine();
            string src = "from io import ";
            var items = e.GetCompletions(src, src.Length, null, null);
            Assert.IsFalse(items.Any(i => i.Kind == CompletionKind.Module),
                "must not show the bare-import module list for from-import member completions");
            Assert.AreEqual("io", e.LastUnresolvedModuleFqn,
                "an unresolvable module should still queue a catalog scan for a later retry");
        }

        [TestMethod]
        public void FromImportMember_OffersTopLevelTypesFromSiblingSolutionProject()
        {
            var solution = new SolutionModel(@"C:\work\sample.sln", "sample");
            var library = new ProjectModel(
                @"C:\work\cuda_python_practice3\pyproject.toml",
                "cuda_python_practice3", solution);
            solution.Projects.Add(library);
            library.Types.Add(new TypeModel(
                "NewClass", "cuda_python_practice3", TypeKind.ConcreteClass, library));

            var e = new PythonCompletionEngine();
            string src = "from cuda_python_practice3 import ";
            var items = e.GetCompletions(src, src.Length, null, solution);

            Assert.IsTrue(items.Any(i => i.Kind == CompletionKind.Class && i.Label == "NewClass"));
            Assert.IsNull(e.LastUnresolvedModuleFqn,
                "a loaded solution project must not trigger an interpreter catalog scan");
        }

        [TestMethod]
        public void FromImportMember_UsesPackageNameForProjectRelativeFiles()
        {
            var solution = new SolutionModel(@"C:\work\pyproject.toml", "sample");
            var library = new ProjectModel(@"C:\work\lib_core\pyproject.toml", "lib_core", solution);
            solution.Projects.Add(library);
            library.ContentFiles.Add("__init__.py");
            library.ContentFiles.Add("models.py");
            var pair = new TypeModel("Pair", "lib_core.models", TypeKind.ConcreteClass, library)
            {
                SourceFilePath = @"C:\work\lib_core\models.py"
            };
            library.Types.Add(pair);

            var engine = new PythonCompletionEngine();
            string source = "from lib_core.models import Pa";
            var items = engine.GetCompletions(source, source.Length, "main.py", solution);

            Assert.IsTrue(items.Any(i => i.Kind == CompletionKind.Class && i.Label == "Pair"),
                "a flat workspace package must resolve its project-relative module file");
            Assert.IsNull(engine.LastUnresolvedModuleFqn,
                "workspace package imports must not trigger an external interpreter scan");
        }

        [TestMethod]
        public void FromImportMember_MapsLegacySrcPackageFilesWithoutSrcPrefix()
        {
            var solution = new SolutionModel(@"C:\work\pyproject.toml", "sample");
            var library = new ProjectModel(@"C:\work\my_pkg\pyproject.toml", "my_pkg", solution);
            solution.Projects.Add(library);
            library.ContentFiles.Add("src/my_pkg/__init__.py");
            library.ContentFiles.Add("src/my_pkg/models.py");
            library.Types.Add(new TypeModel("Pair", "my_pkg.models", TypeKind.ConcreteClass, library)
            {
                SourceFilePath = @"C:\work\my_pkg\src\my_pkg\models.py"
            });

            var engine = new PythonCompletionEngine();
            string source = "from my_pkg.models import Pa";
            var items = engine.GetCompletions(source, source.Length, "main.py", solution);

            Assert.IsTrue(items.Any(i => i.Kind == CompletionKind.Class && i.Label == "Pair"),
                "legacy src-layout packages must resolve under their importable package name");
        }
    }
}
