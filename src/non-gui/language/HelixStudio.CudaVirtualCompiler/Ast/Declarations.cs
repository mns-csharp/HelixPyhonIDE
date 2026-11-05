namespace HelixPythonNamespace.CudaVirtualCompiler.Ast
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Declaration AST nodes plus the TranslationUnit root. Port of the C++
    /// helix::cuda_vcompiler::ast declaration classes (cuda_vcompiler/ast/declarations.h).
    /// </summary>

    /// <summary>
    /// A function parameter. Carries its declared type and (optional) default value
    /// expression so kernel-launch argument matching and memory-space seeding can
    /// read the parameter's pointer-ness and space.
    /// </summary>
    public sealed class Parameter
    {
        public CxxType Type;
        public string Name;
        public Expr DefaultValue;  // nullable
        public SourceSpan Span;

        public Parameter()
        {
            Type = new CxxType();
            Name = string.Empty;
        }
    }

    /// <summary>
    /// A function or kernel definition / declaration. <see cref="Body"/> is null for
    /// a bare prototype. CUDA qualifier flags are folded into <see cref="Space"/>; the
    /// original flags are kept for diagnostics that need to distinguish e.g. an
    /// explicit __host__.
    /// </summary>
    public sealed class FunctionDecl : Decl
    {
        public string Name { get; private set; }
        public CxxType ReturnType { get; private set; }
        public IReadOnlyList<Parameter> Params { get; private set; }
        public FuncSpace Space { get; private set; }
        public Stmt Body { get; private set; }  // nullable (prototype)

        /// <summary>
        /// Optional __launch_bounds__(maxThreads[, minBlocks]) recorded for the
        /// launch analyzer; 0 means unspecified.
        /// </summary>
        public int LaunchBoundsMaxThreads { get; set; }

        /// <summary>
        /// The declarator as written, including any qualification — e.g.
        /// "Coordinate::magnitude" for an out-of-line member definition (Name keeps just
        /// "magnitude"). Lets scope-sensitive passes recognize out-of-line members, whose
        /// class fields are not on the function scope's parent chain.
        /// </summary>
        public string QualifiedName { get; set; }

        public FunctionDecl(string name, CxxType returnType, List<Parameter> parameters,
                            FuncSpace space, Stmt body, SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            ReturnType = returnType;
            Params = parameters ?? new List<Parameter>();
            Space = space;
            Body = body;
            LaunchBoundsMaxThreads = 0;
        }

        public bool IsKernel { get { return Space == FuncSpace.Global; } }
        public bool HasBody { get { return Body != null; } }
    }

    /// <summary>A <c>using namespace a::b;</c> directive — opens the namespace's names
    /// into the current scope for the undefined-name analysis.</summary>
    public sealed class UsingNamespaceDecl : Decl
    {
        public string Namespace { get; private set; }

        public UsingNamespaceDecl(string ns, SourceSpan span) : base(span)
        {
            Namespace = ns ?? string.Empty;
        }
    }

    /// <summary>
    /// A namespace alias — <c>namespace cg = cooperative_groups;</c>. Name resolution treats
    /// the alias as another way to reach the target namespace's members.
    /// </summary>
    public sealed class NamespaceAliasDecl : Decl
    {
        public string Alias { get; private set; }
        public string Target { get; private set; }

        public NamespaceAliasDecl(string alias, string target, SourceSpan span) : base(span)
        {
            Alias = alias ?? string.Empty;
            Target = target ?? string.Empty;
        }
    }

    /// <summary>
    /// A variable / field declaration. <see cref="Storage"/> records CUDA memory-space
    /// storage qualifiers (__device__/__constant__/__shared__/__managed__) that seed
    /// the memory-space analysis.
    /// </summary>
    public sealed class VarDecl : Decl
    {
        public string Name { get; private set; }
        public CxxType Type { get; private set; }
        public StorageClass Storage { get; private set; }
        public Expr Initializer { get; private set; }  // nullable

        /// <summary>
        /// First array dimension expression for `T name[expr]` (nullable). The
        /// emulator evaluates it to size a local/__shared__ array.
        /// </summary>
        public Expr ArraySize { get; set; }

        public VarDecl(string name, CxxType type, StorageClass storage, Expr initializer, SourceSpan span)
            : base(span)
        {
            Name = name ?? string.Empty;
            Type = type;
            Storage = storage;
            Initializer = initializer;
        }
    }

    /// <summary>
    /// One entry of a record's base-clause (`: public Base1, private Base2`). Access is
    /// resolved to the explicit keyword when present, else the C++ default for the
    /// enclosing record kind (private for `class`, public for `struct`) — never
    /// <see cref="AccessSpec.Default"/>, so consumers never need to re-derive it.
    /// </summary>
    public sealed class BaseClassRef
    {
        public string Name { get; }
        public AccessSpec Access { get; }
        public bool IsVirtual { get; }

        public BaseClassRef(string name, AccessSpec access, bool isVirtual)
        {
            Name = name ?? string.Empty;
            Access = access;
            IsVirtual = isVirtual;
        }
    }

    /// <summary>struct / class / union. Members are field/var/function/nested decls.</summary>
    public sealed class RecordDecl : Decl
    {
        public RecordKind Kind { get; private set; }
        public string Name { get; private set; }
        public IReadOnlyList<Decl> Members { get; private set; }

        /// <summary>Base classes from the record's `: ...` clause, in declaration order.</summary>
        public IReadOnlyList<BaseClassRef> BaseClasses { get; private set; }

        public RecordDecl(RecordKind kind, string name, List<Decl> members, SourceSpan span)
            : this(kind, name, members, null, span)
        {
        }

        public RecordDecl(RecordKind kind, string name, List<Decl> members,
                           List<BaseClassRef> baseClasses, SourceSpan span) : base(span)
        {
            Kind = kind;
            Name = name ?? string.Empty;
            Members = members ?? new List<Decl>();
            BaseClasses = baseClasses ?? new List<BaseClassRef>();
        }
    }

    public sealed class EnumDecl : Decl
    {
        public string Name { get; private set; }
        public IReadOnlyList<string> Enumerators { get; private set; }

        public EnumDecl(string name, List<string> enumerators, SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            Enumerators = enumerators ?? new List<string>();
        }
    }

    public sealed class NamespaceDecl : Decl
    {
        public string Name { get; private set; }
        public IReadOnlyList<Decl> Decls { get; private set; }

        public NamespaceDecl(string name, List<Decl> decls, SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            Decls = decls ?? new List<Decl>();
        }
    }

    /// <summary>typedef T name; / using name = T;</summary>
    public sealed class TypeAliasDecl : Decl
    {
        public string Name { get; private set; }
        public CxxType Aliased { get; private set; }

        public TypeAliasDecl(string name, CxxType aliased, SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            Aliased = aliased;
        }
    }

    /// <summary>
    /// template&lt;...&gt; &lt;inner&gt;. Template parameters are kept as opaque type-name
    /// strings; the body is analyzed once with them treated as unknown types (no
    /// instantiation).
    /// </summary>
    public sealed class TemplateDecl : Decl
    {
        public IReadOnlyList<string> TypeParams { get; private set; }
        public Decl Inner { get; private set; }

        public TemplateDecl(List<string> typeParams, Decl inner, SourceSpan span) : base(span)
        {
            TypeParams = typeParams ?? new List<string>();
            Inner = inner;
        }
    }

    /// <summary>Root container for a single .cu/.cuh translation unit.</summary>
    public sealed class TranslationUnit
    {
        private readonly List<Decl> decls_;

        public TranslationUnit()
        {
            decls_ = new List<Decl>();
        }

        public TranslationUnit(List<Decl> decls)
        {
            decls_ = decls ?? new List<Decl>();
        }

        public IReadOnlyList<Decl> Decls { get { return decls_; } }

        /// <summary>Mutable accessor mirroring the C++ non-const Decls() overload.</summary>
        public List<Decl> DeclsMutable { get { return decls_; } }
    }
}
