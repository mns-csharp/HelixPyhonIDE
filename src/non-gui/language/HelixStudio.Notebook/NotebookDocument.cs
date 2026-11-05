using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Notebooks
{
    /// <summary>What a cell is. nbformat 4 defines exactly these three.</summary>
    public enum NotebookCellKind
    {
        Code = 0,
        Markdown = 1,

        /// <summary>Passed through to nbconvert untouched; the IDE shows it as plain text.</summary>
        Raw = 2,
    }

    /// <summary>
    /// One `.ipynb` document, in memory.
    ///
    /// <para>
    /// The load-bearing design decision is that <b>the JSON a notebook was read from is kept and
    /// written back through</b> — see <see cref="SourceObject"/>, and the same on every cell and
    /// output. A notebook is somebody's working file: it carries widget state, Colab cell ids,
    /// kernel provenance, nbextension settings and whatever the last five tools that touched it
    /// wrote, in the key order and with the line endings that tool chose. An editor that reads a
    /// notebook into a tidy model and writes that model back has deleted all of it, and the user
    /// finds out when `git diff` on a one-cell edit is four thousand lines long.
    /// </para>
    /// </summary>
    public sealed class NotebookDocument
    {
        public NotebookDocument()
        {
            Cells = new List<NotebookCell>();
            NbFormat = 4;
            NbFormatMinor = 5;
            NewLine = "\n";
            EndsWithNewLine = true;
        }

        public List<NotebookCell> Cells { get; private set; }

        /// <summary>The notebook's own `metadata` object, verbatim. Never null.</summary>
        public JObject Metadata { get; set; }

        /// <summary>
        /// The JSON object this notebook was read from, or null for one created in the IDE. The
        /// writer updates the fields it models IN PLACE on this object, which is what preserves both
        /// the unmodelled fields and — because a JObject remembers insertion order — the key order
        /// the file already had.
        /// </summary>
        public JObject SourceObject { get; set; }

        /// <summary>
        /// The line ending the file used — LF or CRLF. Colab writes CRLF, Jupyter writes LF, and
        /// rewriting one as the other marks every line of a 40 000-line file as changed.
        /// </summary>
        public string NewLine { get; set; }

        /// <summary>Whether the file ended with a newline. Both are found in the wild; both are kept.</summary>
        public bool EndsWithNewLine { get; set; }

        public int NbFormat { get; set; }
        public int NbFormatMinor { get; set; }

        /// <summary>Where it was read from, or null for a notebook that has never been saved.</summary>
        public string FilePath { get; set; }

        /// <summary>
        /// The `metadata.kernelspec.name` the notebook was last run with ("python3"), or null.
        /// Informational: HelixPython runs its own interpreter and does not pretend otherwise.
        /// </summary>
        public string KernelName
        {
            get
            {
                JToken spec = Metadata == null ? null : Metadata["kernelspec"];
                JToken name = spec == null ? null : spec["name"];
                return name == null ? null : name.ToString();
            }
        }

        /// <summary>`metadata.language_info.name` ("python"), or null when the notebook omits it.</summary>
        public string LanguageName
        {
            get
            {
                JToken info = Metadata == null ? null : Metadata["language_info"];
                JToken name = info == null ? null : info["name"];
                return name == null ? null : name.ToString();
            }
        }

        /// <summary>
        /// Directories put on the kernel's <c>sys.path</c>, so a notebook can import packages that
        /// live anywhere on the disk — the user's own libraries, a repository beside this one, a
        /// book's example package three drives away.
        ///
        /// <para>
        /// Kept in the notebook's own metadata under <c>helixstudio.module_paths</c>, namespaced
        /// for the same reason the per-cell height is: Jupyter and Colab carry unknown metadata
        /// through untouched, so the setting survives a round trip through either of them, and an
        /// editor writing a bare <c>module_paths</c> into somebody else's file would be squatting.
        /// A notebook that never registered one keeps NO such key, which is what keeps an untouched
        /// notebook saving byte for byte.
        /// </para>
        ///
        /// <para>
        /// The paths are absolute. That is a deliberate limitation and not an oversight: the whole
        /// point is to reach a codebase that is nowhere near the notebook, and a relative path that
        /// silently stops resolving after the file is moved would fail in the one way this feature
        /// cannot afford — quietly. A root that has gone missing is reported when it is applied.
        /// </para>
        /// </summary>
        public IList<string> ModuleSearchPaths
        {
            get
            {
                var paths = new List<string>();
                JToken helix = Metadata == null ? null : Metadata["helixstudio"];
                var stored = helix == null ? null : helix["module_paths"] as JArray;
                if (stored == null) return paths;

                foreach (JToken entry in stored)
                {
                    if (entry == null || entry.Type == JTokenType.Null) continue;
                    string path = entry.ToString();
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                }
                return paths;
            }
        }

        /// <summary>
        /// Register a directory on the kernel's import path. Returns false when it was already
        /// there — the caller says "already on the path" rather than writing a duplicate and
        /// reporting success.
        /// </summary>
        public bool AddModuleSearchPath(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return false;

            IList<string> paths = ModuleSearchPaths;
            foreach (string existing in paths)
                if (SamePath(existing, directory)) return false;

            paths.Add(directory);
            WriteModuleSearchPaths(paths);
            return true;
        }

        public bool RemoveModuleSearchPath(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return false;

            IList<string> paths = ModuleSearchPaths;
            for (int i = 0; i < paths.Count; i++)
            {
                if (!SamePath(paths[i], directory)) continue;
                paths.RemoveAt(i);
                WriteModuleSearchPaths(paths);
                return true;
            }
            return false;
        }

        private void WriteModuleSearchPaths(IList<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                // Back to none: take the key out entirely rather than leaving an empty array, so a
                // notebook that had a path added and removed again is the file it started as.
                var existing = Metadata == null ? null : Metadata["helixstudio"] as JObject;
                if (existing != null)
                {
                    existing.Remove("module_paths");
                    if (!existing.HasValues) Metadata.Remove("helixstudio");
                }
                return;
            }

            if (Metadata == null) Metadata = new JObject();
            var helix = Metadata["helixstudio"] as JObject;
            if (helix == null) { helix = new JObject(); Metadata["helixstudio"] = helix; }

            var array = new JArray();
            foreach (string path in paths) array.Add(path);
            helix["module_paths"] = array;
        }

        /// <summary>Windows path comparison: case-insensitive, and a trailing separator means nothing.</summary>
        private static bool SamePath(string left, string right)
        {
            return string.Equals(Trim(left), Trim(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string Trim(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string trimmed = path.TrimEnd('\\', '/');
            // A drive root trims to "C:", which is a different path from "C:\" — keep the separator.
            return trimmed.Length == 2 && trimmed[1] == ':' ? path : trimmed;
        }

        /// <summary>True when nothing but Python could run this. Used to refuse politely, not to guess.</summary>
        public bool IsPython
        {
            get
            {
                string language = LanguageName;
                if (!string.IsNullOrEmpty(language))
                    return language.IndexOf("python", StringComparison.OrdinalIgnoreCase) >= 0;
                string kernel = KernelName;
                return string.IsNullOrEmpty(kernel)
                    || kernel.IndexOf("python", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public int CodeCellCount
        {
            get
            {
                int n = 0;
                foreach (NotebookCell cell in Cells) if (cell.Kind == NotebookCellKind.Code) n++;
                return n;
            }
        }

        public NotebookCell InsertCell(int index, NotebookCellKind kind, string source = null)
        {
            if (index < 0) index = 0;
            if (index > Cells.Count) index = Cells.Count;
            // An id up front, because nbformat 4.5 REQUIRES one on every cell and a cell this IDE
            // created has no JSON to inherit one from. Minted here rather than in the writer so the
            // id is stable across saves — a cell that got a new id every write would show up as a
            // deleted-and-recreated cell in every diff. The writer strips it from a 4.0 document,
            // which forbids ids, so this is safe on notebooks of either vintage.
            var cell = new NotebookCell(kind) { Source = source ?? string.Empty, Id = NotebookFactory.NewCellId() };
            Cells.Insert(index, cell);
            return cell;
        }

        public bool RemoveCellAt(int index)
        {
            if (index < 0 || index >= Cells.Count) return false;
            Cells.RemoveAt(index);
            return true;
        }

        /// <summary>Move a cell one place up or down. Returns its new index, or -1 if it could not move.</summary>
        public int MoveCell(int index, int delta)
        {
            if (index < 0 || index >= Cells.Count || delta == 0) return -1;
            int target = index + delta;
            if (target < 0 || target >= Cells.Count) return -1;
            NotebookCell cell = Cells[index];
            Cells.RemoveAt(index);
            Cells.Insert(target, cell);
            return target;
        }

        /// <summary>Drop every cell's outputs and execution count — "Clear All Outputs".</summary>
        public void ClearAllOutputs()
        {
            foreach (NotebookCell cell in Cells) cell.ClearOutputs();
        }
    }

    /// <summary>One cell: its source, and — for code cells — what running it last produced.</summary>
    public sealed class NotebookCell
    {
        public NotebookCell(NotebookCellKind kind)
        {
            Kind = kind;
            Source = string.Empty;
            Outputs = new List<NotebookOutput>();
        }

        public NotebookCellKind Kind { get; set; }

        /// <summary>
        /// The cell's text with real newlines. On disk this is a JSON array of lines each ENDING in
        /// "\n" — the reader joins it and the writer splits it back, because every other tool in the
        /// world expects that shape and a single-string `source` makes diffs useless.
        /// </summary>
        public string Source { get; set; }

        /// <summary>nbformat 4.5 cell id, preserved when present so ids survive a save.</summary>
        public string Id { get; set; }

        /// <summary>Null when the cell has never run. Code cells only.</summary>
        public int? ExecutionCount { get; set; }

        public List<NotebookOutput> Outputs { get; private set; }

        /// <summary>The cell's own `metadata`, verbatim (collapsed state, Colab ids, tags).</summary>
        public JObject Metadata { get; set; }

        /// <summary>
        /// How many text lines tall the user has made this cell, or 0 for "as tall as it needs".
        ///
        /// <para>
        /// Stored in the cell's own metadata under <c>helixstudio.height_lines</c>, so a box the
        /// user dragged taller is that tall again when the notebook is reopened — including in
        /// Jupyter and Colab, which carry unknown metadata through untouched, exactly as this
        /// writer does. It is a namespaced key for that reason: an editor writing a bare
        /// <c>height</c> into somebody else's file is squatting.
        /// </para>
        ///
        /// <para>
        /// Reading and writing go through the metadata rather than a plain field so a resize is a
        /// document edit like any other — and so a cell nobody resized keeps NO such key, which is
        /// what keeps an untouched notebook saving byte for byte.
        /// </para>
        /// </summary>
        public int HeightInLines
        {
            get
            {
                JToken helix = Metadata == null ? null : Metadata["helixstudio"];
                JToken lines = helix == null ? null : helix["height_lines"];
                if (lines == null) return 0;
                int value;
                return int.TryParse(lines.ToString(), out value) && value > 0 ? value : 0;
            }
            set
            {
                if (value <= 0)
                {
                    var existing = Metadata == null ? null : Metadata["helixstudio"] as JObject;
                    if (existing != null) existing.Remove("height_lines");
                    return;
                }
                if (Metadata == null) Metadata = new JObject();
                var helix = Metadata["helixstudio"] as JObject;
                if (helix == null) { helix = new JObject(); Metadata["helixstudio"] = helix; }
                helix["height_lines"] = value;
            }
        }

        /// <summary>The JSON this cell was read from — see <see cref="NotebookDocument.SourceObject"/>.</summary>
        public JObject SourceObject { get; set; }

        /// <summary>
        /// <see cref="Source"/> as it was when the cell was read, so the writer can tell an edited
        /// cell from an untouched one and leave the untouched one's JSON exactly as it found it.
        /// Notebooks in the wild carry line arrays that do not round-trip through join/split —
        /// a trailing <c>""</c> element is legal, common, and means nothing — and rewriting one is
        /// a diff on a cell the user never opened.
        /// </summary>
        public string OriginalSource { get; set; }

        /// <summary>True when this cell's text differs from what was read from disk.</summary>
        public bool SourceChanged
        {
            get { return SourceObject == null || !string.Equals(Source, OriginalSource, StringComparison.Ordinal); }
        }

        /// <summary>True while the kernel is running this cell — session state, never saved.</summary>
        public bool IsRunning { get; set; }

        /// <summary>
        /// When the current run started, and how long the last one took. Session state: Colab shows
        /// "7s" under a finished cell and a live counter under a running one, and that is worth
        /// having — but it is not part of the notebook FORMAT, so it is never written to disk.
        /// </summary>
        public DateTime? RunStartedUtc { get; set; }

        public TimeSpan? LastRunDuration { get; set; }

        /// <summary>Did the last run raise? Drives the tick / cross beside the cell.</summary>
        public bool LastRunFailed { get; set; }

        /// <summary>True once this cell has run in this session, successfully or not.</summary>
        public bool HasRunThisSession { get { return LastRunDuration.HasValue; } }

        public bool HasOutputs { get { return Outputs.Count > 0; } }

        public void ClearOutputs()
        {
            Outputs.Clear();
            ExecutionCount = null;
        }

        /// <summary>The cell's source split into lines, for line-accurate mapping and rendering.</summary>
        public string[] SourceLines()
        {
            return (Source ?? string.Empty).Split('\n');
        }

        public override string ToString()
        {
            string first = (Source ?? string.Empty).Split('\n')[0];
            return Kind + ": " + (first.Length > 40 ? first.Substring(0, 40) + "…" : first);
        }
    }

    /// <summary>What one execution produced. The four `output_type`s of nbformat 4.</summary>
    public enum NotebookOutputKind
    {
        /// <summary>stdout / stderr text as it was written.</summary>
        Stream = 0,

        /// <summary>The value of the cell's last expression.</summary>
        ExecuteResult = 1,

        /// <summary>Anything `display()`ed — a figure, a table, a widget.</summary>
        DisplayData = 2,

        /// <summary>An exception, with its traceback.</summary>
        Error = 3,

        /// <summary>An `output_type` this version does not know. Kept, shown as its own text.</summary>
        Unknown = 4,
    }

    /// <summary>
    /// One output of one cell.
    ///
    /// A single output carries SEVERAL representations of the same thing — a DataFrame arrives as
    /// `text/html` and `text/plain` together, a figure as `image/png` and `text/plain`. Which one is
    /// shown is the renderer's decision, so all of them are kept here rather than the reader picking
    /// one and discarding the rest.
    /// </summary>
    public sealed class NotebookOutput
    {
        public NotebookOutput()
        {
            Data = new Dictionary<string, string>(StringComparer.Ordinal);
            Traceback = new List<string>();
        }

        public NotebookOutputKind Kind { get; set; }

        /// <summary>"stdout" or "stderr" for <see cref="NotebookOutputKind.Stream"/>.</summary>
        public string StreamName { get; set; }

        /// <summary>Stream text, joined. Empty for the other kinds — read <see cref="Data"/> instead.</summary>
        public string Text { get; set; }

        /// <summary>mime type → payload. Text mimes are joined; binary ones stay base64.</summary>
        public Dictionary<string, string> Data { get; private set; }

        public int? ExecutionCount { get; set; }

        // ---- Error ----------------------------------------------------------------
        public string ErrorName { get; set; }
        public string ErrorValue { get; set; }
        public List<string> Traceback { get; private set; }

        /// <summary>The output's `metadata`, verbatim (image sizing, `needs_background`, …).</summary>
        public JObject Metadata { get; set; }

        /// <summary>The JSON this output was read from — see <see cref="NotebookDocument.SourceObject"/>.</summary>
        public JObject SourceObject { get; set; }

        /// <summary>The raw `output_type` string as written on disk, for an unknown kind.</summary>
        public string RawOutputType { get; set; }

        public bool IsError { get { return Kind == NotebookOutputKind.Error; } }

        /// <summary>
        /// The best plain-text rendering of this output, or empty when it is purely visual.
        /// Deliberately prefers `text/plain`: it is what the notebook's own author saw quoted in
        /// the file, and inventing text from HTML would be a different document.
        /// </summary>
        public string PlainText()
        {
            if (Kind == NotebookOutputKind.Stream) return Text ?? string.Empty;
            if (Kind == NotebookOutputKind.Error)
            {
                if (Traceback.Count > 0) return string.Join("\n", Traceback.ToArray());
                return (ErrorName ?? string.Empty) + ": " + (ErrorValue ?? string.Empty);
            }
            string plain;
            if (Data.TryGetValue("text/plain", out plain)) return plain ?? string.Empty;
            return string.Empty;
        }

        /// <summary>Base64 PNG/JPEG payload when this output is an image, else null.</summary>
        public string ImageBase64(out string mimeType)
        {
            foreach (string mime in ImageMimes)
            {
                string payload;
                if (Data.TryGetValue(mime, out payload) && !string.IsNullOrEmpty(payload))
                {
                    mimeType = mime;
                    // A JSON string array joined with newlines is still valid base64 once the
                    // whitespace goes; base64 decoders differ on whether they tolerate it.
                    return payload.Replace("\n", string.Empty).Replace("\r", string.Empty);
                }
            }
            mimeType = null;
            return null;
        }

        internal static readonly string[] ImageMimes = { "image/png", "image/jpeg", "image/gif" };
    }
}
