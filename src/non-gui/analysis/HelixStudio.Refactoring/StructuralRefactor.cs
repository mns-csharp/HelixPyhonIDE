namespace HelixPythonNamespace.Refactoring
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Refactoring.Structural;
    using HelixPythonNamespace.Refactoring.Vectorization;
    using HelixPythonNamespace.VirtualCompiler;
    using HelixPythonNamespace.VirtualCompiler.Ast;
    using K = HelixPythonNamespace.Refactoring.StructuralCandidate.Kind;

    /// <summary>
    /// Public facade over the structural transform engine. Port of the C++
    /// <c>StructuralRefactor</c>. Pass the exact selected source text (preserving relative
    /// indentation). An empty result means no recognized structural shape.
    /// </summary>
    public static class StructuralRefactor
    {
        public static List<StructuralCandidate> Match(string selectedSource)
        {
            return Match(selectedSource, "");
        }

        public static List<StructuralCandidate> Match(string selectedSource, string fullFileSource)
        {
            List<StructuralCandidate> outList = new List<StructuralCandidate>();

            ParsedSelection parsed = SelectionParser.Parse(selectedSource);
            if (parsed.Module == null) return outList;

            // The verbatim body-slicing transforms (O1 and later families) index into
            // exactly the string the parser saw, so reproduce it here.
            string normalized = BodySlicer.NormalizedSource(selectedSource);

            IReadOnlyList<Stmt> body = parsed.Module.Body;
            foreach (Stmt stmt in body)
            {
                FactoryData factoryFwd = GofPatternMatcher.TryMatchFactoryForward(stmt);
                if (factoryFwd != null) outList.Add(GofPatternEmitter.EmitFactoryIntroduce(factoryFwd));

                FactoryData factoryRev = GofPatternMatcher.TryMatchFactoryReverse(stmt);
                if (factoryRev != null) outList.Add(GofPatternEmitter.EmitFactoryRemove(factoryRev));

                FacadeData facadeIn = GofPatternMatcher.TryMatchFacadeIntroduce(stmt, normalized);
                if (facadeIn != null) outList.Add(GofPatternEmitter.EmitFacadeIntroduce(facadeIn));

                FacadeData facadeRm = GofPatternMatcher.TryMatchFacadeRemove(stmt, normalized);
                if (facadeRm != null) outList.Add(GofPatternEmitter.EmitFacadeRemove(facadeRm));

                StrategyData strategyIn = GofPatternMatcher.TryMatchStrategyIntroduce(stmt);
                if (strategyIn != null) outList.Add(GofPatternEmitter.EmitStrategyIntroduce(strategyIn));

                TemplateMethodData templateIn = GofPatternMatcher.TryMatchTemplateIntroduce(stmt, normalized);
                if (templateIn != null) outList.Add(GofPatternEmitter.EmitTemplateIntroduce(templateIn));

                TemplateMethodData templateRm = GofPatternMatcher.TryMatchTemplateRemove(stmt, normalized);
                if (templateRm != null) outList.Add(GofPatternEmitter.EmitTemplateRemove(templateRm));

                ObserverData observerIn = GofPatternMatcher.TryMatchObserverIntroduce(stmt, normalized);
                if (observerIn != null) outList.Add(GofPatternEmitter.EmitObserverIntroduce(observerIn));

                ObserverData observerRm = GofPatternMatcher.TryMatchObserverRemove(stmt, normalized);
                if (observerRm != null) outList.Add(GofPatternEmitter.EmitObserverRemove(observerRm));

                DecoratorData decoratorIn = GofPatternMatcher.TryMatchDecoratorIntroduce(stmt, normalized);
                if (decoratorIn != null) outList.Add(GofPatternEmitter.EmitDecoratorIntroduce(decoratorIn));

                DictClassData dictToClass = ParadigmMatcher.TryMatchDictToClass(stmt);
                if (dictToClass != null) outList.Add(ParadigmEmitter.EmitDictToClass(dictToClass));

                DictClassData classToDict = ParadigmMatcher.TryMatchClassToDict(stmt);
                if (classToDict != null) outList.Add(ParadigmEmitter.EmitClassToDict(classToDict));

                FunctionsClassData classToFuncs = ParadigmMatcher.TryMatchClassToFunctions(stmt, normalized);
                if (classToFuncs != null) outList.Add(ParadigmEmitter.EmitClassToFunctions(classToFuncs));

                DipData dipIn = SolidMatcher.TryMatchDipIntroduce(stmt, normalized);
                if (dipIn != null) outList.Add(SolidEmitter.EmitDipIntroduce(dipIn));

                SrpSplitData srpSplit = SolidMatcher.TryMatchSrpSplit(stmt, normalized);
                if (srpSplit != null) outList.Add(SolidEmitter.EmitSrpSplit(srpSplit));

                IspSegregateData ispSeg = SolidMatcher.TryMatchIspSegregate(stmt, normalized);
                if (ispSeg != null) outList.Add(SolidEmitter.EmitIspSegregate(ispSeg));
            }

            // Group matchers that consume a *run* of consecutive statements scan the
            // module body separately, advancing past each matched run.
            for (int i = 0; i < body.Count;)
            {
                FunctionsClassData d = ParadigmMatcher.TryMatchFunctionsToClass(body, i, normalized);
                if (d != null)
                {
                    outList.Add(ParadigmEmitter.EmitFunctionsToClass(d));
                    i += d.Methods.Count;
                }
                else i++;
            }
            for (int i = 0; i < body.Count;)
            {
                DipData d = SolidMatcher.TryMatchDipInline(body, i, normalized);
                if (d != null)
                {
                    outList.Add(SolidEmitter.EmitDipInline(d));
                    i += 2;  // Protocol + impl
                }
                else i++;
            }
            for (int i = 0; i < body.Count;)
            {
                StrategyData d = GofPatternMatcher.TryMatchStrategyRemove(body, i);
                if (d != null)
                {
                    outList.Add(GofPatternEmitter.EmitStrategyRemove(d));
                    i += 2;  // dict + dispatch fn
                }
                else i++;
            }
            for (int i = 0; i < body.Count;)
            {
                DecoratorData d = GofPatternMatcher.TryMatchDecoratorRemove(body, i, normalized);
                if (d != null)
                {
                    outList.Add(GofPatternEmitter.EmitDecoratorRemove(d));
                    i += 2;  // component + decorator
                }
                else i++;
            }
            for (int i = 0; i < body.Count;)
            {
                int consumed;
                SrpMergeData d = SolidMatcher.TryMatchSrpMerge(body, i, out consumed, normalized);
                if (d != null)
                {
                    outList.Add(SolidEmitter.EmitSrpMerge(d));
                    i += consumed;
                }
                else i++;
            }
            for (int i = 0; i < body.Count;)
            {
                int consumed;
                IspCollapseData d = SolidMatcher.TryMatchIspCollapse(body, i, out consumed, normalized);
                if (d != null)
                {
                    outList.Add(SolidEmitter.EmitIspCollapse(d));
                    i += consumed;
                }
                else i++;
            }

            // Re-indent each replacement to the selection's common indent / line ending
            // (mirrors the vectorization facade). Line ranges are already in selection
            // coordinates (stripping indent preserves line numbers).
            foreach (StructuralCandidate c in outList)
            {
                c.ReplacementText = SelectionParser.ReapplyIndent(
                    c.ReplacementText, parsed.CommonIndent, parsed.LineEnding);
                ValidateEmitted(c);
            }
            return outList;
        }

        /// <summary>
        /// Apply the first candidate of <paramref name="kind"/> to <paramref name="selectedSource"/>,
        /// splicing its replacement into the matched line range. Returns null if no candidate
        /// of that kind matched.
        /// </summary>
        public static string Apply(string selectedSource, K kind)
        {
            return Apply(selectedSource, kind, "");
        }

        public static string Apply(string selectedSource, K kind, string fullFileSource)
        {
            List<StructuralCandidate> candidates = Match(selectedSource, fullFileSource);
            foreach (StructuralCandidate c in candidates)
            {
                if (c.PatternKind == kind) return ApplyStructuralCandidate(selectedSource, c);
            }
            return null;
        }

        /// <summary>
        /// Splice <c>candidate.ReplacementText</c> into <paramref name="selectedSource"/>,
        /// replacing the 1-based inclusive line range [StartLine, EndLine]. Exposed for
        /// tests and the CLI.
        /// </summary>
        public static string ApplyStructuralCandidate(string selectedSource, StructuralCandidate candidate)
        {
            if (candidate.StartLine < 1 || candidate.EndLine < candidate.StartLine)
            {
                return selectedSource;
            }
            string lineEnding = SelectionParser.DetectLineEnding(selectedSource);

            // Strip trailing CR per line so we rejoin cleanly with `lineEnding`.
            List<string> srcLines = SplitLinesStripCr(selectedSource);
            List<string> replLines = SplitLinesStripCr(candidate.ReplacementText);

            int start = candidate.StartLine;  // 1-based inclusive
            int end = candidate.EndLine;      // 1-based inclusive

            List<string> merged = new List<string>();
            for (int i = 0; i < srcLines.Count; i++)
            {
                int lineNo = i + 1;
                if (lineNo == start) merged.AddRange(replLines);
                if (lineNo >= start && lineNo <= end) continue;  // drop replaced lines
                merged.Add(srcLines[i]);
            }
            // If start was beyond the source (shouldn't happen), append the replacement so
            // nothing is silently lost.
            if (start > srcLines.Count) merged.AddRange(replLines);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < merged.Count; i++)
            {
                sb.Append(merged[i]);
                if (i + 1 < merged.Count) sb.Append(lineEnding);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The reverse kind paired with a forward kind (and vice versa). Used by round-trip
        /// tooling. Returns the same kind if it has no pair.
        /// </summary>
        public static K PairedKind(K k)
        {
            switch (k)
            {
                case K.P1_FactoryIntroduce: return K.P1_FactoryRemove;
                case K.P1_FactoryRemove: return K.P1_FactoryIntroduce;
                case K.P6_FacadeIntroduce: return K.P6_FacadeRemove;
                case K.P6_FacadeRemove: return K.P6_FacadeIntroduce;
                case K.P2_StrategyIntroduce: return K.P2_StrategyRemove;
                case K.P2_StrategyRemove: return K.P2_StrategyIntroduce;
                case K.P5_TemplateIntroduce: return K.P5_TemplateRemove;
                case K.P5_TemplateRemove: return K.P5_TemplateIntroduce;
                case K.P3_ObserverIntroduce: return K.P3_ObserverRemove;
                case K.P3_ObserverRemove: return K.P3_ObserverIntroduce;
                case K.P4_DecoratorIntroduce: return K.P4_DecoratorRemove;
                case K.P4_DecoratorRemove: return K.P4_DecoratorIntroduce;
                case K.O1_FunctionsToClass: return K.O1_ClassToFunctions;
                case K.O1_ClassToFunctions: return K.O1_FunctionsToClass;
                case K.O2_DictToClass: return K.O2_ClassToDict;
                case K.O2_ClassToDict: return K.O2_DictToClass;
                case K.S2_DipIntroduce: return K.S2_DipInline;
                case K.S2_DipInline: return K.S2_DipIntroduce;
                case K.S1_SrpSplit: return K.S1_SrpMerge;
                case K.S1_SrpMerge: return K.S1_SrpSplit;
                case K.S3_IspSegregate: return K.S3_IspCollapse;
                case K.S3_IspCollapse: return K.S3_IspSegregate;
            }
            return k;
        }

        /// <summary>
        /// Map a stable string token to the Kind enum (for the CLI). Returns false when the
        /// token is unknown. C++ returns <c>std::optional</c>.
        /// </summary>
        public static bool KindFromToken(string token, out K kind)
        {
            kind = K.P1_FactoryIntroduce;
            if (token == null) return false;

            if (token == "P1_FactoryIntroduce" || token == "factory") { kind = K.P1_FactoryIntroduce; return true; }
            if (token == "P1_FactoryRemove" || token == "unfactory") { kind = K.P1_FactoryRemove; return true; }
            if (token == "P6_FacadeIntroduce" || token == "facade") { kind = K.P6_FacadeIntroduce; return true; }
            if (token == "P6_FacadeRemove" || token == "unfacade") { kind = K.P6_FacadeRemove; return true; }
            if (token == "P2_StrategyIntroduce" || token == "strategy") { kind = K.P2_StrategyIntroduce; return true; }
            if (token == "P2_StrategyRemove" || token == "unstrategy") { kind = K.P2_StrategyRemove; return true; }
            if (token == "P5_TemplateIntroduce" || token == "template") { kind = K.P5_TemplateIntroduce; return true; }
            if (token == "P5_TemplateRemove" || token == "untemplate") { kind = K.P5_TemplateRemove; return true; }
            if (token == "P3_ObserverIntroduce" || token == "observer") { kind = K.P3_ObserverIntroduce; return true; }
            if (token == "P3_ObserverRemove" || token == "unobserver") { kind = K.P3_ObserverRemove; return true; }
            if (token == "P4_DecoratorIntroduce" || token == "decorator") { kind = K.P4_DecoratorIntroduce; return true; }
            if (token == "P4_DecoratorRemove" || token == "undecorator") { kind = K.P4_DecoratorRemove; return true; }
            if (token == "O1_FunctionsToClass" || token == "funcs2class") { kind = K.O1_FunctionsToClass; return true; }
            if (token == "O1_ClassToFunctions" || token == "class2funcs") { kind = K.O1_ClassToFunctions; return true; }
            if (token == "O2_DictToClass" || token == "dict2class") { kind = K.O2_DictToClass; return true; }
            if (token == "O2_ClassToDict" || token == "class2dict") { kind = K.O2_ClassToDict; return true; }
            if (token == "S2_DipIntroduce" || token == "dip") { kind = K.S2_DipIntroduce; return true; }
            if (token == "S2_DipInline" || token == "undip") { kind = K.S2_DipInline; return true; }
            if (token == "S1_SrpSplit" || token == "srp") { kind = K.S1_SrpSplit; return true; }
            if (token == "S1_SrpMerge" || token == "unsrp") { kind = K.S1_SrpMerge; return true; }
            if (token == "S3_IspSegregate" || token == "isp") { kind = K.S3_IspSegregate; return true; }
            if (token == "S3_IspCollapse" || token == "unisp") { kind = K.S3_IspCollapse; return true; }
            return false;
        }

        public static string TokenForKind(K k)
        {
            switch (k)
            {
                case K.P1_FactoryIntroduce: return "P1_FactoryIntroduce";
                case K.P1_FactoryRemove: return "P1_FactoryRemove";
                case K.P6_FacadeIntroduce: return "P6_FacadeIntroduce";
                case K.P6_FacadeRemove: return "P6_FacadeRemove";
                case K.P2_StrategyIntroduce: return "P2_StrategyIntroduce";
                case K.P2_StrategyRemove: return "P2_StrategyRemove";
                case K.P5_TemplateIntroduce: return "P5_TemplateIntroduce";
                case K.P5_TemplateRemove: return "P5_TemplateRemove";
                case K.P3_ObserverIntroduce: return "P3_ObserverIntroduce";
                case K.P3_ObserverRemove: return "P3_ObserverRemove";
                case K.P4_DecoratorIntroduce: return "P4_DecoratorIntroduce";
                case K.P4_DecoratorRemove: return "P4_DecoratorRemove";
                case K.O1_FunctionsToClass: return "O1_FunctionsToClass";
                case K.O1_ClassToFunctions: return "O1_ClassToFunctions";
                case K.O2_DictToClass: return "O2_DictToClass";
                case K.O2_ClassToDict: return "O2_ClassToDict";
                case K.S2_DipIntroduce: return "S2_DipIntroduce";
                case K.S2_DipInline: return "S2_DipInline";
                case K.S1_SrpSplit: return "S1_SrpSplit";
                case K.S1_SrpMerge: return "S1_SrpMerge";
                case K.S3_IspSegregate: return "S3_IspSegregate";
                case K.S3_IspCollapse: return "S3_IspCollapse";
            }
            return "?";
        }

        public static List<SupportedStructuralPattern> SupportedPatterns()
        {
            List<SupportedStructuralPattern> p = new List<SupportedStructuralPattern>();
            p.Add(new SupportedStructuralPattern(K.P1_FactoryIntroduce,
                "P1 — Factory (introduce)",
                "def create_animal(kind):\n"
                + "    if kind == \"dog\":\n"
                + "        return Dog()\n"
                + "    raise ValueError(kind)",
                "class AnimalFactory:\n"
                + "    _registry = {\n"
                + "        \"dog\": Dog,\n"
                + "    }\n"
                + "\n"
                + "    @classmethod\n"
                + "    def create(cls, kind):\n"
                + "        if kind not in cls._registry:\n"
                + "            raise ValueError(kind)\n"
                + "        return cls._registry[kind]()"));
            p.Add(new SupportedStructuralPattern(K.P1_FactoryRemove,
                "P1 — Factory (remove)",
                "class AnimalFactory:\n"
                + "    _registry = {\"dog\": Dog}\n"
                + "    @classmethod\n"
                + "    def create(cls, kind):\n"
                + "        if kind not in cls._registry:\n"
                + "            raise ValueError(kind)\n"
                + "        return cls._registry[kind]()",
                "def create_animal(kind):\n"
                + "    if kind == \"dog\":\n"
                + "        return Dog()\n"
                + "    raise ValueError(kind)"));
            p.Add(new SupportedStructuralPattern(K.P6_FacadeIntroduce,
                "P6 — Facade (introduce)",
                "def checkout(cart):\n"
                + "    validate(cart)\n"
                + "    charge(cart)\n"
                + "    return ship(cart)",
                "class CheckoutFacade:\n"
                + "    def checkout(self, cart):\n"
                + "        validate(cart)\n"
                + "        charge(cart)\n"
                + "        return ship(cart)"));
            p.Add(new SupportedStructuralPattern(K.P6_FacadeRemove,
                "P6 — Facade (remove)",
                "class CheckoutFacade:\n"
                + "    def checkout(self, cart):\n"
                + "        validate(cart)\n"
                + "        charge(cart)\n"
                + "        return ship(cart)",
                "def checkout(cart):\n"
                + "    validate(cart)\n"
                + "    charge(cart)\n"
                + "    return ship(cart)"));
            p.Add(new SupportedStructuralPattern(K.P2_StrategyIntroduce,
                "P2 — Strategy (introduce)",
                "def execute(mode, x):\n"
                + "    if mode == \"fast\":\n"
                + "        return run_fast(x)\n"
                + "    raise ValueError(mode)",
                "_execute_strategies = {\n"
                + "    \"fast\": run_fast,\n"
                + "}\n"
                + "\n"
                + "\n"
                + "def execute(mode, x):\n"
                + "    if mode not in _execute_strategies:\n"
                + "        raise ValueError(mode)\n"
                + "    return _execute_strategies[mode](x)"));
            p.Add(new SupportedStructuralPattern(K.P2_StrategyRemove,
                "P2 — Strategy (remove)",
                "_execute_strategies = {\n"
                + "    \"fast\": run_fast,\n"
                + "}\n"
                + "\n"
                + "\n"
                + "def execute(mode, x):\n"
                + "    if mode not in _execute_strategies:\n"
                + "        raise ValueError(mode)\n"
                + "    return _execute_strategies[mode](x)",
                "def execute(mode, x):\n"
                + "    if mode == \"fast\":\n"
                + "        return run_fast(x)\n"
                + "    raise ValueError(mode)"));
            p.Add(new SupportedStructuralPattern(K.P5_TemplateIntroduce,
                "P5 — Template Method (introduce)",
                "class ReportBuilder:\n"
                + "    def build(self):\n"
                + "        self.fetch()\n"
                + "        self.save()",
                "class ReportBuilder(ABC):\n"
                + "    def build(self):\n"
                + "        self.fetch()\n"
                + "        self.save()\n"
                + "\n"
                + "    @abstractmethod\n"
                + "    def fetch(self): ...\n"
                + "\n"
                + "    @abstractmethod\n"
                + "    def save(self): ..."));
            p.Add(new SupportedStructuralPattern(K.P5_TemplateRemove,
                "P5 — Template Method (remove)",
                "class ReportBuilder(ABC):\n"
                + "    def build(self):\n"
                + "        self.fetch()\n"
                + "        self.save()\n"
                + "\n"
                + "    @abstractmethod\n"
                + "    def fetch(self): ...\n"
                + "\n"
                + "    @abstractmethod\n"
                + "    def save(self): ...",
                "class ReportBuilder:\n"
                + "    def build(self):\n"
                + "        self.fetch()\n"
                + "        self.save()"));
            p.Add(new SupportedStructuralPattern(K.P3_ObserverIntroduce,
                "P3 — Observer (introduce)",
                "class Stock:\n"
                + "    def set_price(self, price):\n"
                + "        self.price = price",
                "class Stock:\n"
                + "    def __init__(self):\n"
                + "        self._observers = []\n"
                + "\n"
                + "    def subscribe(self, observer):\n"
                + "        self._observers.append(observer)\n"
                + "    ...\n"
                + "    def set_price(self, price):\n"
                + "        self.price = price"));
            p.Add(new SupportedStructuralPattern(K.P4_DecoratorIntroduce,
                "P4 — Decorator (introduce)",
                "class Service:\n"
                + "    def run(self):\n"
                + "        return 1",
                "class Service:\n"
                + "    def run(self):\n"
                + "        return 1\n"
                + "\n"
                + "\n"
                + "class ServiceDecorator:\n"
                + "    def __init__(self, wrapped):\n"
                + "        self._wrapped = wrapped\n"
                + "\n"
                + "    def run(self):\n"
                + "        return self._wrapped.run()"));
            p.Add(new SupportedStructuralPattern(K.O1_FunctionsToClass,
                "O1 — functions to class",
                "def counter_increment(self):\n"
                + "    self.count += 1\n"
                + "\n"
                + "def counter_reset(self):\n"
                + "    self.count = 0",
                "class Counter:\n"
                + "    def increment(self):\n"
                + "        self.count += 1\n"
                + "\n"
                + "    def reset(self):\n"
                + "        self.count = 0"));
            p.Add(new SupportedStructuralPattern(K.O1_ClassToFunctions,
                "O1 — class to functions",
                "class Counter:\n"
                + "    def increment(self):\n"
                + "        self.count += 1\n"
                + "\n"
                + "    def reset(self):\n"
                + "        self.count = 0",
                "def counter_increment(self):\n"
                + "    self.count += 1\n"
                + "\n"
                + "def counter_reset(self):\n"
                + "    self.count = 0"));
            p.Add(new SupportedStructuralPattern(K.O2_DictToClass,
                "O2 — dict factory to class",
                "def make_point(x, y):\n"
                + "    return {\"x\": x, \"y\": y}",
                "class Point:\n"
                + "    def __init__(self, x, y):\n"
                + "        self.x = x\n"
                + "        self.y = y"));
            p.Add(new SupportedStructuralPattern(K.O2_ClassToDict,
                "O2 — class to dict factory",
                "class Point:\n"
                + "    def __init__(self, x, y):\n"
                + "        self.x = x\n"
                + "        self.y = y",
                "def make_point(x, y):\n"
                + "    return {\"x\": x, \"y\": y}"));
            p.Add(new SupportedStructuralPattern(K.S1_SrpSplit,
                "S1 — SRP split (cohesion clusters)",
                "class Account:\n"
                + "    def deposit(self, amount):\n"
                + "        self.balance += amount\n"
                + "\n"
                + "    def log_in(self, user):\n"
                + "        self.user = user",
                "class AccountBalance:\n"
                + "    def deposit(self, amount):\n"
                + "        self.balance += amount\n"
                + "\n"
                + "\n"
                + "class AccountUser:\n"
                + "    def log_in(self, user):\n"
                + "        self.user = user"));
            p.Add(new SupportedStructuralPattern(K.S1_SrpMerge,
                "S1 — SRP merge",
                "class AccountBalance:\n"
                + "    def deposit(self, amount):\n"
                + "        self.balance += amount\n"
                + "\n"
                + "\n"
                + "class AccountUser:\n"
                + "    def log_in(self, user):\n"
                + "        self.user = user",
                "class Account:\n"
                + "    def deposit(self, amount):\n"
                + "        self.balance += amount\n"
                + "\n"
                + "    def log_in(self, user):\n"
                + "        self.user = user"));
            p.Add(new SupportedStructuralPattern(K.S3_IspSegregate,
                "S3 — ISP segregate interface",
                "class Repository(Protocol):\n"
                + "    def fetch(self, key): ...\n"
                + "    def save(self, key, value): ...",
                "class RepositoryFetch(Protocol):\n"
                + "    def fetch(self, key): ...\n"
                + "\n"
                + "\n"
                + "class RepositorySave(Protocol):\n"
                + "    def save(self, key, value): ..."));
            p.Add(new SupportedStructuralPattern(K.S3_IspCollapse,
                "S3 — ISP collapse interfaces",
                "class RepositoryFetch(Protocol):\n"
                + "    def fetch(self, key): ...\n"
                + "\n"
                + "\n"
                + "class RepositorySave(Protocol):\n"
                + "    def save(self, key, value): ...",
                "class Repository(Protocol):\n"
                + "    def fetch(self, key): ...\n"
                + "    def save(self, key, value): ..."));
            p.Add(new SupportedStructuralPattern(K.S2_DipIntroduce,
                "S2 — DIP introduce abstraction",
                "class Service:\n"
                + "    def run(self):\n"
                + "        return 1",
                "class ServiceProtocol(Protocol):\n"
                + "    def run(self): ...\n"
                + "\n"
                + "\n"
                + "class Service(ServiceProtocol):\n"
                + "    def run(self):\n"
                + "        return 1"));
            p.Add(new SupportedStructuralPattern(K.S2_DipInline,
                "S2 — DIP inline abstraction",
                "class ServiceProtocol(Protocol):\n"
                + "    def run(self): ...\n"
                + "\n"
                + "\n"
                + "class Service(ServiceProtocol):\n"
                + "    def run(self):\n"
                + "        return 1",
                "class Service:\n"
                + "    def run(self):\n"
                + "        return 1"));
            return p;
        }

        /// <summary>
        /// Compile the emitted replacement in isolation and verify it is <i>syntactically</i>
        /// well-formed. Semantic diagnostics (undefined name / type errors) are deliberately
        /// ignored: the emitted block is a fragment whose free names (the constructed classes,
        /// the raised exception) are bound in the surrounding module, so those would be false
        /// positives. Only lexer/parser diagnostics (codes beginning "PY1") and Fatal-severity
        /// diagnostics indicate a malformed emission.
        /// </summary>
        private static bool IsSyntacticallyValid(string source)
        {
            CompileResult result = VirtualCompiler.CompileSource(source);
            foreach (CompileDiagnostic d in result.Diagnostics)
            {
                if (d.Severity == ParseSeverity.Fatal) return false;
                if (d.Code != null && d.Code.StartsWith("PY1", StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>
        /// We never drop the candidate — the host preview surfaces the caveat.
        /// </summary>
        private static void ValidateEmitted(StructuralCandidate c)
        {
            if (!IsSyntacticallyValid(c.ReplacementText))
            {
                c.Caveats.Add(new CandidateIssue(
                    "STR-VALIDATE",
                    "Emitted code is not syntactically valid Python; review carefully."));
                c.Confidence = 50;
            }
        }

        private static List<string> SplitLinesStripCr(string s)
        {
            List<string> outList = new List<string>();
            StringBuilder cur = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\n') { outList.Add(StripCr(cur.ToString())); cur.Length = 0; }
                else cur.Append(c);
            }
            outList.Add(StripCr(cur.ToString()));
            return outList;
        }

        private static string StripCr(string s)
        {
            if (s.Length > 0 && s[s.Length - 1] == '\r') return s.Substring(0, s.Length - 1);
            return s;
        }
    }
}
