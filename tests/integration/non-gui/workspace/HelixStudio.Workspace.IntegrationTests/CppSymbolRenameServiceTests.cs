using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Covers <see cref="CppIdentifierRewriter"/> (what a rename touches inside one translation
    /// unit) and <see cref="CppSymbolRenameService"/> (which files are candidates, and that the
    /// rewritten text actually reaches disk). The single most important property proved here is the
    /// shadowing one: a same-named local or a same-named method on an unrelated class must survive a
    /// rename untouched, the C++ counterpart of the property the Python rename engine guarantees.
    /// </summary>
    [TestClass]
    public class CppSymbolRenameServiceTests
    {
        // ---- helpers ---------------------------------------------------------

        private static ProjectModel MakeProject(out string projectDir, string targetName = "lib_core")
        {
            projectDir = Path.Combine(Path.GetTempPath(), "helix_cpprename_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projectDir);
            // CMake-backed projects use a synthetic '<folder>/<target>.cmake-target' marker path, so
            // ProjectDirectory still resolves to the real folder on disk.
            string marker = Path.Combine(projectDir, targetName + ".cmake-target");
            SolutionModel sln = new SolutionModel(Path.Combine(projectDir, "CMakeLists.txt"), "t");
            ProjectModel project = new ProjectModel(marker, targetName, sln);
            project.Language = ProjectLanguage.Cpp;
            sln.Projects.Add(project);
            return project;
        }

        private static ProjectModel AddProject(SolutionModel sln, out string projectDir, string targetName)
        {
            projectDir = Path.Combine(Path.GetTempPath(), "helix_cpprename_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projectDir);
            ProjectModel project = new ProjectModel(
                Path.Combine(projectDir, targetName + ".cmake-target"), targetName, sln);
            project.Language = ProjectLanguage.Cpp;
            sln.Projects.Add(project);
            return project;
        }

        private static string Write(string dir, string name, string content)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static TypeModel TypeIn(ProjectModel project, string name, string file)
        {
            TypeModel t = new TypeModel(name, string.Empty, TypeKind.ConcreteClass, project);
            t.SourceFilePath = file;
            project.Types.Add(t);
            return t;
        }

        // ---- rewriter: declaration + ctor/dtor + references ------------------

        [TestMethod]
        public void RenamesDeclarationConstructorDestructorAndEveryReference()
        {
            const string src =
                "class Widget {\n" +
                "public:\n" +
                "    Widget();\n" +
                "    ~Widget();\n" +
                "    int value() const;\n" +
                "};\n" +
                "\n" +
                "Widget::Widget() {}\n" +
                "Widget::~Widget() {}\n" +
                "\n" +
                "void build() {\n" +
                "    Widget w;\n" +
                "    Widget* p = new Widget();\n" +
                "    delete p;\n" +
                "}\n";

            CppIdentifierRewriter.RewriteResult r = CppIdentifierRewriter.Rewrite(src, "Widget", "Gadget");

            Assert.IsFalse(r.Text.Contains("Widget"), "no spelling of the old name should survive:\n" + r.Text);
            StringAssert.Contains(r.Text, "class Gadget {");
            // C++ requires the constructor and destructor to be spelled exactly like the class.
            StringAssert.Contains(r.Text, "    Gadget();");
            StringAssert.Contains(r.Text, "    ~Gadget();");
            StringAssert.Contains(r.Text, "Gadget::Gadget() {}");
            StringAssert.Contains(r.Text, "Gadget::~Gadget() {}");
            StringAssert.Contains(r.Text, "    Gadget w;");
            StringAssert.Contains(r.Text, "    Gadget* p = new Gadget();");
            Assert.AreEqual(10, r.Count, "10 occurrences: decl, ctor decl, dtor decl, 2x2 out-of-line, 3 uses");
        }

        [TestMethod]
        public void LeavesShadowedLocalAndUnrelatedMethodAlone()
        {
            const string src =
                "class Widget {\n" +
                "public:\n" +
                "    Widget();\n" +
                "};\n" +
                "\n" +
                "class Other {\n" +
                "public:\n" +
                "    void Widget();\n" +
                "};\n" +
                "\n" +
                "void helper() {\n" +
                "    int Widget = 42;\n" +
                "    Widget += 1;\n" +
                "}\n" +
                "\n" +
                "void build() {\n" +
                "    Widget w;\n" +
                "}\n";

            CppIdentifierRewriter.RewriteResult r = CppIdentifierRewriter.Rewrite(src, "Widget", "Gadget");

            // Renamed: the declaration, its constructor, and the genuine type use.
            StringAssert.Contains(r.Text, "class Gadget {");
            StringAssert.Contains(r.Text, "    Gadget();");
            StringAssert.Contains(r.Text, "    Gadget w;");

            // Untouched: a method on an unrelated class that merely shares the spelling.
            StringAssert.Contains(r.Text, "class Other {");
            StringAssert.Contains(r.Text, "    void Widget();");

            // Untouched: a local variable that shadows the type for the whole function.
            StringAssert.Contains(r.Text, "    int Widget = 42;");
            StringAssert.Contains(r.Text, "    Widget += 1;");

            Assert.AreEqual(3, r.Count);
        }

        [TestMethod]
        public void RenamesBaseClauseReference()
        {
            const string src =
                "class Base {\n" +
                "public:\n" +
                "    virtual ~Base();\n" +
                "    virtual int rank() const;\n" +
                "};\n" +
                "\n" +
                "class Derived : public Base {\n" +
                "public:\n" +
                "    int rank() const;\n" +
                "};\n" +
                "\n" +
                "struct Mixed : private Base, public Derived {\n" +
                "};\n";

            CppIdentifierRewriter.RewriteResult r = CppIdentifierRewriter.Rewrite(src, "Base", "Foundation");

            StringAssert.Contains(r.Text, "class Foundation {");
            StringAssert.Contains(r.Text, "    virtual ~Foundation();");
            StringAssert.Contains(r.Text, "class Derived : public Foundation {");
            StringAssert.Contains(r.Text, "struct Mixed : private Foundation, public Derived {");
            Assert.IsFalse(r.Text.Contains("Base"), "no spelling of the old name should survive:\n" + r.Text);
        }

        [TestMethod]
        public void LeavesCoincidentalMemberAccessAlone()
        {
            // `Widget` is both a class and — coincidentally — the name of a method on an unrelated
            // struct. The type rename must touch the class and its uses and nothing else, exactly as
            // the Python rewriter leaves `foo.OldClassName` (attribute access) alone.
            const string src =
                "class Widget {\n" +
                "public:\n" +
                "    void run();\n" +
                "};\n" +
                "\n" +
                "struct Panel {\n" +
                "    int Widget() const;\n" +
                "};\n" +
                "\n" +
                "void go(Panel* q) {\n" +
                "    Panel p;\n" +
                "    int a = p.Widget();\n" +
                "    int b = q->Widget();\n" +
                "    Widget w;\n" +
                "    w.run();\n" +
                "}\n";

            CppIdentifierRewriter.RewriteResult r = CppIdentifierRewriter.Rewrite(src, "Widget", "Gadget");

            StringAssert.Contains(r.Text, "class Gadget {");
            StringAssert.Contains(r.Text, "    Gadget w;");
            StringAssert.Contains(r.Text, "    int Widget() const;");   // declaration on Panel
            StringAssert.Contains(r.Text, "    int a = p.Widget();");   // '.' access
            StringAssert.Contains(r.Text, "    int b = q->Widget();");  // '->' access
            Assert.AreEqual(2, r.Count);
        }

        [TestMethod]
        public void LeavesStringsAndCommentsAlone()
        {
            const string src =
                "// Widget is documented here.\n" +
                "class Widget {\n" +
                "public:\n" +
                "    const char* label() const { return \"Widget\"; }\n" +
                "};\n";

            CppIdentifierRewriter.RewriteResult r = CppIdentifierRewriter.Rewrite(src, "Widget", "Gadget");

            StringAssert.Contains(r.Text, "// Widget is documented here.");
            StringAssert.Contains(r.Text, "return \"Widget\";");
            StringAssert.Contains(r.Text, "class Gadget {");
            Assert.AreEqual(1, r.Count);
        }

        [TestMethod]
        public void FindOccurrencesAgreesWithRewrite()
        {
            const string src =
                "class Widget {\n" +
                "public:\n" +
                "    Widget();\n" +
                "};\n" +
                "void build() { Widget w; }\n";

            var spans = CppIdentifierRewriter.FindOccurrences(src, "Widget");
            CppIdentifierRewriter.RewriteResult r = CppIdentifierRewriter.Rewrite(src, "Widget", "Gadget");

            Assert.AreEqual(r.Count, spans.Count, "Find All References and Rename must never disagree");
            foreach (var s in spans)
                Assert.AreEqual("Widget", src.Substring(s.start, s.end - s.start));
        }

        // ---- service: scope + write-back --------------------------------------

        [TestMethod]
        public void FileScopeTouchesOnlyTheDeclaringFile()
        {
            string dir;
            ProjectModel project = MakeProject(out dir);
            string header = Write(dir, "widget.hpp", "class Widget {\npublic:\n    Widget();\n};\n");
            string other = Write(dir, "user.cpp", "#include \"widget.hpp\"\nvoid f() { Widget w; }\n");

            TypeModel type = TypeIn(project, "Widget", header);
            RenameResultAssert(new CppSymbolRenameService()
                .RenameType(project.Solution, type, "Gadget", CppRenameScope.File), 1);

            StringAssert.Contains(File.ReadAllText(header), "class Gadget {");
            StringAssert.Contains(File.ReadAllText(other), "Widget w;");   // untouched
        }

        [TestMethod]
        public void ProjectScopeRewritesASecondFileOnDisk()
        {
            string dir;
            ProjectModel project = MakeProject(out dir);
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            string header = Write(dir, "shapes.hpp",
                "class Widget {\npublic:\n    Widget();\n    ~Widget();\n};\n");
            string user = Write(Path.Combine(dir, "src"), "user.cpp",
                "#include \"shapes.hpp\"\n" +
                "Widget make() {\n" +
                "    Widget w;\n" +
                "    return w;\n" +
                "}\n");

            TypeModel type = TypeIn(project, "Widget", header);
            var result = new CppSymbolRenameService()
                .RenameType(project.Solution, type, "Gadget", CppRenameScope.Project);

            Assert.AreEqual(2, result.FilesChanged);
            Assert.AreEqual(5, result.TotalHits);

            string headerText = File.ReadAllText(header);
            StringAssert.Contains(headerText, "class Gadget {");
            StringAssert.Contains(headerText, "    Gadget();");
            StringAssert.Contains(headerText, "    ~Gadget();");

            string userText = File.ReadAllText(user);
            StringAssert.Contains(userText, "Gadget make() {");
            StringAssert.Contains(userText, "    Gadget w;");
            Assert.IsFalse(userText.Contains("Widget w"), "the second file must actually be rewritten on disk");
        }

        [TestMethod]
        public void SolutionScopeReachesASecondProject()
        {
            string dirA;
            ProjectModel a = MakeProject(out dirA, "lib_core");
            string dirB;
            ProjectModel b = AddProject(a.Solution, out dirB, "app_cli");

            string header = Write(dirA, "shapes.hpp", "class Widget {\npublic:\n    Widget();\n};\n");
            string appMain = Write(dirB, "main.cpp",
                "#include \"shapes.hpp\"\nint main() { Widget w; return 0; }\n");

            TypeModel type = TypeIn(a, "Widget", header);
            var result = new CppSymbolRenameService()
                .RenameType(a.Solution, type, "Gadget", CppRenameScope.Solution);

            Assert.AreEqual(2, result.FilesChanged);
            StringAssert.Contains(File.ReadAllText(appMain), "Gadget w;");
            Assert.IsNotNull(b);
        }

        [TestMethod]
        public void ProjectScopeSkipsCMakeBuildTrees()
        {
            string dir;
            ProjectModel project = MakeProject(out dir);
            string header = Write(dir, "shapes.hpp", "class Widget {\npublic:\n    Widget();\n};\n");
            string buildDir = Path.Combine(dir, "cmake-build-debug", "CMakeFiles");
            Directory.CreateDirectory(buildDir);
            string generated = Write(buildDir, "probe.cpp", "int main() { Widget w; return 0; }\n");

            TypeModel type = TypeIn(project, "Widget", header);
            new CppSymbolRenameService().RenameType(project.Solution, type, "Gadget", CppRenameScope.Project);

            StringAssert.Contains(File.ReadAllText(header), "class Gadget {");
            StringAssert.Contains(File.ReadAllText(generated), "Widget w;");   // generated tree untouched
        }

        [TestMethod]
        public void RenamesDeclaringFileWhenItsStemMatches()
        {
            string dir;
            ProjectModel project = MakeProject(out dir);
            string header = Write(dir, "Widget.hpp", "class Widget {\npublic:\n    Widget();\n};\n");

            TypeModel type = TypeIn(project, "Widget", header);
            new CppSymbolRenameService().RenameType(project.Solution, type, "Gadget", CppRenameScope.File);

            string renamed = Path.Combine(dir, "Gadget.hpp");
            Assert.IsTrue(File.Exists(renamed), "the declaring file should be renamed with the class");
            Assert.IsFalse(File.Exists(header));
            Assert.AreEqual(renamed, type.SourceFilePath);
            StringAssert.Contains(File.ReadAllText(renamed), "class Gadget {");
        }

        [TestMethod]
        public void RenamesFreeFunctionAcrossTheProject()
        {
            string dir;
            ProjectModel project = MakeProject(out dir);
            string header = Write(dir, "util.hpp", "int total_area();\n");
            string impl = Write(dir, "util.cpp",
                "#include \"util.hpp\"\n" +
                "int total_area() { return 7; }\n" +
                "int caller() {\n" +
                "    int total_area = 3;\n" +   // shadows for this whole function
                "    return total_area;\n" +
                "}\n" +
                "int other() { return total_area(); }\n");

            FunctionModel fn = new FunctionModel
            {
                Name = "total_area",
                Namespace = string.Empty,
                SourceFilePath = header,
                Project = project,
            };
            project.Functions.Add(fn);

            var result = new CppSymbolRenameService()
                .RenameFunction(project.Solution, fn, "sum_area", CppRenameScope.Project);

            Assert.AreEqual(2, result.FilesChanged);
            StringAssert.Contains(File.ReadAllText(header), "int sum_area();");

            string implText = File.ReadAllText(impl);
            StringAssert.Contains(implText, "int sum_area() { return 7; }");
            StringAssert.Contains(implText, "int other() { return sum_area(); }");
            // The shadowing local keeps its name — the whole of caller() is off limits.
            StringAssert.Contains(implText, "    int total_area = 3;");
            StringAssert.Contains(implText, "    return total_area;");
        }

        // ---- member rename ----------------------------------------------------

        [TestMethod]
        public void RenamesMemberAcrossHeaderAndOutOfLineDefinition()
        {
            string dir;
            ProjectModel project = MakeProject(out dir);
            string header = Write(dir, "shapes.hpp",
                "class Circle {\n" +
                "public:\n" +
                "    double area() const;\n" +
                "    double doubled() const { return area() * 2.0; }\n" +
                "};\n" +
                "\n" +
                "class Ring {\n" +
                "public:\n" +
                "    double area() const;\n" +   // same member name on an unrelated class
                "};\n");
            string impl = Write(dir, "shapes.cpp",
                "#include \"shapes.hpp\"\n" +
                "double Circle::area() const { return 3.0; }\n" +
                "double Ring::area() const { return 4.0; }\n");

            TypeModel circle = TypeIn(project, "Circle", header);
            MemberModel member = new MemberModel("area", MemberKind.Method);
            circle.Members.Add(member);

            var result = new CppSymbolRenameService()
                .RenameMember(project.Solution, circle, member, "surface");

            Assert.AreEqual(2, result.FilesChanged);

            string headerText = File.ReadAllText(header);
            StringAssert.Contains(headerText, "    double surface() const;");
            StringAssert.Contains(headerText, "return surface() * 2.0;");
            // Ring::area is a different class's member and must survive.
            StringAssert.Contains(headerText, "class Ring {\npublic:\n    double area() const;");

            string implText = File.ReadAllText(impl);
            StringAssert.Contains(implText, "double Circle::surface() const");
            StringAssert.Contains(implText, "double Ring::area() const");
        }

        [TestMethod]
        public void MemberRenameLeavesForeignObjectAccessAlone()
        {
            // Documented parity limitation, identical to PythonMemberRewriter: only the owning
            // class's body, `this->member`, and `Owner::member` are rewritten. `c.area()` on a
            // Circle-typed variable would need type inference, which neither engine does.
            const string src =
                "class Circle {\n" +
                "public:\n" +
                "    double area() const;\n" +
                "    double twice() const { return this->area() * 2.0; }\n" +
                "};\n" +
                "double total(const Circle& c) { return c.area(); }\n";

            CppIdentifierRewriter.RewriteResult r =
                CppIdentifierRewriter.RewriteMember(src, "Circle", "area", "surface");

            StringAssert.Contains(r.Text, "    double surface() const;");
            StringAssert.Contains(r.Text, "return this->surface() * 2.0;");
            StringAssert.Contains(r.Text, "return c.area();");
            Assert.AreEqual(2, r.Count);
        }

        // ---- real fixture ------------------------------------------------------

        [TestMethod]
        public void RenamesAgainstRealFixtureShapesHeader()
        {
            // The same hand-written C++23 fixture CppTypeExtractorTests uses, so this is not a
            // synthetic snippet crafted to fit the rewriter.
            string repoRoot = FindRepoRoot();
            string shapesHeader = Path.Combine(repoRoot, "tests", "fixtures", "cuda",
                "CudaCppMultiPackageWorkspace", "lib_core", "include", "lib_core", "shapes.cuh");
            Assert.IsTrue(File.Exists(shapesHeader), "fixture file not found: " + shapesHeader);

            string src = File.ReadAllText(shapesHeader);
            int rawMentions = CountWholeWord(src, "Rectangle");
            Assert.IsTrue(rawMentions > 1, "fixture should mention Rectangle more than once");

            CppIdentifierRewriter.RewriteResult r = CppIdentifierRewriter.Rewrite(src, "Rectangle", "Oblong");

            Assert.IsTrue(r.Count > 0, "expected at least the declaration and the Square base clause");
            StringAssert.Contains(r.Text, "class Oblong");
            StringAssert.Contains(r.Text, "public Oblong");   // `class Square : public Rectangle`
            // Nothing outside identifier positions may move: the file must keep its exact length
            // apart from the renamed tokens.
            Assert.AreEqual(src.Length + r.Count * ("Oblong".Length - "Rectangle".Length), r.Text.Length);
        }

        [TestMethod]
        public void RoundTripRenameOverTheWholeFixtureIsByteIdentical()
        {
            // Rename forward then back over every real fixture translation unit and require the
            // result to be byte-identical to the original. This is the strongest corruption check
            // available without a compiler: any span computed even one character wrong, any
            // duplicated or dropped occurrence, and any instability in the shadowing decision
            // (which must resolve identically before and after) shows up here as a diff, on
            // genuine hand-written C++23 with concepts, ranges, lambdas and templates.
            string repoRoot = FindRepoRoot();
            string baseDir = Path.Combine(repoRoot, "tests", "fixtures", "cuda",
                "CudaCppMultiPackageWorkspace");
            Assert.IsTrue(Directory.Exists(baseDir), "fixture source tree not found: " + baseDir);

            // .cuh/.cu rather than .hpp/.cpp: the retained fixture is the CUDA-flavoured copy of the
            // same hand-written C++23. The rewriter is extension-agnostic, so this is the same test.
            // The generated cmake-build-* tree is excluded — rewriting machine output proves nothing.
            string[] files = Directory.GetFiles(baseDir, "*.cuh", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(baseDir, "*.cu", SearchOption.AllDirectories))
                .Where(f => f.IndexOf("cmake-build-", StringComparison.OrdinalIgnoreCase) < 0)
                .ToArray();
            Assert.IsTrue(files.Length > 10, "expected a real multi-file fixture, got " + files.Length);

            string[] symbols = { "Animal", "Rectangle", "Logger", "feed", "area" };
            int touched = 0;
            foreach (string f in files)
            {
                string src = File.ReadAllText(f);
                foreach (string sym in symbols)
                {
                    CppIdentifierRewriter.RewriteResult fwd = CppIdentifierRewriter.Rewrite(src, sym, "Zz" + sym + "Zz");
                    if (fwd.Count == 0) continue;
                    touched++;
                    CppIdentifierRewriter.RewriteResult back =
                        CppIdentifierRewriter.Rewrite(fwd.Text, "Zz" + sym + "Zz", sym);
                    Assert.AreEqual(fwd.Count, back.Count,
                        "round-trip occurrence count differs for '" + sym + "' in " + f);
                    Assert.AreEqual(src, back.Text,
                        "round-trip must be byte-identical for '" + sym + "' in " + f);
                }
            }
            Assert.IsTrue(touched > 5, "expected the fixture to exercise several real renames, got " + touched);
        }

        private static int CountWholeWord(string text, string word)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(word, i, StringComparison.Ordinal)) >= 0)
            {
                bool leftOk = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
                int after = i + word.Length;
                bool rightOk = after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
                if (leftOk && rightOk) n++;
                i = after;
            }
            return n;
        }

        private static void RenameResultAssert(HelixPythonNamespace.Workspace.Services.RenameResult r, int expectedFiles)
        {
            Assert.AreEqual(expectedFiles, r.FilesChanged);
        }

        private static string FindRepoRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && Directory.GetFiles(d.FullName, "*.sln").Length == 0)
                d = d.Parent;
            Assert.IsNotNull(d, "could not locate repo root (the product .sln)");
            return d.FullName;
        }
    }
}
