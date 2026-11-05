using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;
using HelixTextEditorNET;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.Tests
{
    // Regression for incorrect annotation rows around nested and sibling blocks. Root cause:
    // BlockScopeAnalyzer.AddSuite
    // used `lastStatement.Span.End.Line` to find a block's last line, but a compound
    // statement's Span.End only covers its own header — not its nested body — so whenever a
    // suite's last statement was itself compound (e.g. an `if` block whose last statement is
    // a sibling-scoped `while`), the outer block's LastContentLine was reported far too early.
    // That collided with the inner block's own annotation row on the wrong line, corrupting
    // row assignments for every block after it in the file. Fixed by BlockScopeAnalyzer's
    // LastLineOf() recursing into nested suites instead of trusting a compound statement's
    // own Span.End. The synthetic reproduction below is self-contained.
    [TestClass]
    public class BlockScopeAnnotationRegressionTests
    {
        private static void ForceRecompute(PythonEditorControl editor)
        {
            FieldInfo f = typeof(PythonEditorControl).GetField("_blockScope", BindingFlags.Instance | BindingFlags.NonPublic);
            object renderer = f.GetValue(editor);
            MethodInfo m = renderer.GetType().GetMethod("Recompute", BindingFlags.Instance | BindingFlags.NonPublic);
            m.Invoke(renderer, null);
        }

        // Self-contained reproduction of the exact collision pattern: `if o > 0:`'s body
        // ends with `if first_end > b:` (so both close on the same line), immediately
        // followed by a SIBLING `while` at the same indent (not nested — this is the part
        // that was mis-parsed: AddSuite used to treat "if o>0"'s last-statement span as
        // extending through the sibling while's body, corrupting its LastContentLine).
        [TestMethod]
        public void SiblingBlockAfterNestedIf_DoesNotCorruptTheOuterBlocksLastLine()
        {
            string src =
"def f():\n" +                              // 1
"    if o > 0:\n" +                          // 2
"        first_end = 1\n" +                  // 3
"        if first_end > b:\n" +              // 4
"            blocks_append(1)\n" +           // 5
"    while b < 1:\n" +                        // 6
"        e = 1\n" +                           // 7
"        blocks_append(e)\n";                 // 8

            var parsed = HelixPythonNamespace.Python.Python.ParseModule(src);
            Assert.IsFalse(parsed.HasErrors, "source must parse cleanly");

            List<BlockRange> blocks = BlockScopeAnalyzer.Analyze(parsed.Module, src);

            BlockRange outerIf = Find(blocks, BlockKind.If, 2);
            BlockRange innerIf = Find(blocks, BlockKind.If, 4);
            BlockRange whileBlock = Find(blocks, BlockKind.While, 6);

            Assert.AreEqual(5, outerIf.LastContentLine,
                "'if o > 0:' body ends at line 5 ('if first_end > b:' is its last statement) — " +
                "'while' is a SIBLING at the same indent, not nested inside it");
            Assert.AreEqual(5, innerIf.LastContentLine, "'if first_end > b:' body is just line 5");
            Assert.AreEqual(8, whileBlock.LastContentLine, "'while' body runs through its own last line, 8");
        }

        // Same pattern, but through the real annotation pipeline: the outer and inner `if`
        // both close on line 5 (two closers stacking, deepest first) while `while` gets its
        // own independent opener on line 6 — no collision between the two.
        [TestMethod]
        public void SiblingBlockAfterNestedIf_AnnotationsDoNotCollide()
        {
            string src =
"def f():\n" +
"    if o > 0:\n" +
"        first_end = 1\n" +
"        if first_end > b:\n" +
"            blocks_append(1)\n" +
"    while b < 1:\n" +
"        e = 1\n" +
"        blocks_append(e)\n";

            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(600, 400), ShowInTaskbar = false })
            {
                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Text = src;
                Application.DoEvents();
                ForceRecompute(editor);
                Application.DoEvents();

                // Line 5 (0-based doc line 4) must reserve 2 rows: both `if` blocks close there.
                Assert.AreEqual(2, editor.AnnotationGetLines(4),
                    "both nested ifs close on line 5 — 2 stacked closer rows expected");
                // Line 6 (0-based doc line 5) — the while's own header — must reserve exactly
                // 1 row for its opener, NOT be corrupted by a stray closer from the outer if.
                Assert.AreEqual(1, editor.AnnotationGetLines(5),
                    "'while' header must reserve exactly its own opener row, uncorrupted by the outer if");

                form.Close();
            }
        }

        private static BlockRange Find(List<BlockRange> blocks, BlockKind kind, int headerLine)
        {
            foreach (BlockRange b in blocks)
                if (b.Kind == kind && b.HeaderLine == headerLine) return b;
            Assert.Fail("no " + kind + " block with HeaderLine=" + headerLine + " found");
            return default;
        }

        // Second, distinct bug surfaced by the same real file: AddSuite passes the bare
        // SuiteSyntax body as `ownerSpan` for else/elif/except/finally clauses (there's no
        // separate AST node for the keyword itself) — that Suite's .Span starts at its FIRST
        // STATEMENT, not the "else:"/"finally:" keyword, so ParentIndentColumn silently picked
        // up the body's indent instead of the header's own. The opening brace then rendered at
        // the wrong X (glued onto the end of "else:" instead of below it), and depending on
        // exact offsets could even land past the line's own text. Fixed by re-deriving
        // ParentIndentColumn from the header line's actual leading whitespace in source text.
        [TestMethod]
        public void ElseClause_ParentIndentMatchesTheIfsOwnIndent_NotItsBodys()
        {
            string src =
"def f():\n" +          // 1
"    if flag:\n" +        // 2
"        pos = 1\n" +      // 3
"    else:\n" +             // 4
"        pos = 2\n" +        // 5
"    ca = pos\n";              // 6

            var parsed = HelixPythonNamespace.Python.Python.ParseModule(src);
            Assert.IsFalse(parsed.HasErrors, "must parse cleanly");
            List<BlockRange> blocks = BlockScopeAnalyzer.Analyze(parsed.Module, src);

            BlockRange ifBlock = Find(blocks, BlockKind.If, 2);
            BlockRange elseBlock = Find(blocks, BlockKind.If, 4);

            Assert.AreEqual(ifBlock.ParentIndentColumn, elseBlock.ParentIndentColumn,
                "'else:' sits at the same indent as its 'if' — their ParentIndentColumn must match, " +
                "not the (deeper) indent of else's own body");
            Assert.AreEqual(4, elseBlock.ParentIndentColumn, "'else:' has 4 leading spaces in the source");
        }

        [TestMethod]
        public void ExceptClause_ParentIndentMatchesTheTrysOwnIndent_NotItsBodys()
        {
            string src =
"def f():\n" +                   // 1
"    try:\n" +                    // 2
"        risky()\n" +               // 3
"    except ValueError:\n" +          // 4
"        handle()\n";                  // 5

            var parsed = HelixPythonNamespace.Python.Python.ParseModule(src);
            Assert.IsFalse(parsed.HasErrors, "must parse cleanly");
            List<BlockRange> blocks = BlockScopeAnalyzer.Analyze(parsed.Module, src);

            BlockRange tryBlock = Find(blocks, BlockKind.Try, 2);
            BlockRange exceptBlock = Find(blocks, BlockKind.Try, 4);

            Assert.AreEqual(tryBlock.ParentIndentColumn, exceptBlock.ParentIndentColumn,
                "'except:' sits at the same indent as its 'try' — ParentIndentColumn must match");
            Assert.AreEqual(4, exceptBlock.ParentIndentColumn, "'except:' has 4 leading spaces in the source");
        }

        // Third, distinct bug from the same real-world sweep (config.py): AddSuite assumes
        // the header sits exactly one line above the body's first AST statement
        // (`headerLine = firstLine - 1`). Comments aren't AST nodes, so a comment between the
        // header and the first real statement — e.g. `def f():` / `# explains this` / `x = 1`
        // — shifted HeaderLine onto the comment line instead of the `def` line, and the brace
        // rendered attached to the comment, not the header. Fixed by walking back past any
        // blank/comment-only lines to the true header in BlockScopeAnalyzer.
        [TestMethod]
        public void CommentBetweenHeaderAndBody_DoesNotShiftTheHeaderLine()
        {
            string src =
"def f():\n" +                              // 1
"    # explains what follows\n" +             // 2
"    x = 1\n" +                                // 3
"    y = 2\n";                                  // 4

            var parsed = HelixPythonNamespace.Python.Python.ParseModule(src);
            Assert.IsFalse(parsed.HasErrors, "must parse cleanly");
            List<BlockRange> blocks = BlockScopeAnalyzer.Analyze(parsed.Module, src);

            Assert.AreEqual(1, blocks.Count);
            Assert.AreEqual(1, blocks[0].HeaderLine,
                "the header must stay on the 'def f():' line, not slide onto the comment below it");
        }

        // Same pattern with two consecutive comment lines, to confirm the walk-back doesn't
        // stop after just one line.
        [TestMethod]
        public void MultipleCommentLinesBetweenHeaderAndBody_DoesNotShiftTheHeaderLine()
        {
            string src =
"def f():\n" +            // 1
"    # line one\n" +        // 2
"    # line two\n" +         // 3
"    x = 1\n";                 // 4

            var parsed = HelixPythonNamespace.Python.Python.ParseModule(src);
            Assert.IsFalse(parsed.HasErrors, "must parse cleanly");
            List<BlockRange> blocks = BlockScopeAnalyzer.Analyze(parsed.Module, src);

            Assert.AreEqual(1, blocks.Count);
            Assert.AreEqual(1, blocks[0].HeaderLine, "must walk back past both comment lines");
        }

    }
}
