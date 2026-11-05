using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Workspace.Python;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class SymbolResolverTests
    {
        private string _projectDir;
        private SolutionModel _solution;
        private ProjectModel _project;

        [TestInitialize]
        public void Setup()
        {
            _projectDir = Path.Combine(Path.GetTempPath(), "helix_sr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_projectDir);
            string pyproject = Path.Combine(_projectDir, "pyproject.toml");
            File.WriteAllText(pyproject, "[project]\nname=\"t\"\n");
            _solution = new SolutionModel(pyproject, "t");
            _project = new ProjectModel(pyproject, "t", _solution);
            _project.DefaultNamespace = "t";
            _solution.Projects.Add(_project);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (Directory.Exists(_projectDir)) Directory.Delete(_projectDir, recursive: true); }
            catch { /* best effort */ }
        }

        private TypeModel AddType(string ns, string name, string relPath, string parentFqn = null)
        {
            TypeModel t = new TypeModel(name, ns, TypeKind.ConcreteClass, _project);
            t.ParentTypeFqn = parentFqn;
            t.SourceFilePath = Path.Combine(_projectDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            _project.Types.Add(t);
            return t;
        }

        private string FilePath(string rel)
        {
            return Path.Combine(_projectDir, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        private static (int line, int column) Find(string source, string token, int occurrence = 1)
        {
            int idx = -1;
            for (int i = 0; i < occurrence; i++)
            {
                idx = source.IndexOf(token, idx + 1, StringComparison.Ordinal);
                if (idx < 0) throw new InvalidOperationException("Token not found: " + token);
            }
            int line = 1, col = 1;
            for (int i = 0; i < idx; i++)
            {
                if (source[i] == '\n') { line++; col = 1; }
                else col++;
            }
            return (line, col);
        }

        private ResolvedSymbol Resolve(string source, string relPath, string token, int occurrence = 1)
        {
            ModuleSyntax module = Python.Python.ParseModule(source, relPath).Module;
            var (line, col) = Find(source, token, occurrence);
            SymbolResolver resolver = new SymbolResolver(_solution);
            return resolver.Resolve(module, FilePath(relPath), line, col, token);
        }

        [TestMethod]
        public void ResolvesBareNameInSameModule()
        {
            TypeModel expected = AddType("mymod", "MyClass", "mymod.py");
            string src = "class MyClass:\n    pass\n\ndef use():\n    x = MyClass()\n";
            ResolvedSymbol r = Resolve(src, "mymod.py", "MyClass", occurrence: 2); // skip the definition
            Assert.IsNotNull(r);
            Assert.AreSame(expected, r.Target);
        }

        [TestMethod]
        public void ResolvesModuleVariableToItsDeclaration()
        {
            const string src = "SENSITIVITY = {}\n\ndef use():\n    return SENSITIVITY\n";
            ResolvedSymbol r = Resolve(src, "incidence.py", "SENSITIVITY", occurrence: 2);
            Assert.IsNotNull(r);
            Assert.IsNotNull(r.Variable);
            Assert.AreEqual("SENSITIVITY", r.Variable.Name);
            Assert.AreEqual(FilePath("incidence.py"), r.Variable.SourceFilePath);
            Assert.AreEqual(1, r.Variable.SourceLineNumber);
            Assert.AreSame(_project, r.Variable.Project);
        }

        [TestMethod]
        public void SolutionRenameOfModuleVariableSkipsCommentsStringsAndShadowedLocals()
        {
            string declaration = FilePath("incidence.py");
            string consumer = FilePath("consumer.py");
            File.WriteAllText(declaration, "SENSITIVITY = {}\nprint(SENSITIVITY)\n");
            File.WriteAllText(consumer,
                "from incidence import SENSITIVITY\n" +
                "text = 'SENSITIVITY' # SENSITIVITY\n" +
                "def f(SENSITIVITY):\n    return SENSITIVITY\n" +
                "print(SENSITIVITY)\n");
            var variable = Resolve(File.ReadAllText(declaration), "incidence.py", "SENSITIVITY").Variable;

            var computed = new SymbolRenameService().ComputeRenameVariable(
                _solution, variable, "LAW_SENSITIVITY", RenameScope.Solution);

            Assert.AreEqual(2, computed.Edits.Count);
            string rewritten = computed.Edits.Find(e => e.Path == consumer).NewText;
            StringAssert.Contains(rewritten, "from incidence import LAW_SENSITIVITY");
            StringAssert.Contains(rewritten, "print(LAW_SENSITIVITY)");
            StringAssert.Contains(rewritten, "'SENSITIVITY' # SENSITIVITY");
            StringAssert.Contains(rewritten, "def f(SENSITIVITY):\n    return SENSITIVITY");
        }

        [TestMethod]
        public void ResolvesVariableThroughRelativeModuleAlias_LikeInvGamma()
        {
            string invariants = FilePath("invariants.py");
            File.WriteAllText(invariants, "GAMMA: tuple = ()\n");
            const string incidence =
                "from . import invariants as _inv\n\n" +
                "for inv in _inv.GAMMA:\n    pass\n";

            ResolvedSymbol resolved = Resolve(incidence, "incidence.py", "GAMMA");

            Assert.IsNotNull(resolved, "a user-defined module attribute must resolve through its import alias");
            Assert.IsNotNull(resolved.Variable);
            Assert.AreEqual(invariants, resolved.Variable.SourceFilePath);
            Assert.AreEqual(1, resolved.Variable.SourceLineNumber);
            Assert.AreEqual("invariants", resolved.Variable.Namespace);
        }

        [TestMethod]
        public void SolutionRenameUpdatesOnlyAliasesBoundToTheDeclaringModule()
        {
            string invariants = FilePath("invariants.py");
            string incidence = FilePath("incidence.py");
            string unrelated = FilePath("unrelated.py");
            File.WriteAllText(invariants, "GAMMA: tuple = ()\n");
            File.WriteAllText(incidence,
                "from . import invariants as _inv\nfor inv in _inv.GAMMA:\n    pass\n");
            File.WriteAllText(unrelated,
                "class Other:\n    GAMMA = 1\nprint(Other.GAMMA)\n");
            VariableSymbol variable = Resolve(File.ReadAllText(incidence), "incidence.py", "GAMMA").Variable;

            SymbolRenameService.ComputedRename computed = new SymbolRenameService()
                .ComputeRenameVariable(_solution, variable, "LAWS", RenameScope.Solution);

            Assert.AreEqual(2, computed.Edits.Count);
            string changedConsumer = computed.Edits.Find(e => e.Path == incidence).NewText;
            StringAssert.Contains(changedConsumer, "_inv.LAWS");
            Assert.IsFalse(computed.Edits.Exists(e => e.Path == unrelated),
                "an identically named attribute on an unrelated owner must not be renamed");
        }

        [TestMethod]
        public void ResolvesTypedReceiverAttributeToField_NotToOwningClass()
        {
            string modelFile = FilePath("model.py");
            File.WriteAllText(modelFile,
                "class Model:\n    GAMMA = ()\n\n    def read(self):\n        return self.GAMMA\n");
            TypeModel model = AddType("model", "Model", "model.py");
            model.Members.Add(new MemberModel("GAMMA", MemberKind.Field));
            const string client =
                "from model import Model\n\n" +
                "def use():\n    value = Model()\n    return value.GAMMA\n";

            ResolvedSymbol resolved = Resolve(client, "client.py", "GAMMA");

            Assert.IsNotNull(resolved);
            Assert.IsNotNull(resolved.Variable);
            Assert.AreSame(model, resolved.Variable.OwnerType);
            Assert.AreEqual(2, resolved.Variable.SourceLineNumber);
            Assert.IsNull(resolved.Target,
                "selecting a field must never expose the owning class as the rename target");
        }

        [TestMethod]
        public void FieldRenameIsConfinedToItsOwningClass()
        {
            string modelFile = FilePath("model.py");
            File.WriteAllText(modelFile,
                "class Model:\n    GAMMA = ()\n    def read(self):\n        return self.GAMMA\n\n" +
                "class Other:\n    GAMMA = 1\n");
            TypeModel model = AddType("model", "Model", "model.py");
            MemberModel field = new MemberModel("GAMMA", MemberKind.Field);
            model.Members.Add(field);
            var variable = new VariableSymbol
            {
                Name = "GAMMA", Namespace = "model", SourceFilePath = modelFile,
                Project = _project, OwnerType = model, Member = field,
            };

            SymbolRenameService.ComputedRename computed = new SymbolRenameService()
                .ComputeRenameVariable(_solution, variable, "LAWS", RenameScope.Solution);

            Assert.AreEqual(1, computed.Edits.Count);
            StringAssert.Contains(computed.Edits[0].NewText, "class Model:\n    LAWS = ()");
            StringAssert.Contains(computed.Edits[0].NewText, "return self.LAWS");
            StringAssert.Contains(computed.Edits[0].NewText, "class Other:\n    GAMMA = 1");
        }

        [TestMethod]
        public void ResolvesImportedNameViaFromImport()
        {
            TypeModel baz = AddType("foo.bar", "Baz", "foo/bar.py");
            // distractor with same simple name elsewhere — must NOT be picked
            AddType("other", "Baz", "other.py");
            string src = "from foo.bar import Baz\n\ndef use():\n    x = Baz()\n";
            ResolvedSymbol r = Resolve(src, "client.py", "Baz", occurrence: 2);
            Assert.IsNotNull(r);
            Assert.AreSame(baz, r.Target);
        }

        [TestMethod]
        public void ResolvesAliasedModuleAttribute()
        {
            TypeModel baz = AddType("foo.bar", "Baz", "foo/bar.py");
            string src = "import foo.bar as fb\n\ndef use():\n    x = fb.Baz()\n";
            ResolvedSymbol r = Resolve(src, "client.py", "Baz");
            Assert.IsNotNull(r);
            Assert.AreSame(baz, r.Target);
        }

        [TestMethod]
        public void ResolvesWildcardImport()
        {
            TypeModel baz = AddType("foo", "Baz", "foo.py");
            string src = "from foo import *\n\ndef use():\n    x = Baz()\n";
            ResolvedSymbol r = Resolve(src, "client.py", "Baz", occurrence: 1);
            Assert.IsNotNull(r);
            Assert.AreSame(baz, r.Target);
        }

        [TestMethod]
        public void ResolvesNestedClassViaEnclosingScope()
        {
            AddType("mymod", "Outer", "mymod.py");
            TypeModel inner = AddType("mymod", "Inner", "mymod.py", parentFqn: "mymod.Outer");
            string src = "class Outer:\n    class Inner:\n        pass\n    def use(self):\n        return Inner\n";
            ResolvedSymbol r = Resolve(src, "mymod.py", "Inner", occurrence: 2); // click at `return Inner`
            Assert.IsNotNull(r);
            Assert.AreSame(inner, r.Target);
        }

        [TestMethod]
        public void ResolvesInheritedMethodThroughConstructedReceiver()
        {
            TypeModel baseType = AddType("sample_common.applications", "WorkflowApplication",
                "sample_common/applications.py");
            baseType.Members.Add(new MemberModel("run", MemberKind.Method)
            {
                SourceLineNumber = 21,
            });
            TypeModel application = AddType("client", "RunFullPipelineApplication", "client.py");
            application.BaseTypes.Add(new TypeReference("WorkflowApplication", RelationshipKind.Inherits));

            string src =
                "class RunFullPipelineApplication(WorkflowApplication):\n" +
                "    pass\n\n" +
                "RunFullPipelineApplication().run()\n";
            ResolvedSymbol r = Resolve(src, "client.py", "run");

            Assert.IsNotNull(r, "constructor-call receivers must resolve instead of hiding the context command");
            Assert.IsNotNull(r.Function, "an inherited method must surface as a navigable function body");
            Assert.AreEqual("run", r.Function.Name);
            Assert.AreEqual(baseType.SourceFilePath, r.Function.SourceFilePath);
            Assert.AreEqual(21, r.Function.SourceLineNumber);
        }

        [TestMethod]
        public void ResolvesMethodThroughLocallyConstructedReceiver()
        {
            TypeModel writer = AddType("sample_common.manuscript", "ManuscriptGenerator",
                "sample_common/manuscript.py");
            writer.Members.Add(new MemberModel("write", MemberKind.Method) { SourceLineNumber = 573 });
            string src =
                "from sample_common.manuscript import ManuscriptGenerator\n\n" +
                "def execute(arguments):\n" +
                "    generator = ManuscriptGenerator(arguments.results)\n" +
                "    destination = generator.write(arguments.output)\n";
            ResolvedSymbol r = Resolve(src, "client.py", "write");
            Assert.IsNotNull(r, "the local constructor binding must supply the receiver type");
            Assert.IsNotNull(r.Function);
            Assert.AreEqual(writer.SourceFilePath, r.Function.SourceFilePath);
            Assert.AreEqual(573, r.Function.SourceLineNumber);
        }

        [TestMethod]
        public void DoesNotUseAnAssignmentAfterTheCallAsReceiverEvidence()
        {
            TypeModel writer = AddType("sample_common.manuscript", "ManuscriptGenerator",
                "sample_common/manuscript.py");
            writer.Members.Add(new MemberModel("write", MemberKind.Method) { SourceLineNumber = 573 });
            string src =
                "from sample_common.manuscript import ManuscriptGenerator\n\n" +
                "def execute(generator, output):\n" +
                "    generator.write(output)\n" +
                "    generator = ManuscriptGenerator(output)\n";
            ResolvedSymbol r = Resolve(src, "client.py", "write");
            Assert.IsNull(r, "a later binding cannot determine the receiver at an earlier call");
        }

        // ------------------------------------------------------- calls on the class itself
        // Reported: right-clicking `main` in `Cli.main()` produced NO navigation
        // commands at all — no Goto Source, no Go to Function Body — because the resolver only
        // understood two receiver shapes, `Ctor().method()` and `self`/`cls`. A call on the class
        // is the dominant shape in a strict-OOP solution, where every function is a method.

        [TestMethod]
        public void ResolvesMethodCalledOnTheClassItself()
        {
            TypeModel cli = AddType("experiments.cli", "Cli", "experiments/cli.py");
            cli.Members.Add(new MemberModel("main", MemberKind.Method) { SourceLineNumber = 155 });

            string src =
                "from .experiments.cli import Cli\n\n" +
                "def main() -> None:\n" +
                "    Cli.main()\n";
            ResolvedSymbol r = Resolve(src, "main.py", "main", occurrence: 2); // the call, not the def

            Assert.IsNotNull(r, "a call on the class must resolve, or the context menu is empty");
            Assert.IsNotNull(r.Function, "Cli.main is a method: it must offer a navigable body");
            Assert.AreEqual("main", r.Function.Name);
            Assert.AreEqual(cli.SourceFilePath, r.Function.SourceFilePath);
            Assert.AreEqual(155, r.Function.SourceLineNumber);
        }

        [TestMethod]
        public void ResolvesTheClassNamedByARelativeImport_NotASameNamedOneElsewhere()
        {
            // Two classes share the simple name, so the solution-wide fallback cannot decide and
            // ONLY the relative import can. It used to be resolved as if it were absolute — the
            // leading dot dropped — which matched nothing and made resolution a coin toss.
            TypeModel mine = AddType("experiments.cli", "Cli", "experiments/cli.py");
            mine.Members.Add(new MemberModel("main", MemberKind.Method) { SourceLineNumber = 155 });
            TypeModel other = AddType("vendor.cli", "Cli", "vendor/cli.py");
            other.Members.Add(new MemberModel("main", MemberKind.Method) { SourceLineNumber = 9 });

            string src =
                "from .experiments.cli import Cli\n\n" +
                "def main() -> None:\n" +
                "    Cli.main()\n";
            ResolvedSymbol r = Resolve(src, "main.py", "main", occurrence: 2);

            Assert.IsNotNull(r);
            Assert.IsNotNull(r.Function);
            Assert.AreEqual(mine.SourceFilePath, r.Function.SourceFilePath,
                "the import says which Cli this is");
        }

        [TestMethod]
        public void ResolvesAMemberThatIsNotAMethod_ToItsOwningClass()
        {
            // A class constant carries no MemberKind.Method entry, so there is no body to jump to —
            // but the class it belongs to is still a real navigation target, and offering it beats
            // offering nothing (and beats the bare-word fallback jumping somewhere unrelated).
            TypeModel cli = AddType("experiments.cli", "Cli", "experiments/cli.py");

            string src =
                "from .experiments.cli import Cli\n\n" +
                "def main() -> None:\n" +
                "    print(Cli.VERSION)\n";
            ResolvedSymbol r = Resolve(src, "main.py", "VERSION");

            Assert.IsNotNull(r);
            Assert.AreSame(cli, r.Target);
        }

        [TestMethod]
        public void AModuleAttributeIsStillNotAType()
        {
            // `os.path.join` must keep resolving to nothing: the receiver is a module, not a class.
            AddType("mymod", "MyClass", "mymod.py");
            string src = "import os\n\ndef use():\n    return os.getcwd()\n";
            ResolvedSymbol r = Resolve(src, "mymod.py", "getcwd");
            Assert.IsNull(r);
        }

        [TestMethod]
        public void ReturnsNullWhenTokenIsParameter()
        {
            AddType("mymod", "X", "mymod.py"); // there IS a type called X
            string src = "def use(X):\n    X.do()\n";
            ResolvedSymbol r = Resolve(src, "mymod.py", "X", occurrence: 2); // click at `X.do()`
            Assert.IsNull(r);
        }

        [TestMethod]
        public void ReturnsNullWhenClickingImportModulePath()
        {
            AddType("foo.bar", "Baz", "foo/bar.py");
            string src = "from foo.bar import Baz\n";
            ResolvedSymbol r = Resolve(src, "client.py", "foo", occurrence: 1);
            Assert.IsNull(r);
        }

        [TestMethod]
        public void ReturnsNullForEmptyToken()
        {
            AddType("mymod", "X", "mymod.py");
            string src = "\n\n   \n";
            ModuleSyntax m = Python.Python.ParseModule(src, "mymod.py").Module;
            SymbolResolver resolver = new SymbolResolver(_solution);
            Assert.IsNull(resolver.Resolve(m, FilePath("mymod.py"), 3, 2, ""));
            Assert.IsNull(resolver.Resolve(m, FilePath("mymod.py"), 3, 2, "   "));
        }

        [TestMethod]
        public void ReturnsAmbiguousCandidatesWhenMultipleMatch()
        {
            TypeModel fooP = AddType("foo", "Point", "foo.py");
            TypeModel barP = AddType("bar", "Point", "bar.py");
            string src = "def use():\n    return Point()\n";
            ResolvedSymbol r = Resolve(src, "client.py", "Point");
            Assert.IsNotNull(r);
            Assert.IsTrue(r.IsAmbiguous);
            Assert.IsNull(r.Target);
            Assert.AreEqual(2, r.Candidates.Count);
            CollectionAssert.Contains((System.Collections.ICollection)r.Candidates, fooP);
            CollectionAssert.Contains((System.Collections.ICollection)r.Candidates, barP);
        }
    }
}
