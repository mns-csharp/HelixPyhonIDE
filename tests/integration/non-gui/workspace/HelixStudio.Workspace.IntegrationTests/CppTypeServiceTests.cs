using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Scaffolding tests for <see cref="CppTypeService"/> / <see cref="CppSourceEmitter"/>.
    ///
    /// Every test asserts on the LITERAL text written to disk, and the round-trip tests then feed
    /// that same file back through <see cref="CppTypeExtractor"/> — the sibling engine that reads
    /// real C++ — into a fresh <see cref="ProjectModel"/>. Passing both halves means the emitter and
    /// the parser genuinely agree on what valid C++ looks like, not merely that some text got
    /// written.
    /// </summary>
    [TestClass]
    public class CppTypeServiceTests
    {
        private static readonly List<string> TempDirs = new List<string>();

        [ClassCleanup]
        public static void Cleanup()
        {
            foreach (string d in TempDirs)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            }
        }

        /// <summary>
        /// Same shape as CppTypeExtractorTests.MakeProject: a CMake target is modelled with a
        /// synthetic "&lt;target&gt;.cmake-target" marker file path, so ProjectDirectory resolves to
        /// the real folder that would hold the target's CMakeLists.txt.
        /// </summary>
        private static ProjectModel MakeProject(string targetName = "lib_core")
        {
            string temp = Path.Combine(Path.GetTempPath(), "helix_cppsvc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            TempDirs.Add(temp);
            string marker = Path.Combine(temp, targetName + ".cmake-target");
            SolutionModel sln = new SolutionModel(Path.Combine(temp, "CMakeLists.txt"), "t");
            ProjectModel project = new ProjectModel(marker, targetName, sln);
            project.Language = ProjectLanguage.Cpp;
            sln.Projects.Add(project);
            return project;
        }

        /// <summary>Re-parse a written file into a brand-new project — the round-trip proof.</summary>
        private static ProjectModel Reparse(string filePath)
        {
            ProjectModel fresh = MakeProject("reparse_target");
            CppTypeExtractor.ExtractFromFile(fresh, filePath);
            return fresh;
        }

        private static TypeModel FindType(ProjectModel p, string name) =>
            p.Types.FirstOrDefault(t => t.Name == name);

        private static MemberModel Method(TypeModel t, string name) =>
            t.Members.FirstOrDefault(m => m.Name == name);

        // ================================================================ layout

        [TestMethod]
        public void UsesFixtureIncludeSrcLayout()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            TypeModel t = svc.AddClass(p, "Widget");

            string dir = p.ProjectDirectory;
            string expectedHeader = Path.Combine(dir, "include", "lib_core", "Widget.hpp");
            string expectedSource = Path.Combine(dir, "src", "Widget.cpp");

            Assert.AreEqual(expectedHeader, t.SourceFilePath);
            Assert.IsTrue(File.Exists(expectedHeader), "header not written: " + expectedHeader);
            Assert.IsTrue(File.Exists(expectedSource), "source not written: " + expectedSource);
            // The .cpp includes its header exactly the way the fixture's src/errors.cpp does.
            StringAssert.Contains(File.ReadAllText(expectedSource), "#include \"lib_core/Widget.hpp\"");
        }

        // ================================================================ AddClass

        [TestMethod]
        public void AddClassWritesHeaderAndSourceWithRealContent()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            TypeModel t = svc.AddClass(p, "Widget");

            string header = File.ReadAllText(t.SourceFilePath);
            StringAssert.StartsWith(header, "#pragma once");
            StringAssert.Contains(header, "namespace lib_core {");
            StringAssert.Contains(header, "class Widget {");
            StringAssert.Contains(header, "public:");
            StringAssert.Contains(header, "Widget();");
            StringAssert.Contains(header, "};");
            StringAssert.Contains(header, "}  // namespace lib_core");
            // A plain concrete class with no bases and no virtual members declares no destructor —
            // matching the fixture's Circle/Rectangle, which declare none either.
            Assert.IsFalse(header.Contains("~Widget"), "plain class should not declare a destructor");

            string source = File.ReadAllText(CppTypeService.ComputeSourcePath(p, "Widget"));
            StringAssert.Contains(source, "Widget::Widget() {");
            StringAssert.Contains(source, "namespace lib_core {");

            Assert.AreEqual(1, p.Types.Count);
            Assert.AreEqual(TypeKind.ConcreteClass, t.TypeKind);
            Assert.AreEqual("lib_core", t.Namespace);
        }

        [TestMethod]
        public void AddClassRoundTripsThroughCppTypeExtractor()
        {
            ProjectModel p = MakeProject();
            TypeModel t = new CppTypeService().AddClass(p, "Widget");

            ProjectModel fresh = Reparse(t.SourceFilePath);
            TypeModel re = FindType(fresh, "Widget");
            Assert.IsNotNull(re, "emitted header did not re-parse into a type");
            Assert.AreEqual(TypeKind.ConcreteClass, re.TypeKind);
            Assert.AreEqual("lib_core", re.Namespace);
            Assert.AreEqual("lib_core.Widget", re.FullyQualifiedName);
            Assert.AreEqual(0, re.BaseTypes.Count);
            Assert.AreEqual(1, re.Members.Count, "expected just the generated constructor");
            Assert.AreEqual(MemberKind.Constructor, re.Members[0].MemberKind);
            Assert.AreEqual(AccessModifier.Public, re.Members[0].AccessModifier);
        }

        [TestMethod]
        public void AddClassWithMembersGroupsByAccessAndRoundTrips()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            TypeModel t = svc.AddClass(p, "Counter");

            MemberModel inc = new MemberModel("increment", MemberKind.Method) { ReturnType = "void" };
            MemberModel value = new MemberModel("value", MemberKind.Method) { ReturnType = "int" };
            MemberModel count = new MemberModel("count_", MemberKind.Field)
            { ReturnType = "int", AccessModifier = AccessModifier.Private };
            MemberModel limit = new MemberModel("limit_", MemberKind.Field)
            { ReturnType = "int", AccessModifier = AccessModifier.Protected };
            t.Members.Add(inc);
            t.Members.Add(value);
            t.Members.Add(count);
            t.Members.Add(limit);
            svc.RewriteType(t);

            string header = File.ReadAllText(t.SourceFilePath);
            // Real access-specifier blocks, in public → protected → private order, with each
            // member under the right one (not one flat list).
            int pub = header.IndexOf("public:", StringComparison.Ordinal);
            int prot = header.IndexOf("protected:", StringComparison.Ordinal);
            int priv = header.IndexOf("private:", StringComparison.Ordinal);
            Assert.IsTrue(pub >= 0 && prot > pub && priv > prot,
                "expected public: < protected: < private: blocks, got:\n" + header);
            Assert.IsTrue(header.IndexOf("void increment();", StringComparison.Ordinal) > pub);
            Assert.IsTrue(header.IndexOf("int limit_;", StringComparison.Ordinal) > prot);
            Assert.IsTrue(header.IndexOf("int count_;", StringComparison.Ordinal) > priv);

            string source = File.ReadAllText(CppTypeService.ComputeSourcePath(p, "Counter"));
            StringAssert.Contains(source, "void Counter::increment() {");
            StringAssert.Contains(source, "int Counter::value() {");
            StringAssert.Contains(source, "return {};");

            ProjectModel fresh = Reparse(t.SourceFilePath);
            TypeModel re = FindType(fresh, "Counter");
            Assert.IsNotNull(re);
            // ctor + increment + value + limit_ + count_
            Assert.AreEqual(5, re.Members.Count);
            Assert.AreEqual(AccessModifier.Private, Method(re, "count_").AccessModifier);
            Assert.AreEqual(AccessModifier.Protected, Method(re, "limit_").AccessModifier);
            Assert.AreEqual(AccessModifier.Public, Method(re, "increment").AccessModifier);
            Assert.AreEqual("int", Method(re, "value").ReturnType);
        }

        // ================================================================ AddInterface

        [TestMethod]
        public void AddInterfaceEmitsPureVirtualsAndNoSourceFile()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            TypeModel t = svc.AddInterface(p, "ShapeBase");
            t.Members.Add(new MemberModel("area", MemberKind.Method) { ReturnType = "double" });
            t.Members.Add(new MemberModel("perimeter", MemberKind.Method) { ReturnType = "double" });
            svc.RewriteType(t);

            string header = File.ReadAllText(t.SourceFilePath);
            StringAssert.Contains(header, "class ShapeBase {");
            StringAssert.Contains(header, "public:");
            StringAssert.Contains(header, "virtual ~ShapeBase() = default;");
            StringAssert.Contains(header, "virtual double area() = 0;");
            StringAssert.Contains(header, "virtual double perimeter() = 0;");
            // No constructor on an interface, and nothing needs an out-of-line body, so writing a
            // .cpp would produce an empty translation unit — exactly like the fixture's ShapeBase.
            Assert.IsFalse(header.Contains("ShapeBase();"), "interface should declare no constructor");
            Assert.IsFalse(File.Exists(CppTypeService.ComputeSourcePath(p, "ShapeBase")),
                "pure interface must not get a .cpp");
        }

        [TestMethod]
        public void AddInterfaceRoundTripsThroughCppTypeExtractor()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            TypeModel t = svc.AddInterface(p, "ShapeBase");
            t.Members.Add(new MemberModel("area", MemberKind.Method) { ReturnType = "double" });
            t.Members.Add(new MemberModel("perimeter", MemberKind.Method) { ReturnType = "double" });
            svc.RewriteType(t);

            ProjectModel fresh = Reparse(t.SourceFilePath);
            TypeModel re = FindType(fresh, "ShapeBase");
            Assert.IsNotNull(re, "emitted interface header did not re-parse");
            // CppTypeExtractor deliberately never infers Interface/AbstractClass from source (see
            // its class doc-comment) — the round-trip proves the DECLARATIONS survive, and the
            // Interface kind is carried by the in-memory model the service created.
            Assert.AreEqual(TypeKind.ConcreteClass, re.TypeKind);
            Assert.IsNotNull(Method(re, "area"), "area() lost in round-trip");
            Assert.IsNotNull(Method(re, "perimeter"), "perimeter() lost in round-trip");
            Assert.IsNotNull(Method(re, "~ShapeBase"), "virtual destructor lost in round-trip");
            Assert.AreEqual("double", Method(re, "area").ReturnType);
            Assert.AreEqual(3, re.Members.Count);
        }

        // ================================================================ AddAbstractClass

        [TestMethod]
        public void AddAbstractClassMakesOnlyExplicitlyAbstractMembersPureVirtual()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            TypeModel t = svc.AddClass(p, "Repository", ns: null, isAbstract: true);
            t.Members.Add(new MemberModel("save", MemberKind.Method) { ReturnType = "void", IsAbstract = true });
            t.Members.Add(new MemberModel("flush", MemberKind.Method) { ReturnType = "void" });
            svc.RewriteType(t);

            Assert.AreEqual(TypeKind.AbstractClass, t.TypeKind);
            string header = File.ReadAllText(t.SourceFilePath);
            StringAssert.Contains(header, "class Repository {");
            StringAssert.Contains(header, "Repository();");
            StringAssert.Contains(header, "virtual ~Repository() = default;");
            StringAssert.Contains(header, "virtual void save() = 0;");
            // Not marked abstract -> an ordinary method with a real body, which is precisely what
            // separates an abstract class from an interface.
            StringAssert.Contains(header, "void flush();");
            Assert.IsFalse(header.Contains("void flush() = 0;"));

            // A body-having constructor plus the non-abstract method DO need a .cpp.
            string sourcePath = CppTypeService.ComputeSourcePath(p, "Repository");
            Assert.IsTrue(File.Exists(sourcePath), "abstract class with bodies should get a .cpp");
            string source = File.ReadAllText(sourcePath);
            StringAssert.Contains(source, "Repository::Repository() {");
            StringAssert.Contains(source, "void Repository::flush() {");
            Assert.IsFalse(source.Contains("Repository::save"), "pure-virtual member must have no definition");

            ProjectModel fresh = Reparse(t.SourceFilePath);
            TypeModel re = FindType(fresh, "Repository");
            Assert.IsNotNull(re);
            Assert.AreEqual(4, re.Members.Count, "ctor + dtor + save + flush");
            Assert.IsNotNull(Method(re, "save"));
            Assert.IsNotNull(Method(re, "flush"));
        }

        // ================================================================ AddStruct

        [TestMethod]
        public void AddStructEmitsStructKeywordAndRoundTrips()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            TypeModel t = svc.AddStruct(p, "Point3D");
            t.Members.Add(new MemberModel("x", MemberKind.Field) { ReturnType = "double" });
            t.Members.Add(new MemberModel("y", MemberKind.Field) { ReturnType = "double" });
            t.Members.Add(new MemberModel("id_", MemberKind.Field)
            { ReturnType = "int", AccessModifier = AccessModifier.Private });
            svc.RewriteType(t);

            string header = File.ReadAllText(t.SourceFilePath);
            StringAssert.Contains(header, "struct Point3D {");
            StringAssert.Contains(header, "double x;");
            StringAssert.Contains(header, "double y;");
            StringAssert.Contains(header, "private:");
            StringAssert.Contains(header, "int id_;");
            // A struct's leading group is public by definition, so no redundant label — the same
            // way the fixture writes Point3D / Vector.
            Assert.IsTrue(header.IndexOf("public:", StringComparison.Ordinal) < 0,
                "struct's leading public group should carry no redundant label");

            ProjectModel fresh = Reparse(t.SourceFilePath);
            TypeModel re = FindType(fresh, "Point3D");
            Assert.IsNotNull(re);
            Assert.AreEqual(TypeKind.Struct, re.TypeKind);
            Assert.AreEqual(4, re.Members.Count, "ctor + x + y + id_");
            Assert.AreEqual(AccessModifier.Public, Method(re, "x").AccessModifier);
            Assert.AreEqual(AccessModifier.Private, Method(re, "id_").AccessModifier);
            Assert.AreEqual("double", Method(re, "y").ReturnType);
        }

        // ================================================================ base classes

        [TestMethod]
        public void AddClassWithBaseEmitsPublicInheritanceAndRoundTrips()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            TypeModel t = svc.AddClass(p, "Square", ns: null, isAbstract: false, baseTypeName: "Rectangle");

            string header = File.ReadAllText(t.SourceFilePath);
            StringAssert.Contains(header, "class Square : public Rectangle {");
            // The base already owns the virtual destructor; a derived scaffold re-declaring one
            // would be noise (the fixture's Square/Circle/Star declare none).
            Assert.IsFalse(header.Contains("~Square"));

            ProjectModel fresh = Reparse(t.SourceFilePath);
            TypeModel re = FindType(fresh, "Square");
            Assert.IsNotNull(re);
            Assert.AreEqual(1, re.BaseTypes.Count, "base clause lost in round-trip");
            Assert.AreEqual("Rectangle", re.BaseTypes[0].FullyQualifiedName);
            Assert.AreEqual(RelationshipKind.Inherits, re.BaseTypes[0].RelationshipKind);
        }

        [TestMethod]
        public void AddClassWithNamespacedBaseQualifiesWithScopeOperator()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            // Model namespaces are dot-separated; C++ needs '::'.
            TypeModel t = svc.AddClass(p, "Square", ns: "lib_core.shapes", isAbstract: false,
                baseTypeName: "lib_core.Rectangle");

            string header = File.ReadAllText(t.SourceFilePath);
            StringAssert.Contains(header, "namespace lib_core {");
            StringAssert.Contains(header, "namespace shapes {");
            StringAssert.Contains(header, "class Square : public lib_core::Rectangle {");
            Assert.IsFalse(header.Contains("namespace lib_core::shapes"),
                "nested blocks only — the CudaVirtualCompiler parser rejects C++17 nested-namespace syntax");

            ProjectModel fresh = Reparse(t.SourceFilePath);
            TypeModel re = FindType(fresh, "Square");
            Assert.IsNotNull(re);
            Assert.AreEqual("lib_core.shapes", re.Namespace);
            Assert.AreEqual(1, re.BaseTypes.Count);
            Assert.AreEqual("lib_core::Rectangle", re.BaseTypes[0].FullyQualifiedName);
        }

        // ================================================================ enum

        [TestMethod]
        public void AddEnumEmitsScopedEnumHeaderOnly()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            TypeModel t = svc.AddEnum(p, "Severity", null, new[] { "Info", "Warning", "Error" });

            string header = File.ReadAllText(t.SourceFilePath);
            StringAssert.Contains(header, "enum class Severity {");
            StringAssert.Contains(header, "Info,");
            StringAssert.Contains(header, "Warning,");
            StringAssert.Contains(header, "Error,");
            Assert.IsFalse(File.Exists(CppTypeService.ComputeSourcePath(p, "Severity")),
                "an enum needs no translation unit");
        }

        // ================================================================ includes

        [TestMethod]
        public void InfersStandardLibraryIncludesFromMemberTypes()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            TypeModel t = svc.AddClass(p, "Registry");
            MemberModel find = new MemberModel("find", MemberKind.Method) { ReturnType = "std::string" };
            find.Parameters.Add(new MethodParameterModel("keys", "const std::vector<std::string>&"));
            t.Members.Add(find);
            svc.RewriteType(t);

            string header = File.ReadAllText(t.SourceFilePath);
            StringAssert.Contains(header, "#include <string>");
            StringAssert.Contains(header, "#include <vector>");
            StringAssert.Contains(header, "std::string find(const std::vector<std::string>& keys);");
        }

        // ================================================================ RemoveType

        [TestMethod]
        public void RemoveTypeDeletesBothFilesAndModelEntry()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            TypeModel t = svc.AddClass(p, "Widget");
            string header = t.SourceFilePath;
            string source = CppTypeService.ComputeSourcePath(p, "Widget");
            Assert.IsTrue(File.Exists(header) && File.Exists(source));

            svc.RemoveType(p, t);

            Assert.AreEqual(0, p.Types.Count);
            Assert.IsFalse(File.Exists(header), "header should be deleted");
            Assert.IsFalse(File.Exists(source), "source should be deleted");
        }

        // ================================================================ real MSVC compile

        /// <summary>
        /// The strongest available proof: hand the emitted files to the real MSVC toolchain.
        /// <see cref="CppTypeExtractor"/> agreeing with the emitter only shows both sides of THIS
        /// codebase's coarse parser agree; cl.exe accepting the same text shows it is genuinely
        /// valid, compilable C++23.
        ///
        /// Compiles the whole scaffolded target — a pure interface, a derived concrete class that
        /// overrides it, a struct and a scoped enum — plus a probe TU that includes every header
        /// and actually uses the types through a base pointer, so a header that merely parses but
        /// does not semantically work would still fail here. Compile-only (/c); the scaffolds have
        /// declared-but-unimplemented bodies by design, so there is nothing to link.
        ///
        /// NOTE: plain /std:c++23 is REJECTED by this MSVC; the accepted spelling is
        /// /std:c++23preview.
        /// </summary>
        [TestMethod]
        public void GeneratedCodeCompilesWithRealMsvcCpp23()
        {
            string vcvars = FindVcVars();
            if (vcvars == null)
            {
                Assert.Inconclusive("MSVC (vcvars64.bat) not present on this machine.");
                return;
            }

            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();

            TypeModel shapeBase = svc.AddInterface(p, "ShapeBase");
            shapeBase.Members.Add(new MemberModel("area", MemberKind.Method) { ReturnType = "double" });
            shapeBase.Members.Add(new MemberModel("perimeter", MemberKind.Method) { ReturnType = "double" });
            svc.RewriteType(shapeBase);

            TypeModel circle = svc.AddClass(p, "Circle", ns: null, isAbstract: false, baseTypeName: "ShapeBase");
            circle.Members.Add(new MemberModel("area", MemberKind.Method) { ReturnType = "double", IsOverride = true });
            circle.Members.Add(new MemberModel("perimeter", MemberKind.Method) { ReturnType = "double", IsOverride = true });
            circle.Members.Add(new MemberModel("radius_", MemberKind.Field)
            { ReturnType = "double", AccessModifier = AccessModifier.Private });
            svc.RewriteType(circle);

            TypeModel point = svc.AddStruct(p, "Point3D");
            point.Members.Add(new MemberModel("x", MemberKind.Field) { ReturnType = "double" });
            point.Members.Add(new MemberModel("y", MemberKind.Field) { ReturnType = "double" });
            svc.RewriteType(point);

            svc.AddEnum(p, "Severity", null, new[] { "Info", "Warning", "Error" });

            // The derived class must pull in its base's header on its own — otherwise this
            // wouldn't compile at all.
            StringAssert.Contains(File.ReadAllText(circle.SourceFilePath), "#include \"lib_core/ShapeBase.hpp\"");

            string probe = Path.Combine(p.ProjectDirectory, "src", "probe.cpp");
            File.WriteAllText(probe, string.Join("\n", new[]
            {
                "#include \"lib_core/Circle.hpp\"",
                "#include \"lib_core/Point3D.hpp\"",
                "#include \"lib_core/Severity.hpp\"",
                "#include \"lib_core/ShapeBase.hpp\"",
                "",
                "double probe(lib_core::Circle& c, lib_core::Point3D& pt) {",
                "    lib_core::ShapeBase* s = &c;   // requires a real base relationship",
                "    lib_core::Severity sev = lib_core::Severity::Warning;",
                "    return s->area() + s->perimeter() + pt.x + pt.y + static_cast<double>(sev);",
                "}",
                "",
            }));

            string[] sources = Directory.GetFiles(Path.Combine(p.ProjectDirectory, "src"), "*.cpp");
            string includeDir = Path.Combine(p.ProjectDirectory, "include");
            string objDir = Path.Combine(p.ProjectDirectory, "obj");
            Directory.CreateDirectory(objDir);

            var args = new System.Text.StringBuilder();
            args.Append("/std:c++23preview /EHsc /nologo /TP /W3 /c ");
            args.Append("/I\"").Append(includeDir).Append("\" ");
            args.Append("/Fo:\"").Append(objDir.TrimEnd('\\')).Append("\\\\\" ");
            foreach (string s in sources) args.Append('"').Append(s).Append("\" ");

            string log;
            int exit = RunCmd("\"\"" + vcvars + "\" && cl.exe " + args.ToString().Trim() + "\"", out log);
            Assert.AreEqual(0, exit, "cl.exe rejected the generated C++23:\n" + log);
            // Guard against MSVC silently falling back to an older standard on a bad /std value.
            Assert.IsFalse(log.Contains("D9002"), "cl.exe ignored an unknown option:\n" + log);
        }

        private static string FindVcVars()
        {
            string[] candidates =
            {
                @"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat",
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
            };
            foreach (string c in candidates) if (File.Exists(c)) return c;
            string install = HelixPythonNamespace.Workspace.PyToCpp.MsvcToolchain.LocateVsInstall();
            if (string.IsNullOrEmpty(install)) return null;
            string vc = Path.Combine(install, @"VC\Auxiliary\Build\vcvars64.bat");
            return File.Exists(vc) ? vc : null;
        }

        private static int RunCmd(string command, out string log)
        {
            var sb = new System.Text.StringBuilder();
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                log = sb.ToString();
                return proc.ExitCode;
            }
        }

        [TestMethod]
        public void RewritingAsInterfaceDropsTheNowUnneededSourceFile()
        {
            ProjectModel p = MakeProject();
            CppTypeService svc = new CppTypeService();
            TypeModel t = svc.AddClass(p, "Handler");
            string source = CppTypeService.ComputeSourcePath(p, "Handler");
            Assert.IsTrue(File.Exists(source));

            t.TypeKind = TypeKind.Interface;
            svc.RewriteType(t);

            Assert.IsFalse(File.Exists(source),
                "stale .cpp would still be compiled by the CMake target");
            StringAssert.Contains(File.ReadAllText(t.SourceFilePath), "virtual ~Handler() = default;");
        }
    }
}
