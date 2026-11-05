using System;
using System.Drawing;

namespace HelixPythonNamespace.Editor
{
    /// <summary>
    /// The editor half of the debug data tip (Visual Studio's "hover a variable while paused and
    /// see its value"). The editor deliberately cannot evaluate anything —
    /// <c>HelixStudio.Editor</c> does not reference <c>HelixStudio.Debugger</c>, and it must not
    /// start to — so this half only answers two questions: WHAT expression is under the mouse, and
    /// WHERE is it on screen. The host that owns the debugger subscribes, evaluates, and puts the
    /// tip up.
    /// </summary>
    public partial class PythonEditorControl
    {
        /// <summary>
        /// A request for a debug data tip: the user is dwelling on an expression, and a host that
        /// owns a paused debug session may answer with its live value. The host sets
        /// <see cref="DebugDataTipEventArgs.Handled"/> when it has shown a tip, which suppresses the
        /// ordinary signature/doc hover — two tooltips for one dwell is worse than either alone.
        /// </summary>
        public sealed class DebugDataTipEventArgs : EventArgs
        {
            public DebugDataTipEventArgs(string expression, int position, Point screenPoint)
            {
                Expression = expression;
                Position = position;
                ScreenPoint = screenPoint;
            }

            /// <summary>The whole expression under the mouse, e.g. <c>coverage_table.n_checks</c>.</summary>
            public string Expression { get; private set; }

            /// <summary>The document position the mouse dwelled on.</summary>
            public int Position { get; private set; }

            /// <summary>Screen coordinates of the hovered position.</summary>
            public Point ScreenPoint { get; private set; }

            /// <summary>Set by the host once it has a data tip on screen for this expression.</summary>
            public bool Handled { get; set; }
        }

        /// <summary>Raised on dwell so a debugging host can offer a live value for the expression.</summary>
        public event EventHandler<DebugDataTipEventArgs> DebugDataTipRequested;

        /// <summary>
        /// Raised whenever a tip anchored to a document position has to come down — the mouse moved
        /// off, the view scrolled, focus left, the user typed. Without this the host's data tip
        /// outlives the text it points at: it is a separate window, and the dwell ending is an event
        /// only the editor sees.
        /// </summary>
        public event EventHandler DebugDataTipDismissRequested;

        /// <summary>
        /// The expression a data tip should evaluate for the mouse at <paramref name="position"/>,
        /// or null when there is not one.
        ///
        /// Walks LEFT from the hovered identifier across an attribute/subscript chain, so hovering
        /// <c>n_checks</c> inside <c>coverage_table.n_checks</c> evaluates the whole chain instead of
        /// a bare name that means nothing on its own — what every IDE data tip does.
        ///
        /// A chain containing a CALL is refused outright. A data tip appears merely because the
        /// mouse came to rest; evaluating <c>save_all()</c> or <c>queue.pop()</c> because the pointer
        /// paused over it would let inspecting a program change it. Subscripts are allowed
        /// (<c>rows[i]</c>) but only while their brackets balance and contain no call.
        /// </summary>
        internal static string DataTipExpressionAt(string text, int position)
        {
            if (string.IsNullOrEmpty(text) || position < 0 || position >= text.Length) return null;
            if (!IsIdentifierChar(text[position])) return null;

            // The identifier under the mouse.
            int start = position, end = position;
            while (start > 0 && IsIdentifierChar(text[start - 1])) start--;
            while (end + 1 < text.Length && IsIdentifierChar(text[end + 1])) end++;
            end++;                                        // exclusive
            if (char.IsDigit(text[start])) return null;   // a number is not an expression

            // A call on the hovered name itself is not a data-tip target.
            int after = end;
            while (after < text.Length && IsBlank(text[after])) after++;
            if (after < text.Length && text[after] == '(') return null;

            // Walk left over `.name` links, allowing a call-free subscript between them.
            int chainStart = start;
            while (chainStart > 0)
            {
                int i = SkipBlanksLeft(text, chainStart - 1);
                if (i < 0 || text[i] != '.') break;

                int j = SkipBlanksLeft(text, i - 1);
                if (j < 0) break;

                if (text[j] == ']')
                {
                    int open = MatchBracketLeft(text, j);
                    if (open < 0 || ContainsCall(text, open + 1, j)) return null;
                    j = SkipBlanksLeft(text, open - 1);
                    if (j < 0) break;
                }
                // `f().name` / `f()[0].name` — the receiver is a call, so the whole chain is refused
                // rather than silently evaluated as something narrower than what the user pointed at.
                if (text[j] == ')') return null;
                if (!IsIdentifierChar(text[j])) break;

                int nameStart = j;
                while (nameStart > 0 && IsIdentifierChar(text[nameStart - 1])) nameStart--;
                if (char.IsDigit(text[nameStart])) break;  // `1 .bit_length` — a literal, not a name
                chainStart = nameStart;
            }

            // Include one trailing subscript, so hovering `rows` in `rows[i]` reads the element the
            // code reads rather than the container.
            int chainEnd = end;
            int k = end;
            while (k < text.Length && IsBlank(text[k])) k++;
            if (k < text.Length && text[k] == '[')
            {
                int close = MatchBracketRight(text, k);
                if (close > 0 && !ContainsCall(text, k + 1, close)) chainEnd = close + 1;
            }

            string expr = text.Substring(chainStart, chainEnd - chainStart).Trim();
            return expr.Length == 0 ? null : expr;
        }

        private static bool IsBlank(char c) { return c == ' ' || c == '\t'; }

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static int SkipBlanksLeft(string text, int i)
        {
            while (i >= 0 && IsBlank(text[i])) i--;
            return i;
        }

        private static bool ContainsCall(string text, int from, int toExclusive)
        {
            for (int i = from; i < toExclusive && i < text.Length; i++)
                if (text[i] == '(') return true;
            return false;
        }

        /// <summary>Index of the '[' matching the ']' at <paramref name="close"/>, or -1.</summary>
        private static int MatchBracketLeft(string text, int close)
        {
            int depth = 0;
            for (int i = close; i >= 0; i--)
            {
                if (text[i] == ']') depth++;
                else if (text[i] == '[') { depth--; if (depth == 0) return i; }
                else if (text[i] == '\n') return -1;
            }
            return -1;
        }

        /// <summary>Index of the ']' matching the '[' at <paramref name="open"/>, or -1.</summary>
        private static int MatchBracketRight(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '[') depth++;
                else if (text[i] == ']') { depth--; if (depth == 0) return i; }
                else if (text[i] == '\n') return -1;
            }
            return -1;
        }

        /// <summary>
        /// Offers the expression at <paramref name="position"/> to a debugging host. True when the
        /// host put a data tip on screen, in which case the ordinary hover stands down.
        /// </summary>
        private bool TryRaiseDebugDataTip(int position)
        {
            EventHandler<DebugDataTipEventArgs> handler = DebugDataTipRequested;
            if (handler == null) return false;
            string expression;
            try { expression = DataTipExpressionAt(this.Text, position); }
            catch { return false; }
            if (string.IsNullOrEmpty(expression)) return false;

            DebugDataTipEventArgs args;
            try
            {
                args = new DebugDataTipEventArgs(expression, position,
                    PointToScreen(new Point(PointXFromPosition(position), PointYFromPosition(position))));
            }
            catch { return false; }

            try { handler(this, args); }
            catch { return false; }   // a host failure must never break hovering
            return args.Handled;
        }

        /// <summary>
        /// Tells the host to take its data tip down. The tip is the host's own window, so the end of
        /// a dwell — which only the editor sees — has to be forwarded or the tip never closes.
        /// </summary>
        private void RaiseDataTipDismiss()
        {
            EventHandler handler = DebugDataTipDismissRequested;
            if (handler == null) return;
            try { handler(this, EventArgs.Empty); } catch { }
        }
    }
}
