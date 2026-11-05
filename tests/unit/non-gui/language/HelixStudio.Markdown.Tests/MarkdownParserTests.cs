using System;
using System.Linq;
using HelixPythonNamespace.Markdown;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The Markdown engine, asserted through its HTML writer.
    ///
    /// <b>Why HTML and not the block tree.</b> Asserting on the model means one assertion per node
    /// kind and a failure message that reads "expected 3 children, got 2". Asserting on HTML means
    /// comparing against the output every Markdown implementation in the world agrees on, and
    /// getting a diff you can read. It also keeps the tests honest about what the engine produces
    /// rather than about how it is shaped internally — the parser can be restructured freely and
    /// these still hold.
    /// </summary>
    [TestClass]
    public class MarkdownParserTests
    {
        private static string Html(string markdown)
        {
            return MarkdownHtmlWriter.ToHtml(markdown);
        }

        private static void Renders(string markdown, string expectedHtml)
        {
            Assert.AreEqual(expectedHtml, Html(markdown), "input was:\n" + markdown);
        }

        // ---- headings ---------------------------------------------------------------------------

        [TestMethod]
        public void AtxHeadings_AtEveryLevel()
        {
            Renders("# One", "<h1>One</h1>\n");
            Renders("### Three", "<h3>Three</h3>\n");
            Renders("###### Six", "<h6>Six</h6>\n");
        }

        /// <summary>Seven hashes is not a heading — and neither is a hashtag.</summary>
        [TestMethod]
        public void AHashRunThatIsNotAHeading_StaysAParagraph()
        {
            Renders("####### Seven", "<p>####### Seven</p>\n");
            Renders("#hashtag", "<p>#hashtag</p>\n");
        }

        [TestMethod]
        public void AClosingHashRun_IsDecorationAndIsDropped()
        {
            Renders("## Title ##", "<h2>Title</h2>\n");
        }

        [TestMethod]
        public void SetextHeadings()
        {
            Renders("Title\n=====", "<h1>Title</h1>\n");
            Renders("Title\n-----", "<h2>Title</h2>\n");
        }

        // ---- inline -----------------------------------------------------------------------------

        [TestMethod]
        public void EmphasisAndStrong()
        {
            Renders("*a*", "<p><em>a</em></p>\n");
            Renders("_a_", "<p><em>a</em></p>\n");
            Renders("**a**", "<p><strong>a</strong></p>\n");
            Renders("~~a~~", "<p><del>a</del></p>\n");
        }

        /// <summary>
        /// The regression that matters most on this repository's own documents.
        ///
        /// A file describing <c>snake_case</c> identifiers is full of underscores, and treating an
        /// intra-word <c>_</c> as emphasis eats the middle of every one of them. CommonMark's
        /// left-flanking rule says the same thing; this pins it.
        /// </summary>
        [TestMethod]
        public void AnIntraWordUnderscore_IsNotEmphasis()
        {
            Renders("run_phase_two here", "<p>run_phase_two here</p>\n");
            Renders("a_b_c", "<p>a_b_c</p>\n");
        }

        /// <summary>
        /// A code span suppresses every other construct inside it. The README that motivated this
        /// engine is full of <c>`_xxx_kernel = None`</c>; italicising the middle of that is exactly
        /// how a renderer loses a reader's trust on technical prose.
        /// </summary>
        [TestMethod]
        public void ACodeSpan_SuppressesEverythingInsideIt()
        {
            Renders("`_xxx_kernel = None`", "<p><code>_xxx_kernel = None</code></p>\n");
            Renders("`**not strong**`", "<p><code>**not strong**</code></p>\n");
            Renders("`a[b](c)`", "<p><code>a[b](c)</code></p>\n");
        }

        [TestMethod]
        public void ADoubleBacktickSpan_CanContainABacktick()
        {
            Renders("`` a ` b ``", "<p><code>a ` b</code></p>\n");
        }

        [TestMethod]
        public void AnUnmatchedBacktick_IsLiteral()
        {
            Renders("a ` b", "<p>a ` b</p>\n");
        }

        [TestMethod]
        public void BackslashEscapes()
        {
            Renders(@"\*not emphasis\*", "<p>*not emphasis*</p>\n");
            Renders(@"a \| b", "<p>a | b</p>\n");
        }

        [TestMethod]
        public void HtmlSpecialCharactersAreEscaped()
        {
            Renders("a < b & c > d", "<p>a &lt; b &amp; c &gt; d</p>\n");
        }

        // ---- links -------------------------------------------------------------------------------

        [TestMethod]
        public void InlineLinksAndTitles()
        {
            Renders("[text](/url)", "<p><a href=\"/url\">text</a></p>\n");
            Renders("[text](/url \"t\")", "<p><a href=\"/url\" title=\"t\">text</a></p>\n");
        }

        [TestMethod]
        public void ALinkLabelKeepsItsOwnFormatting()
        {
            Renders("[**bold** text](/u)", "<p><a href=\"/u\"><strong>bold</strong> text</a></p>\n");
        }

        [TestMethod]
        public void Images()
        {
            Renders("![alt](/img.png)", "<p><img src=\"/img.png\" alt=\"alt\" /></p>\n");
        }

        [TestMethod]
        public void Autolinks()
        {
            Renders("<https://example.invalid/x>",
                "<p><a href=\"https://example.invalid/x\">https://example.invalid/x</a></p>\n");
            Renders("<a@b.invalid>", "<p><a href=\"mailto:a@b.invalid\">a@b.invalid</a></p>\n");
        }

        /// <summary>
        /// A reference defined AFTER the paragraph that uses it still resolves. This is the reason
        /// the parser runs a definition pass before it resolves any inline content; a single pass
        /// silently drops every forward reference, and forward references are the normal way people
        /// write them (all the definitions at the bottom of the file).
        /// </summary>
        [TestMethod]
        public void AForwardReferenceDefinition_StillResolves()
        {
            Renders("See [the docs][d].\n\n[d]: /docs \"Docs\"",
                "<p>See <a href=\"/docs\" title=\"Docs\">the docs</a>.</p>\n");
        }

        [TestMethod]
        public void AShortcutReferenceThatResolvesToNothing_IsNotALink()
        {
            Renders("[TODO] later", "<p>[TODO] later</p>\n");
        }

        // ---- code blocks --------------------------------------------------------------------------

        [TestMethod]
        public void FencedCodeKeepsItsLanguage()
        {
            Renders("```python\nx = 1\n```",
                "<pre><code class=\"language-python\">x = 1</code></pre>\n");
        }

        [TestMethod]
        public void FencedCodeContentIsNeverInterpreted()
        {
            Renders("```\n# not a heading\n**not strong**\n```",
                "<pre><code># not a heading\n**not strong**</code></pre>\n");
        }

        [TestMethod]
        public void TildeFences()
        {
            Renders("~~~\na\n~~~", "<pre><code>a</code></pre>\n");
        }

        /// <summary>An unclosed fence runs to the end — what the spec says, and what a user
        /// mid-edit needs to see rather than having their document flip to prose.</summary>
        [TestMethod]
        public void AnUnclosedFence_RunsToTheEndOfTheDocument()
        {
            Renders("```\na\nb", "<pre><code>a\nb</code></pre>\n");
        }

        [TestMethod]
        public void IndentedCode()
        {
            Renders("    x = 1", "<pre><code>x = 1</code></pre>\n");
        }

        // ---- lists ---------------------------------------------------------------------------------

        [TestMethod]
        public void TightUnorderedList()
        {
            Renders("- a\n- b", "<ul>\n<li>a</li>\n<li>b</li>\n</ul>\n");
        }

        [TestMethod]
        public void OrderedListKeepsItsStartNumber()
        {
            Renders("3. a\n4. b", "<ol start=\"3\">\n<li>a</li>\n<li>b</li>\n</ol>\n");
        }

        /// <summary>
        /// A blank line between items makes the list LOOSE, and a loose item wraps its content in a
        /// paragraph. It is the single most visible tight/loose difference — get it backwards and
        /// every short bullet list renders double-spaced.
        /// </summary>
        [TestMethod]
        public void ABlankLineBetweenItems_MakesTheListLoose()
        {
            Renders("- a\n\n- b",
                "<ul>\n<li>\n<p>a</p>\n</li>\n<li>\n<p>b</p>\n</li>\n</ul>\n");
        }

        [TestMethod]
        public void NestedLists()
        {
            Renders("- a\n  - b",
                "<ul>\n<li>a\n<ul>\n<li>b</li>\n</ul>\n</li>\n</ul>\n");
        }

        [TestMethod]
        public void TaskListItems()
        {
            Renders("- [x] done\n- [ ] todo",
                "<ul>\n<li><input type=\"checkbox\" disabled=\"\" checked=\"\" /> done</li>\n"
                + "<li><input type=\"checkbox\" disabled=\"\" /> todo</li>\n</ul>\n");
        }

        [TestMethod]
        public void AThematicBreakIsNotAOneItemList()
        {
            Renders("---", "<hr />\n");
            Renders("- - -", "<hr />\n");
            Renders("***", "<hr />\n");
        }

        // ---- blockquotes ------------------------------------------------------------------------------

        [TestMethod]
        public void BlockQuotes()
        {
            Renders("> a", "<blockquote>\n<p>a</p>\n</blockquote>\n");
        }

        [TestMethod]
        public void ALazyContinuationStaysInTheQuote()
        {
            Renders("> a\nb", "<blockquote>\n<p>a\nb</p>\n</blockquote>\n");
        }

        // ---- tables ------------------------------------------------------------------------------------

        [TestMethod]
        public void PipeTable()
        {
            Renders("| a | b |\n|---|---|\n| 1 | 2 |",
                "<table>\n<thead>\n<tr>\n<th>a</th>\n<th>b</th>\n</tr>\n</thead>\n"
                + "<tbody>\n<tr>\n<td>1</td>\n<td>2</td>\n</tr>\n</tbody>\n</table>\n");
        }

        /// <summary>
        /// The DELIMITER row is what makes a table. Two words either side of a pipe, with nothing
        /// under them, is a paragraph — otherwise every sentence containing a vertical bar becomes a
        /// one-row table.
        /// </summary>
        [TestMethod]
        public void APipeWithoutADelimiterRow_IsAParagraph()
        {
            Renders("a | b", "<p>a | b</p>\n");
        }

        [TestMethod]
        public void TableColumnAlignment()
        {
            string html = Html("| a | b | c |\n|:--|:-:|--:|\n| 1 | 2 | 3 |");
            StringAssert.Contains(html, "<th style=\"text-align: left;\">a</th>");
            StringAssert.Contains(html, "<th style=\"text-align: center;\">b</th>");
            StringAssert.Contains(html, "<th style=\"text-align: right;\">c</th>");
        }

        /// <summary>An escaped pipe is cell content, not a column boundary — without this, any
        /// table documenting a shell pipeline or a regex alternation gains phantom columns.</summary>
        [TestMethod]
        public void AnEscapedPipe_IsCellContent()
        {
            var document = MarkdownParser.Parse("| a | b |\n|---|---|\n| x \\| y | z |");
            MarkdownBlock table = document.Root.Children.Single(b => b.Kind == MarkdownBlockKind.Table);
            MarkdownBlock bodyRow = table.Children[1];
            Assert.AreEqual(2, bodyRow.Children.Count, "the escaped pipe must not split the row");
            Assert.AreEqual("x | y", bodyRow.Children[0].Inlines[0].ToPlainText());
        }

        [TestMethod]
        public void ARaggedBodyRow_IsPaddedToTheHeaderWidth()
        {
            var document = MarkdownParser.Parse("| a | b | c |\n|---|---|---|\n| 1 |");
            MarkdownBlock table = document.Root.Children.Single(b => b.Kind == MarkdownBlockKind.Table);
            Assert.AreEqual(3, table.Children[1].Children.Count,
                "every row must be rectangular or the renderer's column pass indexes past the end");
        }

        // ---- document-level ----------------------------------------------------------------------------

        [TestMethod]
        public void ParagraphsAreSeparatedByBlankLines()
        {
            Renders("a\n\nb", "<p>a</p>\n<p>b</p>\n");
        }

        /// <summary>
        /// A soft break is a space, not a line break. Markdown reflows; preserving the source file's
        /// hard wrap would reproduce the author's 90-column margin in a pane of a different width.
        /// </summary>
        [TestMethod]
        public void ASoftBreakJoinsTheLines()
        {
            Renders("a\nb", "<p>a\nb</p>\n");
        }

        [TestMethod]
        public void TwoTrailingSpacesAreAHardBreak()
        {
            Renders("a  \nb", "<p>a<br />\nb</p>\n");
        }

        [TestMethod]
        public void CrlfAndLfParseIdentically()
        {
            Assert.AreEqual(Html("# a\n\n- b\n"), Html("# a\r\n\r\n- b\r\n"));
        }

        [TestMethod]
        public void ALeadingByteOrderMarkIsIgnored()
        {
            Renders("﻿# Title", "<h1>Title</h1>\n");
        }

        [TestMethod]
        public void TheDocumentTitleIsItsFirstH1()
        {
            Assert.AreEqual("The Title", MarkdownParser.Parse("intro\n\n# The Title\n\n## Other").Title);
            Assert.IsNull(MarkdownParser.Parse("no headings here").Title);
        }

        /// <summary>
        /// Raw HTML is escaped by default. A Markdown file is content the IDE opened from disk, not
        /// something the user authored in this session, and rendering a document is not a reason to
        /// honour whatever markup is in it.
        /// </summary>
        [TestMethod]
        public void RawHtmlIsEscapedUnlessExplicitlyAllowed()
        {
            Renders("<div>x</div>", "<p>&lt;div&gt;x&lt;/div&gt;</p>\n");

            var writer = new MarkdownHtmlWriter { AllowRawHtml = true };
            Assert.AreEqual("<div>x</div>\n", writer.Write(MarkdownParser.Parse("<div>x</div>")));
        }

        [TestMethod]
        public void EmptyAndWhitespaceInputProduceNothing()
        {
            Assert.AreEqual(string.Empty, Html(null));
            Assert.AreEqual(string.Empty, Html(string.Empty));
            Assert.AreEqual(string.Empty, Html("   \n\n  \n"));
        }

        /// <summary>
        /// A crash in a renderer that runs on a keystroke debounce is worse than a wrong render, so
        /// the parser is swept over deliberately awkward input. The assertion is only that it
        /// terminates and returns something — the point is that none of these throw.
        /// </summary>
        [TestMethod]
        public void MalformedInputDoesNotThrow()
        {
            string[] nasty =
            {
                "[", "]", "[](", "![", "***", "___", "|", "|---|", "```", "~~~", ">", "- ", "1.",
                "[a][b]", "<", "<<<>>>", "`", "``", "\\", "#", "######", "    ", "\t\t\t",
                "| a |\n|---|\n| \\", "- a\n  - b\n    - c\n      - d", "> > > > deep",
            };
            foreach (string input in nasty)
            {
                string html = Html(input);
                Assert.IsNotNull(html, "input: " + input);
            }
        }
    }
}
