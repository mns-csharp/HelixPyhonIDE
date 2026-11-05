using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.VirtualCompiler.Ast;
using PyParser = HelixPythonNamespace.Python.Python;

namespace HelixPythonNamespace.VirtualCompiler.Typing.Stubs
{
    /// <summary>
    /// Parses a <c>.pyi</c> stub file into a <see cref="StubModule"/>. Uses the
    /// existing Python parser + CstToAst lowering, then walks the module body
    /// (and one level of class bodies) to extract top-level declarations.
    /// </summary>
    public static class StubParser
    {
        public static StubModule Parse(string source, string qualifiedName)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            qualifiedName = qualifiedName ?? string.Empty;

            var parseResult = PyParser.ParseModule(source, qualifiedName + ".pyi");
            Module astModule = null;
            if (parseResult.Module != null)
            {
                astModule = CstToAst.Lower(parseResult.Module);
            }

            Dictionary<string, StubDeclaration> decls = new Dictionary<string, StubDeclaration>(StringComparer.Ordinal);
            if (astModule != null)
            {
                foreach (Stmt s in astModule.Body)
                {
                    StubDeclaration d = TryExtract(s);
                    if (d != null) decls[d.Name] = d;
                }
            }

            List<ParseDiagnostic> diags = new List<ParseDiagnostic>(parseResult.Diagnostics);
            return new StubModule(qualifiedName, decls, diags);
        }

        // ---- Top-level extractors ----

        private static StubDeclaration TryExtract(Stmt s)
        {
            switch (s)
            {
                case FunctionDef f:
                    return BuildFunctionDecl(f.Name, f.Args, f.Returns, f.Span);
                case AsyncFunctionDef af:
                    return BuildFunctionDecl(af.Name, af.Args, af.Returns, af.Span);
                case ClassDef c:
                    return BuildClassDecl(c);
                case AnnAssign an when an.Target is Name nm:
                    {
                        TypeRef t = AnnotationConverter.Convert(an.Annotation);
                        return new StubDeclaration(nm.Id, StubKind.Variable, an.Span, t);
                    }
                case Assign a when a.Targets.Count == 1 && a.Targets[0] is Name aName:
                    {
                        // Heuristic: if RHS is a callable like `TypeVar(...)`, treat as Variable.
                        // Otherwise call it a TypeAlias if it looks like a type expression,
                        // else a Variable. Phase-2 keeps it simple — always Variable.
                        return new StubDeclaration(aName.Id, StubKind.Variable, a.Span, UnknownRef.Instance);
                    }
                case TypeAlias ta when ta.Name is Name tn:
                    {
                        TypeRef t = AnnotationConverter.Convert(ta.Value);
                        return new StubDeclaration(tn.Id, StubKind.TypeAlias, ta.Span, t);
                    }
                default:
                    return null;
            }
        }

        private static StubDeclaration BuildFunctionDecl(string name, Arguments args, Expr returns, Python.Text.SourceSpan span)
        {
            CallableRef call = BuildCallable(args, returns);
            return new StubDeclaration(name, StubKind.Function, span, call);
        }

        private static StubDeclaration BuildClassDecl(ClassDef c)
        {
            // Base expression names, flattened to dotted strings where we can resolve them.
            List<string> baseNames = new List<string>();
            bool isProtocol = false;
            foreach (Expr b in c.Bases)
            {
                string bn = DottedName(b);
                if (bn == null) continue;
                baseNames.Add(bn);
                if (bn == "Protocol" || bn == "typing.Protocol") isProtocol = true;
            }

            Dictionary<string, StubDeclaration> members = new Dictionary<string, StubDeclaration>(StringComparer.Ordinal);
            foreach (Stmt s in c.Body)
            {
                StubDeclaration d = TryExtract(s);
                if (d != null) members[d.Name] = d;
            }

            TypeRef selfType = new ClassRef(c.Name);
            return new StubDeclaration(c.Name, StubKind.Class, c.Span, selfType,
                members: members, baseNames: baseNames, isProtocol: isProtocol);
        }

        // ---- Helpers ----

        private static CallableRef BuildCallable(Arguments args, Expr returns)
        {
            List<CallableParam> paramList = new List<CallableParam>();
            if (args != null)
            {
                int posOnlyIdx = 0;
                int posIdx = 0;
                int defaultBase = (args.PosOnlyArgs.Count + args.Args.Count) - args.Defaults.Count;

                foreach (Arg a in args.PosOnlyArgs)
                {
                    bool hasDefault = posOnlyIdx >= defaultBase;
                    paramList.Add(new CallableParam(a.Name, AnnotationConverter.Convert(a.Annotation),
                        CallableParamKind.PositionalOnly, hasDefault));
                    posOnlyIdx++;
                }
                int globalIdx = args.PosOnlyArgs.Count;
                foreach (Arg a in args.Args)
                {
                    bool hasDefault = globalIdx >= defaultBase;
                    paramList.Add(new CallableParam(a.Name, AnnotationConverter.Convert(a.Annotation),
                        CallableParamKind.PositionalOrKeyword, hasDefault));
                    globalIdx++;
                    posIdx++;
                }
                if (args.VarArg != null)
                {
                    paramList.Add(new CallableParam(args.VarArg.Name,
                        AnnotationConverter.Convert(args.VarArg.Annotation),
                        CallableParamKind.VarPositional, false));
                }
                for (int i = 0; i < args.KwOnlyArgs.Count; i++)
                {
                    Arg a = args.KwOnlyArgs[i];
                    bool hasDefault = i < args.KwDefaults.Count && args.KwDefaults[i] != null;
                    paramList.Add(new CallableParam(a.Name,
                        AnnotationConverter.Convert(a.Annotation),
                        CallableParamKind.KeywordOnly, hasDefault));
                }
                if (args.KwArg != null)
                {
                    paramList.Add(new CallableParam(args.KwArg.Name,
                        AnnotationConverter.Convert(args.KwArg.Annotation),
                        CallableParamKind.VarKeyword, false));
                }
            }

            TypeRef retType = returns != null ? AnnotationConverter.Convert(returns) : UnknownRef.Instance;
            return new CallableRef(paramList, retType);
        }

        private static string DottedName(Expr e)
        {
            switch (e)
            {
                case Name n: return n.Id;
                case AttributeExpr a:
                    {
                        string inner = DottedName(a.Value);
                        return inner != null ? inner + "." + a.Attr : null;
                    }
                default: return null;
            }
        }
    }
}
