using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>
    /// Add Class / Add Interface / Add Abstract Class / Add Struct for a CMake-backed C++
    /// <see cref="ProjectModel"/> — the C++ counterpart to
    /// <see cref="HelixPythonNamespace.Workspace.Services.TypeService"/>, writing real <c>.hpp</c>/<c>.cpp</c>
    /// files through <see cref="CppSourceEmitter"/> exactly as that service writes <c>.py</c>
    /// through <c>PythonSourceEmitter</c>.
    ///
    /// Deliberately NOT an <see cref="HelixPythonNamespace.Workspace.Services.ITypeService"/> implementation:
    /// that interface's shape is Python's (a single module file per type, namespace == package
    /// directory, no access specifiers, no header/source split), and forcing C++ through it would
    /// mean silently ignoring half its members. The overlapping operations keep the same names and
    /// parameter order so a caller switching on <see cref="ProjectLanguage"/> reads the same either
    /// way.
    ///
    /// FILE LAYOUT — matches the hand-written fixture at
    /// <c>tests/fixtures/cpp/CppTestSample/src/lib_core/</c> exactly, so scaffolded files sit
    /// beside hand-written ones without looking foreign:
    /// <code>
    ///   &lt;projectDir&gt;/include/&lt;target&gt;/&lt;Name&gt;.hpp   // declaration; on the target's PUBLIC include path
    ///   &lt;projectDir&gt;/src/&lt;Name&gt;.cpp                 // out-of-line definitions, when any exist
    /// </code>
    /// <c>&lt;projectDir&gt;</c> is <see cref="ProjectModel.ProjectDirectory"/> — for a CMake target the
    /// <see cref="ProjectModel.FilePath"/> is the synthetic <c>&lt;folder&gt;/&lt;target&gt;.cmake-target</c>
    /// marker, whose directory is the real folder holding that target's <c>CMakeLists.txt</c>
    /// (<c>add_library(lib_core STATIC src/errors.cpp)</c> +
    /// <c>target_include_directories(lib_core PUBLIC include)</c>). <c>&lt;target&gt;</c> is
    /// <see cref="ProjectModel.Name"/>, which is why the generated <c>.cpp</c> can include its own
    /// header as <c>"lib_core/Foo.hpp"</c> — the same spelling <c>src/errors.cpp</c> uses.
    ///
    /// The header lives one directory deeper than the source ON PURPOSE: the extra
    /// <c>&lt;target&gt;</c> level is what makes <c>include/</c> safe to put on the public include
    /// path without leaking every header name into consumers' global namespace, and it is the
    /// convention every target in the fixture follows (app_cli, app_gui, app_server, lib_core,
    /// lib_utils). Namespace is NOT mapped to directories the way Python's package path is —
    /// C++ headers are addressed by their include path, not their namespace.
    ///
    /// NOT done here: registering the new <c>.cpp</c> in the target's <c>CMakeLists.txt</c>
    /// sources list. That belongs to the CMake writer (<see cref="CMakeListsWriter"/>) and to the
    /// context-menu integration that owns the whole add-file flow.
    /// </summary>
    public sealed class CppTypeService
    {
        /// <summary>Add a class. <paramref name="isAbstract"/> selects TypeKind.AbstractClass.</summary>
        public TypeModel AddClass(ProjectModel project, string name, string ns = null,
            bool isAbstract = false, string baseTypeName = null)
        {
            TypeKind kind = isAbstract ? TypeKind.AbstractClass : TypeKind.ConcreteClass;
            return AddType(project, name, ns, kind, baseTypeName);
        }

        /// <summary>
        /// Add an interface: an abstract base class with a defaulted virtual destructor and
        /// pure-virtual methods, the pattern the fixture's <c>ShapeBase</c> uses. Header-only —
        /// nothing in it needs an out-of-line definition.
        /// </summary>
        public TypeModel AddInterface(ProjectModel project, string name, string ns = null)
        {
            return AddType(project, name, ns, TypeKind.Interface, null);
        }

        /// <summary>Add a struct (a real, distinct kind in C++ — Python has no counterpart).</summary>
        public TypeModel AddStruct(ProjectModel project, string name, string ns = null,
            string baseTypeName = null)
        {
            return AddType(project, name, ns, TypeKind.Struct, baseTypeName);
        }

        /// <summary>Add a scoped enumeration (<c>enum class</c>). Header-only.</summary>
        public TypeModel AddEnum(ProjectModel project, string name, string ns = null,
            IEnumerable<string> values = null)
        {
            TypeModel type = NewType(project, name, ns, TypeKind.Enum, null);
            if (values != null)
                foreach (string v in values)
                    if (!string.IsNullOrEmpty(v)) type.EnumValues.Add(new EnumValueModel(v));
            project.Types.Add(type);
            WriteType(type);
            return type;
        }

        /// <summary>
        /// Re-emit the files for <paramref name="type"/> after the caller mutated the model
        /// (added members, changed the kind, added a base). Mirrors TypeService's
        /// RewriteModuleForType, which every mutating operation there funnels through.
        /// Deletes a now-unneeded <c>.cpp</c> (e.g. after a class is turned into an interface).
        /// </summary>
        public void RewriteType(TypeModel type)
        {
            WriteType(type);
        }

        /// <summary>
        /// Remove <paramref name="type"/> from the project and delete BOTH backing files
        /// (header and, if present, source). Unlike the Python service there is no "several types
        /// share one module" case to handle here: this service writes exactly one type per
        /// header/source pair, so the files always belong solely to this type. Types the user
        /// hand-wrote several-to-a-header are left on disk untouched — only the model entry goes.
        /// </summary>
        public void RemoveType(ProjectModel project, TypeModel type)
        {
            if (project == null || type == null) return;
            project.Types.Remove(type);

            string header = type.SourceFilePath;
            if (string.IsNullOrEmpty(header) || !IsSoleOwner(project, type, header)) return;

            TryDelete(header);
            string source = SourcePathForHeader(project, header);
            if (source != null) TryDelete(source);
        }

        // ------------------------------------------------------------------ path convention

        /// <summary>&lt;projectDir&gt;/include/&lt;target&gt;/&lt;Name&gt;.hpp</summary>
        public static string ComputeHeaderPath(ProjectModel project, string typeName)
        {
            return Path.Combine(HeaderDirectory(project), typeName + CppSourceEmitter.HeaderExtension);
        }

        /// <summary>&lt;projectDir&gt;/src/&lt;Name&gt;.cpp</summary>
        public static string ComputeSourcePath(ProjectModel project, string typeName)
        {
            return Path.Combine(SourceDirectory(project), typeName + CppSourceEmitter.SourceExtension);
        }

        /// <summary>The include spelling a sibling .cpp uses for the header: <c>&lt;target&gt;/&lt;Name&gt;.hpp</c>.</summary>
        public static string ComputeHeaderInclude(ProjectModel project, string typeName)
        {
            return TargetName(project) + "/" + typeName + CppSourceEmitter.HeaderExtension;
        }

        public static string HeaderDirectory(ProjectModel project)
        {
            return Path.Combine(ProjectDirectory(project), "include", TargetName(project));
        }

        public static string SourceDirectory(ProjectModel project)
        {
            return Path.Combine(ProjectDirectory(project), "src");
        }

        // ------------------------------------------------------------------ helpers

        private TypeModel AddType(ProjectModel project, string name, string ns, TypeKind kind,
            string baseTypeName)
        {
            TypeModel type = NewType(project, name, ns, kind, baseTypeName);
            project.Types.Add(type);
            WriteType(type);
            return type;
        }

        private static TypeModel NewType(ProjectModel project, string name, string ns, TypeKind kind,
            string baseTypeName)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name");

            string effectiveNs = ns ?? DefaultNamespace(project);
            TypeModel type = new TypeModel(name, effectiveNs, kind, project);
            type.SourceFilePath = ComputeHeaderPath(project, name);
            if (!string.IsNullOrWhiteSpace(baseTypeName))
            {
                // C++ has no separate "implements" relationship — see CppTypeExtractor.
                type.BaseTypes.Add(new TypeReference(baseTypeName, RelationshipKind.Inherits));
            }
            return type;
        }

        private static void WriteType(TypeModel type)
        {
            if (type == null || string.IsNullOrEmpty(type.SourceFilePath)) return;
            ProjectModel project = type.Project;

            Directory.CreateDirectory(Path.GetDirectoryName(type.SourceFilePath));
            File.WriteAllText(type.SourceFilePath, CppSourceEmitter.RenderHeader(type), new UTF8Encoding(false));

            string sourcePath = ComputeSourcePath(project, type.Name);
            if (CppSourceEmitter.NeedsSourceFile(type))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath));
                string include = ComputeHeaderInclude(project, type.Name);
                File.WriteAllText(sourcePath, CppSourceEmitter.RenderSource(type, include), new UTF8Encoding(false));
            }
            else
            {
                // Header-only now (pure interface / enum / template). Don't leave a stale .cpp
                // behind that the CMake target would still try to compile.
                TryDelete(sourcePath);
            }
        }

        /// <summary>Never delete a header another TypeModel also claims (hand-written multi-type header).</summary>
        private static bool IsSoleOwner(ProjectModel project, TypeModel type, string header)
        {
            foreach (TypeModel other in project.Types)
            {
                if (ReferenceEquals(other, type)) continue;
                if (string.Equals(other.SourceFilePath, header, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The <c>.cpp</c> that pairs with <paramref name="header"/> under this service's layout,
        /// or null when the header is not one this service would have written (a hand-written
        /// header somewhere else in the tree keeps its neighbours).
        /// </summary>
        private static string SourcePathForHeader(ProjectModel project, string header)
        {
            try
            {
                string expectedDir = Path.GetFullPath(HeaderDirectory(project));
                string actualDir = Path.GetFullPath(Path.GetDirectoryName(header));
                if (!string.Equals(expectedDir, actualDir, StringComparison.OrdinalIgnoreCase)) return null;
                return ComputeSourcePath(project, Path.GetFileNameWithoutExtension(header));
            }
            catch
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string ProjectDirectory(ProjectModel project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            string dir = project.ProjectDirectory;
            if (string.IsNullOrEmpty(dir))
                throw new InvalidOperationException("Project has no directory: " + project.FilePath);
            return dir;
        }

        /// <summary>CMake target name — the include-path prefix and the default namespace.</summary>
        private static string TargetName(ProjectModel project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            return SafeIdentifier(project.Name);
        }

        private static string DefaultNamespace(ProjectModel project)
        {
            return string.IsNullOrEmpty(project.DefaultNamespace)
                ? TargetName(project)
                : project.DefaultNamespace;
        }

        private static string SafeIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return "target";
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else if (c == '-' || c == '.' || c == ' ') sb.Append('_');
            }
            if (sb.Length == 0) return "target";
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }
    }
}
