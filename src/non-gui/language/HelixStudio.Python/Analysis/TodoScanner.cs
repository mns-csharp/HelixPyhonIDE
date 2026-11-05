namespace HelixPythonNamespace.Python.Analysis
{
    using System.Collections.Generic;

    /// <summary>A TODO/FIXME/HACK/XXX/NOTE comment tag found in source. Port of the C++ <c>TodoItem</c>.</summary>
    public sealed class TodoItem
    {
        public string FilePath = "";
        public int Line;        // 1-based
        public int Column;      // 1-based, position of the tag
        public string Tag = ""; // TODO, FIXME, HACK, XXX, NOTE
        public string Message = ""; // text following the tag
    }

    /// <summary>
    /// Regex-free scanner: walks lines, finds the leading Python comment, then matches a
    /// fixed tag prefix list. Port of the C++ <c>TodoScanner</c>.
    ///
    /// <para>Lives in the headless <c>HelixPythonNamespace.Python</c> assembly (not the WinForms editor)
    /// because it is pure text processing — so both the in-app TODO panel and the headless
    /// <c>helix_cli todo</c> command can share one implementation without dragging WinForms
    /// or HelixTextEditorNET into the CLI.</para>
    /// </summary>
    public static class TodoScanner
    {
        private static readonly string[] Tags = { "TODO", "FIXME", "HACK", "XXX", "NOTE" };

        public static List<TodoItem> ScanText(string source, string filePath)
        {
            List<TodoItem> outList = new List<TodoItem>();
            if (source == null) return outList;
            int line = 1;
            int lineStart = 0;
            for (int i = 0; i <= source.Length; i++)
            {
                bool eol = (i == source.Length) || (source[i] == '\n');
                if (!eol) continue;

                int len = i - lineStart;
                if (len > 0 && source[lineStart + len - 1] == '\r') len--;
                string ln = source.Substring(lineStart, len);

                int hashPos;
                if (IsLeadingHashComment(ln, out hashPos))
                {
                    int scan = hashPos + 1;
                    while (scan < ln.Length && (ln[scan] == ' ' || ln[scan] == '\t')) scan++;
                    foreach (string tag in Tags)
                    {
                        if (MatchTagAt(ln, scan, tag))
                        {
                            int msgStart = scan + tag.Length;
                            while (msgStart < ln.Length && (ln[msgStart] == ' ' || ln[msgStart] == ':' || ln[msgStart] == '\t'))
                                msgStart++;
                            outList.Add(new TodoItem
                            {
                                FilePath = filePath,
                                Line = line,
                                Column = scan + 1,
                                Tag = tag,
                                Message = ln.Substring(msgStart),
                            });
                            break;
                        }
                    }
                }

                lineStart = i + 1;
                line++;
            }
            return outList;
        }

        private static bool MatchTagAt(string s, int pos, string tag)
        {
            if (pos + tag.Length > s.Length) return false;
            for (int i = 0; i < tag.Length; i++)
                if (s[pos + i] != tag[i]) return false;
            if (pos + tag.Length < s.Length)
            {
                char next = s[pos + tag.Length];
                if (IsAlnum(next) || next == '_') return false;
            }
            return true;
        }

        private static bool IsLeadingHashComment(string line, out int hashPos)
        {
            hashPos = 0;
            bool inSingle = false, inDouble = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inSingle) { if (c == '\'') inSingle = false; continue; }
                if (inDouble) { if (c == '"') inDouble = false; continue; }
                if (c == '\'') { inSingle = true; continue; }
                if (c == '"') { inDouble = true; continue; }
                if (c == '#') { hashPos = i; return true; }
            }
            return false;
        }

        private static bool IsAlnum(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        }
    }
}
