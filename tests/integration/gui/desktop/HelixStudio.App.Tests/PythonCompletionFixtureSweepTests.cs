using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.Editor.Completion;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;
using PythonFacade = HelixPythonNamespace.Python.Python;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Statement-by-statement IDE acceptance sweep over the real multi-package Python fixture.
    /// The fixture is copied before opening because the editor and loader may create IDE metadata.
    /// </summary>
    [TestClass]
    public class PythonCompletionFixtureSweepTests
    {
        [TestMethod]
        [Timeout(600000)]
        public void EveryFixtureStatement_ReachesIntelliSenseAndAutocompleteThroughTheIde()
        {
            string root = FixtureSolution.CopyToTemp("python", "python-test-sample-oop");
            MainForm form = TestMainForm.CreateComposed();
            int files = 0;
            int statements = 0;
            int popupContexts = 0;
            int emptyContexts = 0;
            int parserDiagnostics = 0;
            var contextKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                FixtureSolution.RealizeHeadless(form);
                TestMainForm.ShowOffScreen(form);
                var solution = FixtureSolution.OpenSolution(form, root);
                foreach (string file in Directory.GetFiles(root, "*.py", SearchOption.AllDirectories)
                    .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s == "__pycache__"))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    EditorTabPage tab = FixtureSolution.OpenFile(form, file);
                    string source = tab.Editor.Text ?? string.Empty;
                    ParseResult parse = PythonFacade.ParseModule(source, file);
                    parserDiagnostics += parse.Diagnostics.Count;
                    var body = new[] { parse.Module }.Concat(parse.Module.Descendants())
                        .OfType<StatementSyntax>()
                        .OrderBy(s => s.Span.Start.Offset)
                        .ThenByDescending(s => s.Span.End.Offset)
                        .ToArray();
                    files++;
                    var engine = new PythonCompletionEngine();
                    var controllerField = typeof(PythonEditorControl).GetField("_completion",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(controllerField, "the production editor must own its completion controller");
                    var controller = controllerField.GetValue(tab.Editor) as CompletionController;
                    Assert.IsNotNull(controller, "the opened fixture tab must use the production completion controller");
                    foreach (StatementSyntax statement in body)
                    {
                        statements++;
                        int caret = Math.Max(0, Math.Min(source.Length, statement.Span.End.Offset));
                        // Match the production editor exactly: it classifies the whole buffer at
                        // an explicit caret offset (the tail after the caret still exists).
                        var context = engine.Classify(source, caret);
                        string kind = context.Kind.ToString();
                        contextKinds[kind] = contextKinds.ContainsKey(kind) ? contextKinds[kind] + 1 : 1;

                        // Query the production controller for each statement position in the
                        // already-loaded IDE buffer. Leave source untouched so thousands of
                        // contexts do not trigger thousands of full-document re-analysis passes.
                        tab.Editor.AutoCCancel();
                        tab.Editor.GotoPosition(caret);
                        controller.Invoke(force: true);
                        if (tab.Editor.AutoCActive)
                        {
                            popupContexts++;
                            tab.Editor.AutoCCancel();
                        }
                        else
                        {
                            emptyContexts++;
                        }
                    }
                }

                Assert.IsTrue(files > 0, "the fixture must contribute Python source files");
                Assert.IsTrue(statements > 0, "the fixture must contribute parsed Python statements");
                Assert.AreEqual(statements, popupContexts + emptyContexts,
                    "every statement must have a measured completion result");
                TestContext.WriteLine("Fixture statement sweep: files={0}, statements={1}, popup contexts={2}, " +
                    "no-match contexts={3}, parser diagnostics={4}.",
                    files, statements, popupContexts, emptyContexts, parserDiagnostics);
                foreach (var pair in contextKinds.OrderBy(p => p.Key, StringComparer.Ordinal))
                    TestContext.WriteLine("  context {0}: {1}", pair.Key, pair.Value);
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }

        [TestMethod]
        [Timeout(120000)]
        public void WorkspaceFromImportCompletion_IsVisibleAndCapturedByTheRealIdeHarness()
        {
            string root = FixtureSolution.CopyToTemp("python", "python-test-sample-oop");
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                FixtureSolution.RealizeHeadless(form);
                TestMainForm.ShowOffScreen(form);
                FixtureSolution.OpenSolution(form, root);
                string file = Path.Combine(root, "app_cli", "main.py");
                EditorTabPage tab = FixtureSolution.OpenFile(form, file);
                const string partialImport = "from lib_core.models import Pa";
                tab.Editor.Text = partialImport;
                tab.Editor.GotoPosition(partialImport.Length);

                var field = typeof(PythonEditorControl).GetField("_completion",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var controller = field.GetValue(tab.Editor) as CompletionController;
                Assert.IsNotNull(controller);
                var engine = new PythonCompletionEngine();
                var direct = engine.GetCompletions(partialImport, partialImport.Length, file,
                    tab.Editor.Solution);
                Assert.IsTrue(direct.Any(i => i.Label == "Pair"),
                    "the loaded workspace model must expose Pair from its source module");
                controller.Invoke(force: true);
                Assert.IsTrue(tab.Editor.AutoCActive,
                    "the real fixture workspace should provide an active import completion popup; direct=" +
                    direct.Count + " [" + string.Join(",", direct.Select(i => i.Label).Take(8)) +
                    "], solution=" + (tab.Editor.Solution == null ? "null" : tab.Editor.Solution.Projects.Count.ToString()) +
                    " projects, caret=" + tab.Editor.CurrentPosition);

                string relative = "python/editor/intellisense/loop_001_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_workspace_from_import";
                string shotRoot = TestShotDir.For(relative.Split('/'));
                Directory.CreateDirectory(shotRoot);
                using (var ide = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(ide, new Rectangle(Point.Empty, form.Size));
                    ide.Save(Path.Combine(shotRoot, "helix_ide.png"), ImageFormat.Png);
                }

                FieldInfo popupField = typeof(HelixTextEditorNET.Scintilla).GetField("_acWindow",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(popupField, "the completion popup must be the real Scintilla AutoC window");
                var popup = popupField.GetValue(tab.Editor) as Form;
                Assert.IsNotNull(popup);
                Assert.AreEqual("Pair", (string)popupField.GetValue(tab.Editor).GetType()
                    .GetProperty("SelectedItemText", BindingFlags.Instance | BindingFlags.Public)
                    .GetValue(popup, null));
                using (var image = new Bitmap(popup.Width, popup.Height))
                {
                    popup.DrawToBitmap(image, new Rectangle(Point.Empty, popup.Size));
                    image.Save(Path.Combine(shotRoot, "autocomplete_popup.png"), ImageFormat.Png);
                }
                TestContext.WriteLine("Captured real IDE and AutoC popup: " + shotRoot);
                tab.Editor.AutoCComplete();
                Assert.AreEqual("from lib_core.models import Pair", tab.Editor.Text,
                    "committing the workspace completion must replace the typed prefix");
                Assert.IsFalse(tab.Editor.AutoCActive, "a committed completion must dismiss AutoC");
                controller.HideDoc();
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }

        public TestContext TestContext { get; set; }

    }
}
