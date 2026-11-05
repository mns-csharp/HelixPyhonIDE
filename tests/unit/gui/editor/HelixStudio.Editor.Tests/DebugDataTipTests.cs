using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The editor half of the debug data tip: what expression is under the mouse, and does a host
    /// that answers a hover take the dwell away from the ordinary signature/doc tooltip.
    ///
    /// The editor cannot evaluate anything — <c>HelixStudio.Editor</c> does not reference
    /// <c>HelixStudio.Debugger</c> — so this is the whole of its responsibility, and it is pure
    /// enough to test without a debugger.
    /// </summary>
    [TestClass]
    public class DebugDataTipTests
    {
        private static string ExprAt(string text, string needle)
        {
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, "needle '" + needle + "' must occur in the fixture");
            MethodInfo m = typeof(PythonEditorControl).GetMethod("DataTipExpressionAt",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "DataTipExpressionAt expected");
            return (string)m.Invoke(null, new object[] { text, at });
        }

        // ---------------------------------------------------------------- what gets evaluated

        [TestMethod]
        public void ABareName_IsItsOwnExpression()
        {
            Assert.AreEqual("fault", ExprAt("for fault in FAULT_IDS:\n", "fault"));
        }

        [TestMethod]
        public void HoveringAMember_EvaluatesTheWholeChain_NotTheBareName()
        {
            // The point of the feature: `n_checks` alone is not a name in scope, and reporting
            // "not available" for it would make the tip useless exactly where it is most wanted.
            Assert.AreEqual("coverage_table.n_checks",
                ExprAt("print(coverage_table.n_checks)\n", "n_checks"));
        }

        [TestMethod]
        public void HoveringTheReceiver_StopsAtTheReceiver()
        {
            // Pointing at `coverage_table` asks about the object, not about one of its members.
            Assert.AreEqual("coverage_table",
                ExprAt("print(coverage_table.n_checks)\n", "coverage_table"));
        }

        [TestMethod]
        public void DeepChainsResolveWholly()
        {
            Assert.AreEqual("self.report.rows", ExprAt("x = self.report.rows\n", "rows"));
        }

        [TestMethod]
        public void ASubscriptOnTheHoveredName_IsIncluded()
        {
            // `rows[i]` is what the code reads, so it is what the tip should report.
            Assert.AreEqual("rows[i]", ExprAt("value = rows[i]\n", "rows"));
        }

        [TestMethod]
        public void ASubscriptEarlierInTheChain_IsCarried()
        {
            Assert.AreEqual("table[key].name", ExprAt("n = table[key].name\n", "name"));
        }

        // ---------------------------------------------------------------- what is refused

        [TestMethod]
        public void ACallIsNeverEvaluated()
        {
            // A data tip appears because the mouse came to rest. Running the user's function on
            // that gesture would let inspecting a program change it.
            Assert.IsNull(ExprAt("save_all()\n", "save_all"));
        }

        [TestMethod]
        public void AChainHangingOffACall_IsRefusedWholesale()
        {
            // Not narrowed to something else — `f().name` has no side-effect-free reading, so the
            // honest answer is to say nothing.
            Assert.IsNull(ExprAt("x = build().name\n", "name"));
        }

        [TestMethod]
        public void ACallInsideASubscript_IsRefused()
        {
            Assert.IsNull(ExprAt("x = table[key()].name\n", "name"));
        }

        [TestMethod]
        public void AMethodCallOnAnObject_IsRefused()
        {
            Assert.IsNull(ExprAt("n = queue.pop()\n", "pop"));
        }

        [TestMethod]
        public void NumbersAndNonIdentifiers_AreNotExpressions()
        {
            Assert.IsNull(ExprAt("count = 42\n", "42"));
            Assert.IsNull(ExprAt("a = b + c\n", "+"));
        }

        [TestMethod]
        public void OutOfRangePositionsAreRefusedRatherThanThrowing()
        {
            MethodInfo m = typeof(PythonEditorControl).GetMethod("DataTipExpressionAt",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNull((string)m.Invoke(null, new object[] { "abc", -1 }));
            Assert.IsNull((string)m.Invoke(null, new object[] { "abc", 99 }));
            Assert.IsNull((string)m.Invoke(null, new object[] { null, 0 }));
            Assert.IsNull((string)m.Invoke(null, new object[] { "", 0 }));
        }

        // ---------------------------------------------------------------- the dwell wiring

        private static PythonEditorControl NewEditor(string text)
        {
            var editor = new PythonEditorControl();
            editor.Text = text;
            var _ = editor.Handle;   // realise, so calltip show/cancel behave as in the app
            return editor;
        }

        private static void RaiseDwell(PythonEditorControl editor, int position)
        {
            MethodInfo m = typeof(PythonEditorControl).GetMethod("OnDwellStart",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "OnDwellStart expected");
            m.Invoke(editor, new object[] { null, new HelixTextEditorNET.DwellEventArgs(position, 0, 0) });
        }

        [TestMethod]
        public void DwellingOffersTheExpressionToTheHost()
        {
            string src = "value = coverage_table.n_checks\n";
            using (PythonEditorControl editor = NewEditor(src))
            {
                string seen = null;
                editor.DebugDataTipRequested += (s, e) => { seen = e.Expression; e.Handled = true; };
                RaiseDwell(editor, src.IndexOf("n_checks", StringComparison.Ordinal));

                Assert.AreEqual("coverage_table.n_checks", seen,
                    "the host must be offered the whole chain the user pointed at");
            }
        }

        [TestMethod]
        public void AHandledDataTip_SuppressesTheOrdinaryHover()
        {
            // Two tooltips for one dwell is worse than either alone, so a handled data tip has to
            // end the dwell handler before the diagnostic/doc hover path runs.
            string src = "value = coverage_table.n_checks\n";
            using (PythonEditorControl editor = NewEditor(src))
            {
                editor.DebugDataTipRequested += (s, e) => e.Handled = true;
                int at = src.IndexOf("n_checks", StringComparison.Ordinal);
                RaiseDwell(editor, at);

                Assert.IsFalse(editor.CallTipActive, "the doc/diagnostic calltip must stand down");
                FieldInfo f = typeof(PythonEditorControl).GetField("_lastHoverPos",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.AreEqual(at, (int)f.GetValue(editor),
                    "the anchor still records the dwell, so a later async hover result is discarded");
            }
        }

        [TestMethod]
        public void AnUnhandledDataTip_LeavesTheOrdinaryHoverAlone()
        {
            // Not debugging is the common case: the editor must keep its normal hover.
            string src = "value = coverage_table.n_checks\n";
            using (PythonEditorControl editor = NewEditor(src))
            {
                bool asked = false;
                editor.DebugDataTipRequested += (s, e) => { asked = true; /* Handled stays false */ };
                RaiseDwell(editor, src.IndexOf("n_checks", StringComparison.Ordinal));

                Assert.IsTrue(asked, "the host is still asked");
                // Reaching the ordinary path is what matters; it runs asynchronously, so the
                // observable fact here is simply that nothing threw and no tip was forced up.
            }
        }

        [TestMethod]
        public void NoHostSubscribed_ChangesNothing()
        {
            string src = "value = coverage_table.n_checks\n";
            using (PythonEditorControl editor = NewEditor(src))
            {
                RaiseDwell(editor, src.IndexOf("n_checks", StringComparison.Ordinal));
                Assert.IsFalse(editor.CallTipActive);
            }
        }

        [TestMethod]
        public void StringsAndCommentsAreNotDataTipTargets()
        {
            string src = "note = \"coverage_table\"  # coverage_table\n";
            using (PythonEditorControl editor = NewEditor(src))
            {
                int asked = 0;
                editor.DebugDataTipRequested += (s, e) => { asked++; e.Handled = true; };
                RaiseDwell(editor, src.IndexOf("coverage_table", StringComparison.Ordinal));
                RaiseDwell(editor, src.LastIndexOf("coverage_table", StringComparison.Ordinal));

                Assert.AreEqual(0, asked,
                    "text inside a literal or a comment is not an expression the debugger can evaluate");
            }
        }

        [TestMethod]
        public void EndingTheDwell_TellsTheHostToTakeTheTipDown()
        {
            // The tip is the host's own window; the end of a dwell is an event only the editor sees.
            using (PythonEditorControl editor = NewEditor("value = total\n"))
            {
                int dismissals = 0;
                editor.DebugDataTipDismissRequested += (s, e) => dismissals++;

                MethodInfo m = typeof(PythonEditorControl).GetMethod("OnDwellEnd",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                m.Invoke(editor, new object[] { null, new HelixTextEditorNET.DwellEventArgs(8, 0, 0) });
                Assert.AreEqual(1, dismissals, "DwellEnd must dismiss");

                MethodInfo dismiss = typeof(PythonEditorControl).GetMethod("DismissHoverTip",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                dismiss.Invoke(editor, null);
                Assert.AreEqual(2, dismissals,
                    "scroll / lost focus / typing all route through DismissHoverTip and must dismiss too");
            }
        }
    }
}
