namespace HelixPythonNamespace.GuiDesigner.Parse
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.GuiDesigner.Model;
    using HelixPythonNamespace.Python.Syntax;
    using K = HelixPythonNamespace.GuiDesigner.Model.GuiControlKind;

    /// <summary>
    /// Imports an existing Tkinter class's widget tree into a <see cref="GuiForm"/> — the "load the
    /// existing GUI first" bootstrap for the GUI Designer Canvas. This is a best-effort, ONE-TIME import
    /// (triggered explicitly, e.g. by "Take Over" — see <c>GuiDesignerForm</c>), not an ongoing
    /// bidirectional sync: once imported, the <c>.designer.json</c> sidecar is the persisted source of
    /// truth going forward (the same role a WinForms .resx plays — the designer never re-reads
    /// hand-edited code back into itself the way this importer does; it only ever reads its own
    /// generated shape). Understands a broader pattern than the emitter's own strict output:
    ///
    /// - `self.NAME = ns.Ctor(parent, kw=v, ...)` OR a bare local `NAME = ns.Ctor(parent, ...)` —
    ///   both self-attributes and local variables are tracked as widgets, since real hand-written code
    ///   often uses a local var for an intermediate container (e.g. `notebook = ttk.Notebook(self)`).
    /// - `WIDGET.place(x=, y=, width=, height=)` / `WIDGET.pack(side=, fill=, expand=)` — either layout
    ///   call updates the matching tracked widget, wherever it appears (not required to immediately
    ///   follow the construction line).
    /// - `CONTAINER.add(CHILD, text='...')` where CONTAINER is a tracked TabControl (`ttk.Notebook`) —
    ///   sets CHILD.ParentId to CONTAINER and CHILD.Props["TabTitle"] to the tab label.
    /// - `self.NAME = SomeImportedClass(parent)` where `SomeImportedClass` is NOT a known tk/ttk
    ///   builtin but IS imported at module level (`from some.module import SomeImportedClass`) — a
    ///   composed custom widget, tracked as <see cref="GuiControlKind.CustomComponent"/>.
    /// - `self.title(...)` / `self.geometry(...)` (a Tk/Toplevel subclass, where the window IS self) or
    ///   `self.master.title(...)` / `self.master.geometry(...)` (a Frame subclass needing a master) —
    ///   either sets the form's Title/Width/Height.
    ///
    /// Deliberately narrow otherwise: only walks `__init__` (plus `_build_ui`/`_wire_events` if
    /// present, for the designer's own generated shape), only one level of `if` nesting, and silently
    /// skips any assignment/call it doesn't recognize — it is not a Python interpreter and never
    /// guesses. Returns null when nothing importable was found, so the caller can fall back to an
    /// empty canvas rather than presenting a plausible-looking but wrong/partial reconstruction as if
    /// it were complete.
    /// </summary>
    public static class TkinterLayoutImporter
    {
        // Reverse of TkinterEmitter.CtorFor. Several GuiControlKind values collapse onto the same ctor
        // there (Panel/FlowLayoutPanel/TableLayoutPanel/Form all emit tk.Frame, for instance) — that's
        // real, unavoidable information loss in the forward direction, so this only recovers one
        // representative Kind per ctor (the most common one for that widget shape).
        private static readonly Dictionary<string, K> CtorToKind = new Dictionary<string, K>(StringComparer.Ordinal)
        {
            { "tk.Button", K.Button },
            { "tk.Label", K.Label },
            { "tk.Entry", K.TextBox },
            { "tk.Text", K.RichTextBox },
            { "tk.Checkbutton", K.CheckBox },
            { "tk.Radiobutton", K.RadioButton },
            { "ttk.Combobox", K.ComboBox },
            { "tk.Listbox", K.ListBox },
            { "ttk.Scale", K.TrackBar },
            { "ttk.Spinbox", K.NumericUpDown },
            { "ttk.Progressbar", K.ProgressBar },
            { "tk.Frame", K.Panel },
            { "ttk.Frame", K.Panel },
            { "tk.LabelFrame", K.GroupBox },
            { "tk.PanedWindow", K.SplitContainer },
            { "ttk.Notebook", K.TabControl },
            { "ttk.Treeview", K.TreeView },
        };

        // WinForms-style prop key <- tk-style kwarg name (inverse of TkinterEmitter.WinFormsToTk).
        // Model props are always stored WinForms-style internally — only the final Emit step
        // translates them to framework-native names.
        private static readonly Dictionary<string, string> TkToWinForms = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "text", "Text" }, { "bg", "BackColor" }, { "fg", "ForeColor" },
            { "cursor", "Cursor" }, { "font", "Font" },
        };

        /// <summary>Attempts to reconstruct a <see cref="GuiForm"/> by walking <paramref name="expectedClassName"/>'s
        /// <c>__init__</c> (and, if present, <c>_build_ui</c>/<c>_wire_events</c>) in <paramref name="source"/>.
        /// Returns null when the class can't be found or nothing recognizable was imported.</summary>
        public static GuiForm TryImport(string source, string expectedClassName)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            ModuleSyntax module;
            try { module = HelixPythonNamespace.Python.Python.ParseModule(source).Module; }
            catch { return null; }
            if (module == null) return null;

            Dictionary<string, string> classToModule = CollectCustomClassImports(module);

            ClassDefSyntax cls = FindClass(module, expectedClassName);
            if (cls == null) return null;

            FunctionDefSyntax initFn = FindMethod(cls, "__init__");
            if (initFn == null) return null;

            var form = new GuiForm { FormClassName = cls.Name, Framework = GuiFramework.Tkinter, Width = 640, Height = 480, Title = cls.Name };
            var byVar = new Dictionary<string, GuiControl>(StringComparer.Ordinal);

            WalkStatements(Flatten(initFn.Body.Statements), form, byVar, classToModule);

            FunctionDefSyntax buildFn = FindMethod(cls, "_build_ui");
            if (buildFn != null) WalkStatements(Flatten(buildFn.Body.Statements), form, byVar, classToModule);

            FunctionDefSyntax wireFn = FindMethod(cls, "_wire_events");
            if (wireFn != null) WalkEvents(wireFn.Body.Statements, byVar);

            return form.Controls.Count > 0 ? form : null;
        }

        // name -> module for every `from module import name [as alias]` at module scope.
        private static Dictionary<string, string> CollectCustomClassImports(ModuleSyntax module)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (StatementSyntax s in module.Statements)
            {
                var fi = s as FromImportStatement;
                if (fi == null || fi.Module == null || fi.IsStar) continue;
                string moduleName = fi.Module.AsString;
                foreach (KeyValuePair<string, string> kv in fi.Names)
                {
                    string localName = kv.Value ?? kv.Key; // alias, else the imported name itself
                    if (!string.IsNullOrEmpty(localName)) map[localName] = moduleName;
                }
            }
            return map;
        }

        private static ClassDefSyntax FindClass(ModuleSyntax module, string expectedName)
        {
            ClassDefSyntax first = null;
            foreach (StatementSyntax s in module.Statements)
            {
                var cls = s as ClassDefSyntax;
                if (cls == null) continue;
                if (first == null) first = cls;
                if (!string.IsNullOrEmpty(expectedName) && string.Equals(cls.Name, expectedName, StringComparison.Ordinal))
                    return cls;
            }
            return first;
        }

        private static FunctionDefSyntax FindMethod(ClassDefSyntax cls, string name)
        {
            foreach (StatementSyntax s in cls.Body.Statements)
            {
                var fn = s as FunctionDefSyntax;
                if (fn != null && string.Equals(fn.Name, name, StringComparison.Ordinal)) return fn;
            }
            return null;
        }

        // Yields every statement plus (one level of) the bodies of any `if`/`elif`/`else` among them —
        // just enough to see through the designer's own `if self.master is not None:` wrapper without
        // becoming a general control-flow interpreter.
        private static IEnumerable<StatementSyntax> Flatten(IReadOnlyList<StatementSyntax> stmts)
        {
            if (stmts == null) yield break;
            foreach (StatementSyntax s in stmts)
            {
                yield return s;
                var ifs = s as IfStatement;
                if (ifs == null) continue;
                if (ifs.Body != null)
                    foreach (StatementSyntax inner in ifs.Body.Statements) yield return inner;
                foreach (IfStatement elif in ifs.Elifs)
                    if (elif.Body != null)
                        foreach (StatementSyntax inner in elif.Body.Statements) yield return inner;
                if (ifs.Else != null)
                    foreach (StatementSyntax inner in ifs.Else.Statements) yield return inner;
            }
        }

        private static void WalkStatements(IEnumerable<StatementSyntax> stmts, GuiForm form,
            Dictionary<string, GuiControl> byVar, Dictionary<string, string> classToModule)
        {
            foreach (StatementSyntax s in stmts)
            {
                var asn = s as AssignmentStatement;
                if (asn != null && asn.Targets.Count == 1)
                {
                    TryTrackConstruction(asn, form, byVar, classToModule);
                    continue;
                }

                CallExpression call = AsCall(s);
                if (call != null) TryApplyCallStatement(call, form, byVar);
            }
        }

        private static void TryTrackConstruction(AssignmentStatement asn, GuiForm form,
            Dictionary<string, GuiControl> byVar, Dictionary<string, string> classToModule)
        {
            string varKey, friendlyName;
            if (!TryVarKey(asn.Targets[0], out varKey, out friendlyName)) return;
            if (byVar.ContainsKey(varKey)) return; // keep the first construction if re-assigned

            var call = asn.Value as CallExpression;
            if (call == null) return;

            GuiControl control = null;
            string ns, ctor;
            if (SplitCallee(call.Callee, out ns, out ctor))
            {
                K kind;
                if (CtorToKind.TryGetValue(ns + "." + ctor, out kind))
                {
                    int defaultW, defaultH;
                    DefaultSizeFor(kind, out defaultW, out defaultH);
                    control = new GuiControl { Id = "ctl" + (form.Controls.Count + 1), Kind = kind, Name = friendlyName, Width = defaultW, Height = defaultH };
                    foreach (CallArgument arg in call.Arguments)
                    {
                        if (arg.Kind != CallArgument.ArgKind.Keyword || string.IsNullOrEmpty(arg.Name)) continue;
                        string value = LiteralString(arg.Value);
                        if (value == null) continue;
                        string wfKey;
                        string key = TkToWinForms.TryGetValue(arg.Name, out wfKey) ? wfKey : arg.Name;
                        control.Props[key] = value;
                    }
                }
            }
            else
            {
                var nameCallee = call.Callee as NameExpression;
                string module;
                if (nameCallee != null && classToModule.TryGetValue(nameCallee.Name, out module))
                {
                    control = new GuiControl { Id = "ctl" + (form.Controls.Count + 1), Kind = K.CustomComponent, Name = friendlyName, Width = 160, Height = 120 };
                    control.Props["ClassName"] = nameCallee.Name;
                    control.Props["Module"] = module;
                }
            }

            if (control == null) return;
            form.Controls.Add(control);
            byVar[varKey] = control;
        }

        private static void TryApplyCallStatement(CallExpression call, GuiForm form, Dictionary<string, GuiControl> byVar)
        {
            var outer = call.Callee as AttributeExpression;
            if (outer == null) return;
            string method = outer.Name;

            if ((method == "title" || method == "geometry") && IsFormHeaderReceiver(outer.Target))
            {
                ApplyFormHeaderCall(call, method, form);
                return;
            }

            GuiControl receiver = ResolveVar(outer.Target, byVar);
            if (receiver == null) return;

            if (method == "place") { ApplyPlace(call, receiver); return; }
            if (method == "pack") { ApplyPack(call, receiver, form); return; }
            if (method == "add" && receiver.Kind == K.TabControl && call.Arguments.Count >= 1)
            {
                GuiControl child = ResolveVar(call.Arguments[0].Value, byVar);
                if (child == null) return;
                child.ParentId = receiver.Id;
                foreach (CallArgument arg in call.Arguments)
                {
                    if (arg.Kind != CallArgument.ArgKind.Keyword || arg.Name != "text") continue;
                    string title = LiteralString(arg.Value);
                    if (title != null) child.Props["TabTitle"] = title;
                }
            }
        }

        // True for `self` directly (a Tk/Toplevel subclass IS the window) or `self.master` (a Frame
        // subclass whose master is the real window).
        private static bool IsFormHeaderReceiver(ExpressionSyntax target)
        {
            var direct = target as NameExpression;
            if (direct != null && direct.Name == "self") return true;
            string masterName;
            return IsSelfAttribute(target, out masterName) && masterName == "master";
        }

        private static void ApplyFormHeaderCall(CallExpression call, string method, GuiForm form)
        {
            if (method == "title")
            {
                string title = LiteralString(FirstPositional(call));
                if (title != null) form.Title = title;
                return;
            }
            string geo = LiteralString(FirstPositional(call));
            int xIdx = geo != null ? geo.IndexOf('x') : -1;
            if (xIdx <= 0) return;
            int w, h;
            if (int.TryParse(geo.Substring(0, xIdx), out w) && int.TryParse(geo.Substring(xIdx + 1), out h))
            { form.Width = w; form.Height = h; }
        }

        private static void ApplyPlace(CallExpression call, GuiControl control)
        {
            foreach (CallArgument arg in call.Arguments)
            {
                if (arg.Kind != CallArgument.ArgKind.Keyword) continue;
                int? v = LiteralInt(arg.Value);
                if (v == null) continue;
                if (arg.Name == "x") control.X = v.Value;
                else if (arg.Name == "y") control.Y = v.Value;
                else if (arg.Name == "width") control.Width = v.Value;
                else if (arg.Name == "height") control.Height = v.Value;
            }
        }

        private static void ApplyPack(CallExpression call, GuiControl control, GuiForm form)
        {
            control.Props["Layout"] = "Pack";
            string fill = "";
            foreach (CallArgument arg in call.Arguments)
            {
                if (arg.Kind != CallArgument.ArgKind.Keyword) continue;
                if (arg.Name == "side")
                {
                    string s = LiteralString(arg.Value);
                    if (s != null) control.Props["PackSide"] = s;
                }
                else if (arg.Name == "fill")
                {
                    string s = LiteralString(arg.Value);
                    if (s != null) { control.Props["PackFill"] = s; fill = s; }
                }
                else if (arg.Name == "expand")
                {
                    string s = BooleanOrLiteralText(arg.Value);
                    if (s != null) control.Props["PackExpand"] = s;
                }
            }

            // A pack(fill=...)-managed widget is sized by its container at runtime, not given an
            // explicit size the way place() requires — so there's no real width/height to recover
            // from the source. Without this, it keeps whatever small per-kind default it was
            // constructed with (e.g. 120x28 for a Notebook), which renders as a tiny box in a mostly
            // empty form instead of something that actually looks like the real layout. Approximate
            // it as filling most of the form instead.
            if (fill == "both" || fill == "x" || fill == "y")
            {
                const int margin = 8;
                control.Width = Math.Max(control.Width, form.Width - margin * 2);
                control.Height = Math.Max(control.Height, form.Height - 40 - margin);
            }
        }

        private static void WalkEvents(IReadOnlyList<StatementSyntax> stmts, Dictionary<string, GuiControl> byVar)
        {
            foreach (StatementSyntax s in stmts)
            {
                CallExpression call = AsCall(s);
                if (call == null) continue;
                var outer = call.Callee as AttributeExpression;
                if (outer == null) continue;
                GuiControl control = ResolveVar(outer.Target, byVar);
                if (control == null) continue;

                if (outer.Name == "config")
                {
                    foreach (CallArgument arg in call.Arguments)
                    {
                        if (arg.Kind != CallArgument.ArgKind.Keyword || arg.Name != "command") continue;
                        string handler = SelfMethodName(arg.Value);
                        if (handler != null) control.Events.Add(new GuiEvent { EventName = "command", HandlerName = handler });
                    }
                }
                else if (outer.Name == "bind" && call.Arguments.Count >= 2)
                {
                    string eventName = LiteralString(call.Arguments[0].Value);
                    string handler = SelfMethodName(call.Arguments[1].Value);
                    if (eventName != null && handler != null)
                        control.Events.Add(new GuiEvent { EventName = eventName, HandlerName = handler });
                }
            }
        }

        // Per-kind fallback size for a control whose real dimensions can't be recovered from the
        // source (e.g. no place()/pack(fill=...) call was found for it) — containers get a size that
        // actually reads as a container on canvas instead of the generic small-control default.
        private static void DefaultSizeFor(K kind, out int width, out int height)
        {
            switch (kind)
            {
                case K.Panel:
                case K.GroupBox:
                case K.TabControl:
                case K.SplitContainer:
                    width = 200; height = 140;
                    return;
                default:
                    width = 120; height = 28;
                    return;
            }
        }

        // ---- small AST helpers ----

        private static CallExpression AsCall(SyntaxNode node)
        {
            var exprStmt = node as ExpressionStatement;
            if (exprStmt != null) return exprStmt.Expression as CallExpression;
            return node as CallExpression;
        }

        private static ExpressionSyntax FirstPositional(CallExpression call)
        {
            foreach (CallArgument a in call.Arguments)
                if (a.Kind == CallArgument.ArgKind.Positional) return a.Value;
            return null;
        }

        // A bare local name (`notebook`) or `self.X` both count as a trackable variable; `key` is the
        // canonical lookup key (`"notebook"` or `"self.X"`), `friendlyName` is what the resulting
        // GuiControl.Name should be (always the bare identifier — re-emission always uses `self.`).
        private static bool TryVarKey(ExpressionSyntax target, out string key, out string friendlyName)
        {
            key = null; friendlyName = null;
            var name = target as NameExpression;
            if (name != null) { key = name.Name; friendlyName = name.Name; return true; }
            string selfName;
            if (IsSelfAttribute(target, out selfName)) { key = "self." + selfName; friendlyName = selfName; return true; }
            return false;
        }

        private static GuiControl ResolveVar(ExpressionSyntax expr, Dictionary<string, GuiControl> byVar)
        {
            string key, friendly;
            if (!TryVarKey(expr, out key, out friendly)) return null;
            GuiControl c;
            return byVar.TryGetValue(key, out c) ? c : null;
        }

        // `self.X` → name = "X".
        private static bool IsSelfAttribute(ExpressionSyntax expr, out string name)
        {
            name = null;
            var ae = expr as AttributeExpression;
            if (ae == null) return false;
            var target = ae.Target as NameExpression;
            if (target == null || target.Name != "self") return false;
            name = ae.Name;
            return true;
        }

        // `tk.Button` / `ttk.Combobox` → ns="tk", ctor="Button". False for a bare-name callee
        // (custom-class construction, handled separately).
        private static bool SplitCallee(ExpressionSyntax callee, out string ns, out string ctor)
        {
            ns = null; ctor = null;
            var ae = callee as AttributeExpression;
            if (ae == null) return false;
            var nsExpr = ae.Target as NameExpression;
            if (nsExpr == null) return false;
            ns = nsExpr.Name;
            ctor = ae.Name;
            return true;
        }

        // `self.HANDLER` (a bound-method reference, no call) → "HANDLER".
        private static string SelfMethodName(ExpressionSyntax expr)
        {
            string name;
            return IsSelfAttribute(expr, out name) ? name : null;
        }

        private static string LiteralString(ExpressionSyntax expr)
        {
            var lit = expr as LiteralExpression;
            if (lit == null || lit.Token == null) return null;
            string s = lit.Token.Value as string;
            return s ?? lit.Token.Text;
        }

        // Covers True/False/None (commonly a bare NameExpression in this parser) as well as an actual
        // string/number literal, for kwargs like `expand=True`.
        private static string BooleanOrLiteralText(ExpressionSyntax expr)
        {
            var ne = expr as NameExpression;
            if (ne != null) return ne.Name;
            return LiteralString(expr);
        }

        private static int? LiteralInt(ExpressionSyntax expr)
        {
            var lit = expr as LiteralExpression;
            if (lit == null || lit.Token == null) return null;
            try { return Convert.ToInt32(lit.Token.Value); }
            catch { /* fall through to text parse */ }
            int parsed;
            return int.TryParse(lit.Token.Text, out parsed) ? (int?)parsed : null;
        }
    }
}
