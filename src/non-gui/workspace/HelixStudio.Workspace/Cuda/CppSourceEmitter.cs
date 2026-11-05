using System;
using System.Collections.Generic;
using System.Text;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>
    /// Renders a <see cref="TypeModel"/> to C++23 source text — the C++ counterpart to
    /// <see cref="HelixPythonNamespace.Workspace.Python.PythonSourceEmitter"/>, and the emitter behind
    /// <see cref="CppTypeService"/>'s Add Class / Add Interface / Add Abstract Class / Add Struct.
    ///
    /// Two structural differences from the Python emitter, both forced by the language:
    ///
    /// 1. HEADER/SOURCE SPLIT. Python renders one module string; C++ renders a declaration
    ///    (<see cref="RenderHeader"/>) and, when anything actually needs an out-of-line body,
    ///    a matching definition file (<see cref="RenderSource"/>). This mirrors the repo's own
    ///    hand-written fixture (tests/fixtures/cpp/CppTestSample): declarations live in
    ///    <c>include/&lt;target&gt;/*.hpp</c>, non-trivial bodies in <c>src/*.cpp</c>. A pure
    ///    interface — only pure-virtual methods plus a defaulted virtual destructor, exactly like
    ///    that fixture's <c>ShapeBase</c> — needs no .cpp at all; <see cref="NeedsSourceFile"/>
    ///    says so, and the service then writes no pointless empty translation unit.
    ///
    /// 2. ACCESS SPECIFIERS. Python encodes access in the member NAME (<c>_x</c> / <c>__x</c>);
    ///    C++ has real <c>public:</c>/<c>protected:</c>/<c>private:</c> blocks, so members are
    ///    GROUPED by <see cref="MemberModel.AccessModifier"/> rather than emitted as one flat
    ///    list, and within a group ordered constructors → destructor → methods → fields, which is
    ///    how the fixture's own classes (Circle, Rectangle, Polygon, HelixError) are laid out.
    ///
    /// Everything emitted here is deliberately re-parseable by <see cref="CppTypeExtractor"/> —
    /// scaffolding a class and re-loading the project must yield an equivalent model. Two
    /// consequences worth knowing before "improving" the output:
    ///   * No <c>explicit</c> on constructors. It is not a keyword in
    ///     <c>HelixPythonNamespace.CudaVirtualCompiler</c>'s lexer, so <c>explicit Foo(int);</c> parses as
    ///     a function named <c>Foo</c> returning a type called <c>explicit</c> instead of as a
    ///     constructor. Idiomatic, but it would break the round-trip.
    ///   * Namespaces are emitted as NESTED blocks (<c>namespace a { namespace b {</c>), never
    ///     C++17 <c>namespace a::b {</c>, which that parser's ParseNamespace does not accept.
    /// </summary>
    public static class CppSourceEmitter
    {
        public const string HeaderExtension = ".hpp";
        public const string SourceExtension = ".cpp";

        private const string Indent = "    ";
        private const string Nl = "\n";

        // ------------------------------------------------------------------ header

        /// <summary>Render the complete <c>.hpp</c> text for <paramref name="type"/>.</summary>
        public static string RenderHeader(TypeModel type)
        {
            if (type == null) return string.Empty;
            StringBuilder sb = new StringBuilder();
            sb.Append("#pragma once").Append(Nl).Append(Nl);

            List<string> projectIncludes = CollectBaseClassIncludes(type);
            List<string> includes = CollectIncludes(type);
            if (projectIncludes.Count > 0 || includes.Count > 0)
            {
                foreach (string inc in projectIncludes) sb.Append("#include \"").Append(inc).Append('"').Append(Nl);
                foreach (string inc in includes) sb.Append("#include <").Append(inc).Append('>').Append(Nl);
                sb.Append(Nl);
            }

            string[] nsParts = SplitNamespace(type.Namespace);
            foreach (string part in nsParts) sb.Append("namespace ").Append(part).Append(" {").Append(Nl);
            if (nsParts.Length > 0) sb.Append(Nl);

            if (type.TypeKind == TypeKind.Enum) RenderEnumBody(sb, type);
            else RenderRecordBody(sb, type);

            if (nsParts.Length > 0) sb.Append(Nl);
            for (int i = nsParts.Length - 1; i >= 0; i--)
                sb.Append("}  // namespace ").Append(nsParts[i]).Append(Nl);

            return sb.ToString();
        }

        private static void RenderEnumBody(StringBuilder sb, TypeModel type)
        {
            sb.Append("enum class ").Append(type.Name);
            if (!string.IsNullOrEmpty(type.EnumUnderlyingType))
                sb.Append(" : ").Append(type.EnumUnderlyingType);
            sb.Append(" {").Append(Nl);
            if (type.EnumValues != null)
            {
                foreach (EnumValueModel v in type.EnumValues)
                {
                    sb.Append(Indent).Append(v.Name);
                    if (!string.IsNullOrEmpty(v.ValueExpression)) sb.Append(" = ").Append(v.ValueExpression);
                    sb.Append(',').Append(Nl);
                }
            }
            sb.Append("};").Append(Nl);
        }

        private static void RenderRecordBody(StringBuilder sb, TypeModel type)
        {
            bool isStruct = type.TypeKind == TypeKind.Struct;
            RenderTemplateHeader(sb, type, string.Empty);

            sb.Append(isStruct ? "struct " : "class ").Append(type.Name);
            if (type.IsSealed) sb.Append(" final");
            RenderBaseClause(sb, type);
            sb.Append(" {").Append(Nl);

            // Access-grouped body. A struct's first public group needs no label (public is the
            // struct default — matches the fixture's Point3D/Vector), every other group does.
            AccessModifier[] order = { AccessModifier.Public, AccessModifier.Protected, AccessModifier.Private };
            bool wroteAnyGroup = false;
            for (int i = 0; i < order.Length; i++)
            {
                List<string> lines = DeclarationsFor(type, order[i]);
                if (lines.Count == 0) continue;
                bool omitLabel = isStruct && !wroteAnyGroup && order[i] == AccessModifier.Public;
                if (wroteAnyGroup) sb.Append(Nl);
                if (!omitLabel) sb.Append(AccessLabel(order[i])).Append(':').Append(Nl);
                foreach (string line in lines) sb.Append(Indent).Append(line).Append(Nl);
                wroteAnyGroup = true;
            }
            sb.Append("};").Append(Nl);
        }

        private static void RenderTemplateHeader(StringBuilder sb, TypeModel type, string prefix)
        {
            if (type.GenericParameters == null || type.GenericParameters.Count == 0) return;
            sb.Append(prefix).Append("template <");
            for (int i = 0; i < type.GenericParameters.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("typename ").Append(type.GenericParameters[i].Name);
            }
            sb.Append('>').Append(Nl);
        }

        private static void RenderBaseClause(StringBuilder sb, TypeModel type)
        {
            if (type.BaseTypes == null || type.BaseTypes.Count == 0) return;
            bool first = true;
            foreach (TypeReference r in type.BaseTypes)
            {
                if (string.IsNullOrEmpty(r.FullyQualifiedName)) continue;
                sb.Append(first ? " : " : ", ");
                // TypeReference carries no C++ access-specifier (RelationshipKind only
                // distinguishes Inherits/Implements, and CppTypeExtractor maps every parsed base
                // clause — public, protected or private — to Inherits). Public inheritance is the
                // overwhelmingly common case and the only one that models "is-a", so emit it.
                sb.Append("public ").Append(CppQualified(r.FullyQualifiedName));
                first = false;
            }
        }

        /// <summary>Declaration lines for one access group, ordered ctors → dtor → methods → fields.</summary>
        private static List<string> DeclarationsFor(TypeModel type, AccessModifier access)
        {
            List<string> ctors = new List<string>();
            List<string> methods = new List<string>();
            List<string> fields = new List<string>();

            foreach (MemberModel m in type.Members)
            {
                if (MapAccess(m.AccessModifier) != access) continue;
                if (m.MemberKind == MemberKind.Constructor) ctors.Add(RenderConstructorDecl(type, m));
                else if (m.MemberKind == MemberKind.Field || m.MemberKind == MemberKind.Property)
                    fields.Add(RenderFieldDecl(m));
                else methods.Add(RenderMethodDecl(type, m));
            }

            if (access == AccessModifier.Public)
            {
                // Synthesized members always land in the public block.
                if (NeedsGeneratedConstructor(type)) ctors.Insert(0, type.Name + "();");
                if (NeedsVirtualDestructor(type)) ctors.Add("virtual ~" + type.Name + "() = default;");
            }

            List<string> all = new List<string>(ctors.Count + methods.Count + fields.Count);
            all.AddRange(ctors);
            all.AddRange(methods);
            all.AddRange(fields);
            return all;
        }

        private static string RenderConstructorDecl(TypeModel type, MemberModel m)
        {
            // No `explicit` — see the class doc-comment: the CudaVirtualCompiler lexer does not
            // know the keyword and would stop recognizing the declaration as a constructor.
            return type.Name + "(" + RenderParams(m) + ");";
        }

        private static string RenderMethodDecl(TypeModel type, MemberModel m)
        {
            StringBuilder sb = new StringBuilder();
            bool pure = IsPureVirtual(type, m);
            if (m.IsStatic) sb.Append("static ");
            else if (pure || (m.IsVirtual && !m.IsOverride)) sb.Append("virtual ");
            sb.Append(ReturnTypeOf(m)).Append(' ').Append(m.Name).Append('(').Append(RenderParams(m)).Append(')');
            if (m.IsOverride) sb.Append(" override");
            if (pure) sb.Append(" = 0");
            sb.Append(';');
            return sb.ToString();
        }

        private static string RenderFieldDecl(MemberModel m)
        {
            StringBuilder sb = new StringBuilder();
            if (m.IsStatic) sb.Append("static ");
            if (m.IsConst) sb.Append("const ");
            sb.Append(FieldTypeOf(m)).Append(' ').Append(m.Name).Append(';');
            return sb.ToString();
        }

        private static string RenderParams(MemberModel m)
        {
            if (m.Parameters == null || m.Parameters.Count == 0) return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < m.Parameters.Count; i++)
            {
                MethodParameterModel p = m.Parameters[i];
                if (i > 0) sb.Append(", ");
                sb.Append(string.IsNullOrEmpty(p.TypeName) ? "int" : p.TypeName).Append(' ').Append(p.Name);
                if (p.HasDefaultValue && !string.IsNullOrEmpty(p.DefaultValue))
                    sb.Append(" = ").Append(p.DefaultValue);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ source

        /// <summary>
        /// True when <paramref name="type"/> has at least one member needing an out-of-line body,
        /// i.e. when a companion <c>.cpp</c> should be written. False for a pure interface / enum,
        /// where a .cpp would be an empty translation unit.
        /// </summary>
        public static bool NeedsSourceFile(TypeModel type)
        {
            if (type == null || type.TypeKind == TypeKind.Enum || type.TypeKind == TypeKind.Interface)
                return false;
            // A templated type must be defined in the header — the definitions cannot be split out.
            if (type.GenericParameters != null && type.GenericParameters.Count > 0) return false;
            if (NeedsGeneratedConstructor(type)) return true;
            foreach (MemberModel m in type.Members)
                if (HasOutOfLineBody(type, m)) return true;
            return false;
        }

        /// <summary>
        /// Render the companion <c>.cpp</c>. <paramref name="headerInclude"/> is the quoted include
        /// path as it must appear from the target's public include dir (e.g. <c>lib_core/Foo.hpp</c>),
        /// matching how the fixture's <c>src/errors.cpp</c> includes <c>"lib_core/errors.hpp"</c>.
        /// </summary>
        public static string RenderSource(TypeModel type, string headerInclude)
        {
            if (type == null) return string.Empty;
            StringBuilder sb = new StringBuilder();
            sb.Append("#include \"").Append(headerInclude ?? (type.Name + HeaderExtension)).Append('"')
              .Append(Nl).Append(Nl);

            string[] nsParts = SplitNamespace(type.Namespace);
            foreach (string part in nsParts) sb.Append("namespace ").Append(part).Append(" {").Append(Nl);
            if (nsParts.Length > 0) sb.Append(Nl);

            bool first = true;
            if (NeedsGeneratedConstructor(type))
            {
                sb.Append(type.Name).Append("::").Append(type.Name).Append("() {").Append(Nl)
                  .Append('}').Append(Nl);
                first = false;
            }
            // Constructors first, then the rest — the same order the header declares them in, so
            // the two files read as a matching pair (as errors.hpp/errors.cpp do in the fixture).
            for (int pass = 0; pass < 2; pass++)
            {
                bool ctorPass = pass == 0;
                foreach (MemberModel m in type.Members)
                {
                    if (!HasOutOfLineBody(type, m)) continue;
                    if ((m.MemberKind == MemberKind.Constructor) != ctorPass) continue;
                    if (!first) sb.Append(Nl);
                    first = false;
                    if (ctorPass)
                    {
                        sb.Append(type.Name).Append("::").Append(type.Name)
                          .Append('(').Append(RenderParams(m)).Append(") {").Append(Nl);
                        sb.Append('}').Append(Nl);
                    }
                    else
                    {
                        string ret = ReturnTypeOf(m);
                        sb.Append(ret).Append(' ').Append(type.Name).Append("::").Append(m.Name)
                          .Append('(').Append(RenderParams(m)).Append(") {").Append(Nl);
                        sb.Append(Indent).Append("// TODO: implement").Append(Nl);
                        if (ret != "void") sb.Append(Indent).Append("return {};").Append(Nl);
                        sb.Append('}').Append(Nl);
                    }
                }
            }

            if (nsParts.Length > 0) sb.Append(Nl);
            for (int i = nsParts.Length - 1; i >= 0; i--)
                sb.Append("}  // namespace ").Append(nsParts[i]).Append(Nl);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ helpers

        private static bool HasOutOfLineBody(TypeModel type, MemberModel m)
        {
            if (m.MemberKind == MemberKind.Field || m.MemberKind == MemberKind.Property) return false;
            if (m.MemberKind == MemberKind.Constructor) return true;
            return !IsPureVirtual(type, m);
        }

        /// <summary>
        /// Every member of an <see cref="TypeKind.Interface"/> is pure-virtual by definition;
        /// on an <see cref="TypeKind.AbstractClass"/> only members the caller explicitly marked
        /// <see cref="MemberModel.IsAbstract"/> are (the rest are ordinary virtual/non-virtual
        /// methods with real bodies — that is exactly what makes it abstract-but-not-an-interface).
        /// </summary>
        private static bool IsPureVirtual(TypeModel type, MemberModel m)
        {
            if (m.MemberKind == MemberKind.Field || m.MemberKind == MemberKind.Property) return false;
            if (m.MemberKind == MemberKind.Constructor) return false;
            if (m.IsStatic) return false;
            if (type.TypeKind == TypeKind.Interface) return true;
            return m.IsAbstract;
        }

        /// <summary>
        /// Interfaces get no constructor (only the defaulted virtual destructor, as with the
        /// fixture's ShapeBase); enums obviously none. Class/struct/abstract-class scaffolds get a
        /// declared default constructor when the caller supplied none, so "Add Class" produces
        /// something immediately constructible with a real body to fill in.
        /// </summary>
        private static bool NeedsGeneratedConstructor(TypeModel type)
        {
            if (type.TypeKind == TypeKind.Interface || type.TypeKind == TypeKind.Enum) return false;
            if (type.GenericParameters != null && type.GenericParameters.Count > 0) return false;
            foreach (MemberModel m in type.Members)
                if (m.MemberKind == MemberKind.Constructor) return false;
            return true;
        }

        /// <summary>
        /// A polymorphic base must be destroyed through a base pointer safely, so the root of a
        /// hierarchy declares <c>virtual ~T() = default;</c>. A DERIVED type does not re-declare
        /// one (its base already has it) — matching the fixture, where ShapeBase declares the
        /// virtual destructor and Circle/Rectangle/Star declare none.
        /// </summary>
        private static bool NeedsVirtualDestructor(TypeModel type)
        {
            if (type.TypeKind == TypeKind.Enum) return false;
            if (type.BaseTypes != null && type.BaseTypes.Count > 0) return false;
            if (type.TypeKind == TypeKind.Interface || type.TypeKind == TypeKind.AbstractClass) return true;
            foreach (MemberModel m in type.Members)
                if (m.IsVirtual || m.IsAbstract) return true;
            return false;
        }

        private static string ReturnTypeOf(MemberModel m)
        {
            return string.IsNullOrEmpty(m.ReturnType) ? "void" : m.ReturnType;
        }

        private static string FieldTypeOf(MemberModel m)
        {
            return string.IsNullOrEmpty(m.ReturnType) ? "int" : m.ReturnType;
        }

        private static string AccessLabel(AccessModifier a)
        {
            switch (a)
            {
                case AccessModifier.Protected: return "protected";
                case AccessModifier.Private: return "private";
                default: return "public";
            }
        }

        /// <summary>C++ has only public/protected/private; C#-only modifiers fold to the nearest.</summary>
        private static AccessModifier MapAccess(AccessModifier a)
        {
            switch (a)
            {
                case AccessModifier.Private:
                case AccessModifier.File:
                    return AccessModifier.Private;
                case AccessModifier.Protected:
                case AccessModifier.ProtectedInternal:
                case AccessModifier.PrivateProtected:
                    return AccessModifier.Protected;
                default:
                    return AccessModifier.Public;
            }
        }

        /// <summary>Model namespaces are dot-separated (CppTypeExtractor builds "a.b"); C++ needs "a::b".</summary>
        private static string CppQualified(string dotted)
        {
            return string.IsNullOrEmpty(dotted) ? string.Empty : dotted.Replace(".", "::");
        }

        internal static string[] SplitNamespace(string ns)
        {
            if (string.IsNullOrEmpty(ns)) return new string[0];
            return ns.Replace("::", ".").Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Quoted includes for base classes that live in the SAME project. Unlike Python, where a
        /// base is resolved at runtime by the import system, C++ needs the base's full definition
        /// visible at the point of derivation — a scaffolded `class Circle : public ShapeBase`
        /// simply will not compile without `#include "lib_core/ShapeBase.hpp"`.
        ///
        /// The include path is derived from the base's own <see cref="TypeModel.SourceFilePath"/>
        /// (its last directory segment plus file name), not hard-coded, so it stays correct for a
        /// base the user hand-wrote elsewhere under the target's include tree. Bases with no
        /// sibling TypeModel (std::exception, a third-party type) are skipped — the emitter has no
        /// way to know their header, and the user adds it while filling in the scaffold.
        /// </summary>
        private static List<string> CollectBaseClassIncludes(TypeModel type)
        {
            List<string> result = new List<string>();
            if (type.BaseTypes == null || type.BaseTypes.Count == 0 || type.Project == null) return result;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TypeReference r in type.BaseTypes)
            {
                if (string.IsNullOrEmpty(r.FullyQualifiedName)) continue;
                TypeModel baseType = FindSibling(type, r);
                if (baseType == null || string.IsNullOrEmpty(baseType.SourceFilePath)) continue;
                if (string.Equals(baseType.SourceFilePath, type.SourceFilePath, StringComparison.OrdinalIgnoreCase))
                    continue;  // same header — already visible
                string include = IncludeSpellingFor(baseType.SourceFilePath);
                if (include != null && seen.Add(include)) result.Add(include);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static TypeModel FindSibling(TypeModel type, TypeReference r)
        {
            string simple = r.SimpleName;
            int scope = simple.LastIndexOf("::", StringComparison.Ordinal);
            if (scope >= 0) simple = simple.Substring(scope + 2);
            TypeModel fallback = null;
            foreach (TypeModel candidate in type.Project.Types)
            {
                if (ReferenceEquals(candidate, type)) continue;
                if (candidate.FullyQualifiedName == r.FullyQualifiedName) return candidate;
                if (fallback == null && candidate.Name == simple) fallback = candidate;
            }
            return fallback;
        }

        /// <summary>"…/include/lib_core/ShapeBase.hpp" → "lib_core/ShapeBase.hpp".</summary>
        private static string IncludeSpellingFor(string headerPath)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(headerPath);
                string file = System.IO.Path.GetFileName(headerPath);
                if (string.IsNullOrEmpty(file)) return null;
                string leaf = string.IsNullOrEmpty(dir) ? null : System.IO.Path.GetFileName(dir);
                return string.IsNullOrEmpty(leaf) ? file : leaf + "/" + file;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Best-effort standard-library includes inferred from the type names the model mentions.
        /// A scaffold that says a method returns <c>std::string</c> should compile without the
        /// user first hunting for the header.
        /// </summary>
        private static List<string> CollectIncludes(TypeModel type)
        {
            HashSet<string> found = new HashSet<string>(StringComparer.Ordinal);
            List<string> texts = new List<string>();
            foreach (MemberModel m in type.Members)
            {
                if (!string.IsNullOrEmpty(m.ReturnType)) texts.Add(m.ReturnType);
                if (m.Parameters != null)
                    foreach (MethodParameterModel p in m.Parameters)
                        if (!string.IsNullOrEmpty(p.TypeName)) texts.Add(p.TypeName);
            }
            foreach (string t in texts)
            {
                if (t.IndexOf("std::string", StringComparison.Ordinal) >= 0) found.Add("string");
                if (t.IndexOf("std::vector", StringComparison.Ordinal) >= 0) found.Add("vector");
                if (t.IndexOf("std::map", StringComparison.Ordinal) >= 0) found.Add("map");
                if (t.IndexOf("std::optional", StringComparison.Ordinal) >= 0) found.Add("optional");
                if (t.IndexOf("std::function", StringComparison.Ordinal) >= 0) found.Add("functional");
                if (t.IndexOf("std::shared_ptr", StringComparison.Ordinal) >= 0
                    || t.IndexOf("std::unique_ptr", StringComparison.Ordinal) >= 0
                    || t.IndexOf("std::weak_ptr", StringComparison.Ordinal) >= 0) found.Add("memory");
                if (t.IndexOf("size_t", StringComparison.Ordinal) >= 0) found.Add("cstddef");
            }
            List<string> result = new List<string>(found);
            result.Sort(StringComparer.Ordinal);
            return result;
        }
    }
}
