namespace HelixPythonNamespace.Refactoring.Structural
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Refactoring.Vectorization;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    // ---------------------------------------------------------------------------
    // MatchData carriers for the SOLID transforms. Port of the C++
    // helix::refactoring::structural MatchData structs.

    /// <summary>
    /// S2 DIP match data — introduce / inline an abstraction. The concrete shape is a
    /// plain class; the abstracted shape is that class implementing a sibling
    /// <c>&lt;Name&gt;Protocol(Protocol)</c> whose method headers mirror the class. The
    /// class body is carried verbatim so the transform round-trips byte-exact; only the
    /// class header gains/loses the <c>(&lt;Name&gt;Protocol)</c> base.
    /// </summary>
    public sealed class DipData
    {
        public string ClassName;                              // e.g. "PaymentService"
        public string ProtocolName;                           // e.g. "PaymentServiceProtocol"
        public List<string> MethodHeaders = new List<string>();   // verbatim `    def m(self, ...):` lines
        public List<string> ClassBodyLines = new List<string>();  // verbatim class body (indent kept)
        public int StartLine;
        public int EndLine;
    }

    /// <summary>One SRP cohesion cluster carried into the emitter.</summary>
    public sealed class SrpClassPart
    {
        public string ClassName;                              // "AccountBalance"
        public List<string> BodyLines = new List<string>();   // verbatim methods (indent 4), blank-separated
    }

    /// <summary>S1 SRP split data — >= 2 cohesion clusters.</summary>
    public sealed class SrpSplitData
    {
        public List<SrpClassPart> Parts = new List<SrpClassPart>();
        public int StartLine;
        public int EndLine;
    }

    /// <summary>S1 SRP merge data — a common-prefix class name plus all methods verbatim.</summary>
    public sealed class SrpMergeData
    {
        public string MergedName;                             // common-prefix class name, e.g. "Account"
        public List<string> BodyLines = new List<string>();   // all methods from all parts (indent 4)
        public int StartLine;
        public int EndLine;
    }

    /// <summary>One ISP role protocol (a single method stub) carried into the emitter.</summary>
    public sealed class IspRole
    {
        public string ProtocolName;                           // "RepositoryFetch"
        public List<string> MethodLines = new List<string>(); // verbatim single method stub (indent 4)
    }

    /// <summary>S3 ISP segregate data — >= 2 role protocols.</summary>
    public sealed class IspSegregateData
    {
        public List<IspRole> Roles = new List<IspRole>();
        public int StartLine;
        public int EndLine;
    }

    /// <summary>S3 ISP collapse data — common-prefix protocol name plus all method stubs.</summary>
    public sealed class IspCollapseData
    {
        public string ProtocolName;                           // common-prefix protocol name
        public List<string> MethodLines = new List<string>(); // all method stubs concatenated (indent 4)
        public int StartLine;
        public int EndLine;
    }

    /// <summary>
    /// Deterministic AST-pattern matchers for the SOLID structural family (S1 SRP,
    /// S2 DIP, S3 ISP — each forward + reverse). Port of the C++
    /// <c>helix::refactoring::structural</c> matcher free functions. Each
    /// <c>TryMatch*</c> returns the extracted data or <c>null</c> on no-match.
    /// </summary>
    public static class SolidMatcher
    {
        // ---- shared local detectors ----

        private static bool EndsWith(string s, string p)
        {
            return s != null && p != null && s.Length >= p.Length &&
                   string.CompareOrdinal(s, s.Length - p.Length, p, 0, p.Length) == 0;
        }

        // All members are methods with a `self` first positional param (DIP variant:
        // rejects any pos-only args).
        private static bool AllSelfMethods(ClassDef cd)
        {
            if (cd.Body.Count == 0) return false;
            foreach (Stmt bs in cd.Body)
            {
                FunctionDef fn = bs as FunctionDef;
                if (fn == null) return false;
                Arguments a = fn.Args;
                if (a == null || a.Args.Count == 0 || a.PosOnlyArgs.Count != 0) return false;
                if (a.Args[0].Name != "self") return false;
            }
            return true;
        }

        private static Name SingleBaseName(ClassDef cd)
        {
            if (cd.Bases.Count != 1 || cd.Keywords.Count != 0) return null;
            return cd.Bases[0] as Name;
        }

        // ---- S2 DIP -------------------------------------------------------------

        /// <summary>
        /// Forward: a plain top-level ClassDef (no bases / keywords / decorators) whose
        /// members are all methods with a <c>self</c> first param, not named
        /// <c>&lt;X&gt;Protocol</c> or <c>&lt;X&gt;Factory</c>.
        /// </summary>
        public static DipData TryMatchDipIntroduce(Stmt s, string normalizedSource)
        {
            ClassDef cd = s as ClassDef;
            if (cd == null) return null;
            if (cd.Bases.Count != 0 || cd.Keywords.Count != 0 || cd.DecoratorList.Count != 0)
            {
                return null;
            }
            if (EndsWith(cd.Name, "Protocol") || EndsWith(cd.Name, "Factory")) return null;
            if (!AllSelfMethods(cd)) return null;

            DipData d = new DipData();
            d.ClassName = cd.Name;
            d.ProtocolName = cd.Name + "Protocol";
            foreach (Stmt bs in cd.Body)
            {
                FunctionDef fn = (FunctionDef)bs;
                d.MethodHeaders.Add(BodySlicer.LineText(normalizedSource, fn.Span.Start.Line));
            }
            d.ClassBodyLines = BodySlicer.SliceIndentedBody(
                normalizedSource, cd.Span.Start.Line, cd.Body[0].Span.Start.Line);
            d.StartLine = cd.Span.Start.Line;
            d.EndLine = cd.Span.End.Line;
            return d;
        }

        /// <summary>
        /// Reverse: a consecutive pair starting at <paramref name="startIdx"/>:
        /// <c>class &lt;N&gt;Protocol(Protocol): ...</c> then
        /// <c>class &lt;N&gt;(&lt;N&gt;Protocol): ...</c> collapses back to bare
        /// <c>class &lt;N&gt;</c>.
        /// </summary>
        public static DipData TryMatchDipInline(IReadOnlyList<Stmt> body, int startIdx, string normalizedSource)
        {
            if (startIdx + 1 >= body.Count) return null;
            ClassDef proto = body[startIdx] as ClassDef;
            ClassDef impl = body[startIdx + 1] as ClassDef;
            if (proto == null || impl == null) return null;
            if (!EndsWith(proto.Name, "Protocol")) return null;

            Name protoBase = SingleBaseName(proto);
            if (protoBase == null || protoBase.Id != "Protocol") return null;

            Name implBase = SingleBaseName(impl);
            if (implBase == null || implBase.Id != proto.Name) return null;
            if (impl.Name + "Protocol" != proto.Name) return null;
            if (impl.DecoratorList.Count != 0 || impl.Body.Count == 0) return null;

            DipData d = new DipData();
            d.ClassName = impl.Name;
            d.ProtocolName = proto.Name;
            d.ClassBodyLines = BodySlicer.SliceIndentedBody(
                normalizedSource, impl.Span.Start.Line, impl.Body[0].Span.Start.Line);
            d.StartLine = proto.Span.Start.Line;
            d.EndLine = impl.Span.End.Line;
            return d;
        }

        // ---- S1 SRP: self-field collection ------------------------------------

        // Recursively collect every `self.<field>` access in an expression.
        private static void CollectSelfFieldsExpr(Expr e, ISet<string> outFields)
        {
            if (e == null) return;
            AttributeExpr a = e as AttributeExpr;
            if (a != null)
            {
                if (VecHelpers.IsNameWithId(a.Value, "self")) outFields.Add(a.Attr);
                CollectSelfFieldsExpr(a.Value, outFields);
                return;
            }
            Call c = e as Call;
            if (c != null)
            {
                CollectSelfFieldsExpr(c.Func, outFields);
                foreach (Expr arg in c.Args) CollectSelfFieldsExpr(arg, outFields);
                foreach (Keyword kw in c.Keywords) CollectSelfFieldsExpr(kw.Value, outFields);
                return;
            }
            BinOpExpr b = e as BinOpExpr;
            if (b != null) { CollectSelfFieldsExpr(b.Left, outFields); CollectSelfFieldsExpr(b.Right, outFields); return; }
            UnaryOpExpr u = e as UnaryOpExpr;
            if (u != null) { CollectSelfFieldsExpr(u.Operand, outFields); return; }
            BoolOpExpr bo = e as BoolOpExpr;
            if (bo != null) { foreach (Expr v in bo.Values) CollectSelfFieldsExpr(v, outFields); return; }
            Compare cmp = e as Compare;
            if (cmp != null)
            {
                CollectSelfFieldsExpr(cmp.Left, outFields);
                foreach (Expr cc in cmp.Comparators) CollectSelfFieldsExpr(cc, outFields);
                return;
            }
            Subscript sub = e as Subscript;
            if (sub != null) { CollectSelfFieldsExpr(sub.Value, outFields); CollectSelfFieldsExpr(sub.Slice, outFields); return; }
            IfExp ie = e as IfExp;
            if (ie != null)
            {
                CollectSelfFieldsExpr(ie.Test, outFields);
                CollectSelfFieldsExpr(ie.Body, outFields);
                CollectSelfFieldsExpr(ie.OrElse, outFields);
                return;
            }
            TupleExpr t = e as TupleExpr;
            if (t != null) { foreach (Expr x in t.Elts) CollectSelfFieldsExpr(x, outFields); return; }
            ListExpr l = e as ListExpr;
            if (l != null) { foreach (Expr x in l.Elts) CollectSelfFieldsExpr(x, outFields); return; }
            Dict dd = e as Dict;
            if (dd != null)
            {
                foreach (Expr k in dd.Keys) CollectSelfFieldsExpr(k, outFields);
                foreach (Expr v in dd.Values) CollectSelfFieldsExpr(v, outFields);
                return;
            }
        }

        private static void CollectSelfFieldsStmt(Stmt s, ISet<string> outFields)
        {
            if (s == null) return;
            Assign asg = s as Assign;
            if (asg != null)
            {
                foreach (Expr t in asg.Targets) CollectSelfFieldsExpr(t, outFields);
                CollectSelfFieldsExpr(asg.Value, outFields);
                return;
            }
            AugAssign aug = s as AugAssign;
            if (aug != null) { CollectSelfFieldsExpr(aug.Target, outFields); CollectSelfFieldsExpr(aug.Value, outFields); return; }
            AnnAssign ann = s as AnnAssign;
            if (ann != null) { CollectSelfFieldsExpr(ann.Target, outFields); CollectSelfFieldsExpr(ann.Value, outFields); return; }
            Return r = s as Return;
            if (r != null) { CollectSelfFieldsExpr(r.Value, outFields); return; }
            ExprStmt es = s as ExprStmt;
            if (es != null) { CollectSelfFieldsExpr(es.Value, outFields); return; }
            If i = s as If;
            if (i != null)
            {
                CollectSelfFieldsExpr(i.Test, outFields);
                CollectSelfFieldsStmts(i.Body, outFields);
                CollectSelfFieldsStmts(i.OrElse, outFields);
                return;
            }
            For f = s as For;
            if (f != null)
            {
                CollectSelfFieldsExpr(f.Target, outFields);
                CollectSelfFieldsExpr(f.Iter, outFields);
                CollectSelfFieldsStmts(f.Body, outFields);
                CollectSelfFieldsStmts(f.OrElse, outFields);
                return;
            }
            While w = s as While;
            if (w != null)
            {
                CollectSelfFieldsExpr(w.Test, outFields);
                CollectSelfFieldsStmts(w.Body, outFields);
                CollectSelfFieldsStmts(w.OrElse, outFields);
                return;
            }
            Raise rz = s as Raise;
            if (rz != null) { CollectSelfFieldsExpr(rz.Exc, outFields); CollectSelfFieldsExpr(rz.Cause, outFields); return; }
            Assert az = s as Assert;
            if (az != null) { CollectSelfFieldsExpr(az.Test, outFields); CollectSelfFieldsExpr(az.Msg, outFields); return; }
        }

        private static void CollectSelfFieldsStmts(IReadOnlyList<Stmt> body, ISet<string> outFields)
        {
            foreach (Stmt s in body) CollectSelfFieldsStmt(s, outFields);
        }

        private static string CapitalizeFirst(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            char c0 = s[0];
            if (c0 >= 'a' && c0 <= 'z') c0 = (char)(c0 - ('a' - 'A'));
            return c0 + s.Substring(1);
        }

        private static List<string> SliceMethod(string normalizedSource, FunctionDef fn)
        {
            List<string> outLines = new List<string>();
            outLines.Add(BodySlicer.LineText(normalizedSource, fn.Span.Start.Line));
            List<string> body = BodySlicer.SliceIndentedBody(
                normalizedSource, fn.Span.Start.Line, fn.Body[0].Span.Start.Line);
            foreach (string l in body) outLines.Add(l);
            return outLines;
        }

        private static bool IsPlainClassS(ClassDef cd)
        {
            return cd.Bases.Count == 0 && cd.Keywords.Count == 0 && cd.DecoratorList.Count == 0;
        }

        private static bool AllSelfMethodsS(ClassDef cd)
        {
            if (cd.Body.Count == 0) return false;
            foreach (Stmt bs in cd.Body)
            {
                FunctionDef fn = bs as FunctionDef;
                if (fn == null || fn.DecoratorList.Count != 0) return false;
                Arguments a = fn.Args;
                if (a == null || a.Args.Count == 0 || a.Args[0].Name != "self") return false;
            }
            return true;
        }

        /// <summary>
        /// Forward: a plain top-level ClassDef whose <c>self</c> methods (each touching
        /// >= 1 field) form >= 2 disjoint cohesion clusters by shared
        /// <c>self.&lt;field&gt;</c> access.
        /// </summary>
        public static SrpSplitData TryMatchSrpSplit(Stmt s, string normalizedSource)
        {
            ClassDef cd = s as ClassDef;
            if (cd == null || !IsPlainClassS(cd)) return null;
            if (EndsWith(cd.Name, "Protocol") || EndsWith(cd.Name, "Factory") ||
                EndsWith(cd.Name, "Facade") || EndsWith(cd.Name, "Decorator"))
            {
                return null;
            }
            if (!AllSelfMethodsS(cd) || cd.Body.Count < 2) return null;

            int n = cd.Body.Count;
            List<FunctionDef> methods = new List<FunctionDef>();
            List<HashSet<string>> fields = new List<HashSet<string>>();
            foreach (Stmt bs in cd.Body)
            {
                FunctionDef fn = (FunctionDef)bs;
                if (fn.Name == "__init__") return null;  // shared seed obscures clusters
                HashSet<string> f = new HashSet<string>(System.StringComparer.Ordinal);
                CollectSelfFieldsStmts(fn.Body, f);
                if (f.Count == 0) return null;  // a method with no field cohesion isn't clusterable
                methods.Add(fn);
                fields.Add(f);
            }

            // Union-find over methods linked by shared fields.
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            System.Func<int, int> find = null;
            find = delegate (int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            };
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    bool shared = false;
                    foreach (string f in fields[i]) { if (fields[j].Contains(f)) { shared = true; break; } }
                    if (shared) parent[find(i)] = find(j);
                }
            }

            // Group methods by root, preserving first-appearance order.
            List<int> order = new List<int>();
            List<List<int>> groups = new List<List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = find(i);
                int gi = order.IndexOf(r);
                if (gi < 0) { order.Add(r); groups.Add(new List<int>()); gi = order.Count - 1; }
                groups[gi].Add(i);
            }
            if (groups.Count < 2) return null;  // not a multi-responsibility god class

            SrpSplitData d = new SrpSplitData();
            foreach (List<int> grp in groups)
            {
                SortedSet<string> clusterFields = new SortedSet<string>(System.StringComparer.Ordinal);
                foreach (int mi in grp) foreach (string f in fields[mi]) clusterFields.Add(f);
                string repField = clusterFields.Min;  // lexicographically smallest
                SrpClassPart part = new SrpClassPart();
                part.ClassName = cd.Name + CapitalizeFirst(repField);
                bool first = true;
                foreach (int mi in grp)
                {
                    if (!first) part.BodyLines.Add("");
                    first = false;
                    foreach (string l in SliceMethod(normalizedSource, methods[mi])) part.BodyLines.Add(l);
                }
                d.Parts.Add(part);
            }
            d.StartLine = cd.Span.Start.Line;
            d.EndLine = cd.Span.End.Line;
            return d;
        }

        /// <summary>
        /// Reverse: a run (>= 2) of consecutive plain all-self-method ClassDefs whose
        /// names share a non-empty common prefix ending on a CapWord boundary; merges
        /// into one class.
        /// </summary>
        public static SrpMergeData TryMatchSrpMerge(IReadOnlyList<Stmt> body, int startIdx, out int consumed,
                                                    string normalizedSource)
        {
            consumed = 0;
            // Maximal run of consecutive plain all-self-method classes.
            List<ClassDef> run = new List<ClassDef>();
            for (int i = startIdx; i < body.Count; i++)
            {
                ClassDef cd = body[i] as ClassDef;
                if (cd == null || !IsPlainClassS(cd) || !AllSelfMethodsS(cd)) break;
                run.Add(cd);
            }
            if (run.Count < 2) return null;

            string prefix = CommonPrefixWithCapBoundary(run);
            if (prefix == null) return null;

            SrpMergeData d = new SrpMergeData();
            d.MergedName = prefix;
            bool first = true;
            foreach (ClassDef cd in run)
            {
                foreach (Stmt bs in cd.Body)
                {
                    FunctionDef fn = (FunctionDef)bs;
                    if (!first) d.BodyLines.Add("");
                    first = false;
                    foreach (string l in SliceMethod(normalizedSource, fn)) d.BodyLines.Add(l);
                }
            }
            d.StartLine = run[0].Span.Start.Line;
            d.EndLine = run[run.Count - 1].Span.End.Line;
            consumed = run.Count;
            return d;
        }

        // Longest common prefix of the run's class names, requiring the prefix to start
        // with an uppercase letter and each suffix to begin a new CapWord. Returns the
        // prefix, or null when it is not a clean class-name boundary.
        private static string CommonPrefixWithCapBoundary(List<ClassDef> run)
        {
            string prefix = run[0].Name;
            for (int i = 1; i < run.Count; i++)
            {
                string nm = run[i].Name;
                int k = 0;
                while (k < prefix.Length && k < nm.Length && prefix[k] == nm[k]) k++;
                prefix = prefix.Substring(0, k);
            }
            if (prefix.Length == 0) return null;
            if (!(prefix[0] >= 'A' && prefix[0] <= 'Z')) return null;
            // Each suffix must start a new CapWord (uppercase right after the prefix),
            // so the prefix is a clean class name and not a mid-word cut.
            foreach (ClassDef cd in run)
            {
                if (cd.Name.Length <= prefix.Length) return null;
                char nextc = cd.Name[prefix.Length];
                if (!(nextc >= 'A' && nextc <= 'Z')) return null;
            }
            return prefix;
        }

        // ---- S3 ISP -------------------------------------------------------------

        private static bool IsProtocolClass(ClassDef cd)
        {
            if (cd.Bases.Count != 1 || cd.Keywords.Count != 0 || cd.DecoratorList.Count != 0)
            {
                return false;
            }
            return VecHelpers.IsNameWithId(cd.Bases[0], "Protocol");
        }

        // Every member is a `self` method (the role stub); returns false otherwise.
        private static bool AllSelfMethodStubs(ClassDef cd)
        {
            if (cd.Body.Count == 0) return false;
            foreach (Stmt bs in cd.Body)
            {
                FunctionDef fn = bs as FunctionDef;
                if (fn == null || fn.DecoratorList.Count != 0) return false;
                Arguments a = fn.Args;
                if (a == null || a.Args.Count == 0 || a.Args[0].Name != "self") return false;
            }
            return true;
        }

        /// <summary>
        /// Forward: a <c>&lt;Name&gt;(Protocol)</c> ClassDef (single Protocol base, no
        /// keywords / decorators) with >= 2 <c>self</c> method members (the role stubs).
        /// </summary>
        public static IspSegregateData TryMatchIspSegregate(Stmt s, string normalizedSource)
        {
            ClassDef cd = s as ClassDef;
            if (cd == null || !IsProtocolClass(cd)) return null;
            if (!AllSelfMethodStubs(cd) || cd.Body.Count < 2) return null;

            IspSegregateData d = new IspSegregateData();
            HashSet<string> seenRoles = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (Stmt bs in cd.Body)
            {
                FunctionDef fn = (FunctionDef)bs;
                IspRole role = new IspRole();
                string capm = CapitalizeFirst(fn.Name);
                role.ProtocolName = cd.Name + capm;
                if (!seenRoles.Add(role.ProtocolName)) return null;  // name clash
                role.MethodLines = SliceMethod(normalizedSource, fn);
                d.Roles.Add(role);
            }
            d.StartLine = cd.Span.Start.Line;
            d.EndLine = cd.Span.End.Line;
            return d;
        }

        /// <summary>
        /// Reverse: a run (>= 2) of consecutive <c>(Protocol)</c> ClassDefs whose names
        /// share a non-empty common prefix ending on a CapWord boundary; merges into one.
        /// </summary>
        public static IspCollapseData TryMatchIspCollapse(IReadOnlyList<Stmt> body, int startIdx, out int consumed,
                                                         string normalizedSource)
        {
            consumed = 0;
            List<ClassDef> run = new List<ClassDef>();
            for (int i = startIdx; i < body.Count; i++)
            {
                ClassDef cd = body[i] as ClassDef;
                if (cd == null || !IsProtocolClass(cd) || !AllSelfMethodStubs(cd)) break;
                run.Add(cd);
            }
            if (run.Count < 2) return null;

            string prefix = CommonPrefixWithCapBoundary(run);
            if (prefix == null) return null;

            IspCollapseData d = new IspCollapseData();
            d.ProtocolName = prefix;
            foreach (ClassDef cd in run)
            {
                foreach (Stmt bs in cd.Body)
                {
                    FunctionDef fn = (FunctionDef)bs;
                    foreach (string l in SliceMethod(normalizedSource, fn)) d.MethodLines.Add(l);
                }
            }
            d.StartLine = run[0].Span.Start.Line;
            d.EndLine = run[run.Count - 1].Span.End.Line;
            consumed = run.Count;
            return d;
        }
    }
}
