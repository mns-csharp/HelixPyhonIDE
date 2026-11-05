using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Python.Semantics;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Walks a HelixPythonNamespace.Python <see cref="ModuleSyntax"/> and produces
    /// <see cref="TypeModel"/> instances attached to the given
    /// <see cref="ProjectModel"/>. This is the one translation point between
    /// the parser library and the tool's domain model.
    /// </summary>
    public static class PythonTypeExtractor
    {
        public static void ExtractInto(ProjectModel project, ModuleSyntax module, string filePath)
        {
            if (project == null || module == null) return;
            SemanticModel sem = new SemanticModel(module);
            string moduleNamespace = DeriveModuleNamespace(project, filePath);
            foreach (ClassDefSyntax cls in TopLevelClasses(module))
            {
                TypeModel type = BuildTypeModel(project, sem, cls, moduleNamespace, parentFqn: null, filePath);
                project.Types.Add(type);
                AddNestedClasses(project, sem, cls, type.FullyQualifiedName, moduleNamespace, filePath);
            }
            foreach (FunctionDefSyntax fn in TopLevelFunctions(module))
            {
                FunctionModel f = new FunctionModel
                {
                    Name = fn.Name,
                    Namespace = moduleNamespace,
                    SourceFilePath = filePath,
                    SourceLineNumber = fn.Span.Start.Line,
                    IsAsync = fn.IsAsync,
                    Documentation = Docstrings.Of(fn.Body),
                    Project = project,
                };
                project.Functions.Add(f);
            }
        }

        private static IEnumerable<ClassDefSyntax> TopLevelClasses(ModuleSyntax module)
        {
            foreach (StatementSyntax s in module.Statements)
                if (s is ClassDefSyntax c) yield return c;
        }

        private static IEnumerable<FunctionDefSyntax> TopLevelFunctions(ModuleSyntax module)
        {
            foreach (StatementSyntax s in module.Statements)
                if (s is FunctionDefSyntax fn) yield return fn;
        }

        private static void AddNestedClasses(ProjectModel project, SemanticModel sem, ClassDefSyntax parent,
            string parentFqn, string ns, string filePath)
        {
            if (parent.Body == null) return;
            foreach (StatementSyntax s in parent.Body.Statements)
            {
                if (s is ClassDefSyntax nested)
                {
                    TypeModel nestedModel = BuildTypeModel(project, sem, nested, ns, parentFqn, filePath);
                    project.Types.Add(nestedModel);
                    AddNestedClasses(project, sem, nested, nestedModel.FullyQualifiedName, ns, filePath);
                }
            }
        }

        private static TypeModel BuildTypeModel(ProjectModel project, SemanticModel sem, ClassDefSyntax cls,
            string ns, string parentFqn, string filePath)
        {
            ClassFlavor flavor = sem.ClassifyClass(cls);
            TypeKind kind = MapKind(flavor, cls);
            TypeModel model = new TypeModel(cls.Name, ns, kind, project);
            model.ParentTypeFqn = parentFqn;
            model.SourceFilePath = filePath;
            model.SourceLineNumber = cls.Span.Start.Line;
            model.Documentation = Docstrings.Of(cls.Body);

            foreach (ExpressionSyntax baseExpr in cls.BaseExpressions)
            {
                string name = BaseName(baseExpr);
                if (string.IsNullOrEmpty(name)) continue;
                // Protocol / ABC markers classify the class itself, not a real base.
                if (IsClassificationBase(name, flavor)) continue;
                RelationshipKind rel = flavor == ClassFlavor.AbstractBaseClass || kind == TypeKind.ConcreteClass
                    ? RelationshipKind.Inherits
                    : RelationshipKind.Implements;
                model.BaseTypes.Add(new TypeReference(name, rel));
            }

            foreach (TypeParameterSyntax tp in cls.TypeParameters)
            {
                GenericParameterModel g = new GenericParameterModel(tp.Name);
                if (tp.Bound != null) g.Constraints.Add(BaseName(tp.Bound) ?? "");
                model.GenericParameters.Add(g);
            }

            // Classic (pre-PEP 695) generics carry their type parameters as a subscripted
            // Generic[...] / Protocol[...] base — `class Stack(Generic[T])`, `class P(Protocol[T, U])`
            // — rather than PEP 695 `class Stack[T]` syntax. Surface those too so the tree shows the
            // generic indicator (Stack<T>). Skipped when PEP 695 params already captured them.
            if (model.GenericParameters.Count == 0)
            {
                foreach (ExpressionSyntax baseExpr in cls.BaseExpressions)
                {
                    SubscriptExpression sub = baseExpr as SubscriptExpression;
                    if (sub == null) continue;
                    string target = BaseName(sub.Target);
                    if (string.IsNullOrEmpty(target)) continue;
                    string last = target.Contains(".") ? target.Substring(target.LastIndexOf('.') + 1) : target;
                    if (last != "Generic" && last != "Protocol") continue;
                    foreach (string p in TypeParamNamesFromSubscript(sub.Index))
                        model.GenericParameters.Add(new GenericParameterModel(p));
                }
            }

            if (cls.Body != null)
            {
                // Fields first (class-level vars + instance `self.x` attributes), then methods —
                // the conventional class-diagram layout. Fields are deduped by name; a type
                // annotation seen anywhere wins.
                List<string> fieldOrder = new List<string>();
                Dictionary<string, MemberModel> fields = new Dictionary<string, MemberModel>(StringComparer.Ordinal);
                List<MemberModel> methods = new List<MemberModel>();

                foreach (StatementSyntax s in cls.Body.Statements)
                {
                    if (s is FunctionDefSyntax fn)
                    {
                        MemberModel m = BuildMemberModel(sem, fn);
                        if (m != null) methods.Add(m);
                        // Instance attributes assigned anywhere in this method as `self.<name> = ...`.
                        CollectSelfFields(fn, fields, fieldOrder);
                    }
                    else if (s is AssignmentStatement asn)
                    {
                        CollectClassFields(asn, fields, fieldOrder);
                    }
                }

                foreach (string fn in fieldOrder) model.Members.Add(fields[fn]);
                foreach (MemberModel mm in methods) model.Members.Add(mm);
            }
            return model;
        }

        // Class-body assignment(s) -> Field member(s): `count = 0`, `count: int`, `x = y = 0`.
        private static void CollectClassFields(AssignmentStatement asn,
            Dictionary<string, MemberModel> fields, List<string> order)
        {
            string type = asn.Annotation != null ? BaseName(asn.Annotation) : null;
            foreach (ExpressionSyntax target in asn.Targets)
                AddFieldTarget(target, type, fields, order);
        }

        // Instance attributes: every `self.<name> = ...` (incl. `self.x: int = ...`) inside a method.
        private static void CollectSelfFields(FunctionDefSyntax fn,
            Dictionary<string, MemberModel> fields, List<string> order)
        {
            // Annotated parameters of this method, so `self.x = x` can inherit `x`'s declared type
            // (the ubiquitous constructor pattern) instead of defaulting to object.
            Dictionary<string, string> paramTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ParameterSyntax pp in fn.Parameters)
                if (pp.Annotation != null) paramTypes[pp.Name] = BaseName(pp.Annotation);

            foreach (SyntaxNode node in fn.Descendants())
            {
                AssignmentStatement asn = node as AssignmentStatement;
                if (asn == null) continue;
                string type = asn.Annotation != null ? BaseName(asn.Annotation) : null;
                // Infer from the RHS when it's a bare reference to a typed parameter: `self.x = x`.
                string inferred;
                if (type == null && asn.Value is NameExpression rhs && paramTypes.TryGetValue(rhs.Name, out inferred))
                    type = inferred;
                foreach (ExpressionSyntax target in asn.Targets)
                    AddSelfTarget(target, type, fields, order);
            }
        }

        // A plain class-level target: a bare name, or a tuple/list of names.
        private static void AddFieldTarget(ExpressionSyntax target, string type,
            Dictionary<string, MemberModel> fields, List<string> order)
        {
            if (target is NameExpression ne) AddField(ne.Name, type, fields, order);
            else if (target is TupleExpression te) foreach (ExpressionSyntax el in te.Elements) AddFieldTarget(el, null, fields, order);
            else if (target is ListExpression le) foreach (ExpressionSyntax el in le.Elements) AddFieldTarget(el, null, fields, order);
        }

        // An instance target: `self.<name>` (direct only — not `self.x.y`), or a tuple/list thereof.
        private static void AddSelfTarget(ExpressionSyntax target, string type,
            Dictionary<string, MemberModel> fields, List<string> order)
        {
            if (target is AttributeExpression ae && ae.Target is NameExpression baseName && baseName.Name == "self")
                AddField(ae.Name, type, fields, order);
            else if (target is TupleExpression te) foreach (ExpressionSyntax el in te.Elements) AddSelfTarget(el, null, fields, order);
            else if (target is ListExpression le) foreach (ExpressionSyntax el in le.Elements) AddSelfTarget(el, null, fields, order);
        }

        private static void AddField(string name, string type,
            Dictionary<string, MemberModel> fields, List<string> order)
        {
            if (string.IsNullOrEmpty(name)) return;
            // Skip dunder class vars (__slots__, __doc__, …) — they're machinery, not domain fields.
            if (name.Length > 4 && name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal))
                return;
            MemberModel existing;
            if (fields.TryGetValue(name, out existing))
            {
                if (string.IsNullOrEmpty(existing.ReturnType) && !string.IsNullOrEmpty(type))
                    existing.ReturnType = type; // a later annotation fills in the type
                return;
            }
            MemberModel m = new MemberModel(name, MemberKind.Field);
            m.ReturnType = type;
            m.AccessModifier = FieldVisibility(name);
            fields[name] = m;
            order.Add(name);
        }

        // Python convention: `__x` (name-mangled) = private, `_x` = protected, else public.
        private static AccessModifier FieldVisibility(string name)
        {
            if (name.StartsWith("__", StringComparison.Ordinal)) return AccessModifier.Private;
            if (name.StartsWith("_", StringComparison.Ordinal)) return AccessModifier.Protected;
            return AccessModifier.Public;
        }

        private static MemberModel BuildMemberModel(SemanticModel sem, FunctionDefSyntax fn)
        {
            MethodFlavor flavor = sem.ClassifyMethod(fn);
            MemberKind kind;
            if (fn.Name == "__init__") kind = MemberKind.Constructor;
            else if (flavor.IsProperty) kind = MemberKind.Property;
            else kind = MemberKind.Method;

            MemberModel m = new MemberModel(fn.Name, kind);
            m.IsAbstract = flavor.IsAbstract;
            m.IsStatic = flavor.IsStatic;
            m.IsAsync = fn.IsAsync;
            m.IsDeprecated = flavor.IsDeprecated;
            m.AccessModifier = MapVisibility(flavor.Visibility);
            m.SourceLineNumber = fn.Span.Start.Line;
            m.ReturnType = fn.ReturnAnnotation == null ? null : BaseName(fn.ReturnAnnotation);
            m.Documentation = Docstrings.Of(fn.Body);

            int skip = (flavor.HasSelfParameter || flavor.HasClsParameter) ? 1 : 0;
            for (int i = skip; i < fn.Parameters.Count; i++)
            {
                ParameterSyntax p = fn.Parameters[i];
                string typeName = p.Annotation == null ? null : BaseName(p.Annotation);
                MethodParameterModel mp = new MethodParameterModel(p.Name, typeName ?? string.Empty);
                if (p.DefaultValue != null)
                {
                    mp.HasDefaultValue = true;
                    mp.DefaultValue = BaseName(p.DefaultValue);
                }
                m.Parameters.Add(mp);
            }
            return m;
        }

        private static TypeKind MapKind(ClassFlavor flavor, ClassDefSyntax cls)
        {
            switch (flavor)
            {
                case ClassFlavor.Protocol: return TypeKind.Interface;
                case ClassFlavor.AbstractBaseClass: return TypeKind.AbstractClass;
                case ClassFlavor.Dataclass: return TypeKind.Record;
                case ClassFlavor.Enum: return TypeKind.Enum;
                case ClassFlavor.NamedTuple: return TypeKind.Record;
                case ClassFlavor.TypedDict: return TypeKind.Struct;
                default: return TypeKind.ConcreteClass;
            }
        }

        private static AccessModifier MapVisibility(MemberVisibility v)
        {
            switch (v)
            {
                case MemberVisibility.Protected: return AccessModifier.Protected;
                case MemberVisibility.Private: return AccessModifier.Private;
                default: return AccessModifier.Public;
            }
        }

        private static bool IsClassificationBase(string name, ClassFlavor flavor)
        {
            if (flavor == ClassFlavor.Protocol) return name == "Protocol" || name == "typing.Protocol";
            if (flavor == ClassFlavor.AbstractBaseClass) return name == "ABC" || name == "abc.ABC";
            if (flavor == ClassFlavor.Enum) return name == "Enum" || name == "enum.Enum" || name.StartsWith("enum.");
            if (flavor == ClassFlavor.NamedTuple) return name == "NamedTuple" || name == "typing.NamedTuple";
            if (flavor == ClassFlavor.TypedDict) return name == "TypedDict" || name == "typing.TypedDict";
            return false;
        }

        private static string BaseName(ExpressionSyntax expr)
        {
            switch (expr)
            {
                case NameExpression ne: return ne.Name;
                case DottedName dn: return dn.AsString;
                // A base/annotation written as attribute access rather than a plain name — e.g.
                // `class MainWindow(tk.Tk)` after `import tkinter as tk`, or `class Foo(ttk.Frame)`.
                // The parser never produces a DottedName for these (that's import-statement-only —
                // see ParseDottedName vs the general expression parser); without this case the whole
                // base was silently dropped, which broke GUI-framework base-class detection for any
                // class module-qualifying its base instead of importing it by bare name.
                case AttributeExpression ae:
                    string targetName = BaseName(ae.Target);
                    return string.IsNullOrEmpty(targetName) ? ae.Name : targetName + "." + ae.Name;
                case UnparsedExpression ue: return JoinTokenText(ue);
                default: return null;
            }
        }

        // Type-parameter names inside a Generic[...] / Protocol[...] subscript: a single name
        // (Generic[T]) or a tuple of names (Generic[T, U]). Non-name items (Generic[list[int]]) are
        // skipped — those are concrete specializations, not declared type parameters.
        private static IEnumerable<string> TypeParamNamesFromSubscript(ExpressionSyntax index)
        {
            List<string> names = new List<string>();
            if (index == null) return names;
            if (index is TupleExpression tup)
            {
                foreach (ExpressionSyntax e in tup.Elements)
                {
                    if (e is NameExpression ne && !string.IsNullOrEmpty(ne.Name)) names.Add(ne.Name);
                }
            }
            else if (index is NameExpression n && !string.IsNullOrEmpty(n.Name))
            {
                names.Add(n.Name);
            }
            return names;
        }

        private static string JoinTokenText(UnparsedExpression ue)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (Token t in ue.Tokens) sb.Append(t.Text);
            return sb.ToString();
        }

        private static string DeriveModuleNamespace(ProjectModel project, string filePath)
        {
            if (project == null || string.IsNullOrEmpty(filePath)) return project?.DefaultNamespace ?? string.Empty;
            string projectDir = Path.GetDirectoryName(project.FilePath);
            if (string.IsNullOrEmpty(projectDir)) return project.DefaultNamespace ?? string.Empty;
            string rel;
            try { rel = GetRelativePath(projectDir, filePath); }
            catch { return project.DefaultNamespace ?? string.Empty; }
            string dir = Path.GetDirectoryName(rel) ?? string.Empty;
            string fileStem = Path.GetFileNameWithoutExtension(rel);
            string dotted = dir.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
            if (fileStem == "__init__") return dotted.TrimStart('.');
            string full = string.IsNullOrEmpty(dotted) ? fileStem : dotted.TrimStart('.') + "." + fileStem;
            // Strip a leading "src." because PEP 621 src-layouts don't include "src" in the import path.
            if (full.StartsWith("src.", StringComparison.Ordinal)) full = full.Substring(4);
            return full;
        }

        private static string GetRelativePath(string fromDir, string toPath)
        {
            Uri fromUri = new Uri(fromDir.EndsWith(Path.DirectorySeparatorChar.ToString()) ? fromDir : fromDir + Path.DirectorySeparatorChar);
            Uri toUri = new Uri(toPath);
            if (fromUri.Scheme != toUri.Scheme) return toPath;
            Uri rel = fromUri.MakeRelativeUri(toUri);
            return Uri.UnescapeDataString(rel.ToString().Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
