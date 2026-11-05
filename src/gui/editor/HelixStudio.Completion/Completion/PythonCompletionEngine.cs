using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// Context classification for a completion request.
    /// </summary>
    public enum CompletionContextKind
    {
        General,           // identifier at expression position
        Decorator,         // after `@` on a Python decorator line
        Member,            // after `<expr>.`
        Import,            // on a bare `import` line, or the module-name part of `from … import`
        FromImportMember,  // after `from <module> import ` — members of <module>, not module names
        // Native-language completion inside #include delimiters.
        PreprocessorInclude,
    }

    public struct CompletionContext
    {
        public CompletionContextKind Kind;
        public string Prefix;       // already-typed prefix at caret
        public int PrefixStart;     // offset where the prefix begins
        public string MemberBase;   // the text before the `.` when Kind == Member (e.g. "obj" or "self")
        public string FromImportModule; // the module name between `from` and `import` when Kind == FromImportMember
        public char IncludeDelimiter;
    }

    /// <summary>
    /// Produces a list of <see cref="CompletionItem"/>s for a caret position. Pure —
    /// callers feed it source text and optional solution data; it returns items. The
    /// controller decides what to show in the UI.
    /// </summary>
    public sealed class PythonCompletionEngine : ICompletionEngine
    {
        // Optional — set by CompletionController once it has a live/growing PythonCatalog.
        // Null means "no catalog wired up yet", in which case member-dot completion on an
        // imported module falls straight through to today's behavior (no completions), same
        // as before this was added — this can only add coverage, never regress it.
        public HelixPythonNamespace.Catalog.PythonCatalog Catalog { get; set; }

        // Reported when AddMemberCompletions needed a module FQN that Catalog doesn't have
        // symbols for yet, so CompletionController can decide whether to kick off a scan.
        // Cleared at the start of every GetCompletions call.
        public string LastUnresolvedModuleFqn { get; private set; }

        private static readonly string[] Keywords =
        {
            "False", "None", "True", "and", "as", "assert", "async", "await",
            "break", "class", "continue", "def", "del", "elif", "else", "except",
            "finally", "for", "from", "global", "if", "import", "in", "is",
            "lambda", "nonlocal", "not", "or", "pass", "raise", "return",
            "try", "while", "with", "yield",
        };

        private static readonly string[] Builtins =
        {
            "abs", "all", "any", "ascii", "bin", "bool", "bytearray", "bytes",
            "callable", "chr", "classmethod", "compile", "complex", "delattr",
            "dict", "dir", "divmod", "enumerate", "eval", "exec", "filter",
            "float", "format", "frozenset", "getattr", "globals", "hasattr",
            "hash", "help", "hex", "id", "input", "int", "isinstance",
            "issubclass", "iter", "len", "list", "locals", "map", "max",
            "memoryview", "min", "next", "object", "oct", "open", "ord", "pow",
            "print", "property", "range", "repr", "reversed", "round", "set",
            "setattr", "slice", "sorted", "staticmethod", "str", "sum", "super",
            "tuple", "type", "vars", "zip",
        };

        private static readonly Dictionary<string, string> BuiltinSignatures =
            new Dictionary<string, string>
            {
                { "print", "print(*objects, sep=' ', end='\\n', file=sys.stdout, flush=False)" },
                { "range", "range(stop) | range(start, stop[, step])" },
                { "len",   "len(s) -> int" },
                { "int",   "int(x=0) | int(x, base=10)" },
                { "float", "float(x=0)" },
                { "str",   "str(object='')" },
                { "list",  "list(iterable=())" },
                { "dict",  "dict(**kwargs)" },
                { "set",   "set(iterable=())" },
                { "tuple", "tuple(iterable=())" },
                { "open",  "open(file, mode='r', …)" },
                { "input", "input([prompt]) -> str" },
                { "sorted","sorted(iterable, /, *, key=None, reverse=False)" },
                { "map",   "map(func, *iterables)" },
                { "filter","filter(function, iterable)" },
                { "enumerate","enumerate(iterable, start=0)" },
                { "zip",   "zip(*iterables)" },
                { "isinstance","isinstance(obj, classinfo) -> bool" },
                { "hasattr","hasattr(obj, name) -> bool" },
                { "getattr","getattr(obj, name[, default])" },
                { "setattr","setattr(obj, name, value)" },
                { "type",  "type(object) | type(name, bases, dict)" },
                { "super", "super() -> proxy" },
            };

        private static readonly string[] StrMethods =
        {
            "capitalize","casefold","center","count","encode","endswith","expandtabs",
            "find","format","format_map","index","isalnum","isalpha","isascii",
            "isdecimal","isdigit","isidentifier","islower","isnumeric","isprintable",
            "isspace","istitle","isupper","join","ljust","lower","lstrip","maketrans",
            "partition","removeprefix","removesuffix","replace","rfind","rindex",
            "rjust","rpartition","rsplit","rstrip","split","splitlines","startswith",
            "strip","swapcase","title","translate","upper","zfill",
        };
        private static readonly string[] ListMethods =
        { "append","clear","copy","count","extend","index","insert","pop","remove","reverse","sort" };
        private static readonly string[] DictMethods =
        { "clear","copy","fromkeys","get","items","keys","pop","popitem","setdefault","update","values" };
        private static readonly string[] TupleMethods = { "count", "index" };
        private static readonly string[] SetMethods =
        {
            "add","clear","copy","difference","difference_update","discard","intersection",
            "intersection_update","isdisjoint","issubset","issuperset","pop","remove",
            "symmetric_difference","symmetric_difference_update","union","update",
        };

        private static readonly string[,] Decorators =
        {
            { "@staticmethod", "Declare a method that receives neither self nor cls." },
            { "@classmethod", "Declare a method that receives the class as cls." },
            { "@property", "Expose a method as a managed attribute." },
            { "@abstractmethod", "Mark a method as abstract (abc.abstractmethod)." },
            { "@dataclass", "Generate data-oriented class methods (dataclasses.dataclass)." },
            { "@cached_property", "Compute an attribute once and cache it (functools.cached_property)." },
            { "@final", "Tell type checkers that a class or method cannot be overridden (typing.final)." },
            { "@override", "Tell type checkers that a method overrides a base method (typing.override)." },
        };

        private static readonly string[,] ClassDunderMethods =
        {
            { "__init__", "self", "Initialize a new instance." },
            { "__new__", "cls, *args, **kwargs", "Create and return a new instance." },
            { "__repr__", "self", "Return the developer-facing representation." },
            { "__str__", "self", "Return the user-facing string representation." },
            { "__bytes__", "self", "Return the bytes representation." },
            { "__format__", "self, format_spec", "Format the instance." },
            { "__hash__", "self", "Return the instance hash." },
            { "__bool__", "self", "Return the truth value." },
            { "__len__", "self", "Return the container length." },
            { "__iter__", "self", "Return an iterator." },
            { "__next__", "self", "Return the next item." },
            { "__contains__", "self, item", "Test membership." },
            { "__getitem__", "self, key", "Read an indexed item." },
            { "__setitem__", "self, key, value", "Assign an indexed item." },
            { "__delitem__", "self, key", "Delete an indexed item." },
            { "__enter__", "self", "Enter a context manager." },
            { "__exit__", "self, exc_type, exc_value, traceback", "Exit a context manager." },
            { "__call__", "self, *args, **kwargs", "Call the instance like a function." },
            { "__eq__", "self, other", "Compare for equality." },
            { "__ne__", "self, other", "Compare for inequality." },
            { "__lt__", "self, other", "Compare as less than." },
            { "__le__", "self, other", "Compare as less than or equal." },
            { "__gt__", "self, other", "Compare as greater than." },
            { "__ge__", "self, other", "Compare as greater than or equal." },
            { "__add__", "self, other", "Implement addition." },
            { "__sub__", "self, other", "Implement subtraction." },
            { "__mul__", "self, other", "Implement multiplication." },
            { "__truediv__", "self, other", "Implement true division." },
            { "__getattr__", "self, name", "Resolve an otherwise missing attribute." },
            { "__setattr__", "self, name, value", "Assign an attribute." },
            { "__delattr__", "self, name", "Delete an attribute." },
        };

        private static readonly Dictionary<string, string[]> ScientificTypeMethods =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "ndarray", new[] { "astype", "copy", "reshape", "ravel", "flatten", "transpose", "mean", "sum", "max", "min", "std", "var", "tolist", "item" } },
            { "Tensor", new[] { "to", "cpu", "cuda", "detach", "clone", "contiguous", "view", "reshape", "transpose", "permute", "mean", "sum", "max", "min", "item", "tolist", "numpy" } },
            { "Generator", new[] { "random", "integers", "choice", "normal", "uniform", "gamma", "permutation", "shuffle", "standard_normal" } },
            { "Path", new[] { "exists", "is_file", "is_dir", "read_text", "read_bytes", "write_text", "write_bytes", "resolve", "absolute", "expanduser", "joinpath", "with_name", "with_suffix", "glob", "rglob", "iterdir", "mkdir", "unlink", "rename", "replace" } },
            { "Pattern", new[] { "search", "match", "fullmatch", "findall", "finditer", "split", "sub", "subn" } },
            { "Match", new[] { "group", "groups", "groupdict", "start", "end", "span", "expand" } },
        };

        // Calling one of these by name constructs that exact builtin type — used by the
        // static single-file type inference below to type `x = list(...)`-style assignments.
        private static readonly HashSet<string> BuiltinContainerCtors = new HashSet<string>(StringComparer.Ordinal)
        { "list", "dict", "set", "tuple", "str", "int", "float", "bool", "frozenset", "bytearray", "bytes" };

        // -------------------------------------------------------------------- API

        public CompletionContext Classify(string source, int caretOffset)
        {
            CompletionContext ctx = new CompletionContext
            {
                Kind = CompletionContextKind.General,
                Prefix = string.Empty,
                MemberBase = string.Empty,
                PrefixStart = caretOffset,
            };
            if (string.IsNullOrEmpty(source) || caretOffset <= 0 || caretOffset > source.Length)
                return ctx;

            // Walk backwards to find prefix word boundaries.
            int p = caretOffset;
            while (p > 0 && IsIdChar(source[p - 1])) p--;
            ctx.PrefixStart = p;
            ctx.Prefix = source.Substring(p, caretOffset - p);

            // A decorator is its own completion grammar. Include the '@' in the replacement
            // range and in every label so committing `@sta` produces exactly `@staticmethod`,
            // while keywords and builtins never leak into this popup.
            if (p > 0 && source[p - 1] == '@')
            {
                ctx.PrefixStart = p - 1;
                ctx.Prefix = "@" + ctx.Prefix;
                ctx.Kind = CompletionContextKind.Decorator;
                return ctx;
            }

            // Check for a `.` immediately before the prefix (member access).
            int d = p - 1;
            if (d >= 0 && source[d] == '.')
            {
                int baseEnd = d;
                int baseStart = d;
                if (baseStart > 0)
                {
                    char prev = source[baseStart - 1];
                    // Literal receiver? Walk back for the matching open bracket / quote.
                    if (prev == ']')
                        baseStart = MatchOpen(source, baseStart - 1, '[', ']');
                    else if (prev == ')')
                        baseStart = MatchOpen(source, baseStart - 1, '(', ')');
                    else if (prev == '}')
                        baseStart = MatchOpen(source, baseStart - 1, '{', '}');
                    else if (prev == '"' || prev == '\'')
                        baseStart = FindOpenQuote(source, baseStart - 1, prev);
                    else
                    {
                        // Dotted-name chain (`a.b.prefix`).
                        while (baseStart > 0 && (IsIdChar(source[baseStart - 1]) || source[baseStart - 1] == '.'))
                            baseStart--;
                    }
                }
                ctx.MemberBase = source.Substring(baseStart, baseEnd - baseStart);
                ctx.Kind = CompletionContextKind.Member;
                return ctx;
            }

            // Import line? Scan current line for leading "import " or "from X import".
            int lineStart = source.LastIndexOf('\n', Math.Max(0, caretOffset - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            string line = source.Substring(lineStart, caretOffset - lineStart);
            string trimmed = line.TrimStart();

            // `from <module> import <cursor>` — checked BEFORE the bare-"from " case below, which
            // would otherwise also match this line and route it to the "list all modules" branch
            // (the exact bug this exists to fix: the caret here wants MEMBERS of <module>, not a
            // fresh list of importable modules). Matches with or without names already typed after
            // "import " (e.g. "from io import Str" completing "Str", or "from io import a, " with
            // more names still to come) — only the module name and the keyword shape matter.
            string fromModule = TryParseFromImportModule(trimmed);
            if (fromModule != null)
            {
                ctx.Kind = CompletionContextKind.FromImportMember;
                ctx.FromImportModule = fromModule;
                return ctx;
            }

            if (trimmed.StartsWith("import ", StringComparison.Ordinal) ||
                trimmed.StartsWith("from ", StringComparison.Ordinal))
            {
                ctx.Kind = CompletionContextKind.Import;
            }

            return ctx;
        }

        // Recognizes "from <module> import " at the start of the (trimmed) current line up to the
        // caret, returning the module name, or null if the line isn't in that shape yet (still
        // typing the module name, or no "import" keyword reached). Deliberately simple — matches
        // this codebase's existing hand-rolled line-prefix checks (e.g. the "import "/"from "
        // StartsWith checks just above) rather than a full statement parse, since the line being
        // typed is very often not yet syntactically complete.
        private static string TryParseFromImportModule(string trimmedLine)
        {
            if (!trimmedLine.StartsWith("from ", StringComparison.Ordinal)) return null;
            int afterFrom = 5; // "from ".Length
            while (afterFrom < trimmedLine.Length && trimmedLine[afterFrom] == ' ') afterFrom++;

            int moduleStart = afterFrom;
            int i = moduleStart;
            while (i < trimmedLine.Length && (IsIdChar(trimmedLine[i]) || trimmedLine[i] == '.')) i++;
            if (i == moduleStart) return null; // no module name typed yet
            string module = trimmedLine.Substring(moduleStart, i - moduleStart);

            while (i < trimmedLine.Length && trimmedLine[i] == ' ') i++;
            if (i >= trimmedLine.Length || !trimmedLine.Substring(i).StartsWith("import", StringComparison.Ordinal))
                return null; // still typing the module name — no "import" keyword reached yet
            i += "import".Length;
            if (i >= trimmedLine.Length || trimmedLine[i] != ' ') return null; // "importx" etc — not the keyword

            return module;
        }

        public List<CompletionItem> GetCompletions(
            string source, int caretOffset, string filePath,
            SolutionModel solution)
        {
            CompletionContext ctx = Classify(source, caretOffset);
            return GetCompletions(source, caretOffset, filePath, solution, ctx);
        }

        public List<CompletionItem> GetCompletions(
            string source, int caretOffset, string filePath,
            SolutionModel solution, CompletionContext ctx)
        {
            var items = new List<CompletionItem>();
            LastUnresolvedModuleFqn = null;

            ModuleSyntax module = null;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source ?? string.Empty, filePath).Module; }
            catch { /* be lenient — we still offer keywords/builtins from strings */ }

            switch (ctx.Kind)
            {
                case CompletionContextKind.Decorator:
                    AddDecoratorCompletions(items);
                    break;
                case CompletionContextKind.Member:
                    AddMemberCompletions(items, ctx.MemberBase, caretOffset, module, solution);
                    break;
                case CompletionContextKind.Import:
                    AddImportCompletions(items, solution);
                    break;
                case CompletionContextKind.FromImportMember:
                    AddFromImportMemberCompletions(items, ctx.FromImportModule, solution);
                    break;
                default:
                    AddGeneralCompletions(items, source, caretOffset, module, solution);
                    break;
            }

            // The popup commits by display label, so two rows named `def` cannot be
            // distinguished at commit time. De-dup by label (not kind); snippets are added
            // before keywords and therefore deliberately win shortcut collisions.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unique = new List<CompletionItem>(items.Count);
            foreach (var it in items)
            {
                // Python's leading-underscore convention marks implementation details. Keep
                // ordinary `obj.` completion focused on the public surface, but reveal hidden
                // and dunder members as soon as the user explicitly types `_`.
                if (ctx.Kind == CompletionContextKind.Member &&
                    !StartsWithUnderscore(ctx.Prefix) && StartsWithUnderscore(it.Label))
                    continue;
                if (seen.Add(it.Label)) unique.Add(it);
            }
            return unique;
        }

        private static bool StartsWithUnderscore(string value)
        {
            return !string.IsNullOrEmpty(value) && value[0] == '_';
        }

        private static void AddDecoratorCompletions(List<CompletionItem> items)
        {
            for (int i = 0; i < Decorators.GetLength(0); i++)
                items.Add(new CompletionItem(
                    Decorators[i, 0], CompletionKind.Keyword, null,
                    "Python decorator", Decorators[i, 1]));
        }

        // -------------------------------------------------------------- general scope

        private void AddGeneralCompletions(
            List<CompletionItem> items, string source, int caretOffset,
            ModuleSyntax module, SolutionModel solution)
        {
            foreach (CompletionItem snippet in PythonSnippetCatalog.Items()) items.Add(snippet);

            foreach (string kw in Keywords)
                items.Add(new CompletionItem(kw, CompletionKind.Keyword, null, KeywordDetail(kw)));

            foreach (string bn in Builtins)
            {
                BuiltinSignatures.TryGetValue(bn, out string sig);
                items.Add(new CompletionItem(bn, CompletionKind.Builtin, null, sig));
            }

            if (IsDefNameInClassBody(source, caretOffset)) AddClassDunderCompletions(items);

            if (module != null)
            {
                // Module-scope symbols.
                foreach (StatementSyntax s in module.Statements)
                {
                    if (s is FunctionDefSyntax fd)
                        items.Add(new CompletionItem(fd.Name, CompletionKind.Function, null, FunctionDetail(fd), DocstringOf(fd.Body)));
                    else if (s is ClassDefSyntax cd)
                        items.Add(new CompletionItem(cd.Name, CompletionKind.Class, null, "class " + cd.Name, DocstringOf(cd.Body)));
                    else if (s is ImportStatement imp)
                    {
                        foreach (var n in imp.Names)
                        {
                            string local = !string.IsNullOrEmpty(n.Alias)
                                ? n.Alias
                                : (n.Name?.Parts != null && n.Name.Parts.Count > 0 ? n.Name.Parts[0] : null);
                            if (!string.IsNullOrEmpty(local))
                                items.Add(new CompletionItem(local, CompletionKind.Module));
                        }
                    }
                    else if (s is FromImportStatement fi && !fi.IsStar)
                    {
                        foreach (var kv in fi.Names)
                        {
                            string local = string.IsNullOrEmpty(kv.Value) ? kv.Key : kv.Value;
                            if (!string.IsNullOrEmpty(local))
                                items.Add(new CompletionItem(local, CompletionKind.Module));
                        }
                    }
                }

                // Enclosing-function params + locals.
                FunctionDefSyntax enclosing = FindEnclosingFunction(module, caretOffset);
                if (enclosing != null)
                {
                    foreach (var p in enclosing.Parameters)
                        if (!string.IsNullOrEmpty(p.Name))
                            items.Add(new CompletionItem(p.Name, CompletionKind.Parameter, null,
                                "parameter of " + enclosing.Name));
                    if (enclosing.Body != null)
                    {
                        var bindings = new HashSet<string>(StringComparer.Ordinal);
                        CollectBodyBindings(enclosing.Body, bindings);
                        foreach (string b in bindings)
                            items.Add(new CompletionItem(b, CompletionKind.Variable));
                    }
                }
            }

            // Solution-wide types + functions (cross-file).
            if (solution != null)
            {
                foreach (TypeModel t in solution.GetAllTypes())
                {
                    if (!string.IsNullOrEmpty(t.Name))
                        items.Add(new CompletionItem(t.Name, CompletionKind.Class, null, t.FullyQualifiedName));
                }
                foreach (ProjectModel p in solution.Projects)
                {
                    foreach (FunctionModel fn in p.Functions)
                    {
                        if (!string.IsNullOrEmpty(fn.Name))
                            items.Add(new CompletionItem(fn.Name, CompletionKind.Function, null,
                                fn.FullyQualifiedName));
                    }
                }
            }
        }

        // -------------------------------------------------------------- member

        private void AddMemberCompletions(
            List<CompletionItem> items, string memberBase, int caretOffset,
            ModuleSyntax module, SolutionModel solution)
        {
            if (string.IsNullOrEmpty(memberBase)) return;

            // If the base looks like a string/list/dict literal, use hard-coded lists.
            char first = memberBase[0];
            if (first == '"' || first == '\'')
            {
                foreach (string m in StrMethods) items.Add(CallableCompletion(m, CompletionKind.Method));
                return;
            }
            if (first == '[')
            {
                foreach (string m in ListMethods) items.Add(CallableCompletion(m, CompletionKind.Method));
                return;
            }
            if (first == '{')
            {
                foreach (string m in DictMethods) items.Add(CallableCompletion(m, CompletionKind.Method));
                return;
            }

            // Try the rightmost simple name in the dotted chain — resolve it to a TypeModel.
            int lastDot = memberBase.LastIndexOf('.');
            string leaf = lastDot < 0 ? memberBase : memberBase.Substring(lastDot + 1);

            // 1) Direct class-name match: `ClassName.member` (solution-wide, then this file).
            if (!string.IsNullOrEmpty(leaf))
            {
                TypeModel solutionMatch = FindSolutionType(solution, leaf);
                if (solutionMatch != null) { AddSolutionTypeMembers(items, solutionMatch); return; }
                ClassDefSyntax localMatch = FindLocalClass(module, leaf);
                if (localMatch != null) { AddClassMembers(items, localMatch); return; }
            }

            // 2) `self.X` — members of the actual enclosing class (falls back to the union of
            // every class in the file if the caret's containing class couldn't be pinned down).
            if (string.Equals(memberBase, "self", StringComparison.Ordinal) && module != null)
            {
                ClassDefSyntax enclosing = FindEnclosingClass(module, caretOffset);
                if (enclosing != null) { AddClassMembers(items, enclosing); return; }
                foreach (ClassDefSyntax cd in AllClasses(module)) AddClassMembers(items, cd);
                return;
            }

            // 3) `self.attr.` — resolve attr's type from `self.attr = …` assignments in the class.
            if (lastDot > 0 && string.Equals(memberBase.Substring(0, lastDot), "self", StringComparison.Ordinal))
            {
                string attrType = ResolveSelfAttributeType(leaf, module, caretOffset, 0);
                if (!string.IsNullOrEmpty(attrType)) AddResolvedTypeMembers(items, attrType, module, solution);
                return;
            }

            // 4) Import-bound name (`np.` where the file has `import numpy as np`, or `numpy.`
            // directly). Resolved via the file's own import statements, then looked up in the
            // (optional) live-interpreter Catalog for that module's scanned members. Tried before
            // variable-assignment inference below: an import binding is an unambiguous signal —
            // `np`/`numpy` is never itself reassigned in reasonable code — so there's nothing case
            // 5 could usefully add once a name resolves to an imported module.
            if (lastDot < 0 && module != null)
            {
                string moduleFqn = ResolveImportAlias(module, memberBase);
                if (!string.IsNullOrEmpty(moduleFqn))
                {
                    if (Catalog != null && Catalog.HasModule(moduleFqn))
                        foreach (HelixPythonNamespace.Catalog.CatalogSymbol sym in Catalog.MembersOfModule(moduleFqn))
                            items.Add(CatalogSymbolToItem(sym));
                    else
                        LastUnresolvedModuleFqn = moduleFqn;
                    return;
                }
            }

            // 5) Ordinary variable — static, single-file inference from its most recent
            // assignment before the caret (instantiation, literal, annotated parameter, or a
            // same-scope copy of another already-typed name).
            if (lastDot < 0)
            {
                string inferredType = ResolveNameType(memberBase, module, caretOffset, 0);
                if (!string.IsNullOrEmpty(inferredType)) AddResolvedTypeMembers(items, inferredType, module, solution);
            }
        }

        // The local name an `import X [as Y]` statement binds, resolved to the FQN Catalog
        // indexes symbols under. Aliased (`import numpy as np`) binds the FULL dotted target to
        // the alias; unaliased (`import numpy` / `import os.path`) only binds the TOP-level
        // package name in Python's own namespace rules, so `os.path.` (a dotted memberBase) is
        // deliberately out of scope here — only the `lastDot < 0` single-name case is handled.
        private static string ResolveImportAlias(ModuleSyntax module, string localName)
        {
            if (module == null || string.IsNullOrEmpty(localName)) return null;
            foreach (StatementSyntax s in module.Statements)
            {
                if (!(s is ImportStatement imp)) continue;
                foreach (var n in imp.Names)
                {
                    if (n.Name?.Parts == null || n.Name.Parts.Count == 0) continue;
                    string local = !string.IsNullOrEmpty(n.Alias) ? n.Alias : n.Name.Parts[0];
                    if (!string.Equals(local, localName, StringComparison.Ordinal)) continue;
                    return !string.IsNullOrEmpty(n.Alias)
                        ? string.Join(".", n.Name.Parts)
                        : n.Name.Parts[0];
                }
            }
            return null;
        }

        private static CompletionItem CatalogSymbolToItem(HelixPythonNamespace.Catalog.CatalogSymbol sym)
        {
            CompletionKind kind;
            switch (sym.Kind)
            {
                case "class": kind = CompletionKind.Class; break;
                case "method": kind = CompletionKind.Method; break;
                case "function": kind = CompletionKind.Function; break;
                default: kind = CompletionKind.Variable; break;
            }
            string detail = !string.IsNullOrEmpty(sym.Signature) ? sym.Name + sym.Signature : sym.Name;
            string doc = !string.IsNullOrEmpty(sym.FullDoc) ? sym.FullDoc : sym.Summary;
            return new CompletionItem(sym.Name, kind, null, detail, string.IsNullOrEmpty(doc) ? null : doc);
        }

        // Quick Info variant: the user is looking at a name they already wrote, so the one-line
        // detail names it fully (`asyncio.Semaphore(value=1)`) rather than bare, the way a
        // completion list entry — where the module is implied by what was typed — does.
        private static CompletionItem CatalogSymbolToItem(HelixPythonNamespace.Catalog.CatalogSymbol sym, string displayFqn)
        {
            CompletionItem plain = CatalogSymbolToItem(sym);
            if (string.IsNullOrEmpty(displayFqn)) return plain;
            string detail = !string.IsNullOrEmpty(sym.Signature) ? displayFqn + sym.Signature : displayFqn;
            if (string.Equals(sym.Kind, "class", StringComparison.Ordinal)) detail = "class " + detail;
            else if (string.Equals(sym.Kind, "module", StringComparison.Ordinal)) detail = "module " + displayFqn;
            return new CompletionItem(sym.Name, plain.Kind, null, detail, plain.Documentation);
        }

        // -------------------------------------------------------------- member: shared lookups

        private void AddClassMembers(List<CompletionItem> items, ClassDefSyntax cd)
        {
            if (cd == null || cd.Body == null) return;
            HashSet<string> methodNames = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> attrNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (StatementSyntax inner in cd.Body.Statements)
            {
                if (inner is FunctionDefSyntax m && methodNames.Add(m.Name))
                    items.Add(CallableCompletion(m.Name, CompletionKind.Method,
                        FunctionDetail(m), DocstringOf(m.Body)));
            }
            // Instance attributes: every `self.NAME` referenced anywhere in the class
            // (assignments in __init__ or elsewhere, and reads).
            foreach (SyntaxNode n in Walk(cd))
            {
                if (n is AttributeExpression ae && ae.Target is NameExpression tn
                    && string.Equals(tn.Name, "self", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(ae.Name))
                {
                    attrNames.Add(ae.Name);
                }
            }
            foreach (string a in attrNames)
                if (!methodNames.Contains(a))
                    items.Add(new CompletionItem(a, CompletionKind.Field, null, "instance attribute"));
        }

        private static TypeModel FindSolutionType(SolutionModel solution, string name)
        {
            if (solution == null || string.IsNullOrEmpty(name)) return null;
            foreach (TypeModel t in solution.GetAllTypes())
                if (string.Equals(t.Name, name, StringComparison.Ordinal)) return t;
            return null;
        }

        private static void AddSolutionTypeMembers(List<CompletionItem> items, TypeModel t)
        {
            foreach (MemberModel m in t.Members)
            {
                var kind = m.MemberKind == HelixPythonNamespace.Core.Model.Enums.MemberKind.Property
                    ? CompletionKind.Property : CompletionKind.Method;
                items.Add(kind == CompletionKind.Method
                    ? CallableCompletion(m.Name, kind, MemberDetail(m))
                    : new CompletionItem(m.Name, kind, null, MemberDetail(m)));
            }
        }

        private static CompletionItem CallableCompletion(
            string name, CompletionKind kind, string detail = null, string documentation = null)
        {
            return new CompletionItem(name, kind, name + "()", detail, documentation);
        }

        private static ClassDefSyntax FindLocalClass(ModuleSyntax module, string name)
        {
            if (module == null || string.IsNullOrEmpty(name)) return null;
            foreach (StatementSyntax s in module.Statements)
                if (s is ClassDefSyntax cd && string.Equals(cd.Name, name, StringComparison.Ordinal)) return cd;
            return null;
        }

        private static FunctionDefSyntax FindLocalFunction(ModuleSyntax module, string name)
        {
            if (module == null || string.IsNullOrEmpty(name)) return null;
            foreach (StatementSyntax s in module.Statements)
                if (s is FunctionDefSyntax fd && string.Equals(fd.Name, name, StringComparison.Ordinal)) return fd;
            return null;
        }

        private static IEnumerable<ClassDefSyntax> AllClasses(ModuleSyntax module)
        {
            if (module == null) yield break;
            foreach (StatementSyntax s in module.Statements)
                if (s is ClassDefSyntax cd) yield return cd;
        }

        private static ClassDefSyntax FindEnclosingClass(ModuleSyntax module, int caretOffset)
        {
            ClassDefSyntax containing = null;
            ClassDefSyntax lastBeforeCaret = null;
            foreach (SyntaxNode n in Walk(module))
            {
                if (!(n is ClassDefSyntax cd)) continue;
                int s = cd.Span.Start.Offset, e = cd.Span.End.Offset;
                if (caretOffset >= s && caretOffset <= e)
                {
                    if (containing == null || cd.Span.Start.Offset > containing.Span.Start.Offset)
                        containing = cd;
                }
                if (s <= caretOffset)
                {
                    if (lastBeforeCaret == null || cd.Span.Start.Offset > lastBeforeCaret.Span.Start.Offset)
                        lastBeforeCaret = cd;
                }
            }
            return containing ?? lastBeforeCaret;
        }

        private void AddResolvedTypeMembers(
            List<CompletionItem> items, string typeName, ModuleSyntax module, SolutionModel solution)
        {
            if (AddBuiltinTypeMembers(items, typeName)) return;
            TypeModel t = FindSolutionType(solution, typeName);
            if (t != null) { AddSolutionTypeMembers(items, t); return; }
            ClassDefSyntax local = FindLocalClass(module, typeName);
            if (local != null) AddClassMembers(items, local);
        }

        private bool AddBuiltinTypeMembers(List<CompletionItem> items, string typeName)
        {
            typeName = CompletionBaseType(typeName);
            if (string.Equals(typeName, "str", StringComparison.Ordinal))
            { foreach (string m in StrMethods) items.Add(CallableCompletion(m, CompletionKind.Method)); return true; }
            if (string.Equals(typeName, "list", StringComparison.Ordinal))
            { foreach (string m in ListMethods) items.Add(CallableCompletion(m, CompletionKind.Method)); return true; }
            if (string.Equals(typeName, "dict", StringComparison.Ordinal))
            { foreach (string m in DictMethods) items.Add(CallableCompletion(m, CompletionKind.Method)); return true; }
            if (string.Equals(typeName, "tuple", StringComparison.Ordinal))
            { foreach (string m in TupleMethods) items.Add(CallableCompletion(m, CompletionKind.Method)); return true; }
            if (string.Equals(typeName, "set", StringComparison.Ordinal) || string.Equals(typeName, "frozenset", StringComparison.Ordinal))
            { foreach (string m in SetMethods) items.Add(CallableCompletion(m, CompletionKind.Method)); return true; }
            string[] scientific;
            if (ScientificTypeMethods.TryGetValue(typeName, out scientific))
            {
                foreach (string m in scientific)
                    items.Add(new CompletionItem(m, CompletionKind.Method, null,
                        typeName + "." + m + "(…)", "member of " + typeName));
                return true;
            }
            return false;
        }

        private static void AddClassDunderCompletions(List<CompletionItem> items)
        {
            for (int i = 0; i < ClassDunderMethods.GetLength(0); i++)
            {
                string name = ClassDunderMethods[i, 0];
                string parameters = ClassDunderMethods[i, 1];
                string signature = name + "(" + parameters + ")";
                items.Add(new CompletionItem(name, CompletionKind.Method,
                    signature + ":", "def " + signature, ClassDunderMethods[i, 2]));
            }
        }

        private static bool IsDefNameInClassBody(string source, int caretOffset)
        {
            if (string.IsNullOrEmpty(source) || caretOffset < 0) return false;
            caretOffset = Math.Min(caretOffset, source.Length);
            int lineStart = source.LastIndexOf('\n', Math.Max(0, caretOffset - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            string current = source.Substring(lineStart, caretOffset - lineStart);
            int currentIndent = LeadingIndent(current);
            string trimmed = current.TrimStart(' ', '\t');
            if (!trimmed.StartsWith("def ", StringComparison.Ordinal)
                && !trimmed.StartsWith("async def ", StringComparison.Ordinal)) return false;

            int scanEnd = lineStart > 0 ? lineStart - 1 : 0;
            while (scanEnd > 0)
            {
                int previousStart = source.LastIndexOf('\n', Math.Max(0, scanEnd - 1));
                previousStart = previousStart < 0 ? 0 : previousStart + 1;
                string line = source.Substring(previousStart, scanEnd - previousStart).TrimEnd('\r');
                string content = line.TrimStart(' ', '\t');
                if (content.Length > 0 && !content.StartsWith("#", StringComparison.Ordinal))
                {
                    int indent = LeadingIndent(line);
                    if (indent < currentIndent)
                        return content.StartsWith("class ", StringComparison.Ordinal) && content.EndsWith(":", StringComparison.Ordinal);
                }
                if (previousStart == 0) break;
                scanEnd = previousStart - 1;
            }
            return false;
        }

        private static int LeadingIndent(string line)
        {
            int width = 0;
            while (width < line.Length && (line[width] == ' ' || line[width] == '\t')) width++;
            return width;
        }

        private static string CompletionBaseType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return typeName;
            string value = typeName.Trim();
            int union = value.IndexOf('|');
            if (union >= 0) value = value.Substring(0, union).Trim();
            int bracket = value.IndexOf('[');
            if (bracket >= 0) value = value.Substring(0, bracket);
            int dot = value.LastIndexOf('.');
            if (dot >= 0) value = value.Substring(dot + 1);
            return value.Trim();
        }

        // -------------------------------------------------------------- member: static single-file type inference
        //
        // Deliberately bounded: this resolves the common, unambiguous cases (direct
        // instantiation, literals, annotated parameters, same-scope copies, `self.attr`
        // assignments) via plain textual/AST scanning of the current file — no cross-function
        // control-flow analysis, no generics, no overload resolution. Anything it can't
        // confidently resolve returns null, which callers treat exactly like "no completions
        // available" (today's behavior), so this can only add coverage, never regress it.

        private const int MaxInferenceDepth = 6;

        private string ResolveNameType(string name, ModuleSyntax module, int caretOffset, int depth)
        {
            if (depth > MaxInferenceDepth || module == null || string.IsNullOrEmpty(name)) return null;

            FunctionDefSyntax fn = FindEnclosingFunction(module, caretOffset);
            if (fn != null)
            {
                foreach (ParameterSyntax p in fn.Parameters)
                    if (string.Equals(p.Name, name, StringComparison.Ordinal) && p.Annotation != null)
                        return TypeExpressionName(p.Annotation);

                AssignmentStatement best = BestNameAssignment(Walk(fn), name, caretOffset);
                if (best != null)
                {
                    if (best.Annotation != null) return TypeExpressionName(best.Annotation);
                    return ResolveExpressionType(best.Value, module, best.Span.Start.Offset, depth + 1);
                }
            }

            AssignmentStatement modBest = BestNameAssignment(Walk(module), name, caretOffset);
            if (modBest != null)
            {
                if (modBest.Annotation != null) return TypeExpressionName(modBest.Annotation);
                return ResolveExpressionType(modBest.Value, module, modBest.Span.Start.Offset, depth + 1);
            }

            return null;
        }

        private string ResolveSelfAttributeType(string attrName, ModuleSyntax module, int caretOffset, int depth)
        {
            if (depth > MaxInferenceDepth || module == null || string.IsNullOrEmpty(attrName)) return null;

            ClassDefSyntax enclosing = FindEnclosingClass(module, caretOffset);
            IEnumerable<ClassDefSyntax> candidates = enclosing != null
                ? new[] { enclosing }
                : AllClasses(module);

            foreach (ClassDefSyntax cd in candidates)
            {
                AssignmentStatement best = BestSelfAttrAssignment(Walk(cd), attrName, caretOffset);
                if (best != null)
                {
                    if (best.Annotation != null) return TypeExpressionName(best.Annotation);
                    return ResolveExpressionType(best.Value, module, best.Span.Start.Offset, depth + 1);
                }
            }
            return null;
        }

        private string ResolveExpressionType(ExpressionSyntax expr, ModuleSyntax module, int caretOffset, int depth)
        {
            if (depth > MaxInferenceDepth || expr == null) return null;

            if (expr is LiteralExpression le) return LiteralTypeName(le);
            if (expr is ListExpression) return "list";
            if (expr is DictExpression) return "dict";
            if (expr is TupleExpression) return "tuple";
            if (expr is SetExpression) return "set";
            if (expr is ParenthesizedExpression pe)
                return ResolveExpressionType(pe.Inner, module, caretOffset, depth + 1);
            if (expr is SubscriptExpression se)
                return ResolveExpressionType(se.Target, module, caretOffset, depth + 1);
            if (expr is ConditionalExpression conditional)
            {
                string yes = ResolveExpressionType(conditional.IfTrue, module, caretOffset, depth + 1);
                string no = ResolveExpressionType(conditional.IfFalse, module, caretOffset, depth + 1);
                return string.Equals(yes, no, StringComparison.Ordinal) ? yes : null;
            }

            if (expr is CallExpression ce && ce.Callee is NameExpression callee)
            {
                if (BuiltinContainerCtors.Contains(callee.Name)) return callee.Name;
                if (FindLocalClass(module, callee.Name) != null) return callee.Name;
                FunctionDefSyntax localFn = FindLocalFunction(module, callee.Name);
                if (localFn != null && localFn.ReturnAnnotation != null)
                    return TypeExpressionName(localFn.ReturnAnnotation);
                string importedModule = ResolveFromImportModule(module, callee.Name, out string originalName);
                if (!string.IsNullOrEmpty(importedModule) && !string.IsNullOrEmpty(originalName) &&
                    char.IsUpper(originalName[0])) return originalName;
                return null;
            }

            if (expr is CallExpression localMethodCall && localMethodCall.Callee is AttributeExpression localMethod)
            {
                FunctionDefSyntax method = FindMethodDefinition(module, localMethod);
                if (method != null && method.ReturnAnnotation != null)
                    return TypeExpressionName(method.ReturnAnnotation);
            }

            // `mod.factory()` — a call through an imported module. The only place a return type
            // can come from is the catalogued signature, and only when the live interpreter
            // actually exposed an annotation (plenty of stdlib builtins expose none, which is why
            // Quick Info also keeps a catalog-side fallback for the member itself).
            if (expr is CallExpression mce && mce.Callee is AttributeExpression mcallee && Catalog != null)
            {
                string modFqn = ImportedModuleFqnOf(module, mcallee.Target);
                if (!string.IsNullOrEmpty(modFqn))
                {
                    HelixPythonNamespace.Catalog.CatalogSymbol sym = Catalog.Lookup(modFqn + "." + mcallee.Name);
                    if (sym != null)
                    {
                        if (string.Equals(sym.Kind, "class", StringComparison.Ordinal)) return sym.Name;
                        string returned = ReturnTypeFromSignature(sym.Signature);
                        if (!string.IsNullOrEmpty(returned)) return returned;
                    }
                }
            }

            if (expr is CallExpression standardCall && standardCall.Callee is AttributeExpression standardCallee)
            {
                string qualified = ExpressionQualifiedName(standardCallee);
                string known = StandardCallReturnType(qualified);
                if (!string.IsNullOrEmpty(known)) return known;
                string receiverType = ResolveExpressionType(standardCallee.Target, module, caretOffset, depth + 1);
                if (receiverType == "ndarray" || receiverType == "Tensor") return receiverType;
                if (receiverType == "Generator")
                    return standardCallee.Name == "shuffle" ? null : "ndarray";
                if (receiverType == "Path")
                {
                    if (standardCallee.Name == "read_text") return "str";
                    if (standardCallee.Name == "read_bytes") return "bytes";
                    if (standardCallee.Name == "resolve" || standardCallee.Name == "absolute" ||
                        standardCallee.Name == "expanduser" || standardCallee.Name == "joinpath" ||
                        standardCallee.Name == "with_name" || standardCallee.Name == "with_suffix") return "Path";
                }
            }

            if (expr is NameExpression ne)
                return ResolveNameType(ne.Name, module, caretOffset, depth + 1);

            if (expr is AttributeExpression ae && ae.Target is NameExpression tn
                && string.Equals(tn.Name, "self", StringComparison.Ordinal))
                return ResolveSelfAttributeType(ae.Name, module, caretOffset, depth + 1);

            return null;
        }

        // The simple type name a catalogued signature returns: "(x: int) -> Handle" -> "Handle".
        // Only a top-level `->` counts (one inside a `Callable[[int], str]` parameter annotation
        // is not this function's return type), and only the last one, so a signature whose
        // parameters carry their own arrows still resolves correctly.
        private static string ReturnTypeFromSignature(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return null;
            int depth = 0, arrow = -1;
            for (int i = 0; i + 1 < signature.Length; i++)
            {
                char c = signature[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (depth == 0 && c == '-' && signature[i + 1] == '>') arrow = i;
            }
            if (arrow < 0) return null;
            string returned = signature.Substring(arrow + 2).Trim();
            if (returned.Length == 0) return null;

            // Reduce `typing.List[int]` / `asyncio.TimerHandle` to the bare name the member
            // lookups index by — the same reduction TypeExpressionName does for annotations.
            int bracket = returned.IndexOfAny(new[] { '[', '(' });
            if (bracket >= 0) returned = returned.Substring(0, bracket);
            int dot = returned.LastIndexOf('.');
            if (dot >= 0) returned = returned.Substring(dot + 1);
            returned = returned.Trim();
            foreach (char c in returned)
                if (!IsIdChar(c)) return null;
            return returned.Length == 0 ? null : returned;
        }

        private static string LiteralTypeName(LiteralExpression le)
        {
            if (le == null || le.Token == null) return null;
            switch (le.Token.Kind)
            {
                case TokenKind.String: return "str";
                case TokenKind.Number:
                    if (le.Token.Value is double) return "float";
                    if (le.Token.Value is System.Numerics.Complex) return "complex";
                    return "int";
                case TokenKind.KwTrue:
                case TokenKind.KwFalse:
                    return "bool";
                default:
                    return null;
            }
        }

        private static string TypeExpressionName(ExpressionSyntax annotation)
        {
            if (annotation is NameExpression ne) return ne.Name;
            if (annotation is AttributeExpression ae) return ae.Name;
            if (annotation is SubscriptExpression se) return TypeExpressionName(se.Target);
            if (annotation is ParenthesizedExpression pe) return TypeExpressionName(pe.Inner);
            return null;
        }

        private static string ExpressionQualifiedName(ExpressionSyntax expression)
        {
            if (expression is NameExpression name) return name.Name;
            if (expression is AttributeExpression attribute)
            {
                string owner = ExpressionQualifiedName(attribute.Target);
                return string.IsNullOrEmpty(owner) ? null : owner + "." + attribute.Name;
            }
            return null;
        }

        private static string StandardCallReturnType(string qualified)
        {
            if (string.IsNullOrEmpty(qualified)) return null;
            if (qualified == "np.random.default_rng" || qualified == "numpy.random.default_rng") return "Generator";
            if (qualified.StartsWith("np.", StringComparison.Ordinal) || qualified.StartsWith("numpy.", StringComparison.Ordinal))
            {
                string leaf = qualified.Substring(qualified.LastIndexOf('.') + 1);
                if (leaf == "array" || leaf == "asarray" || leaf == "zeros" || leaf == "ones" ||
                    leaf == "empty" || leaf == "full" || leaf == "arange" || leaf == "linspace" ||
                    leaf == "concatenate" || leaf == "stack") return "ndarray";
            }
            if (qualified.StartsWith("torch.", StringComparison.Ordinal)) return "Tensor";
            if (qualified == "pathlib.Path") return "Path";
            if (qualified == "re.compile") return "Pattern";
            return null;
        }

        private static FunctionDefSyntax FindMethodDefinition(ModuleSyntax module, AttributeExpression call)
        {
            if (module == null || call == null || string.IsNullOrEmpty(call.Name)) return null;
            ClassDefSyntax owner = null;
            if (call.Target is NameExpression target)
            {
                if (target.Name == "self" || target.Name == "cls")
                    owner = call.FirstAncestor<ClassDefSyntax>();
                else
                    owner = FindLocalClass(module, target.Name);
            }
            if (owner == null || owner.Body == null) return null;
            foreach (StatementSyntax statement in owner.Body.Statements)
                if (statement is FunctionDefSyntax method && method.Name == call.Name) return method;
            return null;
        }

        private static AssignmentStatement BestNameAssignment(IEnumerable<SyntaxNode> nodes, string name, int caretOffset)
        {
            AssignmentStatement bestBefore = null;
            AssignmentStatement fallback = null;
            foreach (SyntaxNode n in nodes)
            {
                if (!(n is AssignmentStatement asn)) continue;
                bool matches = false;
                foreach (ExpressionSyntax t in asn.Targets)
                    if (t is NameExpression tne && string.Equals(tne.Name, name, StringComparison.Ordinal)) { matches = true; break; }
                if (!matches) continue;
                if (fallback == null || asn.Span.Start.Offset > fallback.Span.Start.Offset) fallback = asn;
                if (asn.Span.Start.Offset <= caretOffset &&
                    (bestBefore == null || asn.Span.Start.Offset > bestBefore.Span.Start.Offset))
                    bestBefore = asn;
            }
            return bestBefore ?? fallback;
        }

        private static AssignmentStatement BestSelfAttrAssignment(IEnumerable<SyntaxNode> nodes, string attrName, int caretOffset)
        {
            AssignmentStatement bestBefore = null;
            AssignmentStatement fallback = null;
            foreach (SyntaxNode n in nodes)
            {
                if (!(n is AssignmentStatement asn)) continue;
                bool matches = false;
                foreach (ExpressionSyntax t in asn.Targets)
                    if (t is AttributeExpression ae && ae.Target is NameExpression tn
                        && string.Equals(tn.Name, "self", StringComparison.Ordinal)
                        && string.Equals(ae.Name, attrName, StringComparison.Ordinal)) { matches = true; break; }
                if (!matches) continue;
                if (fallback == null || asn.Span.Start.Offset > fallback.Span.Start.Offset) fallback = asn;
                if (asn.Span.Start.Offset <= caretOffset &&
                    (bestBefore == null || asn.Span.Start.Offset > bestBefore.Span.Start.Offset))
                    bestBefore = asn;
            }
            return bestBefore ?? fallback;
        }

        // -------------------------------------------------------------- import

        // One-line summary for each entry in AddImportCompletions's hardcoded module list, so the
        // RHS doc panel (CompletionDocPanel) has something to show — it hides itself whenever both
        // CompletionItem.Detail and .Documentation are empty, which is why bare `import`/`from`
        // suggestions used to show no doc at all. Wording matches each module's own one-line
        // summary in the Python standard library documentation. No live/catalog source exists for
        // a MODULE's own docstring (only its MEMBERS' — see AddFromImportMemberCompletions), so
        // this is a static table rather than something scanned.
        private static readonly Dictionary<string, string> CommonModuleSummaries =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "os", "Miscellaneous operating system interfaces." },
            { "sys", "System-specific parameters and functions." },
            { "math", "Mathematical functions (sin, sqrt, floor, etc.)." },
            { "json", "JSON encoder and decoder." },
            { "re", "Regular expression matching operations." },
            { "datetime", "Basic date and time types." },
            { "collections", "Container datatypes (deque, Counter, OrderedDict, etc.)." },
            { "itertools", "Functions creating iterators for efficient looping." },
            { "functools", "Higher-order functions and operations on callable objects." },
            { "io", "Core tools for working with streams." },
            { "pathlib", "Object-oriented filesystem paths." },
            { "subprocess", "Subprocess management." },
            { "threading", "Thread-based parallelism." },
            { "multiprocessing", "Process-based parallelism." },
            { "socket", "Low-level networking interface." },
            { "logging", "Flexible event logging for applications." },
            { "unittest", "Unit testing framework." },
            { "argparse", "Parser for command-line options, arguments, and sub-commands." },
            { "csv", "CSV file reading and writing." },
            { "sqlite3", "DB-API 2.0 interface for SQLite databases." },
            { "hashlib", "Secure hashes and message digests." },
            { "random", "Generate pseudo-random numbers." },
            { "string", "Common string operations." },
            { "time", "Time access and conversions." },
            { "copy", "Shallow and deep copy operations." },
            { "pickle", "Python object serialization." },
            { "typing", "Support for type hints." },
            { "abc", "Abstract base classes." },
            { "enum", "Support for enumerations." },
            { "dataclasses", "Generate special methods on user-defined classes." },
            { "contextlib", "Utilities for with-statement contexts." },
            { "operator", "Standard operators as functions." },
            { "shutil", "High-level file operations." },
            { "glob", "Unix style pathname pattern expansion." },
            { "tempfile", "Generate temporary files and directories." },
            { "traceback", "Print or retrieve a stack traceback." },
            { "inspect", "Inspect live objects (modules, classes, functions, etc.)." },
            { "pprint", "Data pretty printer." },
            { "textwrap", "Text wrapping and filling." },
            { "warnings", "Warning control." },
            { "decimal", "Decimal fixed-point and floating-point arithmetic." },
            { "fractions", "Rational numbers." },
            { "numbers", "Numeric abstract base classes." },
            { "cmath", "Mathematical functions for complex numbers." },
            { "statistics", "Mathematical statistics functions." },
            { "asyncio", "Asynchronous I/O, event loop, coroutines and tasks." },
            { "heapq", "Heap queue algorithm (priority queue)." },
            { "bisect", "Array bisection algorithm for maintaining sorted lists." },
            { "uuid", "UUID objects according to RFC 4122." },
            { "base64", "Base16, Base32, Base64, Base85 data encodings." },
            { "struct", "Interpret bytes as packed binary data." },
            { "queue", "A synchronized queue class." },
            { "secrets", "Generate secure random numbers for managing secrets." },
            { "signal", "Set handlers for asynchronous events." },
            { "zipfile", "Work with ZIP archives." },
            { "configparser", "Configuration file parser." },
            { "urllib", "URL handling modules." },
        };

        private void AddImportCompletions(List<CompletionItem> items, SolutionModel solution)
        {
            AddWorkspaceModuleCompletions(items, solution);

            foreach (KeyValuePair<string, string> kv in CommonModuleSummaries)
                items.Add(new CompletionItem(kv.Key, CompletionKind.Module, null, kv.Value));

            // Plus solution-project names (approximate stand-in for in-solution packages).
            if (solution != null)
            {
                foreach (ProjectModel p in solution.Projects)
                    if (!string.IsNullOrEmpty(p.Name))
                        items.Add(new CompletionItem(p.Name, CompletionKind.Module, null, "solution project"));
            }
        }

        private static void AddWorkspaceModuleCompletions(List<CompletionItem> items, SolutionModel solution)
        {
            if (solution == null) return;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProjectModel project in solution.Projects)
            {
                if (project == null || project.Language != ProjectLanguage.Python || project.ContentFiles == null)
                    continue;

                foreach (string contentFile in project.ContentFiles)
                {
                    string moduleName = WorkspaceModuleName(contentFile, project);
                    if (string.IsNullOrEmpty(moduleName)) continue;
                    if (seen.Add(moduleName))
                        items.Add(new CompletionItem(moduleName, CompletionKind.Module, null,
                            "local Python module in " + project.Name));
                }
            }
        }

        private static string WorkspaceModuleName(string contentFile, ProjectModel project)
        {
            if (string.IsNullOrWhiteSpace(contentFile)) return null;

            string normalizedPath = contentFile.Replace('\\', '/');
            // Legacy src-layout projects store their importable package below src/. The
            // workspace inventory is project-relative, so remove that source-root marker before
            // deriving a Python import path.
            if (normalizedPath.StartsWith("src/", StringComparison.Ordinal))
                normalizedPath = normalizedPath.Substring("src/".Length);

            string moduleName = WorkspaceModuleName(normalizedPath);
            if (string.IsNullOrEmpty(moduleName) || project == null || project.ContentFiles == null)
                return moduleName;

            // A workspace member whose own directory contains __init__.py is itself an
            // importable package (the common flat layout: lib_core/models.py -> lib_core.models).
            // ContentFiles are relative to that member, so the package name is otherwise lost.
            bool projectIsPackage = project.ContentFiles.Any(path =>
                string.Equals((path ?? string.Empty).Replace('\\', '/'), "__init__.py",
                    StringComparison.Ordinal));
            string packageName = (project.Name ?? string.Empty).Replace('-', '_');
            if (projectIsPackage && IsPythonIdentifier(packageName) &&
                !string.Equals(moduleName, packageName, StringComparison.Ordinal) &&
                !moduleName.StartsWith(packageName + ".", StringComparison.Ordinal))
                return packageName + "." + moduleName;

            return moduleName;
        }

        private static string WorkspaceModuleName(string contentFile)
        {
            if (string.IsNullOrWhiteSpace(contentFile)) return null;

            string normalizedPath = contentFile.Replace('\\', '/');
            if (!string.Equals(System.IO.Path.GetExtension(normalizedPath), ".py",
                StringComparison.OrdinalIgnoreCase))
                return null;

            string[] parts = normalizedPath.Split('/');
            int count = parts.Length;
            string stem = System.IO.Path.GetFileNameWithoutExtension(parts[count - 1]);
            if (string.Equals(stem, "__init__", StringComparison.Ordinal))
                count--;
            else
                parts[count - 1] = stem;

            if (count == 0) return null;
            for (int i = 0; i < count; i++)
                if (!IsPythonIdentifier(parts[i])) return null;

            return string.Join(".", parts, 0, count);
        }

        private static bool IsPythonIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) ||
                !(value[0] == '_' || char.IsLetter(value[0])))
                return false;

            for (int i = 1; i < value.Length; i++)
                if (value[i] != '_' && !char.IsLetterOrDigit(value[i]))
                    return false;

            return true;
        }

        // `from <module> import <cursor>` — members of <module>, via the same live-interpreter
        // Catalog lookup the `X.<cursor>` import-bound-name case uses (AddMemberCompletions,
        // case 4 above). Deliberately does NOT fall back to AddImportCompletions's module list on
        // a miss: showing nothing is less misleading than a list of unrelated top-level modules,
        // which was the bug this exists to fix. A miss still queues a background catalog scan via
        // LastUnresolvedModuleFqn (consumed by CompletionController.TryStartCatalogScan) so a
        // retry after the scan completes can succeed.
        private void AddFromImportMemberCompletions(
            List<CompletionItem> items, string moduleName, SolutionModel solution)
        {
            if (string.IsNullOrEmpty(moduleName)) return;

            bool foundWorkspaceModule = false;
            if (solution != null)
            {
                foreach (ProjectModel project in solution.Projects)
                {
                    if (project == null || project.Language != ProjectLanguage.Python) continue;

                    if (string.Equals(project.Name, moduleName, StringComparison.Ordinal) ||
                        string.Equals(project.Name.Replace('-', '_'), moduleName, StringComparison.Ordinal))
                    {
                        foundWorkspaceModule = true;
                        AddProjectMembers(items, project);
                    }

                    foreach (string contentFile in project.ContentFiles)
                    {
                        string candidateModule = WorkspaceModuleName(contentFile, project);
                        if (string.IsNullOrEmpty(candidateModule)) continue;

                        if (string.Equals(candidateModule, moduleName, StringComparison.Ordinal))
                        {
                            foundWorkspaceModule = true;
                            AddFileMembers(items, project, contentFile);
                        }
                        else if (candidateModule.StartsWith(moduleName + ".", StringComparison.Ordinal))
                        {
                            // `from package import child` may import an immediate submodule or
                            // subpackage even when its parent does not re-export it in __init__.py.
                            string childPath = candidateModule.Substring(moduleName.Length + 1);
                            int nextDot = childPath.IndexOf('.');
                            string child = nextDot < 0 ? childPath : childPath.Substring(0, nextDot);
                            if (!string.IsNullOrEmpty(child))
                            {
                                foundWorkspaceModule = true;
                                items.Add(new CompletionItem(child, CompletionKind.Module, null,
                                    "workspace submodule of " + moduleName));
                            }
                        }
                    }
                }
            }

            if (Catalog != null && Catalog.HasModule(moduleName))
                foreach (HelixPythonNamespace.Catalog.CatalogSymbol sym in Catalog.MembersOfModule(moduleName))
                    items.Add(CatalogSymbolToItem(sym));
            else if (!foundWorkspaceModule)
                LastUnresolvedModuleFqn = moduleName;
        }

        private static void AddProjectMembers(List<CompletionItem> items, ProjectModel project)
        {
            foreach (TypeModel type in project.Types)
            {
                if (type.IsNested) continue;
                items.Add(new CompletionItem(type.Name, CompletionKind.Class, null,
                    "class " + type.FullyQualifiedName));
            }

            foreach (FunctionModel function in project.Functions)
                items.Add(new CompletionItem(function.Name, CompletionKind.Function, null,
                    function.FullyQualifiedName));
        }

        private static void AddFileMembers(List<CompletionItem> items, ProjectModel project, string relativePath)
        {
            string expectedPath;
            try
            {
                expectedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    project.ProjectDirectory, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            }
            catch { return; }

            foreach (TypeModel type in project.Types)
            {
                if (type.IsNested || !SameSourcePath(type.SourceFilePath, expectedPath, project.ProjectDirectory))
                    continue;
                items.Add(new CompletionItem(type.Name, CompletionKind.Class, null,
                    "class " + type.FullyQualifiedName));
            }

            foreach (FunctionModel function in project.Functions)
            {
                if (!SameSourcePath(function.SourceFilePath, expectedPath, project.ProjectDirectory)) continue;
                items.Add(new CompletionItem(function.Name, CompletionKind.Function, null,
                    function.FullyQualifiedName));
            }
        }

        private static bool SameSourcePath(string sourcePath, string expectedPath, string projectDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return false;
            try
            {
                string fullPath = System.IO.Path.IsPathRooted(sourcePath)
                    ? System.IO.Path.GetFullPath(sourcePath)
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDirectory, sourcePath));
                return string.Equals(fullPath, expectedPath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // -------------------------------------------------------------- quick info / hover

        /// <summary>What a Quick Info (F1 / Ctrl+K,Ctrl+I) or hover lookup resolved to.</summary>
        public sealed class QuickInfoResult
        {
            /// <summary>The symbol description to show. Never null when the result isn't null.</summary>
            public CompletionItem Item;

            /// <summary>
            /// Set when the answer needed a module the <see cref="Catalog"/> has not scanned yet.
            /// The caller kicks a background scan and re-asks; <see cref="Item"/> still carries the
            /// best answer available right now, so something useful shows immediately.
            /// </summary>
            public string UnresolvedModuleFqn;
        }

        /// <summary>
        /// Describe the symbol at <paramref name="caretOffset"/>.
        ///
        /// Quick Info used to be answered by asking for the COMPLETION LIST at the caret and
        /// looking for an entry whose label matched the word — which only ever worked for symbols
        /// that happen to be completion candidates at that exact position. It answered nothing for
        /// the two most ordinary cases: a module name on its own `import` line (the import
        /// completion list is a fixed table of ~45 common modules, so anything outside it — e.g.
        /// `asyncio` — matched nothing), and a member of an imported module (`asyncio.Semaphore`),
        /// whose completion list is empty until a background interpreter scan has populated the
        /// catalog. This resolves the symbol directly instead, and reports the module it still
        /// needs scanned rather than silently showing nothing.
        ///
        /// Pure and thread-safe: no UI, no scan started here — safe to call from the hover worker.
        /// </summary>
        public QuickInfoResult GetQuickInfo(string source, int caretOffset, string filePath, SolutionModel solution)
        {
            if (string.IsNullOrEmpty(source)) return null;

            int start, end;
            if (!TryGetWordAt(source, caretOffset, out start, out end)) return null;
            string word = source.Substring(start, end - start);
            if (string.IsNullOrEmpty(word)) return null;

            string receiver = ReceiverChainBefore(source, start);

            ModuleSyntax module = null;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source, filePath).Module; }
            catch { /* an unparseable buffer still gets the catalog/completion fallbacks below */ }

            // 1) `<module>.<member>` — the receiver is a name an `import` statement bound.
            if (!string.IsNullOrEmpty(receiver) && receiver.IndexOf('.') < 0 && module != null)
            {
                string moduleFqn = ResolveImportAlias(module, receiver);
                if (!string.IsNullOrEmpty(moduleFqn))
                {
                    string fqn = moduleFqn + "." + word;
                    HelixPythonNamespace.Catalog.CatalogSymbol sym = Catalog != null ? Catalog.Lookup(fqn) : null;
                    if (sym != null) return new QuickInfoResult { Item = CatalogSymbolToItem(sym, fqn) };
                    // Nothing scanned yet: name what it is now, and ask for the scan.
                    return new QuickInfoResult
                    {
                        Item = new CompletionItem(word, CompletionKind.Field, null, fqn),
                        UnresolvedModuleFqn = moduleFqn,
                    };
                }
            }

            // 2) Any other receiver (`self.`, `SomeClass.`, a typed local) — the member-completion
            //    machinery already resolves those, so reuse it and pick out the matching entry.
            if (!string.IsNullOrEmpty(receiver))
            {
                var memberItems = new List<CompletionItem>();
                string savedUnresolved = LastUnresolvedModuleFqn;
                try { AddMemberCompletions(memberItems, receiver, end, module, solution); }
                catch { }
                string unresolved = LastUnresolvedModuleFqn;
                LastUnresolvedModuleFqn = savedUnresolved;
                CompletionItem hit = RichestMatch(memberItems, word);
                if (hit != null) return new QuickInfoResult { Item = hit, UnresolvedModuleFqn = unresolved };
            }

            // 2b) The receiver is an EXPRESSION, not a plain dotted name — a call, a subscript, a
            //     literal: `asyncio.get_running_loop().call_later`, `make_pipe()[0].send`. The
            //     textual receiver scan above stops dead at the `)`, so the word looked like a
            //     bare name and resolved to nothing at all. Resolve it off the parse tree instead.
            //     Placed before the bare-name cases below deliberately: the word is known to be an
            //     attribute access here, so a module-level function that happens to share its name
            //     is definitely the wrong answer.
            if (module != null)
            {
                QuickInfoResult viaExpr = DescribeExpressionMember(module, word, start, end, solution);
                if (viaExpr != null) return viaExpr;
            }

            // 3) A bare name that an import statement bound to a module — `import asyncio` and
            //    every later bare `asyncio`.
            if (module != null)
            {
                string moduleFqn = ResolveImportAlias(module, word);
                if (!string.IsNullOrEmpty(moduleFqn))
                {
                    HelixPythonNamespace.Catalog.CatalogSymbol modSym = Catalog != null ? Catalog.Lookup(moduleFqn) : null;
                    string doc = modSym != null && !string.IsNullOrEmpty(modSym.FullDoc) ? modSym.FullDoc : null;
                    if (doc == null)
                    {
                        string summary;
                        if (CommonModuleSummaries.TryGetValue(moduleFqn, out summary)) doc = summary;
                    }
                    bool scanned = Catalog != null && (modSym != null || Catalog.HasModule(moduleFqn));
                    return new QuickInfoResult
                    {
                        Item = new CompletionItem(word, CompletionKind.Module, null, "module " + moduleFqn, doc),
                        UnresolvedModuleFqn = scanned ? null : moduleFqn,
                    };
                }

                // 4) A name a `from <module> import <name>` statement bound.
                string fromModule = ResolveFromImportModule(module, word, out string originalName);
                if (!string.IsNullOrEmpty(fromModule))
                {
                    string fqn = fromModule + "." + originalName;
                    HelixPythonNamespace.Catalog.CatalogSymbol sym = Catalog != null ? Catalog.Lookup(fqn) : null;
                    if (sym != null) return new QuickInfoResult { Item = CatalogSymbolToItem(sym, fqn) };
                    return new QuickInfoResult
                    {
                        Item = new CompletionItem(word, CompletionKind.Field, null, fqn),
                        UnresolvedModuleFqn = Catalog != null && Catalog.HasModule(fromModule) ? null : fromModule,
                    };
                }

                // 5) A class, function or method DEFINED in this file — at any nesting depth, not
                //    just module level (that limit is why hovering a method showed nothing).
                CompletionItem local = LocalDefinitionInfo(module, word);
                if (local != null) return new QuickInfoResult { Item = local };
            }

            // 6) Fall back to the completion list (keywords, builtins, solution-wide symbols,
            //    parameters and locals all describe themselves there).
            List<CompletionItem> all;
            try { all = GetCompletions(source, end, filePath, solution); }
            catch { all = null; }
            CompletionItem best = RichestMatch(all, word);
            return best == null ? null : new QuickInfoResult { Item = best };
        }

        // The word containing (or immediately preceding) the caret. A caret sitting just past the
        // last character of a word still describes that word — that is where it lands after
        // double-clicking or after typing the name.
        private static bool TryGetWordAt(string source, int caretOffset, out int start, out int end)
        {
            start = end = 0;
            if (caretOffset < 0 || caretOffset > source.Length) return false;
            int i = caretOffset;
            if (i >= source.Length || !IsIdChar(source[i]))
            {
                if (i > 0 && IsIdChar(source[i - 1])) i--;
                else return false;
            }
            start = i;
            while (start > 0 && IsIdChar(source[start - 1])) start--;
            end = i;
            while (end < source.Length && IsIdChar(source[end])) end++;
            // A name may not begin with a digit — a caret inside `3.14` isn't a symbol.
            return end > start && !char.IsDigit(source[start]);
        }

        // The dotted receiver written immediately before `wordStart`, or "" when the word stands
        // alone. `asyncio.Semaphore` with the caret on Semaphore yields "asyncio".
        private static string ReceiverChainBefore(string source, int wordStart)
        {
            int d = wordStart - 1;
            if (d < 0 || source[d] != '.') return string.Empty;
            int baseEnd = d;
            int baseStart = d;
            while (baseStart > 0 && (IsIdChar(source[baseStart - 1]) || source[baseStart - 1] == '.'))
                baseStart--;
            return baseEnd > baseStart ? source.Substring(baseStart, baseEnd - baseStart) : string.Empty;
        }

        /// <summary>
        /// Describe <paramref name="word"/> when it is a member of an expression the textual
        /// receiver scan can't express — `asyncio.get_running_loop().call_later`,
        /// `build()[0].send`, `self.items[i].run`. Null when the word isn't an attribute access
        /// or nothing can be said about it.
        /// </summary>
        private QuickInfoResult DescribeExpressionMember(
            ModuleSyntax module, string word, int wordStart, int wordEnd, SolutionModel solution)
        {
            AttributeExpression access = FindAttributeAccessAt(module, word, wordStart, wordEnd);
            if (access == null || access.Target == null) return null;

            // 1) The receiver's type, whenever this file says enough to name it (a local class
            //    instantiated inline, a helper with a `-> T` return annotation, a subscripted
            //    literal, an annotated parameter…).
            string typeName = ResolveExpressionType(access.Target, module, access.Span.Start.Offset, 0);
            if (!string.IsNullOrEmpty(typeName))
            {
                var typeItems = new List<CompletionItem>();
                try { AddResolvedTypeMembers(typeItems, typeName, module, solution); }
                catch { }
                CompletionItem hit = RichestMatch(typeItems, word);
                if (hit != null) return new QuickInfoResult { Item = hit };
            }

            // 2) The receiver produced something this file cannot name — the common stdlib shape,
            //    where a factory carries no return annotation at all (`inspect.signature(
            //    asyncio.get_running_loop)` is literally `()`), so there is no type to chase.
            //    Answer from the module the chain STARTS in: `asyncio.…().call_later` is described
            //    by the catalogued `call_later` method of an asyncio class. Scoped to that one
            //    module's namespace on purpose — a global by-name search across the catalog would
            //    happily attach some unrelated class's docs to a same-named method.
            string rootModule = RootModuleOfChain(module, access.Target);
            if (string.IsNullOrEmpty(rootModule)) return null;

            HelixPythonNamespace.Catalog.CatalogSymbol method = RichestCatalogMethod(rootModule, word);
            if (method != null)
                return new QuickInfoResult { Item = CatalogMethodToItem(method) };

            // Nothing catalogued for that module yet. Report it so the caller kicks off the
            // interpreter scan; the next hover has the real docs. (No item detail — showing a
            // bare name in a tooltip is noise, and the scan is the useful part.)
            if (Catalog == null || !Catalog.HasModule(rootModule))
                return new QuickInfoResult
                {
                    Item = new CompletionItem(word, CompletionKind.Method),
                    UnresolvedModuleFqn = rootModule,
                };
            return null;
        }

        // The `<expr>.word` node the hovered word names. Matched by name AND span containment, so
        // repeated `.call_later` calls in one file each resolve against their own receiver; the
        // SMALLEST containing match wins, which is the access itself rather than an enclosing one.
        private static AttributeExpression FindAttributeAccessAt(
            ModuleSyntax module, string word, int wordStart, int wordEnd)
        {
            if (module == null) return null;
            AttributeExpression best = null;
            foreach (SyntaxNode n in Walk(module))
            {
                AttributeExpression ae = n as AttributeExpression;
                if (ae == null || !string.Equals(ae.Name, word, StringComparison.Ordinal)) continue;
                int s = ae.Span.Start.Offset, e = ae.Span.End.Offset;
                if (wordStart < s || wordEnd > e) continue;
                if (best == null || (e - s) < (best.Span.End.Offset - best.Span.Start.Offset)) best = ae;
            }
            return best;
        }

        // The module an expression names outright, resolved through this file's imports:
        // `asyncio` -> "asyncio", `os.path` -> "os.path", `np` -> "numpy". Null for anything that
        // isn't a pure dotted name (a call, a subscript, a local variable) — used where the answer
        // becomes half of a catalog FQN, so a near-miss would be a wrong lookup, not a missing one.
        private static string ImportedModuleFqnOf(ModuleSyntax module, ExpressionSyntax expr)
        {
            var parts = new List<string>();
            for (int guard = 0; expr != null && guard < 64; guard++)
            {
                if (expr is AttributeExpression ae) { parts.Insert(0, ae.Name); expr = ae.Target; continue; }
                NameExpression ne = expr as NameExpression;
                if (ne == null) return null;
                string head = ResolveImportAlias(module, ne.Name);
                if (string.IsNullOrEmpty(head)) return null;
                parts.Insert(0, head);
                return string.Join(".", parts);
            }
            return null;
        }

        // The module FQN a postfix chain starts in: walk down through calls, subscripts and
        // attributes to the leftmost name, then resolve it through this file's imports.
        // `asyncio.get_running_loop()` -> "asyncio". Null when the chain doesn't start at an
        // imported module (a local variable, `self`, a literal).
        private static string RootModuleOfChain(ModuleSyntax module, ExpressionSyntax expr)
        {
            for (int guard = 0; expr != null && guard < 64; guard++)
            {
                if (expr is CallExpression ce) { expr = ce.Callee; continue; }
                if (expr is SubscriptExpression se) { expr = se.Target; continue; }
                if (expr is AttributeExpression ae) { expr = ae.Target; continue; }
                NameExpression ne = expr as NameExpression;
                return ne == null ? null : ResolveImportAlias(module, ne.Name);
            }
            return null;
        }

        // The best-documented catalogued method named `name` on any class of `moduleFqn`.
        // asyncio catalogues `call_later` four times over (AbstractEventLoop, BaseEventLoop,
        // ProactorEventLoop, SelectorEventLoop) and only the concrete ones carry the docstring —
        // so "first registered wins" (PythonCatalog.LookupByName) would answer with an empty tip.
        private HelixPythonNamespace.Catalog.CatalogSymbol RichestCatalogMethod(string moduleFqn, string name)
        {
            if (Catalog == null || string.IsNullOrEmpty(moduleFqn) || string.IsNullOrEmpty(name)) return null;
            string prefix = moduleFqn + ".";
            HelixPythonNamespace.Catalog.CatalogSymbol best = null;
            foreach (HelixPythonNamespace.Catalog.CatalogSymbol s in Catalog.Entries)
            {
                if (!string.Equals(s.Kind, "method", StringComparison.Ordinal)) continue;
                if (!string.Equals(s.Name, name, StringComparison.Ordinal)) continue;
                // Members of a CLASS of this module: moduleFqn is "asyncio.BaseEventLoop".
                if (s.ModuleFqn == null || !s.ModuleFqn.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (best == null || Documentation(s).Length > Documentation(best).Length) best = s;
            }
            return best;
        }

        private static string Documentation(HelixPythonNamespace.Catalog.CatalogSymbol s)
        {
            if (s == null) return string.Empty;
            return !string.IsNullOrEmpty(s.FullDoc) ? s.FullDoc : (s.Summary ?? string.Empty);
        }

        // A catalogued method rendered for a hover tip: `asyncio.BaseEventLoop.call_later(delay,
        // callback, *args, context=None)`. The bound `self` is dropped — the user is looking at a
        // call through an instance, where it is never written.
        private static CompletionItem CatalogMethodToItem(HelixPythonNamespace.Catalog.CatalogSymbol sym)
        {
            string signature = sym.Signature ?? string.Empty;
            if (signature.StartsWith("(self, ", StringComparison.Ordinal))
                signature = "(" + signature.Substring("(self, ".Length);
            else if (signature == "(self)") signature = "()";
            string doc = Documentation(sym);
            return new CompletionItem(sym.Name, CompletionKind.Method, null,
                sym.FullyQualifiedName() + signature, string.IsNullOrEmpty(doc) ? null : doc);
        }

        // A single identifier can appear in a completion list more than once (e.g. as a bare
        // import alias with no detail AND as a solution-wide function with an FQN). Prefer the
        // entry that actually carries something to display.
        private static CompletionItem RichestMatch(List<CompletionItem> items, string word)
        {
            if (items == null) return null;
            CompletionItem best = null;
            foreach (CompletionItem it in items)
            {
                if (!string.Equals(it.Label, word, StringComparison.Ordinal)) continue;
                if (best == null) { best = it; continue; }
                bool bestHas = !string.IsNullOrEmpty(best.Detail) || !string.IsNullOrEmpty(best.Documentation);
                bool itHas = !string.IsNullOrEmpty(it.Detail) || !string.IsNullOrEmpty(it.Documentation);
                if (itHas && !bestHas) best = it;
            }
            return best;
        }

        // `from contextlib import asynccontextmanager as acm` binds `acm`; report the module and
        // the name as it exists inside that module.
        private static string ResolveFromImportModule(ModuleSyntax module, string localName, out string originalName)
        {
            originalName = localName;
            if (module == null || string.IsNullOrEmpty(localName)) return null;
            foreach (StatementSyntax s in module.Statements)
            {
                FromImportStatement fi = s as FromImportStatement;
                if (fi == null || fi.IsStar || fi.Module?.Parts == null || fi.Module.Parts.Count == 0) continue;
                foreach (var kv in fi.Names)
                {
                    string local = string.IsNullOrEmpty(kv.Value) ? kv.Key : kv.Value;
                    if (!string.Equals(local, localName, StringComparison.Ordinal)) continue;
                    originalName = kv.Key;
                    return string.Join(".", fi.Module.Parts);
                }
            }
            return null;
        }

        // A class/function/method declared anywhere in this file, described with its signature and
        // docstring. Walks the whole tree so methods and nested helpers resolve, not only the
        // module-level definitions the general completion list offers.
        private static CompletionItem LocalDefinitionInfo(ModuleSyntax module, string name)
        {
            foreach (StatementSyntax s in module.Statements)
            {
                CompletionItem hit = LocalDefinitionInfo(s, name);
                if (hit != null) return hit;
            }
            return null;
        }

        private static CompletionItem LocalDefinitionInfo(SyntaxNode node, string name)
        {
            foreach (SyntaxNode n in Walk(node))
            {
                FunctionDefSyntax fd = n as FunctionDefSyntax;
                if (fd != null && string.Equals(fd.Name, name, StringComparison.Ordinal))
                    return new CompletionItem(name, CompletionKind.Function, null,
                        FunctionDetail(fd), DocstringOf(fd.Body));
                ClassDefSyntax cd = n as ClassDefSyntax;
                if (cd != null && string.Equals(cd.Name, name, StringComparison.Ordinal))
                    return new CompletionItem(name, CompletionKind.Class, null,
                        "class " + cd.Name, DocstringOf(cd.Body));
            }
            return null;
        }

        // -------------------------------------------------------------- helpers

        private static bool IsIdChar(char c) => c == '_' || char.IsLetterOrDigit(c);

        private static int MatchOpen(string source, int closeIdx, char open, char close)
        {
            int depth = 1;
            for (int i = closeIdx - 1; i >= 0; i--)
            {
                char ch = source[i];
                if (ch == close) depth++;
                else if (ch == open) { depth--; if (depth == 0) return i; }
            }
            return closeIdx; // unbalanced — fall back to just the close char
        }

        private static int FindOpenQuote(string source, int closeIdx, char quote)
        {
            for (int i = closeIdx - 1; i >= 0; i--)
            {
                if (source[i] == quote && (i == 0 || source[i - 1] != '\\')) return i;
            }
            return closeIdx;
        }

        private static FunctionDefSyntax FindEnclosingFunction(ModuleSyntax module, int caretOffset)
        {
            // Innermost function whose span contains the caret — or, if none contains,
            // the last function that starts at or before the caret (the caret is probably
            // on a trailing line the parser didn't include in the body span).
            FunctionDefSyntax containing = null;
            FunctionDefSyntax lastBeforeCaret = null;
            foreach (SyntaxNode n in Walk(module))
            {
                if (!(n is FunctionDefSyntax fd)) continue;
                int s = fd.Span.Start.Offset, e = fd.Span.End.Offset;
                if (caretOffset >= s && caretOffset <= e)
                {
                    if (containing == null || fd.Span.Start.Offset > containing.Span.Start.Offset)
                        containing = fd;
                }
                if (s <= caretOffset)
                {
                    if (lastBeforeCaret == null || fd.Span.Start.Offset > lastBeforeCaret.Span.Start.Offset)
                        lastBeforeCaret = fd;
                }
            }
            return containing ?? lastBeforeCaret;
        }

        private static IEnumerable<SyntaxNode> Walk(SyntaxNode root)
        {
            if (root == null) yield break;
            Stack<SyntaxNode> st = new Stack<SyntaxNode>();
            st.Push(root);
            while (st.Count > 0)
            {
                SyntaxNode n = st.Pop();
                yield return n;
                if (n.Children != null)
                    foreach (var c in n.Children) if (c != null) st.Push(c);

                // Fill omissions: ExpressionStatement / AssignmentStatement bypass Children.
                if (n is ExpressionStatement es && es.Expression != null) st.Push(es.Expression);
                else if (n is AssignmentStatement asn)
                {
                    foreach (var t in asn.Targets) if (t != null) st.Push(t);
                    if (asn.Value != null) st.Push(asn.Value);
                }
            }
        }

        private static void CollectBodyBindings(SuiteSyntax body, HashSet<string> set)
        {
            if (body == null) return;
            foreach (StatementSyntax s in body.Statements)
            {
                if (s is AssignmentStatement asn)
                {
                    foreach (var t in asn.Targets)
                        if (t is NameExpression ne) set.Add(ne.Name);
                }
                else if (s is ForStatement fs && fs.Target is NameExpression fne)
                    set.Add(fne.Name);
                else if (s is FunctionDefSyntax inner)
                    set.Add(inner.Name);
                else if (s is ClassDefSyntax ic)
                    set.Add(ic.Name);
            }
        }

        private static string KeywordDetail(string kw)
        {
            switch (kw)
            {
                case "def":    return "def — define a function";
                case "class":  return "class — define a class";
                case "if":     return "if — conditional";
                case "elif":   return "elif — else-if branch";
                case "else":   return "else — fallback branch";
                case "for":    return "for — iterate over a sequence";
                case "while":  return "while — loop while condition is true";
                case "return": return "return — return a value from a function";
                case "import": return "import — load a module";
                case "from":   return "from — selective import";
                case "try":    return "try — exception-handling block";
                case "except": return "except — handle an exception";
                case "with":   return "with — context manager";
                case "lambda": return "lambda — anonymous function";
                case "yield":  return "yield — generator yield";
                default: return null;
            }
        }

        // PEP 257 docstring of a def/class. Lives in HelixPythonNamespace.Python so the Solution Explorer's
        // symbol tooltips and this doc panel can never disagree about a symbol's documentation.
        private static string DocstringOf(SuiteSyntax body)
        {
            return Docstrings.Of(body);
        }

        private static string FunctionDetail(FunctionDefSyntax fd)
        {
            var ps = fd.Parameters != null
                ? string.Join(", ", fd.Parameters.Select(ParameterDetail))
                : string.Empty;
            string returns = DisplayTypeExpression(fd.ReturnAnnotation);
            return (fd.IsAsync ? "async def " : "def ") + fd.Name + "(" + ps + ")" +
                (string.IsNullOrEmpty(returns) ? string.Empty : " -> " + returns);
        }

        private static string ParameterDetail(ParameterSyntax parameter)
        {
            string prefix = parameter.Kind == ParameterKind.VarPositional ? "*" :
                parameter.Kind == ParameterKind.VarKeyword ? "**" : string.Empty;
            string text = prefix + parameter.Name;
            string annotation = DisplayTypeExpression(parameter.Annotation);
            if (!string.IsNullOrEmpty(annotation)) text += ": " + annotation;
            if (parameter.DefaultValue != null)
            {
                string value = DisplayTypeExpression(parameter.DefaultValue);
                text += "=" + (string.IsNullOrEmpty(value) ? "…" : value);
            }
            return text;
        }

        private static string DisplayTypeExpression(ExpressionSyntax expression)
        {
            if (expression == null) return null;
            if (expression is NameExpression name) return name.Name;
            if (expression is AttributeExpression attribute)
            {
                string owner = DisplayTypeExpression(attribute.Target);
                return string.IsNullOrEmpty(owner) ? attribute.Name : owner + "." + attribute.Name;
            }
            if (expression is SubscriptExpression subscript)
                return DisplayTypeExpression(subscript.Target) + "[" + DisplayTypeExpression(subscript.Index) + "]";
            if (expression is TupleExpression tuple)
                return string.Join(", ", tuple.Elements.Select(DisplayTypeExpression));
            if (expression is ParenthesizedExpression parenthesized)
                return "(" + DisplayTypeExpression(parenthesized.Inner) + ")";
            if (expression is LiteralExpression literal) return literal.Token != null ? literal.Token.Text : null;
            return null;
        }

        private static string MemberDetail(MemberModel m)
        {
            if (m.MemberKind == HelixPythonNamespace.Core.Model.Enums.MemberKind.Property)
                return m.Name + (string.IsNullOrEmpty(m.ReturnType) ? "" : ": " + m.ReturnType);
            string ps = m.Parameters != null
                ? string.Join(", ", m.Parameters.Select(p => p.Name))
                : string.Empty;
            return m.Name + "(" + ps + ")" + (string.IsNullOrEmpty(m.ReturnType) ? "" : " -> " + m.ReturnType);
        }
    }
}
