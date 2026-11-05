namespace HelixPythonNamespace.Refactoring.Structural
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.Refactoring;
    using HelixPythonNamespace.VirtualCompiler.Ast;
    using H = HelixPythonNamespace.Refactoring.Vectorization.VecHelpers;

    // MatchData for paradigm transforms (procedural <-> OOP). Port of the C++
    // structural::DictClassData / MethodInfo / FunctionsClassData.

    /// <summary>
    /// O2 dict-of-data &lt;-&gt; class. The plain (procedural) shape is a factory function
    /// returning a dict literal of {"field": field}; the OOP shape is a class with an
    /// __init__ that assigns each field to self. Both directions share this class.
    /// </summary>
    public sealed class DictClassData
    {
        public string FuncName;                 // plain side: e.g. "make_point"
        public string ClassName;                // OOP side: e.g. "Point"
        public List<string> Fields = new List<string>(); // ordered; param == dict key == value
        public int StartLine;
        public int EndLine;
    }

    /// <summary>
    /// O1 free functions &lt;-&gt; class. One method carried between the procedural and OOP
    /// forms; the body text is lifted verbatim (dedented to column 0) so the transform
    /// round-trips byte-exact.
    /// </summary>
    public sealed class MethodInfo
    {
        public string MethodName;               // OOP side, e.g. "increment"
        public string FuncName;                 // procedural side, e.g. "counter_increment"
        public string ParamsText;               // rendered, e.g. "self, amount"
        public List<string> BodyLines = new List<string>(); // dedented to column 0, verbatim
    }

    /// <summary>
    /// O1 free functions &lt;-&gt; class. The procedural shape is a run of consecutive
    /// top-level functions sharing a name prefix and a <c>self</c> first parameter; the OOP
    /// shape is a class whose methods are those functions with the prefix stripped.
    /// </summary>
    public sealed class FunctionsClassData
    {
        public string Prefix;                   // procedural side, e.g. "counter"
        public string ClassName;                // OOP side, e.g. "Counter"
        public List<MethodInfo> Methods = new List<MethodInfo>();
        public int StartLine;
        public int EndLine;
    }

    /// <summary>
    /// Structural matchers for the paradigm family (procedural &lt;-&gt; OOP). Port of the C++
    /// <c>structural::TryMatch_DictToClass / _ClassToDict / _FunctionsToClass /
    /// _ClassToFunctions</c> and their helpers.
    /// </summary>
    public static class ParadigmMatcher
    {
        // ---- small string helpers (C++ StartsWith / EndsWith were free functions) ----

        private static bool StartsWith(string s, string p)
        {
            return s.StartsWith(p, StringComparison.Ordinal);
        }

        private static bool EndsWith(string s, string p)
        {
            return s.EndsWith(p, StringComparison.Ordinal);
        }

        // ASCII-only case folding to mirror the C++ 'C'-locale std::toupper/std::tolower.
        private static char ToUpperAscii(char c)
        {
            return (c >= 'a' && c <= 'z') ? (char)(c - 32) : c;
        }

        private static char ToLowerAscii(char c)
        {
            return (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
        }

        // ---- shape predicates (were file-static in the C++ anonymous namespace) ----

        /// <summary>
        /// Extract simple positional parameter names; returns false if the signature uses
        /// anything beyond plain positional args (annotations rejected too — presence of
        /// defaults/varargs/etc rejects to keep the template round-trip exact).
        /// </summary>
        private static bool SimplePositionalParams(Arguments a, List<string> outp)
        {
            if (a == null) return false;
            if (a.PosOnlyArgs.Count > 0 || a.KwOnlyArgs.Count > 0 ||
                a.VarArg != null || a.KwArg != null || a.Defaults.Count > 0)
            {
                return false;
            }
            foreach (Arg arg in a.Args)
            {
                if (arg.Annotation != null) return false;
                outp.Add(arg.Name);
            }
            return true;
        }

        /// <summary><c>self.field</c> attribute on a Name("self").</summary>
        private static bool IsSelfAttr(Expr e, out string fieldOut)
        {
            fieldOut = null;
            AttributeExpr attr = e as AttributeExpr;
            if (attr == null) return false;
            if (!H.IsNameWithId(attr.Value, "self")) return false;
            fieldOut = attr.Attr;
            return true;
        }

        private static bool IsLowerAlnum(string s)
        {
            if (s.Length == 0) return false;
            if (!(s[0] >= 'a' && s[0] <= 'z')) return false;
            foreach (char c in s)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) return false;
            }
            return true;
        }

        /// <summary>
        /// <c>Counter</c>: first char A-Z, the rest lowercase alnum. Exactly the shape
        /// <see cref="ClassNameForPrefix"/> produces, so the prefix bijection is byte-exact.
        /// </summary>
        private static bool IsCapitalizedWord(string s)
        {
            if (s.Length < 2) return s.Length == 1 && s[0] >= 'A' && s[0] <= 'Z';
            if (!(s[0] >= 'A' && s[0] <= 'Z')) return false;
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) return false;
            }
            return true;
        }

        private static bool IsDunder(string s)
        {
            return s.Length >= 4 && StartsWith(s, "__") && EndsWith(s, "__");
        }

        /// <summary>
        /// Render a plain positional parameter list, including annotations / defaults, via
        /// PyExprPrint. Rejects pos-only / kw-only / *args / **kwargs and any expression
        /// PyExprPrint cannot render (returns null) so transforms only fire on shapes that
        /// re-render identically.
        /// </summary>
        private static string RenderSimpleParams(Arguments a)
        {
            if (a == null) return null;
            if (a.PosOnlyArgs.Count > 0 || a.KwOnlyArgs.Count > 0 ||
                a.VarArg != null || a.KwArg != null)
            {
                return null;
            }
            IReadOnlyList<Arg> args = a.Args;
            IReadOnlyList<Expr> defaults = a.Defaults;
            int firstDefault = args.Count - defaults.Count;
            StringBuilder outSb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) outSb.Append(", ");
                outSb.Append(args[i].Name);
                if (args[i].Annotation != null)
                {
                    string ann = H.PyExprPrint(args[i].Annotation);
                    if (ann == "<unsupported>") return null;
                    outSb.Append(": ").Append(ann);
                }
                if (i >= firstDefault)
                {
                    string def = H.PyExprPrint(defaults[i - firstDefault]);
                    if (def == "<unsupported>") return null;
                    outSb.Append(args[i].Annotation != null ? " = " + def : "=" + def);
                }
            }
            return outSb.ToString();
        }

        /// <summary>First param of a function is exactly <c>self</c> (plain positional).</summary>
        private static bool HasSelfFirstParam(FunctionDef fn)
        {
            Arguments a = fn.Args;
            if (a == null || a.Args.Count == 0) return false;
            if (a.PosOnlyArgs.Count > 0) return false;
            return a.Args[0].Name == "self";
        }

        private static string PrefixOf(string funcName, out string methodOut)
        {
            methodOut = "";
            int pos = funcName.IndexOf('_');
            if (pos < 0 || pos == 0 || pos + 1 >= funcName.Length) return "";
            methodOut = funcName.Substring(pos + 1);
            return funcName.Substring(0, pos);
        }

        // ---- naming bijections (public statics per the port contract) ----

        /// <summary>Naming bijection: make_point -&gt; Point.</summary>
        public static string DataClassNameForFunc(string funcName)
        {
            string core = funcName;
            if (StartsWith(core, "make_")) core = core.Substring(5);
            if (core.Length == 0) core = funcName;
            if (core.Length > 0) core = ToUpperAscii(core[0]) + core.Substring(1);
            return core;
        }

        /// <summary>Naming bijection: Point -&gt; make_point.</summary>
        public static string DataFuncNameForClass(string className)
        {
            string core = className;
            if (core.Length > 0) core = ToLowerAscii(core[0]) + core.Substring(1);
            return "make_" + core;
        }

        /// <summary>Naming bijection: counter -&gt; Counter (capitalize first char only).</summary>
        public static string ClassNameForPrefix(string prefix)
        {
            if (prefix.Length == 0) return prefix;
            return ToUpperAscii(prefix[0]) + prefix.Substring(1);
        }

        /// <summary>Naming bijection: Counter -&gt; counter (lowercase every char).</summary>
        public static string PrefixForClassName(string className)
        {
            char[] chars = className.ToCharArray();
            for (int i = 0; i < chars.Length; i++) chars[i] = ToLowerAscii(chars[i]);
            return new string(chars);
        }

        // ---- O2 dict <-> class ----

        /// <summary>
        /// Forward: a top-level FunctionDef shaped like
        /// <c>def make_X(a, b): return {"a": a, "b": b}</c>. Returns null on no-match.
        /// </summary>
        public static DictClassData TryMatchDictToClass(Stmt s)
        {
            FunctionDef fn = s as FunctionDef;
            if (fn == null) return null;
            if (!StartsWith(fn.Name, "make_")) return null;

            List<string> parms = new List<string>();
            if (!SimplePositionalParams(fn.Args, parms) || parms.Count == 0) return null;

            IReadOnlyList<Stmt> body = fn.Body;
            if (body.Count != 1) return null;
            Return ret = body[0] as Return;
            if (ret == null) return null;
            Dict dict = ret.Value as Dict;
            if (dict == null) return null;

            IReadOnlyList<Expr> keys = dict.Keys;
            IReadOnlyList<Expr> vals = dict.Values;
            if (keys.Count != parms.Count) return null;

            DictClassData d = new DictClassData();
            for (int i = 0; i < keys.Count; i++)
            {
                Constant kc = keys[i] as Constant;
                if (kc == null || !(kc.Value is string)) return null;
                string key = (string)kc.Value;
                // strict clean shape: key == value-name == param-name (positionally)
                if (!H.IsNameWithId(vals[i], key)) return null;
                if (key != parms[i]) return null;
                d.Fields.Add(key);
            }

            d.FuncName = fn.Name;
            d.ClassName = DataClassNameForFunc(fn.Name);
            d.StartLine = fn.Span.Start.Line;
            d.EndLine = fn.Span.End.Line;
            return d;
        }

        /// <summary>
        /// Reverse: a top-level ClassDef shaped like
        /// <c>class X: def __init__(self, a, b): self.a = a; self.b = b</c>.
        /// Returns null on no-match.
        /// </summary>
        public static DictClassData TryMatchClassToDict(Stmt s)
        {
            ClassDef cd = s as ClassDef;
            if (cd == null) return null;
            if (cd.Bases.Count > 0) return null;
            if (EndsWith(cd.Name, "Factory")) return null;  // disambiguate from P1

            if (cd.Body.Count != 1) return null;
            FunctionDef init = cd.Body[0] as FunctionDef;
            if (init == null || init.Name != "__init__") return null;
            if (init.DecoratorList.Count > 0) return null;

            List<string> parms = new List<string>();
            if (!SimplePositionalParams(init.Args, parms)) return null;
            if (parms.Count == 0 || parms[0] != "self") return null;
            List<string> fields = new List<string>();
            for (int i = 1; i < parms.Count; i++) fields.Add(parms[i]);
            if (fields.Count == 0) return null;

            IReadOnlyList<Stmt> body = init.Body;
            if (body.Count != fields.Count) return null;
            for (int i = 0; i < fields.Count; i++)
            {
                Assign asn = body[i] as Assign;
                if (asn == null || asn.Targets.Count != 1) return null;
                string field;
                if (!IsSelfAttr(asn.Targets[0], out field)) return null;
                if (field != fields[i]) return null;
                if (!H.IsNameWithId(asn.Value, fields[i])) return null;
            }

            DictClassData d = new DictClassData();
            d.ClassName = cd.Name;
            d.FuncName = DataFuncNameForClass(cd.Name);
            d.Fields = fields;
            d.StartLine = cd.Span.Start.Line;
            d.EndLine = cd.Span.End.Line;
            return d;
        }

        // ---- O1 free functions <-> class ----

        /// <summary>
        /// Forward: a maximal run of consecutive plain FunctionDefs in <paramref name="body"/>
        /// starting at <paramref name="startIdx"/>, each with first param <c>self</c>, a shared
        /// lowercase prefix, separated by exactly one blank line. Returns null if the run is
        /// shorter than two functions or any function is unsuitable.
        /// <paramref name="normalizedSource"/> must be <see cref="BodySlicer.NormalizedSource"/>
        /// of the selection so bodies can be sliced verbatim.
        /// </summary>
        public static FunctionsClassData TryMatchFunctionsToClass(
            IReadOnlyList<Stmt> body, int startIdx, string normalizedSource)
        {
            if (startIdx >= body.Count) return null;

            string prefix = "";
            List<MethodInfo> methods = new List<MethodInfo>();
            FunctionDef prevFn = null;
            int startLine = 0, endLine = 0;

            for (int i = startIdx; i < body.Count; i++)
            {
                FunctionDef fn = body[i] as FunctionDef;
                if (fn == null) break;
                if (fn.DecoratorList.Count > 0) break;
                if (!HasSelfFirstParam(fn)) break;

                string method;
                string pre = PrefixOf(fn.Name, out method);
                if (pre.Length == 0 || !IsLowerAlnum(pre) || IsDunder(fn.Name)) break;
                if (prefix.Length == 0) prefix = pre;
                else if (pre != prefix) break;

                // Consecutive members must be separated by exactly one blank line so the
                // reverse re-emits identical spacing. Counted from the def header (reliable)
                // rather than the previous body's end span (overshoots).
                if (prevFn != null &&
                    BodySlicer.BlankLinesAbove(normalizedSource, fn.Span.Start.Line) != 1)
                {
                    break;
                }

                string parms = RenderSimpleParams(fn.Args);
                if (parms == null) break;

                MethodInfo m = new MethodInfo();
                m.MethodName = method;
                m.FuncName = fn.Name;
                m.ParamsText = parms;
                List<string> lines = BodySlicer.SliceIndentedBody(
                    normalizedSource, fn.Span.Start.Line, fn.Body[0].Span.Start.Line);
                int w = BodySlicer.CommonIndentWidth(lines);
                m.BodyLines = BodySlicer.Dedent(lines, w);
                methods.Add(m);

                if (startLine == 0) startLine = fn.Span.Start.Line;
                endLine = fn.Span.End.Line;
                prevFn = fn;
            }

            if (methods.Count < 2) return null;

            FunctionsClassData d = new FunctionsClassData();
            d.Prefix = prefix;
            d.ClassName = ClassNameForPrefix(prefix);
            d.Methods = methods;
            d.StartLine = startLine;
            d.EndLine = endLine;
            return d;
        }

        /// <summary>
        /// Reverse: a top-level ClassDef whose members are &gt;= 2 plain methods (each with a
        /// <c>self</c> first param, no dunder names), no bases/decorators, and whose name is a
        /// single capitalized lowercase word. Bodies sliced verbatim from
        /// <paramref name="normalizedSource"/>. Returns null on no-match.
        /// </summary>
        public static FunctionsClassData TryMatchClassToFunctions(Stmt s, string normalizedSource)
        {
            ClassDef cd = s as ClassDef;
            if (cd == null) return null;
            if (cd.Bases.Count > 0 || cd.Keywords.Count > 0 || cd.DecoratorList.Count > 0)
            {
                return null;
            }
            if (!IsCapitalizedWord(cd.Name) || EndsWith(cd.Name, "Factory")) return null;
            if (cd.Body.Count < 2) return null;

            string prefix = PrefixForClassName(cd.Name);
            if (!IsLowerAlnum(prefix)) return null;

            List<MethodInfo> methods = new List<MethodInfo>();
            FunctionDef prevFn = null;
            foreach (Stmt bs in cd.Body)
            {
                FunctionDef fn = bs as FunctionDef;
                if (fn == null) return null;
                if (fn.DecoratorList.Count > 0) return null;
                if (IsDunder(fn.Name)) return null;
                if (!HasSelfFirstParam(fn)) return null;
                // Methods separated by exactly one blank line (matches forward output).
                if (prevFn != null &&
                    BodySlicer.BlankLinesAbove(normalizedSource, fn.Span.Start.Line) != 1)
                {
                    return null;
                }
                string parms = RenderSimpleParams(fn.Args);
                if (parms == null) return null;

                MethodInfo m = new MethodInfo();
                m.MethodName = fn.Name;
                m.FuncName = prefix + "_" + fn.Name;
                m.ParamsText = parms;
                List<string> lines = BodySlicer.SliceIndentedBody(
                    normalizedSource, fn.Span.Start.Line, fn.Body[0].Span.Start.Line);
                int w = BodySlicer.CommonIndentWidth(lines);
                m.BodyLines = BodySlicer.Dedent(lines, w);
                methods.Add(m);
                prevFn = fn;
            }

            FunctionsClassData d = new FunctionsClassData();
            d.Prefix = prefix;
            d.ClassName = cd.Name;
            d.Methods = methods;
            d.StartLine = cd.Span.Start.Line;
            d.EndLine = cd.Span.End.Line;
            return d;
        }
    }
}
