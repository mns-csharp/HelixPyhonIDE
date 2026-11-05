using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// SRS solution_explorer_rules §3.2–3.4: virtual overlays are render-only —
    /// braces/Bézier per block, inferred-type inlay markers per assignment — and
    /// never touch the document. These tests cover the pure computation layers and
    /// the editor toggles.
    /// </summary>
    [TestClass]
    public class VirtualOverlayTests
    {
        // ------------------------------------------------------------ §3.3 inference

        private static Dictionary<int, string> Markers(string code)
        {
            return VirtualTypeMarkerRenderer.ComputeMarkers(code);
        }

        [TestMethod]
        public void Infers_Primitives_Containers_And_None()
        {
            var m = Markers(
                "count = 5\n" +          // 0
                "ratio = 3.14\n" +       // 1
                "name = \"ada\"\n" +     // 2
                "flag = True\n" +        // 3
                "items = [1, 2]\n" +     // 4
                "table = {}\n" +         // 5
                "pair = (1, 2)\n" +      // 6
                "unique = {1, 2}\n" +    // 7
                "nothing = None\n");     // 8
            Assert.AreEqual("int", m[0]);
            Assert.AreEqual("float", m[1]);
            Assert.AreEqual("str", m[2]);
            Assert.AreEqual("bool", m[3]);
            Assert.AreEqual("list[int]", m[4]);
            Assert.AreEqual("dict", m[5]);
            Assert.AreEqual("tuple[int, int]", m[6]);
            Assert.AreEqual("set", m[7]);
            Assert.AreEqual("None", m[8]);
        }

        [TestMethod]
        public void Infers_UserClasses_Callables_And_Builtins()
        {
            var m = Markers(
                "obj = Rectangle()\n" +          // 0 — user class by convention
                "fn = lambda a, b: a\n" +        // 1 — callable signature (§3.3 list)
                "total = len([1])\n" +           // 2 — builtin return type
                "text = f\"{1}\"\n");            // 3 — f-string
            Assert.AreEqual("Rectangle", m[0]);
            Assert.AreEqual("Callable[[Any, Any], Any]", m[1]);
            Assert.AreEqual("int", m[2]);
            Assert.AreEqual("str", m[3]);
        }

        [TestMethod]
        public void Infers_QualifiedObjectFactories_AndAnnotatedMethodReturns()
        {
            var m = Markers(
                "class Factory:\n" +
                "    def create(self) -> Widget:\n" +
                "        return Widget()\n" +
                "widget = Factory.create()\n" +
                "tensor = torch.stack([])\n" +
                "array = np.array([])\n" +
                "frame = pd.DataFrame()\n");
            Assert.AreEqual("Widget", m[3]);
            Assert.AreEqual("Tensor", m[4]);
            Assert.AreEqual("ndarray", m[5]);
            Assert.AreEqual("DataFrame", m[6]);
        }

        [TestMethod]
        public void Propagates_ObjectTypes_ThroughNames_Operators_AndMethods()
        {
            var m = Markers(
                "angle = torch.tensor([1.0])\n" +
                "c = torch.cos(angle)\n" +
                "o = torch.ones_like(c)\n" +
                "row = torch.stack([o, c])\n" +
                "scaled = row * c\n" +
                "reduced = scaled.sum(dim=-1)\n" +
                "custom = Factory.make()\n");
            for (int line = 0; line < 6; line++) Assert.AreEqual("Tensor", m[line], "line " + line);
            Assert.AreEqual("couldn't infer type", m[6]);
        }

        [TestMethod]
        public void TensorConsumerConstraints_FlowBackwardThroughUnannotatedHelpers()
        {
            var m = Markers(
                "class Geometry:\n" +
                "    def normalize(v):\n" +
                "        return v / v.norm()\n" +
                "    def frame(a, b, c):\n" +
                "        x = Geometry.normalize(c - b)\n" +
                "        tmp = Geometry.normalize(b - a)\n" +
                "        y = Geometry.normalize(torch.cross(tmp, x))\n" +
                "        z = Geometry.normalize(torch.cross(x, y))\n" +
                "        rot = torch.stack([x, y, z])\n" +
                "        transformed = (rot * x).sum(dim=-1)\n");
            for (int line = 4; line <= 9; line++) Assert.AreEqual("Tensor", m[line], "line " + line);
        }

        [TestMethod]
        public void Infers_DestructuringAssignments_AndReturnStatements()
        {
            var m = Markers(
                "class Geometry:\n" +
                "    def cross(a, b):\n" +
                "        a0, a1, a2 = a[..., 0], a[..., 1], a[..., 2]\n" +
                "        result = torch.stack([a0, a1, a2])\n" +
                "        return result\n");
            Assert.AreEqual("tuple[Tensor, Tensor, Tensor]", m[2]);
            Assert.AreEqual("Tensor", m[3]);
            Assert.AreEqual("Tensor", m[4]);
        }

        [TestMethod]
        public void Propagates_UnparameterizedDictValues_ThroughTupleDestructuring()
        {
            var m = Markers(
                "class ManuscriptGenerator:\n" +
                "    def _section(self, name: str) -> dict:\n" +
                "        return {}\n" +
                "    def accuracy(self) -> list[str]:\n" +
                "        board, literature = self._section('leaderboard'), self._section('literature')\n" +
                "        two_seed, s1337, s2024 = (board['sample_two_seed'], board['sample_s1337'], board['sample_s2024'])\n" +
                "        mean = two_seed['mean']\n" +
                "        return [str(mean)]\n");
            Assert.AreEqual("tuple[dict, dict]", m[4]);
            Assert.AreEqual("tuple[Any, Any, Any]", m[5]);
            Assert.AreEqual("Any", m[6]);
            Assert.AreEqual("list[str]", m[7]);
        }

        [TestMethod]
        public void Destructures_InferredTupleReturn_FromUnannotatedHelper()
        {
            var m = Markers(
                "class Geometry:\n" +
                "    @staticmethod\n" +
                "    def local_frame(ca_prev, ca_curr, ca_next):\n" +
                "        x = torch.stack([ca_prev, ca_curr, ca_next])\n" +
                "        return x, ca_curr\n" +
                "    @staticmethod\n" +
                "    def rebuild(ca_prev, ca_curr, ca_next):\n" +
                "        rot, tr = Geometry.local_frame(ca_prev, ca_curr, ca_next)\n" +
                "        n = torch.stack([rot, tr])\n" +
                "        return n\n");
            Assert.AreEqual("tuple[Tensor, Tensor]", m[7]);
            Assert.AreEqual("Tensor", m[8]);
            Assert.AreEqual("Tensor", m[9]);
        }

        [TestMethod]
        public void RegisteredBuffers_RemainTensors_ThroughTupleAndMethodCalls()
        {
            var m = Markers(
                "class Model:\n" +
                "    def __init__(self):\n" +
                "        self.register_buffer('ideal_n', torch.tensor([1.0]))\n" +
                "        self.register_buffer('ideal_c', torch.tensor([2.0]))\n" +
                "    def forward(self, ca):\n" +
                "        ideals = (self.ideal_n.to(ca.dtype), self.ideal_c.to(ca.dtype))\n" +
                "        first, second = ideals\n" +
                "        return torch.stack([first, second])\n");
            Assert.AreEqual("tuple[Tensor, Tensor]", m[5]);
            Assert.AreEqual("tuple[Tensor, Tensor]", m[6]);
            Assert.AreEqual("Tensor", m[7]);
        }

        [TestMethod]
        public void Infers_Shapes_Comprehensions_StandardLibrary_AndNumpyCalls()
        {
            var m = Markers(
                "def analyze(a: Tensor, root: Path, table: dict):\n" +
                "    B, L, A, _ = a.shape\n" +
                "    names = [x for x in range(B)]\n" +
                "    child = root / 'data'\n" +
                "    report = child.with_suffix('.csv')\n" +
                "    value = table.get('value')\n" +
                "    scale = math.sqrt(4.0)\n" +
                "    joined = os.path.join('a', 'b')\n" +
                "    u, s, vt = np.linalg.svd(a)\n");
            Assert.AreEqual("tuple[int, int, int, int]", m[1]);
            Assert.AreEqual("list", m[2]);
            Assert.AreEqual("Path", m[3]);
            Assert.AreEqual("Path", m[4]);
            Assert.AreEqual("Any", m[5]);
            Assert.AreEqual("float", m[6]);
            Assert.AreEqual("str", m[7]);
            Assert.AreEqual("tuple[ndarray, ndarray, ndarray]", m[8]);
        }

        [TestMethod]
        public void Understands_TypingTuple_Optional_AndForwardReturnAnnotations()
        {
            var m = Markers(
                "class Factory:\n" +
                "    @classmethod\n" +
                "    def create(cls) -> 'Factory':\n" +
                "        return Factory()\n" +
                "    @staticmethod\n" +
                "    def coordinates() -> Tuple[Optional[float], float, float]:\n" +
                "        return None, 1.0, 2.0\n" +
                "obj = Factory.create()\n" +
                "x, r, angle = Factory.coordinates()\n");
            Assert.AreEqual("Factory", m[7]);
            Assert.AreEqual("tuple[float | None, float, float]", m[8]);
        }

        [TestMethod]
        public void Infers_ComprehensionDestructuring_FluentObjects_AndScientificCalls()
        {
            var m = Markers(
                "class Model:\n" +
                "    def __init__(self, head: Head):\n" +
                "        self.head = head\n" +
                "items = [np.asarray(x) for x in [1, 2]]\n" +
                "left, right = [np.asarray(x) for x in [1, 2]]\n" +
                "model = Model(Head()).to('cpu').eval()\n" +
                "q, r = np.linalg.qr(np.asarray([]))\n" +
                "stat, p = wilcoxon([], [])\n" +
                "header = next(stream, '').rstrip().split(',')\n");
            Assert.AreEqual("Head", m[2]);
            Assert.AreEqual("list[ndarray]", m[3]);
            Assert.AreEqual("tuple[ndarray, ndarray]", m[4]);
            Assert.AreEqual("Model", m[5]);
            Assert.AreEqual("tuple[ndarray, ndarray]", m[6]);
            Assert.AreEqual("tuple[float, float]", m[7]);
            Assert.AreEqual("list[str]", m[8]);
        }

        [TestMethod]
        public void Infers_MethodAliases_TypedLists_Aggregates_AndPathHelpers()
        {
            var m = Markers(
                "handler = Workflow.run\n" +
                "flags = [False] * 4\n" +
                "total = sum(x for x in flags)\n" +
                "ok = all(flags)\n" +
                "base, ext = os.path.splitext('file.py')\n" +
                "absolute = os.path.isabs('file.py')\n" +
                "parent = Path('x').resolve().parents[0]\n");
            Assert.AreEqual("Callable", m[0]);
            Assert.AreEqual("list[bool]", m[1]);
            Assert.AreEqual("float", m[2]);
            Assert.AreEqual("bool", m[3]);
            Assert.AreEqual("tuple[str, str]", m[4]);
            Assert.AreEqual("bool", m[5]);
            Assert.AreEqual("Path", m[6]);
        }

        [TestMethod]
        public void DeclaredFunctionReturnOverridesAnUnknownCallChain()
        {
            var m = Markers("def main() -> int:\n    return Application().run()\n");
            Assert.AreEqual("int", m[1]);
        }

        [TestMethod]
        public void Infers_InstanceAttributes_FromAnnotations_Narrowing_Json_AndConditionals()
        {
            var m = Markers(
                "class ManuscriptGenerator:\n" +
                "    def __init__(self, results: Path | dict, figures: Path | None = None) -> None:\n" +
                "        if isinstance(results, dict):\n" +
                "            self._results = results\n" +
                "        else:\n" +
                "            path = Path(results)\n" +
                "            self._results = json.loads(path.read_text())\n" +
                "        self._figures = Path(figures) if figures else None\n");
            Assert.AreEqual("dict", m[3], "isinstance must narrow the union-annotated parameter");
            Assert.AreEqual("Path", m[5]);
            Assert.AreEqual("Any", m[6], "json.loads follows its standard-library Any return contract");
            Assert.AreEqual("Path | None", m[7]);
        }

        [TestMethod]
        public void Infers_MethodReturns_FromAnnotatedBuiltinReceivers()
        {
            var m = Markers(
                "class ManuscriptGenerator:\n" +
                "    @classmethod\n" +
                "    def ascii(cls, text: str) -> str:\n" +
                "        for character, replacement in cls.ASCII_EQUIVALENTS.items():\n" +
                "            text = text.replace(character, replacement)\n" +
                "        encoded = text.encode('ascii', 'replace')\n" +
                "        decoded = encoded.decode('ascii')\n" +
                "        return decoded\n");
            Assert.AreEqual("str", m[4]);
            Assert.AreEqual("bytes", m[5]);
            Assert.AreEqual("str", m[6]);
            Assert.AreEqual("str", m[7]);
        }

        [TestMethod]
        public void ImportedMethodReturnAnnotationFeedsQualifiedCallInference()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-overlay-" + System.Guid.NewGuid().ToString("N"));
            string package = Path.Combine(root, "pkg");
            Directory.CreateDirectory(package);
            try
            {
                File.WriteAllText(Path.Combine(package, "tools.py"),
                    "class Writer:\n    def write(self, path) -> Path:\n        return path\n");
                string main = Path.Combine(root, "main.py");
                string code = "from pkg.tools import Writer\nwriter = Writer()\ndestination = writer.write('out')\n";
                File.WriteAllText(main, code);
                var m = VirtualTypeMarkerRenderer.ComputeMarkers(code, main);
                Assert.AreEqual("Path", m[2]);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [TestMethod]
        public void RelativeImportedMethodReturnAnnotationFeedsQualifiedCallInference()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-overlay-relative-" + System.Guid.NewGuid().ToString("N"));
            string package = Path.Combine(root, "pkg");
            Directory.CreateDirectory(package);
            try
            {
                File.WriteAllText(Path.Combine(package, "geometry.py"),
                    "class Geometry:\n    @staticmethod\n    def frame() -> Tuple[float, float, float]:\n        return 1.0, 2.0, 3.0\n");
                string client = Path.Combine(package, "client.py");
                string code = "from .geometry import Geometry\nx, y, z = Geometry.frame()\n";
                File.WriteAllText(client, code);
                var m = VirtualTypeMarkerRenderer.ComputeMarkers(code, client);
                Assert.AreEqual("tuple[float, float, float]", m[1]);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [TestMethod]
        public void PackageReexportCarriesMethodReturnAnnotations()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-overlay-package-" + System.Guid.NewGuid().ToString("N"));
            string package = Path.Combine(root, "pkg");
            Directory.CreateDirectory(package);
            try
            {
                File.WriteAllText(Path.Combine(package, "geometry.py"),
                    "class Geometry:\n    @staticmethod\n    def frame() -> Tuple[int, int]:\n        return 1, 2\n");
                File.WriteAllText(Path.Combine(package, "__init__.py"), "from .geometry import Geometry\n");
                string client = Path.Combine(root, "client.py");
                string code = "from pkg import Geometry\nx, y = Geometry.frame()\n";
                File.WriteAllText(client, code);
                var m = VirtualTypeMarkerRenderer.ComputeMarkers(code, client);
                Assert.AreEqual("tuple[int, int]", m[1]);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [TestMethod]
        public void MarkerFontTracksTheRenderedEditorZoom()
        {
            using (var editor = new PythonEditorControl())
            {
                editor.ApplyFontSettings("Consolas", 19f);
                Assert.AreEqual(19f, VirtualTypeMarkerRenderer.MarkerFontSizeFor(editor));
                editor.ZoomFontSize(2);
                Assert.AreEqual(21f, VirtualTypeMarkerRenderer.MarkerFontSizeFor(editor));
            }
        }

        [TestMethod]
        public void Infers_Arithmetic_Conservatively()
        {
            var m = Markers(
                "a = 2 + 3\n" +        // 0 int
                "b = 2 / 1\n" +        // 1 float (true division)
                "c = 1.5 * 2\n" +      // 2 float
                "d = \"x\" + \"y\"\n" + // 3 str
                "e = unknown_call()\n"); // 4 — no marker
            Assert.AreEqual("int", m[0]);
            Assert.AreEqual("float", m[1]);
            Assert.AreEqual("float", m[2]);
            Assert.AreEqual("str", m[3]);
            Assert.IsFalse(m.ContainsKey(4), "unknown expressions get NO marker — wrong is worse than none");
        }

        [TestMethod]
        public void Infers_CommonScientificRuntimeObjects()
        {
            var m = Markers(
                "rng = np.random.default_rng(7)\n" +
                "samples = rng.normal(size=(4, 3))\n" +
                "elapsed = time.perf_counter()\n" +
                "pattern = re.compile('x+')\n" +
                "match = pattern.search('xx')\n" +
                "clean = re.sub('x', '', 'xx')\n" +
                "cupy_array = cp.from_dlpack(samples)\n");
            Assert.AreEqual("Generator", m[0]);
            Assert.AreEqual("ndarray", m[1]);
            Assert.AreEqual("float", m[2]);
            Assert.AreEqual("Pattern", m[3]);
            Assert.AreEqual("Match | None", m[4]);
            Assert.AreEqual("str", m[5]);
            Assert.AreEqual("cupy.ndarray", m[6]);
        }

        [TestMethod]
        public void Infers_TruthfulBooleanFallbackUnions()
        {
            var m = Markers(
                "primary = 'value'\n" +
                "fallback = 'default'\n" +
                "selected = primary or fallback\n" +
                "optional = None\n" +
                "resolved = optional or fallback\n");
            Assert.AreEqual("str", m[2]);
            Assert.AreEqual("str", m[4]);
        }

        [TestMethod]
        public void SkipsAnnotatedAndMultiTargetAssignments()
        {
            var m = Markers(
                "x: int = 1\n" +      // 0 — real annotation wins
                "a = b = 2\n" +       // 1 — multi-target skipped
                "y = 3\n");           // 2 — plain, marked
            Assert.IsFalse(m.ContainsKey(0), "explicitly annotated assignment keeps its real annotation");
            Assert.IsFalse(m.ContainsKey(1), "chained assignment is skipped");
            Assert.AreEqual("int", m[2]);
        }

        [TestMethod]
        public void MarkersAreDisplayOnly_SourceIsNeverModified()
        {
            const string code = "count = 5\n";
            VirtualTypeMarkerRenderer.ComputeMarkers(code);
            Assert.AreEqual("count = 5\n", code, "§3.5: computation must never rewrite the source");
        }

        // ------------------------------------------------------------ §3.2/§3.4 toggles

        [TestMethod]
        public void Editor_DefaultsToVirtualBraces_AndTogglesToBezier()
        {
            using (var editor = new PythonEditorControl())
            {
                Assert.IsTrue(editor.VirtualBracesEnabled, "§3.2: braces are the default overlay");

                editor.VirtualBracesEnabled = false;   // §3.4: disabling braces selects the Bézier connectors
                Assert.IsFalse(editor.VirtualBracesEnabled);

                editor.VirtualBracesEnabled = true;
                Assert.IsTrue(editor.VirtualBracesEnabled);
            }
        }

        [TestMethod]
        public void Editor_TypeMarkers_DefaultOn_AndToggle()
        {
            using (var editor = new PythonEditorControl())
            {
                Assert.IsTrue(editor.TypeMarkersEnabled, "§3.3 markers default on");
                editor.TypeMarkersEnabled = false;
                Assert.IsFalse(editor.TypeMarkersEnabled);
            }
        }

        [TestMethod]
        public void OverlaysRenderInsideTheBufferedPaintFrame_NotFromWndProcAfterward()
        {
            MethodInfo paint = typeof(PythonEditorControl).GetMethod("OnPaint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo wndProc = typeof(PythonEditorControl).GetMethod("WndProc",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(paint);
            Assert.AreEqual(typeof(PythonEditorControl), paint.DeclaringType,
                "the editor must compose overlays into Scintilla's buffered PaintEventArgs graphics");
            Assert.AreNotEqual(typeof(PythonEditorControl), wndProc.DeclaringType,
                "a post-base WM_PAINT/Graphics.FromHwnd pass exposes two frames and visibly flickers");
        }
    }
}
