using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.CudaVirtualCompiler.Ast;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>
    /// Walks a <see cref="TranslationUnit"/> produced by <see cref="HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler"/>
    /// and produces <see cref="TypeModel"/>/<see cref="FunctionModel"/> instances attached to the
    /// given <see cref="ProjectModel"/> — the C++ counterpart to
    /// <see cref="HelixPythonNamespace.Workspace.Python.PythonTypeExtractor"/>, and the piece that lets a
    /// CMake-backed <see cref="ProjectModel"/> populate the same project-tree/context-menu
    /// machinery Python projects already use (see <see cref="ProjectLanguage.Cpp"/>).
    ///
    /// Deliberately NOT auto-detected here: TypeKind.Interface / TypeKind.AbstractClass for
    /// existing classes. The parser doesn't yet capture `virtual`/`= 0` on member functions, and a
    /// method body being absent (`void Foo();`) is the ordinary "declared in the header, defined
    /// out-of-line in the .cpp" C++ style — not a reliable abstractness signal the way Python's
    /// `class X(ABC)` / `class X(Protocol)` markers are. Guessing would misclassify most real
    /// code, so every parsed class/struct/union is a plain ConcreteClass/Struct; only classes
    /// scaffolded through <c>CppTypeService</c> (which know the kind because the user chose it)
    /// ever become Interface/AbstractClass.
    /// </summary>
    public static class CppTypeExtractor
    {
        private static readonly CudaVirtualCompiler.CudaCompileOptions ParseOnlyOptions =
            new CudaVirtualCompiler.CudaCompileOptions
            {
                EnableLexParse = true,
                EnableAstValidator = false,
                EnableSymtablePass = false,
                EnableQualifierSync = false,
                EnableLaunchConfig = false,
                EnableMemorySpace = false,
                EnableLint = false,
                EnableLanguageStandard = false,
                EnableNameResolution = false,
            };

        /// <summary>Parse <paramref name="filePath"/> and extract its types/functions into <paramref name="project"/>.
        /// Swallows parse failures (a syntactically broken file just contributes nothing, mirroring
        /// how a Python file with a syntax error is skipped rather than aborting the whole tree build).</summary>
        public static void ExtractFromFile(ProjectModel project, string filePath)
        {
            if (project == null || string.IsNullOrEmpty(filePath)) return;
            CudaVirtualCompiler.CudaCompileResult result;
            try
            {
                result = CudaVirtualCompiler.CudaVirtualCompiler.CompileFile(filePath, ParseOnlyOptions);
            }
            catch
            {
                result = null;
            }
            if (result != null && result.Unit != null)
                ExtractInto(project, result.Unit, filePath);
            // GoogleTest's TEST/TEST_F/TEST_P are macros the parser cannot see as functions.
            // File nodes in the tree are derived from extracted symbols, so a gtest file with
            // nothing else in it contributed ZERO symbols and was INVISIBLE in Solution
            // Explorer ("I see no scaffold for the unit tests").
            ExtractGoogleTestMacros(project, filePath);
        }

        internal static void ExtractGoogleTestMacros(ProjectModel project, string filePath)
        {
            string text;
            try { text = System.IO.File.ReadAllText(filePath); } catch { return; }
            var re = new System.Text.RegularExpressions.Regex(
                @"^[ \t]*(TEST(?:_F|_P)?)\s*\(\s*(\w+)\s*,\s*(\w+)\s*\)",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            foreach (System.Text.RegularExpressions.Match m in re.Matches(text))
            {
                string display = m.Groups[1].Value + "(" + m.Groups[2].Value + ", " + m.Groups[3].Value + ")";
                bool exists = false;
                foreach (FunctionModel f in project.Functions)
                    if (f.Name == display && f.SourceFilePath == filePath) { exists = true; break; }
                if (exists) continue;

                int line = 1;
                for (int i = 0; i < m.Index && i < text.Length; i++)
                    if (text[i] == '\n') line++;
                project.Functions.Add(new FunctionModel
                {
                    Name = display,
                    Namespace = string.Empty,
                    SourceFilePath = filePath,
                    SourceLineNumber = line,
                    Project = project,
                });
            }
        }

        public static void ExtractInto(ProjectModel project, TranslationUnit unit, string filePath)
        {
            if (project == null || unit == null) return;
            WalkDecls(project, unit.Decls, filePath, ns: string.Empty, parentFqn: null);
        }

        private static void WalkDecls(ProjectModel project, IReadOnlyList<Decl> decls, string filePath,
            string ns, string parentFqn)
        {
            foreach (Decl d in decls)
            {
                if (d is NamespaceDecl nsDecl)
                {
                    string childNs = string.IsNullOrEmpty(ns) ? nsDecl.Name : ns + "." + nsDecl.Name;
                    WalkDecls(project, nsDecl.Decls, filePath, childNs, parentFqn);
                }
                else if (d is TemplateDecl tmpl)
                {
                    AddTemplated(project, tmpl, filePath, ns, parentFqn);
                }
                else if (d is RecordDecl rec)
                {
                    AddRecord(project, rec, null, ns, parentFqn, filePath);
                }
                else if (d is FunctionDecl fn && parentFqn == null)
                {
                    // Top-level (namespace-scope) free function only; member functions are
                    // walked via AddRecord's own member loop instead.
                    project.Functions.Add(new FunctionModel
                    {
                        Name = fn.Name,
                        Namespace = ns,
                        SourceFilePath = filePath,
                        SourceLineNumber = fn.Lineno,
                        Project = project,
                    });
                }
                // VarDecl/EnumDecl/TypeAliasDecl at namespace scope: not surfaced as tree nodes,
                // matching Python (which only ever surfaces classes + functions, never module vars).
            }
        }

        private static void AddTemplated(ProjectModel project, TemplateDecl tmpl, string filePath,
            string ns, string parentFqn)
        {
            if (tmpl.Inner is RecordDecl rec)
            {
                AddRecord(project, rec, tmpl.TypeParams, ns, parentFqn, filePath);
            }
            else if (tmpl.Inner is FunctionDecl fn && parentFqn == null)
            {
                project.Functions.Add(new FunctionModel
                {
                    Name = fn.Name,
                    Namespace = ns,
                    SourceFilePath = filePath,
                    SourceLineNumber = fn.Lineno,
                    Project = project,
                });
            }
        }

        private static void AddRecord(ProjectModel project, RecordDecl rec, IReadOnlyList<string> templateParams,
            string ns, string parentFqn, string filePath)
        {
            // Anonymous struct/union member (`struct { int x; } anon;`) — no name to key a
            // TypeModel on; skip rather than throwing (TypeModel requires a non-empty name).
            if (string.IsNullOrEmpty(rec.Name)) return;

            TypeModel model = BuildTypeModel(project, rec, templateParams, ns, parentFqn, filePath);

            // A bare forward declaration (`class Foo;`) parses to the same empty-Members shape
            // as a genuinely empty definition (`class Foo {};`). Prefer whichever occurrence
            // actually has members; don't let a later forward-declaration stub overwrite an
            // earlier real definition, and don't duplicate the entry either way.
            TypeModel existing = FindExisting(project, model.FullyQualifiedName);
            if (existing != null)
            {
                if (rec.Members.Count == 0) return;
                if (existing.Members.Count > 0) return;
                project.Types.Remove(existing);
            }
            project.Types.Add(model);

            foreach (Decl member in rec.Members)
            {
                if (member is RecordDecl nestedRec)
                    AddRecord(project, nestedRec, null, ns, model.FullyQualifiedName, filePath);
                else if (member is TemplateDecl nestedTmpl && nestedTmpl.Inner is RecordDecl nestedTmplRec)
                    AddRecord(project, nestedTmplRec, nestedTmpl.TypeParams, ns, model.FullyQualifiedName, filePath);
                else if (member is EnumDecl nestedEnum)
                    AddEnum(project, nestedEnum, ns, model.FullyQualifiedName, filePath);
            }
        }

        private static TypeModel FindExisting(ProjectModel project, string fqn)
        {
            for (int i = 0; i < project.Types.Count; i++)
                if (project.Types[i].FullyQualifiedName == fqn) return project.Types[i];
            return null;
        }

        private static TypeModel BuildTypeModel(ProjectModel project, RecordDecl rec,
            IReadOnlyList<string> templateParams, string ns, string parentFqn, string filePath)
        {
            TypeKind kind = rec.Kind == RecordKind.Struct ? TypeKind.Struct
                          : rec.Kind == RecordKind.Union ? TypeKind.Struct
                          : TypeKind.ConcreteClass;

            TypeModel model = new TypeModel(rec.Name, ns, kind, project);
            model.ParentTypeFqn = parentFqn;
            model.SourceFilePath = filePath;
            model.SourceLineNumber = rec.Lineno;

            foreach (BaseClassRef b in rec.BaseClasses)
            {
                if (string.IsNullOrEmpty(b.Name)) continue;
                // C++ has no separate "implements" relationship the way Python's
                // Protocol/ABC split does — every base clause (public/private/protected,
                // virtual or not) is structural inheritance.
                model.BaseTypes.Add(new TypeReference(b.Name, RelationshipKind.Inherits));
            }

            if (templateParams != null)
                foreach (string p in templateParams)
                    if (!string.IsNullOrEmpty(p)) model.GenericParameters.Add(new GenericParameterModel(p));

            foreach (Decl member in rec.Members)
            {
                if (member is FunctionDecl fn)
                    model.Members.Add(BuildMemberModel(fn, rec.Name));
                else if (member is VarDecl v && !string.IsNullOrEmpty(v.Name))
                    model.Members.Add(BuildFieldModel(v));
                // Nested RecordDecl/EnumDecl/TemplateDecl are handled by the caller (AddRecord),
                // which adds them as sibling TypeModels with ParentTypeFqn set — matching how
                // PythonTypeExtractor surfaces nested classes.
            }
            return model;
        }

        private static MemberModel BuildMemberModel(FunctionDecl fn, string recordName)
        {
            bool isDestructor = fn.Name.Length > 0 && fn.Name[0] == '~';
            MemberKind kind = (!isDestructor && fn.Name == recordName) ? MemberKind.Constructor : MemberKind.Method;
            MemberModel m = new MemberModel(fn.Name, kind);
            m.ReturnType = (fn.ReturnType != null && !fn.ReturnType.IsEmpty()) ? fn.ReturnType.ToString() : null;
            m.AccessModifier = MapAccess(fn.Access);
            m.SourceLineNumber = fn.Lineno;

            foreach (Parameter p in fn.Params)
            {
                MethodParameterModel mp = new MethodParameterModel(p.Name,
                    p.Type != null ? p.Type.ToString() : string.Empty);
                // The default-value expression isn't re-rendered to text (no expression-to-source
                // emitter exists yet for this AST) — only its presence is recorded.
                if (p.DefaultValue != null) mp.HasDefaultValue = true;
                m.Parameters.Add(mp);
            }
            return m;
        }

        private static MemberModel BuildFieldModel(VarDecl v)
        {
            MemberModel m = new MemberModel(v.Name, MemberKind.Field);
            m.ReturnType = v.Type != null ? v.Type.ToString() : null;
            m.AccessModifier = MapAccess(v.Access);
            m.SourceLineNumber = v.Lineno;
            m.IsStatic = v.Storage == StorageClass.Static;
            return m;
        }

        private static void AddEnum(ProjectModel project, EnumDecl en, string ns, string parentFqn, string filePath)
        {
            if (string.IsNullOrEmpty(en.Name)) return;
            TypeModel model = new TypeModel(en.Name, ns, TypeKind.Enum, project);
            model.ParentTypeFqn = parentFqn;
            model.SourceFilePath = filePath;
            model.SourceLineNumber = en.Lineno;
            foreach (string name in en.Enumerators)
                if (!string.IsNullOrEmpty(name)) model.EnumValues.Add(new EnumValueModel(name));

            TypeModel existing = FindExisting(project, model.FullyQualifiedName);
            if (existing != null) project.Types.Remove(existing);
            project.Types.Add(model);
        }

        private static AccessModifier MapAccess(AccessSpec access)
        {
            switch (access)
            {
                case AccessSpec.Private: return AccessModifier.Private;
                case AccessSpec.Protected: return AccessModifier.Protected;
                // Public and Default (namespace/global scope, not applicable) both read as Public.
                default: return AccessModifier.Public;
            }
        }
    }
}
