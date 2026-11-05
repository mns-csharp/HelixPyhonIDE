using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Resolves a right-clicked token in a Python source file to the
    /// <see cref="TypeModel"/> it references, using AST-scoped lookup:
    /// imports, wildcard imports, enclosing class/function scope, and the
    /// module's own namespace are all taken into account before falling back
    /// to a simple-name search across the whole solution.
    /// </summary>
    public sealed class SymbolResolver
    {
        private readonly SolutionModel _solution;

        public SymbolResolver(SolutionModel solution)
        {
            _solution = solution ?? throw new ArgumentNullException(nameof(solution));
        }

        /// <summary>
        /// Resolve the token the user clicked on.
        /// </summary>
        /// <param name="module">Parsed module for the file. Must not be null.</param>
        /// <param name="filePath">Absolute path to the source file (used to derive the module namespace).</param>
        /// <param name="line">1-based line number of the click position.</param>
        /// <param name="column">1-based column number of the click position.</param>
        /// <param name="token">The word under the click as reported by the editor (e.g. from Scintilla's WordStartPosition/WordEndPosition). Empty/null returns null.</param>
        /// <returns>
        /// A <see cref="ResolvedSymbol"/> describing the target, or null when:
        /// the token is empty, the click is inside an import's module path (packages are
        /// out of scope), the token names a parameter/local in the enclosing function,
        /// or no matching type exists in the solution.
        /// </returns>
        public ResolvedSymbol Resolve(ModuleSyntax module, string filePath, int line, int column, string token)
        {
            if (module == null) return null;
            if (string.IsNullOrWhiteSpace(token)) return null;

            SyntaxNode leaf = FindLeafAt(module, line, column);

            // Click inside a `from X.Y import ...` or `import X.Y` module path -> package, skip.
            if (IsInImportModulePath(leaf)) return null;

            // Local variable / parameter shadowing: walk up FunctionDefSyntax ancestors.
            if (IsLocalOrParameter(leaf, token)) return null;

            // Module variables and constants are real navigable symbols too. This remains after
            // the local-shadow guard so a local binding with the same spelling is never mistaken
            // for the global declaration.
            VariableSymbol variable = FindModuleVariable(module, filePath, token);
            if (variable != null) return new ResolvedSymbol(null, token, null, null, variable);

            string moduleNamespace = DeriveModuleNamespace(filePath);

            Dictionary<string, string> aliasToFqn;
            List<string> wildcards;
            BuildImports(module, SelfPackageOf(moduleNamespace, filePath), out aliasToFqn, out wildcards);
            string enclosingClassFqn = FindEnclosingClassFqn(leaf, moduleNamespace);

            VariableSymbol importedVariable = TryResolveImportedModuleVariable(
                leaf, token, aliasToFqn, filePath);
            if (importedVariable != null)
                return new ResolvedSymbol(null, token, null, null, importedVariable);

            VariableSymbol memberVariable = TryResolveMemberVariable(
                leaf, token, moduleNamespace, enclosingClassFqn, aliasToFqn);
            if (memberVariable != null)
                return new ResolvedSymbol(null, token, null, null, memberVariable);

            // Method call through a freshly-constructed receiver, e.g.
            // `RunFullPipelineApplication().run()`. FlattenChain intentionally handles only
            // dotted names, so this common Python form previously fell through and the editor
            // omitted "Goto Function Body" entirely. Resolve the receiver type, then walk its
            // inheritance chain for the clicked method.
            FunctionModel calledMethod = TryResolveCalledMethod(
                leaf, token, moduleNamespace, enclosingClassFqn, aliasToFqn);
            if (calledMethod != null) return new ResolvedSymbol(null, token, null, calledMethod);

            // If click lands on the attribute-name portion of `a.b.C`, use the full dotted chain.
            string chain = TryBuildAttributeChain(leaf, line, column);

            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(chain))
            {
                candidates.Add(chain);

                int firstDot = chain.IndexOf('.');
                string head = firstDot < 0 ? chain : chain.Substring(0, firstDot);
                string tail = firstDot < 0 ? string.Empty : chain.Substring(firstDot);
                if (aliasToFqn.TryGetValue(head, out string mappedHead))
                {
                    candidates.Add(mappedHead + tail);
                }
            }
            else
            {
                if (aliasToFqn.TryGetValue(token, out string aliased)) candidates.Add(aliased);
                foreach (string w in wildcards) candidates.Add(w + "." + token);
                if (!string.IsNullOrEmpty(enclosingClassFqn)) candidates.Add(enclosingClassFqn + "+" + token);
                if (!string.IsNullOrEmpty(moduleNamespace)) candidates.Add(moduleNamespace + "." + token);
            }

            foreach (string fqn in candidates)
            {
                TypeModel t = LookupExact(fqn);
                if (t != null) return new ResolvedSymbol(t, token, null);
                FunctionModel fn = LookupFunction(fqn);
                if (fn != null) return new ResolvedSymbol(null, token, null, fn);
            }

            // The clicked name is a member of a class we DID resolve (`Cli.SOME_CONST`, an
            // attribute rather than a method): navigate to the owning class. Deliberately ahead of
            // the simple-name search below, which knows only the bare word and would happily jump
            // to an unrelated module-level `main` when the user clicked `Cli.main`.
            TypeModel owner = TryResolveAttributeOwnerType(leaf, token, moduleNamespace, aliasToFqn);
            if (owner != null) return new ResolvedSymbol(owner, token, null);

            // Fallback: simple-name search across all projects — types first.
            List<TypeModel> sameName = new List<TypeModel>();
            foreach (TypeModel t in _solution.GetAllTypes())
            {
                if (string.Equals(t.Name, token, StringComparison.Ordinal)) sameName.Add(t);
            }
            if (sameName.Count == 1) return new ResolvedSymbol(sameName[0], token, null);
            if (sameName.Count > 1) return new ResolvedSymbol(null, token, sameName);

            // Then a parallel function simple-name search. No picker for multi-match
            // functions — if there are two `def foo()` in the solution, we don't guess.
            List<FunctionModel> fnMatches = new List<FunctionModel>();
            foreach (ProjectModel p in _solution.Projects)
            {
                foreach (FunctionModel f in p.Functions)
                {
                    if (string.Equals(f.Name, token, StringComparison.Ordinal)) fnMatches.Add(f);
                }
            }
            if (fnMatches.Count == 1) return new ResolvedSymbol(null, token, null, fnMatches[0]);

            return null;
        }

        private VariableSymbol FindModuleVariable(ModuleSyntax module, string filePath, string token)
        {
            foreach (StatementSyntax statement in module.Statements)
            {
                AssignmentStatement assignment = statement as AssignmentStatement;
                if (assignment == null) continue;
                foreach (ExpressionSyntax target in assignment.Targets)
                {
                    NameExpression name = target as NameExpression;
                    if (name == null || !string.Equals(name.Name, token, StringComparison.Ordinal)) continue;
                    return new VariableSymbol
                    {
                        Name = token,
                        Namespace = DeriveModuleNamespace(filePath),
                        SourceFilePath = filePath,
                        SourceLineNumber = name.Span.Start.Line,
                        SourceColumnNumber = name.Span.Start.Column,
                        Project = FindOwningProject(filePath),
                    };
                }
            }
            return null;
        }

        private ProjectModel FindOwningProject(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string full;
            try { full = System.IO.Path.GetFullPath(filePath); }
            catch { return null; }
            ProjectModel best = null;
            int bestLength = -1;
            foreach (ProjectModel project in _solution.Projects)
            {
                if (project == null || string.IsNullOrEmpty(project.ProjectDirectory)) continue;
                string root;
                try { root = System.IO.Path.GetFullPath(project.ProjectDirectory).TrimEnd('\\') + "\\"; }
                catch { continue; }
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && root.Length > bestLength)
                {
                    best = project;
                    bestLength = root.Length;
                }
            }
            return best;
        }

        private VariableSymbol TryResolveImportedModuleVariable(SyntaxNode leaf, string token,
            Dictionary<string, string> aliasToFqn, string requestingFile)
        {
            AttributeExpression attribute = leaf as AttributeExpression;
            if (attribute == null || attribute.Name != token) return null;
            string receiver = FlattenChain(attribute.Target);
            if (string.IsNullOrEmpty(receiver)) return null;
            string moduleName;
            if (!aliasToFqn.TryGetValue(receiver, out moduleName)) return null;
            string sourceFile = FindModuleFile(moduleName, requestingFile);
            if (sourceFile == null) return null;
            ModuleSyntax importedModule;
            try
            {
                importedModule = global::HelixPythonNamespace.Python.Python.ParseModule(
                    System.IO.File.ReadAllText(sourceFile), sourceFile).Module;
            }
            catch { return null; }
            return FindModuleVariable(importedModule, sourceFile, token);
        }

        private string FindModuleFile(string moduleName, string requestingFile)
        {
            if (string.IsNullOrEmpty(moduleName)) return null;
            string relative = moduleName.Replace('.', System.IO.Path.DirectorySeparatorChar);
            ProjectModel preferred = FindOwningProject(requestingFile);
            var projects = new List<ProjectModel>();
            if (preferred != null) projects.Add(preferred);
            foreach (ProjectModel project in _solution.Projects)
                if (project != null && !ReferenceEquals(project, preferred)) projects.Add(project);
            foreach (ProjectModel project in projects)
            {
                string root = project.ProjectDirectory;
                if (string.IsNullOrEmpty(root)) continue;
                string[] candidates =
                {
                    System.IO.Path.Combine(root, relative + ".py"),
                    System.IO.Path.Combine(root, relative, "__init__.py"),
                    System.IO.Path.Combine(root, "src", relative + ".py"),
                    System.IO.Path.Combine(root, "src", relative, "__init__.py"),
                };
                foreach (string candidate in candidates)
                    if (System.IO.File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private VariableSymbol TryResolveMemberVariable(SyntaxNode leaf, string token,
            string moduleNamespace, string enclosingClassFqn, Dictionary<string, string> aliasToFqn)
        {
            AttributeExpression attribute = leaf as AttributeExpression;
            if (attribute == null || attribute.Name != token) return null;
            TypeModel owner = null;
            NameExpression receiver = attribute.Target as NameExpression;
            if (receiver != null)
            {
                if (receiver.Name == "self" || receiver.Name == "cls")
                    owner = ResolveReceiverType(enclosingClassFqn, moduleNamespace, aliasToFqn);
                else
                {
                    owner = ResolveReceiverType(receiver.Name, moduleNamespace, aliasToFqn);
                    if (owner == null)
                        owner = ResolveLocalReceiverType(leaf, receiver.Name, moduleNamespace,
                            aliasToFqn, new HashSet<string>(StringComparer.Ordinal));
                }
            }
            if (owner == null) return null;
            TypeModel declaringType;
            MemberModel member = FindDataMember(owner, token,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), out declaringType);
            if (member == null || declaringType == null || string.IsNullOrEmpty(declaringType.SourceFilePath))
                return null;
            int line = member.SourceLineNumber > 0 ? member.SourceLineNumber
                : FindMemberDeclarationLine(declaringType.SourceFilePath, declaringType.Name, token);
            return new VariableSymbol
            {
                Name = token,
                Namespace = declaringType.Namespace,
                SourceFilePath = declaringType.SourceFilePath,
                SourceLineNumber = line,
                Project = declaringType.Project,
                OwnerType = declaringType,
                Member = member,
            };
        }

        private MemberModel FindDataMember(TypeModel type, string name, HashSet<string> visited,
            out TypeModel declaringType)
        {
            declaringType = null;
            if (type == null || !visited.Add(type.FullyQualifiedName)) return null;
            foreach (MemberModel member in type.Members)
            {
                if (member.MemberKind != MemberKind.Method && member.MemberKind != MemberKind.Constructor
                    && string.Equals(member.Name, name, StringComparison.Ordinal))
                {
                    declaringType = type;
                    return member;
                }
            }
            foreach (TypeReference baseReference in type.BaseTypes)
            {
                TypeModel baseType = baseReference.ResolvedType
                    ?? ResolveReceiverType(baseReference.FullyQualifiedName, type.Namespace,
                        new Dictionary<string, string>(StringComparer.Ordinal));
                MemberModel inherited = FindDataMember(baseType, name, visited, out declaringType);
                if (inherited != null) return inherited;
            }
            return null;
        }

        private static int FindMemberDeclarationLine(string filePath, string className, string memberName)
        {
            try
            {
                ModuleSyntax module = global::HelixPythonNamespace.Python.Python.ParseModule(
                    System.IO.File.ReadAllText(filePath), filePath).Module;
                foreach (StatementSyntax statement in module.Statements)
                {
                    ClassDefSyntax type = statement as ClassDefSyntax;
                    if (type == null || type.Name != className || type.Body == null) continue;
                    foreach (SyntaxNode node in AstIdentifierRewriter.EnumerateAll(type.Body))
                    {
                        AssignmentStatement assignment = node as AssignmentStatement;
                        if (assignment == null) continue;
                        foreach (ExpressionSyntax target in assignment.Targets)
                        {
                            NameExpression name = target as NameExpression;
                            if (name != null && name.Name == memberName) return name.Span.Start.Line;
                            AttributeExpression attribute = target as AttributeExpression;
                            if (attribute != null && attribute.Name == memberName
                                && attribute.Target is NameExpression self && self.Name == "self")
                                return attribute.Span.Start.Line;
                        }
                    }
                }
            }
            catch { }
            return 1;
        }

        // ------------------------------------------------------------------ tree walking

        private static SyntaxNode FindLeafAt(SyntaxNode root, int line, int column)
        {
            if (root == null) return null;
            if (!SpanContains(root.Span, line, column)) return null;
            foreach (SyntaxNode child in root.Children)
            {
                if (child == null) continue;
                SyntaxNode hit = FindLeafAt(child, line, column);
                if (hit != null) return hit;
            }
            return root;
        }

        private static bool SpanContains(SourceSpan span, int line, int column)
        {
            SourcePosition s = span.Start;
            SourcePosition e = span.End;
            if (line < s.Line || line > e.Line) return false;
            if (line == s.Line && column < s.Column) return false;
            if (line == e.Line && column > e.Column) return false;
            return true;
        }

        private static bool IsInImportModulePath(SyntaxNode leaf)
        {
            SyntaxNode n = leaf;
            while (n != null)
            {
                // DottedName inside an ImportStatement.ImportedName or FromImportStatement.Module.
                if (n is DottedName)
                {
                    SyntaxNode p = n.Parent;
                    while (p != null)
                    {
                        if (p is ImportStatement) return true;
                        if (p is FromImportStatement fi && ReferenceEquals(fi.Module, n)) return true;
                        p = p.Parent;
                    }
                }
                n = n.Parent;
            }
            return false;
        }

        private static bool IsLocalOrParameter(SyntaxNode leaf, string token)
        {
            FunctionDefSyntax fn = leaf?.FirstAncestor<FunctionDefSyntax>();
            while (fn != null)
            {
                foreach (ParameterSyntax p in fn.Parameters)
                {
                    if (string.Equals(p.Name, token, StringComparison.Ordinal)) return true;
                }
                if (fn.Body != null && BodyAssignsName(fn.Body, token)) return true;
                fn = fn.FirstAncestor<FunctionDefSyntax>();
            }
            return false;
        }

        private static bool BodyAssignsName(SuiteSyntax body, string token)
        {
            foreach (StatementSyntax s in body.Statements)
            {
                if (s is AssignmentStatement asn)
                {
                    foreach (ExpressionSyntax tgt in asn.Targets)
                    {
                        if (tgt is NameExpression ne && string.Equals(ne.Name, token, StringComparison.Ordinal))
                            return true;
                    }
                }
            }
            return false;
        }

        private static string TryBuildAttributeChain(SyntaxNode leaf, int line, int column)
        {
            // FindLeafAt returns the deepest node whose span contains the click.
            // If the click is on the rightmost segment of a.b.C, the leaf is the
            // AttributeExpression itself (descent stops because no child covers the
            // click past the Target's span). If the click is on an earlier segment,
            // the leaf descends into a NameExpression or a shorter AttributeExpression.
            // Using the leaf directly gives the chain corresponding to what was clicked.
            if (leaf is AttributeExpression ae) return FlattenChain(ae);
            return null;
        }

        private static string FlattenChain(ExpressionSyntax expr)
        {
            if (expr is NameExpression ne) return ne.Name;
            if (expr is DottedName dn) return dn.AsString;
            if (expr is AttributeExpression ae)
            {
                string head = FlattenChain(ae.Target);
                if (string.IsNullOrEmpty(head)) return null;
                return head + "." + ae.Name;
            }
            return null;
        }

        private FunctionModel TryResolveCalledMethod(SyntaxNode leaf, string token,
            string moduleNamespace, string enclosingClassFqn,
            Dictionary<string, string> aliasToFqn)
        {
            AttributeExpression attribute = leaf as AttributeExpression;
            SyntaxNode cursor = leaf;
            while (attribute == null && cursor != null)
            {
                attribute = cursor as AttributeExpression;
                cursor = cursor.Parent;
            }
            if (attribute == null || !string.Equals(attribute.Name, token, StringComparison.Ordinal))
                return null;

            string receiverName = null;
            CallExpression call = attribute.Target as CallExpression;
            if (call != null)
            {
                NameExpression calleeName = call.Callee as NameExpression;
                if (calleeName != null) receiverName = calleeName.Name;
            }
            else
            {
                NameExpression receiver = attribute.Target as NameExpression;
                if (receiver != null)
                {
                    // `self.m()` / `cls.m()` resolve against the enclosing class. ANY other bare
                    // name is tried as a class name — `Cli.main()`, a call on the class itself, is
                    // the dominant call shape in this IDE's own strict-OOP model (rule 2 forces
                    // every function to be a method, so calls go through the class or an instance).
                    // Only the two constructed-receiver forms were handled before, so right-clicking
                    // `main` in `Cli.main()` resolved to nothing and the editor's context menu
                    // offered no navigation at all — no Goto Source, no Go to Function Body.
                    //
                    // A name that is not a class simply fails to resolve below (`os.path.join`,
                    // an instance variable), and a class that has no such method fails at
                    // FindMethod, so this never invents a target.
                    receiverName = (receiver.Name == "self" || receiver.Name == "cls")
                        ? enclosingClassFqn
                        : receiver.Name;
                }
            }
            if (string.IsNullOrEmpty(receiverName)) return null;

            TypeModel receiverType = ResolveReceiverType(receiverName, moduleNamespace, aliasToFqn);
            if (receiverType == null)
                receiverType = ResolveLocalReceiverType(leaf, receiverName, moduleNamespace, aliasToFqn,
                    new HashSet<string>(StringComparer.Ordinal));
            if (receiverType == null) return null;

            TypeModel declaringType;
            MemberModel member = FindMethod(receiverType, token, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                out declaringType);
            if (member == null || declaringType == null || string.IsNullOrEmpty(declaringType.SourceFilePath))
                return null;

            return new FunctionModel
            {
                Name = member.Name,
                Namespace = declaringType.FullyQualifiedName,
                SourceFilePath = declaringType.SourceFilePath,
                SourceLineNumber = member.SourceLineNumber,
                IsAsync = member.IsAsync,
                Project = declaringType.Project,
            };
        }

        /// <summary>
        /// Resolves the runtime class of a local receiver from the nearest preceding binding:
        /// `writer = Writer(); writer.write()`, annotated locals/parameters, and simple aliases.
        /// Only bindings in the same function scope and before the call are considered; ambiguity
        /// or an expression that is not a constructor remains unresolved rather than guessed.
        /// </summary>
        private TypeModel ResolveLocalReceiverType(SyntaxNode leaf, string localName,
            string moduleNamespace, Dictionary<string, string> aliasToFqn, HashSet<string> visited)
        {
            if (leaf == null || string.IsNullOrEmpty(localName)) return null;
            FunctionDefSyntax function = leaf.FirstAncestor<FunctionDefSyntax>();
            if (function == null) return null;
            return ResolveBindingInFunction(function, leaf.Span.Start.Line, localName,
                moduleNamespace, aliasToFqn, visited);
        }

        private TypeModel ResolveBindingInFunction(FunctionDefSyntax function, int beforeLine, string localName,
            string moduleNamespace, Dictionary<string, string> aliasToFqn, HashSet<string> visited)
        {
            if (function == null || string.IsNullOrEmpty(localName) || !visited.Add(localName)) return null;
            foreach (ParameterSyntax parameter in function.Parameters)
                if (parameter.Name == localName && parameter.Annotation != null)
                    return ResolveTypeExpression(parameter.Annotation, moduleNamespace, aliasToFqn);

            AssignmentStatement nearest = null;
            if (function.Body != null)
            {
                foreach (SyntaxNode node in function.Body.Descendants())
                {
                    var assignment = node as AssignmentStatement;
                    if (assignment == null || assignment.Span.Start.Line >= beforeLine) continue;
                    if (!ReferenceEquals(assignment.FirstAncestor<FunctionDefSyntax>(), function)) continue;
                    if (!AssignsName(assignment, localName)) continue;
                    if (nearest == null || assignment.Span.Start.Line > nearest.Span.Start.Line) nearest = assignment;
                }
            }
            if (nearest == null) return null;
            if (nearest.Annotation != null)
            {
                TypeModel annotated = ResolveTypeExpression(nearest.Annotation, moduleNamespace, aliasToFqn);
                if (annotated != null) return annotated;
            }

            ExpressionSyntax value = nearest.Value;
            var call = value as CallExpression;
            if (call != null)
                return ResolveTypeExpression(call.Callee, moduleNamespace, aliasToFqn);

            var alias = value as NameExpression;
            return alias != null
                ? ResolveBindingInFunction(function, nearest.Span.Start.Line, alias.Name,
                    moduleNamespace, aliasToFqn, visited)
                : null;
        }

        private static bool AssignsName(AssignmentStatement assignment, string name)
        {
            if (assignment.Targets == null) return false;
            foreach (ExpressionSyntax target in assignment.Targets)
                if (target is NameExpression local && local.Name == name) return true;
            return false;
        }

        private TypeModel ResolveTypeExpression(ExpressionSyntax expression, string moduleNamespace,
            Dictionary<string, string> aliasToFqn)
        {
            string name = FlattenChain(expression);
            if (string.IsNullOrEmpty(name)) return null;
            int dot = name.IndexOf('.');
            string head = dot < 0 ? name : name.Substring(0, dot);
            string tail = dot < 0 ? string.Empty : name.Substring(dot);
            string imported;
            if (aliasToFqn.TryGetValue(head, out imported)) name = imported + tail;
            return ResolveReceiverType(name, moduleNamespace, aliasToFqn);
        }

        /// <summary>
        /// When the click is on the attribute half of <c>Something.token</c> and
        /// <c>Something</c> names a class, that class — so navigation is still offered for a
        /// member the type model does not carry as a method (a class constant, a property, an
        /// attribute assigned in <c>__init__</c>). Null for every other shape.
        /// </summary>
        private TypeModel TryResolveAttributeOwnerType(SyntaxNode leaf, string token,
            string moduleNamespace, Dictionary<string, string> aliasToFqn)
        {
            AttributeExpression attribute = leaf as AttributeExpression;
            if (attribute == null || !string.Equals(attribute.Name, token, StringComparison.Ordinal))
                return null;
            NameExpression receiver = attribute.Target as NameExpression;
            if (receiver == null || receiver.Name == "self" || receiver.Name == "cls") return null;
            return ResolveReceiverType(receiver.Name, moduleNamespace, aliasToFqn);
        }

        private TypeModel ResolveReceiverType(string name, string moduleNamespace,
            Dictionary<string, string> aliasToFqn)
        {
            TypeModel exact = LookupExact(name);
            if (exact != null) return exact;
            if (aliasToFqn.TryGetValue(name, out string imported))
            {
                exact = LookupExact(imported);
                if (exact != null) return exact;
            }
            if (!string.IsNullOrEmpty(moduleNamespace))
            {
                exact = LookupExact(moduleNamespace + "." + name);
                if (exact != null) return exact;
            }
            TypeModel match = null;
            foreach (TypeModel type in _solution.GetAllTypes())
            {
                if (!string.Equals(type.Name, name, StringComparison.Ordinal)) continue;
                if (match != null) return null; // ambiguous: never guess
                match = type;
            }
            return match;
        }

        private MemberModel FindMethod(TypeModel type, string name, HashSet<string> visited,
            out TypeModel declaringType)
        {
            declaringType = null;
            if (type == null || !visited.Add(type.FullyQualifiedName)) return null;
            foreach (MemberModel member in type.Members)
            {
                if (member.MemberKind == MemberKind.Method &&
                    string.Equals(member.Name, name, StringComparison.Ordinal))
                {
                    declaringType = type;
                    return member;
                }
            }
            foreach (TypeReference baseReference in type.BaseTypes)
            {
                TypeModel baseType = baseReference.ResolvedType
                    ?? ResolveReceiverType(baseReference.FullyQualifiedName, type.Namespace,
                        new Dictionary<string, string>(StringComparer.Ordinal));
                MemberModel inherited = FindMethod(baseType, name, visited, out declaringType);
                if (inherited != null) return inherited;
            }
            return null;
        }

        private static string FindEnclosingClassFqn(SyntaxNode leaf, string moduleNamespace)
        {
            List<string> classNames = new List<string>();
            SyntaxNode n = leaf;
            while (n != null)
            {
                if (n is ClassDefSyntax cls) classNames.Insert(0, cls.Name);
                n = n.Parent;
            }
            if (classNames.Count == 0) return null;
            string outer = string.IsNullOrEmpty(moduleNamespace)
                ? classNames[0]
                : moduleNamespace + "." + classNames[0];
            for (int i = 1; i < classNames.Count; i++) outer = outer + "+" + classNames[i];
            return outer;
        }

        // ------------------------------------------------------------------ import map

        /// <summary>
        /// Maps each name bound by an import to the fully-qualified name it refers to.
        ///
        /// <paramref name="selfPackage"/> is the package the file belongs to, and is what a
        /// RELATIVE import is resolved against: <c>from .experiments.cli import Cli</c> inside
        /// <c>rouse_model_verification/main.py</c> binds
        /// <c>rouse_model_verification.experiments.cli.Cli</c>, not <c>experiments.cli.Cli</c>.
        /// The dots used to be dropped, so every relative import produced a name that matched
        /// nothing and resolution fell through to a solution-wide simple-name search — which is
        /// silently wrong the moment two packages contain a class of the same name.
        /// </summary>
        private static void BuildImports(ModuleSyntax module, string selfPackage,
            out Dictionary<string, string> aliasToFqn, out List<string> wildcards)
        {
            aliasToFqn = new Dictionary<string, string>(StringComparer.Ordinal);
            wildcards = new List<string>();
            foreach (StatementSyntax s in module.Statements)
            {
                if (s is FromImportStatement fi && (fi.Module != null || fi.RelativeDots > 0))
                {
                    string from = ResolveImportedModule(fi, selfPackage);
                    if (from == null) continue;   // relative import above the root package

                    if (fi.IsStar)
                    {
                        if (from.Length > 0) wildcards.Add(from);
                        continue;
                    }
                    foreach (KeyValuePair<string, string> kv in fi.Names)
                    {
                        string local = kv.Value ?? kv.Key;
                        aliasToFqn[local] = from.Length == 0 ? kv.Key : from + "." + kv.Key;
                    }
                }
                else if (s is ImportStatement imp)
                {
                    foreach (ImportStatement.ImportedName n in imp.Names)
                    {
                        string local = n.Alias ?? n.Name.AsString;
                        aliasToFqn[local] = n.Name.AsString;
                    }
                }
            }
        }

        /// <summary>
        /// The module a <c>from … import …</c> reads from, in this model's namespace convention:
        /// names are relative to the PROJECT directory, so the package root is the empty string,
        /// not the distribution name. Absolute imports are returned as written; a relative one
        /// walks <paramref name="selfPackage"/> up one level per dot beyond the first (PEP 328).
        ///
        /// Returns "" when the import lands on the package root — <c>from .experiments.cli import
        /// Cli</c> in a top-level <c>main.py</c> is <c>experiments.cli</c>, with nothing prefixed —
        /// and null when the dots walk ABOVE the root, which no name can be resolved against.
        /// </summary>
        private static string ResolveImportedModule(FromImportStatement fi, string selfPackage)
        {
            string tail = fi.Module != null ? fi.Module.AsString : string.Empty;
            if (fi.RelativeDots <= 0) return tail;

            string anchor = selfPackage ?? string.Empty;
            for (int i = 1; i < fi.RelativeDots; i++)
            {
                if (string.IsNullOrEmpty(anchor)) return null;   // above the root package
                anchor = ParentPackage(anchor);
            }
            if (string.IsNullOrEmpty(anchor)) return tail;
            return string.IsNullOrEmpty(tail) ? anchor : anchor + "." + tail;
        }

        /// <summary>Drops the last dotted segment: <c>a.b.c</c> -&gt; <c>a.b</c>, <c>a</c> -&gt; "".</summary>
        private static string ParentPackage(string dotted)
        {
            if (string.IsNullOrEmpty(dotted)) return string.Empty;
            int lastDot = dotted.LastIndexOf('.');
            return lastDot < 0 ? string.Empty : dotted.Substring(0, lastDot);
        }

        /// <summary>
        /// The package a file's own module belongs to — what its relative imports anchor on. For
        /// <c>pkg/sub/mod.py</c> (namespace <c>pkg.sub.mod</c>) that is <c>pkg.sub</c>; for
        /// <c>pkg/sub/__init__.py</c> the namespace IS the package, so nothing is dropped.
        /// </summary>
        private static string SelfPackageOf(string moduleNamespace, string filePath)
        {
            if (string.IsNullOrEmpty(moduleNamespace)) return string.Empty;
            bool isPackageInit = !string.IsNullOrEmpty(filePath) && string.Equals(
                System.IO.Path.GetFileNameWithoutExtension(filePath), "__init__", StringComparison.OrdinalIgnoreCase);
            return isPackageInit ? moduleNamespace : ParentPackage(moduleNamespace);
        }

        // ------------------------------------------------------------------ module namespace

        /// <summary>
        /// Derives the Python module namespace ("foo.bar.baz") from a file path by
        /// matching against known projects in the solution. Mirrors
        /// <see cref="PythonTypeExtractor"/>'s namespace derivation: the deepest
        /// project whose directory is an ancestor of <paramref name="filePath"/> wins;
        /// the relative path is converted to dotted form with a leading "src."
        /// stripped and "__init__" collapsed.
        /// </summary>
        public string DeriveModuleNamespace(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return string.Empty;
            ProjectModel best = null;
            int bestLen = -1;
            foreach (ProjectModel p in _solution.Projects)
            {
                string dir = p.ProjectDirectory;
                if (string.IsNullOrEmpty(dir)) continue;
                if (IsPathUnder(dir, filePath) && dir.Length > bestLen)
                {
                    best = p;
                    bestLen = dir.Length;
                }
            }
            if (best == null) return string.Empty;
            return DeriveNamespaceRelative(best, filePath);
        }

        private static bool IsPathUnder(string dir, string file)
        {
            string normDir = dir.Replace('/', System.IO.Path.DirectorySeparatorChar)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar);
            string normFile = file.Replace('/', System.IO.Path.DirectorySeparatorChar);
            return normFile.StartsWith(normDir + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string DeriveNamespaceRelative(ProjectModel project, string filePath)
        {
            string projectDir = project.ProjectDirectory;
            if (string.IsNullOrEmpty(projectDir)) return project.DefaultNamespace ?? string.Empty;
            string rel;
            try
            {
                Uri fromUri = new Uri(projectDir.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString())
                    ? projectDir : projectDir + System.IO.Path.DirectorySeparatorChar);
                Uri toUri = new Uri(filePath);
                if (fromUri.Scheme != toUri.Scheme) return project.DefaultNamespace ?? string.Empty;
                rel = Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString()
                    .Replace('/', System.IO.Path.DirectorySeparatorChar));
            }
            catch { return project.DefaultNamespace ?? string.Empty; }
            string dir = System.IO.Path.GetDirectoryName(rel) ?? string.Empty;
            string stem = System.IO.Path.GetFileNameWithoutExtension(rel);
            string dotted = dir.Replace(System.IO.Path.DirectorySeparatorChar, '.')
                .Replace(System.IO.Path.AltDirectorySeparatorChar, '.');
            string full;
            if (stem == "__init__") full = dotted.TrimStart('.');
            else full = string.IsNullOrEmpty(dotted) ? stem : dotted.TrimStart('.') + "." + stem;
            if (full.StartsWith("src.", StringComparison.Ordinal)) full = full.Substring(4);
            return full;
        }

        // ------------------------------------------------------------------ exact lookup (no simple-name fallback)

        private TypeModel LookupExact(string fullyQualifiedName)
        {
            if (string.IsNullOrWhiteSpace(fullyQualifiedName)) return null;
            string normalized = fullyQualifiedName.Replace("::", ".");
            foreach (ProjectModel project in _solution.Projects)
            {
                foreach (TypeModel t in project.Types)
                {
                    if (string.Equals(t.FullyQualifiedName, normalized, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }
            return null;
        }

        private FunctionModel LookupFunction(string fullyQualifiedName)
        {
            if (string.IsNullOrWhiteSpace(fullyQualifiedName)) return null;
            string normalized = fullyQualifiedName.Replace("::", ".");
            foreach (ProjectModel project in _solution.Projects)
            {
                foreach (FunctionModel fn in project.Functions)
                {
                    if (string.Equals(fn.FullyQualifiedName, normalized, StringComparison.OrdinalIgnoreCase))
                        return fn;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Result of a <see cref="SymbolResolver.Resolve"/> call.
    /// </summary>
    public sealed class ResolvedSymbol
    {
        public ResolvedSymbol(TypeModel target, string token, IReadOnlyList<TypeModel> candidates)
            : this(target, token, candidates, null) { }

        public ResolvedSymbol(TypeModel target, string token,
                              IReadOnlyList<TypeModel> candidates,
                              FunctionModel function)
            : this(target, token, candidates, function, null) { }

        public ResolvedSymbol(TypeModel target, string token,
                              IReadOnlyList<TypeModel> candidates,
                              FunctionModel function,
                              VariableSymbol variable)
        {
            Target = target;
            Token = token;
            Candidates = candidates;
            Function = function;
            Variable = variable;
        }

        /// <summary>Chosen type; null when <see cref="Candidates"/> requires disambiguation or when the match is a <see cref="Function"/>.</summary>
        public TypeModel Target { get; }

        /// <summary>The token text the user clicked on.</summary>
        public string Token { get; }

        /// <summary>Populated only when the resolver cannot pick between multiple types sharing the simple name. Caller should show a picker.</summary>
        public IReadOnlyList<TypeModel> Candidates { get; }

        /// <summary>Chosen module-level function, when the token resolves to a <c>def</c> at module scope rather than a class.</summary>
        public FunctionModel Function { get; }

        /// <summary>Chosen module-level variable or constant.</summary>
        public VariableSymbol Variable { get; }

        public bool IsAmbiguous { get { return Target == null && Function == null && Variable == null && Candidates != null && Candidates.Count > 1; } }
    }

    /// <summary>A module-level Python assignment that can be navigated and renamed.</summary>
    public sealed class VariableSymbol
    {
        public string Name { get; set; }
        public string Namespace { get; set; }
        public string SourceFilePath { get; set; }
        public int SourceLineNumber { get; set; }
        public int SourceColumnNumber { get; set; }
        public ProjectModel Project { get; set; }
        public TypeModel OwnerType { get; set; }
        public MemberModel Member { get; set; }
    }
}
