namespace HelixPythonNamespace.Refactoring.Structural
{
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.Refactoring;

    /// <summary>
    /// Pure-string templates composing a <see cref="StructuralCandidate"/> from GoF-pattern
    /// MatchData. Port of the C++ <c>structural::PatternEmitter</c>. Emits '\n' line endings;
    /// the facade re-applies the host's line ending and common indent.
    /// </summary>
    public static class GofPatternEmitter
    {
        // Forward: plain if/elif create-function -> registry factory class.
        public static StructuralCandidate EmitFactoryIntroduce(FactoryData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.P1_FactoryIntroduce;
            c.Label = "Introduce Factory: " + d.FuncName + "() -> class " + d.ClassName;

            StringBuilder outSb = new StringBuilder();
            outSb.Append("class ").Append(d.ClassName).Append(":\n");
            outSb.Append("    _registry = {\n");
            foreach (CaseEntry kv in d.Cases)
            {
                outSb.Append("        \"").Append(kv.Key).Append("\": ").Append(kv.Value).Append(",\n");
            }
            outSb.Append("    }\n");
            outSb.Append("\n");
            outSb.Append("    @classmethod\n");
            outSb.Append("    def create(cls, ").Append(d.ParamName).Append("):\n");
            outSb.Append("        if ").Append(d.ParamName).Append(" not in cls._registry:\n");
            outSb.Append("            raise ").Append(d.RaiseText).Append("\n");
            outSb.Append("        return cls._registry[").Append(d.ParamName).Append("]()");
            c.ReplacementText = outSb.ToString();

            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // Reverse: registry factory class -> plain if/elif create-function.
        public static StructuralCandidate EmitFactoryRemove(FactoryData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.P1_FactoryRemove;
            c.Label = "Remove Factory: class " + d.ClassName + " -> " + d.FuncName + "()";

            StringBuilder outSb = new StringBuilder();
            outSb.Append("def ").Append(d.FuncName).Append("(").Append(d.ParamName).Append("):\n");
            bool first = true;
            foreach (CaseEntry kv in d.Cases)
            {
                string kw = first ? "if" : "elif";
                first = false;
                outSb.Append("    ").Append(kw).Append(" ").Append(d.ParamName)
                     .Append(" == \"").Append(kv.Key).Append("\":\n");
                outSb.Append("        return ").Append(kv.Value).Append("()\n");
            }
            outSb.Append("    raise ").Append(d.RaiseText);
            c.ReplacementText = outSb.ToString();

            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // P6 Facade forward: free orchestration function -> facade class method.
        public static StructuralCandidate EmitFacadeIntroduce(FacadeData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.P6_FacadeIntroduce;
            c.Label = "Introduce Facade: " + d.FuncName + "() -> class " + d.ClassName;

            string methodParams = (d.ParamsText.Length == 0) ? "self" : "self, " + d.ParamsText;
            List<string> lines = new List<string>();
            lines.Add("class " + d.ClassName + ":");
            lines.Add("    def " + d.FuncName + "(" + methodParams + ")" + d.ReturnText + ":");
            foreach (string bl in BodySlicer.Indent(d.BodyLines, 8)) lines.Add(bl);
            c.ReplacementText = BodySlicer.Join(lines);

            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // P6 Facade reverse: facade class method -> free function.
        public static StructuralCandidate EmitFacadeRemove(FacadeData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.P6_FacadeRemove;
            c.Label = "Remove Facade: class " + d.ClassName + " -> " + d.FuncName + "()";

            List<string> lines = new List<string>();
            lines.Add("def " + d.FuncName + "(" + d.ParamsText + ")" + d.ReturnText + ":");
            foreach (string bl in BodySlicer.Indent(d.BodyLines, 4)) lines.Add(bl);
            c.ReplacementText = BodySlicer.Join(lines);

            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // P2 Strategy forward: if/elif dispatch -> strategy table + dispatch fn.
        public static StructuralCandidate EmitStrategyIntroduce(StrategyData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.P2_StrategyIntroduce;
            c.Label = "Introduce Strategy: " + d.FuncName + "() -> " + d.DictName + " table";

            StringBuilder outSb = new StringBuilder();
            outSb.Append(d.DictName).Append(" = {\n");
            foreach (CaseEntry kv in d.Cases)
            {
                outSb.Append("    \"").Append(kv.Key).Append("\": ").Append(kv.Value).Append(",\n");
            }
            outSb.Append("}\n\n\n");
            outSb.Append("def ").Append(d.FuncName).Append("(").Append(d.FullParamsText).Append("):\n");
            outSb.Append("    if ").Append(d.KeyParam).Append(" not in ").Append(d.DictName).Append(":\n");
            outSb.Append("        raise ").Append(d.RaiseText).Append("\n");
            outSb.Append("    return ").Append(d.DictName).Append("[").Append(d.KeyParam)
                 .Append("](").Append(d.CallArgsText).Append(")");
            c.ReplacementText = outSb.ToString();
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // P2 Strategy reverse: strategy table + dispatch fn -> if/elif dispatch.
        public static StructuralCandidate EmitStrategyRemove(StrategyData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.P2_StrategyRemove;
            c.Label = "Remove Strategy: " + d.DictName + " table -> " + d.FuncName + "()";

            StringBuilder outSb = new StringBuilder();
            outSb.Append("def ").Append(d.FuncName).Append("(").Append(d.FullParamsText).Append("):\n");
            bool first = true;
            foreach (CaseEntry kv in d.Cases)
            {
                outSb.Append("    ").Append(first ? "if" : "elif").Append(" ").Append(d.KeyParam)
                     .Append(" == \"").Append(kv.Key).Append("\":\n");
                outSb.Append("        return ").Append(kv.Value).Append("(").Append(d.CallArgsText).Append(")\n");
                first = false;
            }
            outSb.Append("    raise ").Append(d.RaiseText);
            c.ReplacementText = outSb.ToString();
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // P5 Template Method forward: skeleton class -> ABC + abstract step stubs.
        public static StructuralCandidate EmitTemplateIntroduce(TemplateMethodData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.P5_TemplateIntroduce;
            c.Label = "Introduce Template Method: class " + d.ClassName + " : ABC";

            List<string> lines = new List<string>();
            lines.Add("class " + d.ClassName + "(ABC):");
            foreach (string l in d.TemplateLines) lines.Add(l);
            foreach (string step in d.Steps)
            {
                lines.Add("");
                lines.Add("    @abstractmethod");
                lines.Add("    def " + step + "(self): ...");
            }
            c.ReplacementText = BodySlicer.Join(lines);

            c.RequiredImports.Add("from abc import ABC, abstractmethod");
            c.AuxEdits.Add("from abc import ABC, abstractmethod");
            c.Caveats.Add(new CandidateIssue(
                "STR-TEMPLATE-IMPORT",
                "Requires `from abc import ABC, abstractmethod` at module top; primitive " +
                "operations become abstract and must be overridden by subclasses."));
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // P5 Template Method reverse: ABC + abstract step stubs -> skeleton class.
        public static StructuralCandidate EmitTemplateRemove(TemplateMethodData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.P5_TemplateRemove;
            c.Label = "Remove Template Method: class " + d.ClassName + " (drop ABC + stubs)";

            List<string> lines = new List<string>();
            lines.Add("class " + d.ClassName + ":");
            foreach (string l in d.TemplateLines) lines.Add(l);
            c.ReplacementText = BodySlicer.Join(lines);

            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // The canonical observer infrastructure block (4 methods, indent 4), shared by
        // forward emission and asserted (by name) on reverse.
        private static List<string> ObserverInfraLines()
        {
            return new List<string>
            {
                "    def __init__(self):",
                "        self._observers = []",
                "",
                "    def subscribe(self, observer):",
                "        self._observers.append(observer)",
                "",
                "    def unsubscribe(self, observer):",
                "        self._observers.remove(observer)",
                "",
                "    def notify(self):",
                "        for observer in self._observers:",
                "            observer(self)",
            };
        }

        // P3 Observer forward: subject class -> subject + observer infrastructure.
        public static StructuralCandidate EmitObserverIntroduce(ObserverData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.P3_ObserverIntroduce;
            c.Label = "Introduce Observer: class " + d.ClassName + " (+ subscribe/notify)";

            List<string> lines = new List<string>();
            lines.Add("class " + d.ClassName + ":");
            foreach (string l in ObserverInfraLines()) lines.Add(l);
            lines.Add("");
            foreach (string l in d.BodyLines) lines.Add(l);
            c.ReplacementText = BodySlicer.Join(lines);

            c.Lossy = true;
            c.Caveats.Add(new CandidateIssue(
                "STR-OBSERVER-LOSSY",
                "Injects observer infrastructure (subscribe/unsubscribe/notify); the " +
                "reverse removes it, discarding any registered observers. Behaviorally " +
                "equivalent for the subject's own methods."));
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // P3 Observer reverse: subject + observer infrastructure -> subject class.
        public static StructuralCandidate EmitObserverRemove(ObserverData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.P3_ObserverRemove;
            c.Label = "Remove Observer: class " + d.ClassName + " (drop subscribe/notify)";

            List<string> lines = new List<string>();
            lines.Add("class " + d.ClassName + ":");
            foreach (string l in d.BodyLines) lines.Add(l);
            c.ReplacementText = BodySlicer.Join(lines);

            c.Lossy = true;
            c.Caveats.Add(new CandidateIssue(
                "STR-OBSERVER-LOSSY",
                "Removes observer infrastructure (subscribe/unsubscribe/notify)."));
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // P4 Decorator forward: component class -> component + delegating decorator.
        public static StructuralCandidate EmitDecoratorIntroduce(DecoratorData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.P4_DecoratorIntroduce;
            c.Label = "Introduce Decorator: class " + d.ClassName + "Decorator wraps " + d.ClassName;

            List<string> lines = new List<string>(d.ClassLines);  // component verbatim
            lines.Add("");
            lines.Add("");
            lines.Add("class " + d.ClassName + "Decorator:");
            lines.Add("    def __init__(self, wrapped):");
            lines.Add("        self._wrapped = wrapped");
            foreach (DecoMethod m in d.Methods)
            {
                lines.Add("");
                string paramsText = (m.DefParams.Length == 0) ? "self" : "self, " + m.DefParams;
                lines.Add("    def " + m.Name + "(" + paramsText + "):");
                lines.Add("        return self._wrapped." + m.Name + "(" + m.CallArgs + ")");
            }
            c.ReplacementText = BodySlicer.Join(lines);

            c.Lossy = true;
            c.Caveats.Add(new CandidateIssue(
                "STR-DECORATOR-LOSSY",
                "Adds a delegating decorator class; the reverse removes it. The wrapper " +
                "forwards every public method to the wrapped instance unchanged."));
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }

        // P4 Decorator reverse: component + delegating decorator -> component class.
        public static StructuralCandidate EmitDecoratorRemove(DecoratorData d)
        {
            StructuralCandidate c = new StructuralCandidate();
            c.PatternKind = StructuralCandidate.Kind.P4_DecoratorRemove;
            c.Label = "Remove Decorator: drop class " + d.ClassName + "Decorator";

            c.ReplacementText = BodySlicer.Join(d.ClassLines);  // component verbatim
            c.Lossy = true;
            c.Caveats.Add(new CandidateIssue(
                "STR-DECORATOR-LOSSY", "Removes the delegating decorator class."));
            c.StartLine = d.StartLine;
            c.EndLine = d.EndLine;
            return c;
        }
    }
}
