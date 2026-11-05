using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Notebooks
{
    /// <summary>
    /// <see cref="NotebookDocument"/> → `.ipynb` on disk.
    ///
    /// <para>
    /// <b>The document is written by PATCHING the JSON it was read from, not by serialising a fresh
    /// object.</b> That one decision is what makes "open a notebook and save it untouched" produce
    /// no diff at all — measured across the whole fixture corpus, where it would otherwise produce
    /// fourteen enormous ones. A JObject remembers the order its keys were read in, so updating a
    /// field in place leaves it exactly where the previous tool put it; anything the model does not
    /// know about is never touched; and the file's own line endings and trailing newline are
    /// remembered on the document and restored here. Colab writes CRLF and its own key order,
    /// Jupyter writes LF and a different one, and neither is wrong.
    /// </para>
    ///
    /// <para>
    /// Cells the IDE created have no original JSON, so they are written in nbformat's canonical key
    /// order — which is what Jupyter would have written for a new cell anyway.
    /// </para>
    /// </summary>
    public static class NotebookWriter
    {
        /// <summary>Serialise to the exact text that should land on disk.</summary>
        public static string Write(NotebookDocument document)
        {
            if (document == null) throw new ArgumentNullException("document");

            JObject root = document.SourceObject ?? new JObject();
            bool fresh = document.SourceObject == null;

            if (fresh)
            {
                // nbformat's own order for a notebook nobody has written before.
                root["cells"] = WriteCells(document);
                root["metadata"] = document.Metadata ?? new JObject();
                root["nbformat"] = document.NbFormat <= 0 ? 4 : document.NbFormat;
                root["nbformat_minor"] = document.NbFormatMinor < 0 ? 5 : document.NbFormatMinor;
            }
            else
            {
                // In place: every assignment replaces a value and keeps its position.
                root["cells"] = WriteCells(document);
                if (document.Metadata != null) root["metadata"] = document.Metadata;
                root["nbformat"] = document.NbFormat <= 0 ? 4 : document.NbFormat;
                root["nbformat_minor"] = document.NbFormatMinor < 0 ? 5 : document.NbFormatMinor;
            }

            var text = new StringBuilder();
            using (var stringWriter = new StringWriter(text))
            using (var writer = new JsonTextWriter(stringWriter)
            {
                Formatting = Formatting.Indented,
                Indentation = 2,
                IndentChar = ' ',
            })
            {
                root.WriteTo(writer);
            }

            string newLine = string.IsNullOrEmpty(document.NewLine) ? "\n" : document.NewLine;
            if (document.EndsWithNewLine) text.Append('\n');

            string json = text.ToString();
            // JsonTextWriter emits Environment.NewLine; normalise to LF once, then to the file's own.
            json = json.Replace("\r\n", "\n");
            return newLine == "\n" ? json : json.Replace("\n", newLine);
        }

        /// <summary>
        /// Save to <paramref name="path"/>. The write goes to a temp file in the same directory and
        /// is then moved into place: a notebook is often the only copy of hours of work, and a
        /// process killed halfway through a direct write leaves a truncated file that neither
        /// Jupyter nor this IDE can open.
        /// </summary>
        public static void WriteFile(NotebookDocument document, string path)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required", "path");

            string json = Write(document);
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

            string temporary = path + ".helix-tmp";
            var utf8NoBom = new UTF8Encoding(false);
            // WriteAllText, not a StreamWriter with AutoFlush: the newline style is already in the
            // string and must not be translated again on the way out.
            File.WriteAllText(temporary, json, utf8NoBom);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            catch (IOException)
            {
                // A file locked by another process (Jupyter has it open, a sync client is scanning
                // it): fall back to writing in place rather than leaving the edit unsaved, and let
                // the exception out if even that fails.
                File.WriteAllText(path, json, utf8NoBom);
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            }
        }

        private static JArray WriteCells(NotebookDocument document)
        {
            var cells = new JArray();
            bool wantIds = document.NbFormat > 4 || (document.NbFormat == 4 && document.NbFormatMinor >= 5);

            foreach (NotebookCell cell in document.Cells)
            {
                JObject cellObject = cell.SourceObject ?? NewCellObject(cell, wantIds);
                cellObject["cell_type"] = KindToken(cell.Kind);

                // nbformat 4.5 requires a cell id; 4.0 forbids one, and a 4.0 file carrying one
                // fails `jupyter nbconvert`'s validation.
                if (wantIds && !string.IsNullOrEmpty(cell.Id)) cellObject["id"] = cell.Id;
                else if (!wantIds) cellObject.Remove("id");

                cellObject["metadata"] = cell.Metadata ?? new JObject();
                // Only rewrite text the user actually changed. A line array that does not survive
                // join/split unchanged (a trailing empty element, which Colab writes) is left
                // exactly as it was found rather than "corrected" into a diff.
                if (cell.SourceChanged) cellObject["source"] = SplitLines(cell.Source);

                if (cell.Kind == NotebookCellKind.Code)
                {
                    cellObject["execution_count"] = cell.ExecutionCount.HasValue
                        ? (JToken)cell.ExecutionCount.Value
                        : JValue.CreateNull();
                    cellObject["outputs"] = WriteOutputs(cell);
                }
                else
                {
                    // A code cell the user turned into markdown must not keep either field: they
                    // are invalid on a markdown cell and nbformat validation rejects them.
                    cellObject.Remove("execution_count");
                    cellObject.Remove("outputs");
                }

                cells.Add(cellObject);
            }
            return cells;
        }

        /// <summary>nbformat's canonical key order, for a cell that has no history to preserve.</summary>
        private static JObject NewCellObject(NotebookCell cell, bool wantIds)
        {
            var cellObject = new JObject();
            cellObject["cell_type"] = KindToken(cell.Kind);
            if (wantIds && !string.IsNullOrEmpty(cell.Id)) cellObject["id"] = cell.Id;
            if (cell.Kind == NotebookCellKind.Code) cellObject["execution_count"] = JValue.CreateNull();
            cellObject["metadata"] = new JObject();
            if (cell.Kind == NotebookCellKind.Code) cellObject["outputs"] = new JArray();
            cellObject["source"] = new JArray();
            return cellObject;
        }

        private static JArray WriteOutputs(NotebookCell cell)
        {
            var outputs = new JArray();
            foreach (NotebookOutput output in cell.Outputs)
            {
                JObject outputObject = output.SourceObject ?? new JObject();
                outputObject["output_type"] = OutputTypeToken(output);

                switch (output.Kind)
                {
                    case NotebookOutputKind.Stream:
                        outputObject["name"] = output.StreamName ?? "stdout";
                        outputObject["text"] = SplitLines(output.Text);
                        break;

                    case NotebookOutputKind.Error:
                        outputObject["ename"] = output.ErrorName ?? string.Empty;
                        outputObject["evalue"] = output.ErrorValue ?? string.Empty;
                        var traceback = new JArray();
                        foreach (string line in output.Traceback) traceback.Add(line);
                        outputObject["traceback"] = traceback;
                        break;

                    default:
                        if (output.Kind == NotebookOutputKind.ExecuteResult)
                        {
                            outputObject["execution_count"] = output.ExecutionCount.HasValue
                                ? (JToken)output.ExecutionCount.Value
                                : JValue.CreateNull();
                        }
                        outputObject["data"] = WriteMimeBundle(output, outputObject["data"] as JObject);
                        if (outputObject["metadata"] == null || output.Metadata != null)
                            outputObject["metadata"] = output.Metadata ?? new JObject();
                        if (output.Kind == NotebookOutputKind.Unknown && !string.IsNullOrEmpty(output.Text))
                            outputObject["text"] = SplitLines(output.Text);
                        break;
                }
                outputs.Add(outputObject);
            }
            return outputs;
        }

        /// <summary>
        /// The mime bundle, patched onto the one that was read so each mime keeps its position and
        /// its on-disk shape — a payload that arrived as a line array goes back as a line array, one
        /// that arrived as a single string stays one string.
        /// </summary>
        private static JObject WriteMimeBundle(NotebookOutput output, JObject existing)
        {
            var data = existing ?? new JObject();

            // A mime the model no longer has was removed by an edit; drop it rather than resurrect.
            var stale = new List<string>();
            foreach (KeyValuePair<string, JToken> pair in data)
                if (!output.Data.ContainsKey(pair.Key)) stale.Add(pair.Key);
            foreach (string mime in stale) data.Remove(mime);

            foreach (KeyValuePair<string, string> pair in output.Data)
            {
                string mime = pair.Key;
                string payload = pair.Value ?? string.Empty;

                JToken structured = TryParseStructured(mime, payload);
                if (structured != null) { data[mime] = structured; continue; }

                if (IsBinaryMime(mime))
                {
                    // Base64 stays one string: splitting it into "lines" is legal but no writer does
                    // it, and it turns an unchanged image into an enormous diff.
                    data[mime] = payload;
                    continue;
                }

                // Keep a scalar that was read as a scalar; only text that WAS a line array, or that
                // is new, is written as one.
                JToken previous = data[mime];
                data[mime] = previous != null && previous.Type == JTokenType.String
                    ? (JToken)payload
                    : SplitLines(payload);
            }
            return data;
        }

        /// <summary>
        /// A mime whose payload the reader re-serialised from an object/array goes back as JSON, not
        /// as a string containing JSON — otherwise application/json outputs and the Colab bundles
        /// change type on every save.
        /// </summary>
        private static JToken TryParseStructured(string mime, string payload)
        {
            if (string.IsNullOrEmpty(payload)) return null;
            char first = payload[0];
            if (first != '{' && first != '[') return null;
            bool looksStructured = mime.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0
                                || mime.StartsWith("application/vnd.", StringComparison.OrdinalIgnoreCase);
            if (!looksStructured) return null;
            try { return JToken.Parse(payload); }
            catch (JsonException) { return null; }
        }

        private static bool IsBinaryMime(string mime)
        {
            if (string.IsNullOrEmpty(mime)) return false;
            if (Array.IndexOf(NotebookOutput.ImageMimes, mime) >= 0) return true;
            return mime.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// "a\nb\n" → ["a\n", "b\n"]; "a\nb" → ["a\n", "b"]. The trailing newline belongs to the
        /// line before it, and a text that ends without one must NOT gain an empty last element —
        /// that is the difference between a clean save and a one-line diff on every cell.
        /// </summary>
        internal static JArray SplitLines(string text)
        {
            var array = new JArray();
            if (string.IsNullOrEmpty(text)) return array;

            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                array.Add(text.Substring(start, i - start + 1));
                start = i + 1;
            }
            if (start < text.Length) array.Add(text.Substring(start));
            return array;
        }

        private static string KindToken(NotebookCellKind kind)
        {
            switch (kind)
            {
                case NotebookCellKind.Markdown: return "markdown";
                case NotebookCellKind.Raw: return "raw";
                default: return "code";
            }
        }

        private static string OutputTypeToken(NotebookOutput output)
        {
            switch (output.Kind)
            {
                case NotebookOutputKind.Stream: return "stream";
                case NotebookOutputKind.ExecuteResult: return "execute_result";
                case NotebookOutputKind.DisplayData: return "display_data";
                case NotebookOutputKind.Error: return "error";
                default: return output.RawOutputType ?? "display_data";
            }
        }
    }
}
