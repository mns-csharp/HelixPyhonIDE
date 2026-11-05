using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python.Semantics
{
    /// <summary>
    /// Lightweight semantic analysis over a <see cref="ModuleSyntax"/>.
    /// Answers the queries the OOP tool needs without a full type checker.
    /// </summary>
    public sealed class SemanticModel
    {
        private readonly ModuleSyntax _module;
        private readonly Dictionary<string, string> _imports;

        public SemanticModel(ModuleSyntax module)
        {
            _module = module ?? throw new ArgumentNullException(nameof(module));
            _imports = BuildImportMap(module);
        }

        public ModuleSyntax Module => _module;

        /// <summary>
        /// Classifies a class by inspecting its bases and decorators.
        /// Precedence: Protocol > AbstractBaseClass > TypedDict > NamedTuple > Enum > Dataclass > Concrete.
        /// </summary>
        public ClassFlavor ClassifyClass(ClassDefSyntax cls)
        {
            if (cls == null) throw new ArgumentNullException(nameof(cls));

            foreach (ExpressionSyntax baseExpr in cls.BaseExpressions)
            {
                string name = ResolveBaseName(baseExpr);
                if (name == null) continue;

                // Protocol precedence: 'typing.Protocol' or aliased 'Protocol'.
                if (IsNameResolvingTo(name, "typing", "Protocol")) return ClassFlavor.Protocol;

                if (IsNameResolvingTo(name, "abc", "ABC")) return ClassFlavor.AbstractBaseClass;

                if (IsNameResolvingTo(name, "typing", "TypedDict")) return ClassFlavor.TypedDict;
                if (IsNameResolvingTo(name, "typing", "NamedTuple")) return ClassFlavor.NamedTuple;
                if (IsNameResolvingTo(name, "enum", "Enum")) return ClassFlavor.Enum;
                if (IsNameResolvingTo(name, "enum", "IntEnum")) return ClassFlavor.Enum;
                if (IsNameResolvingTo(name, "enum", "StrEnum")) return ClassFlavor.Enum;
                if (IsNameResolvingTo(name, "enum", "Flag")) return ClassFlavor.Enum;
            }

            // Metaclass keyword can push ABC classification.
            foreach (KeyValuePair<string, ExpressionSyntax> kv in cls.KeywordArguments)
            {
                if (kv.Key == "metaclass")
                {
                    string name = ResolveBaseName(kv.Value);
                    if (IsNameResolvingTo(name, "abc", "ABCMeta")) return ClassFlavor.AbstractBaseClass;
                }
            }

            foreach (DecoratorSyntax decorator in cls.Decorators)
            {
                string name = ResolveBaseName(decorator.Expression);
                if (name == null) continue;
                if (IsNameResolvingTo(name, "dataclasses", "dataclass")) return ClassFlavor.Dataclass;
                // dataclass_transform / attr.s etc. could be added here.
            }

            return ClassFlavor.ConcreteClass;
        }

        public MethodFlavor ClassifyMethod(FunctionDefSyntax fn)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));
            MethodFlavor flavor = new MethodFlavor { IsInstance = true };
            foreach (DecoratorSyntax d in fn.Decorators)
            {
                string n = ResolveBaseName(d.Expression);
                if (n == null) continue;
                if (n == "staticmethod") { flavor.IsStatic = true; flavor.IsInstance = false; }
                else if (n == "classmethod") { flavor.IsClassMethod = true; flavor.IsInstance = false; }
                else if (n == "property") flavor.IsProperty = true;
                else if (n == "abstractmethod" || IsNameResolvingTo(n, "abc", "abstractmethod")) flavor.IsAbstract = true;
                else if (n.EndsWith(".setter") || n.EndsWith(".getter") || n.EndsWith(".deleter")) flavor.IsProperty = true;
                // PEP 702 @deprecated("msg"), spelled `deprecated`, `warnings.deprecated`,
                // `typing.deprecated`, `typing_extensions.deprecated`, or `deprecation.deprecated`.
                else if (n == "deprecated" || n.EndsWith(".deprecated")) flavor.IsDeprecated = true;
            }
            if (fn.Parameters.Count > 0)
            {
                string first = fn.Parameters[0].Name;
                flavor.HasSelfParameter = first == "self";
                flavor.HasClsParameter = first == "cls";
            }
            flavor.Visibility = VisibilityFromName(fn.Name);
            return flavor;
        }

        public MemberVisibility VisibilityFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return MemberVisibility.Public;
            // Dunder ("__x__") are always public by convention.
            if (name.Length >= 4 && name.StartsWith("__") && name.EndsWith("__")) return MemberVisibility.Public;
            if (name.StartsWith("__")) return MemberVisibility.Private;
            if (name.StartsWith("_")) return MemberVisibility.Protected;
            return MemberVisibility.Public;
        }

        public IEnumerable<ClassDefSyntax> AllClasses()
        {
            foreach (SyntaxNode n in _module.Descendants())
                if (n is ClassDefSyntax c) yield return c;
        }

        public IEnumerable<FunctionDefSyntax> MethodsOf(ClassDefSyntax cls)
        {
            if (cls?.Body == null) yield break;
            foreach (StatementSyntax s in cls.Body.Statements)
                if (s is FunctionDefSyntax fn) yield return fn;
        }

        // ---------- helpers ----------

        private static Dictionary<string, string> BuildImportMap(ModuleSyntax module)
        {
            // Local alias → dotted canonical form. e.g. "Protocol" → "typing.Protocol" given
            // "from typing import Protocol".
            Dictionary<string, string> map = new Dictionary<string, string>();
            foreach (StatementSyntax s in module.Statements)
            {
                if (s is FromImportStatement fi && fi.Module != null)
                {
                    foreach (KeyValuePair<string, string> kv in fi.Names)
                    {
                        string local = kv.Value ?? kv.Key;
                        map[local] = fi.Module.AsString + "." + kv.Key;
                    }
                }
                else if (s is ImportStatement imp)
                {
                    foreach (ImportStatement.ImportedName n in imp.Names)
                    {
                        string local = n.Alias ?? n.Name.AsString;
                        map[local] = n.Name.AsString;
                    }
                }
            }
            return map;
        }

        private string ResolveBaseName(ExpressionSyntax expr)
        {
            if (expr is NameExpression ne) return ne.Name;
            if (expr is DottedName dn) return dn.AsString;
            if (expr is AttributeExpression ae)
            {
                string leftName = ResolveBaseName(ae.Target);
                if (string.IsNullOrEmpty(leftName)) return null;
                return leftName + "." + ae.Name;
            }
            if (expr is CallExpression ce) return ResolveBaseName(ce.Callee);
            return null;
        }

        private bool IsNameResolvingTo(string localName, string expectedModule, string expectedMember)
        {
            if (localName == null) return false;
            string dotted = expectedModule + "." + expectedMember;
            if (localName == dotted) return true;
            if (localName == expectedMember && _imports.TryGetValue(expectedMember, out string canonical) && canonical == dotted)
                return true;
            // Also accept `module.Member` when `module` is imported directly.
            if (_imports.TryGetValue(expectedModule, out string canonicalModule) && canonicalModule == expectedModule
                && localName == expectedModule + "." + expectedMember)
                return true;
            return false;
        }
    }

    public sealed class MethodFlavor
    {
        public bool IsStatic;
        public bool IsClassMethod;
        public bool IsInstance;
        public bool IsProperty;
        public bool IsAbstract;
        public bool IsDeprecated;
        public bool HasSelfParameter;
        public bool HasClsParameter;
        public MemberVisibility Visibility;
    }

    public enum MemberVisibility
    {
        Public,
        Protected,
        Private
    }
}
