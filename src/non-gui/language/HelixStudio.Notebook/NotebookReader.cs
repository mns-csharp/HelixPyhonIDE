using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Notebooks
{
    /// <summary>Why a file could not be read as a notebook. Never a bare "failed".</summary>
    public sealed class NotebookReadResult
    {
        private NotebookReadResult(NotebookDocument document, string error)
        {
            Document = document;
            Error = error;
        }

        public NotebookDocument Document { get; private set; }
        public string Error { get; private set; }
        public bool Succeeded { get { return Document != null; } }

        public static NotebookReadResult Ok(NotebookDocument document) { return new NotebookReadResult(document, null); }
        public static NotebookReadResult Failed(string error) { return new NotebookReadResult(null, error); }
    }

    /// <summary>
    /// `.ipynb` on disk → <see cref="NotebookDocument"/>.
    ///
    /// Tolerant on purpose. Notebooks in the wild are written by Colab, Jupyter Lab, nbconvert,
    /// papermill and a long tail of scripts; fields are missing, `source` is sometimes a string and
    /// sometimes an array, `execution_count` is sometimes a float, and outputs carry mime bundles
    /// nobody documented. Every one of those parses here rather than throwing, because the
    /// alternative is an IDE that cannot open the user's actual file.
    ///
    /// What it will NOT do is guess about structure: a file whose `cells` is not an array, or whose
    /// JSON does not parse, is reported as an error with the reason, never as an empty notebook.
    /// </summary>
    public static class NotebookReader
    {
        public static NotebookReadResult ReadFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return NotebookReadResult.Failed("no path given");
            try
            {
                // Detected encoding, BOM tolerated: Colab and Windows tooling both produce them.
                string json = File.ReadAllText(path, Encoding.UTF8);
                NotebookReadResult result = Read(json);
                if (result.Succeeded) result.Document.FilePath = path;
                return result;
            }
            catch (IOException ex) { return NotebookReadResult.Failed(ex.Message); }
            catch (UnauthorizedAccessException ex) { return NotebookReadResult.Failed(ex.Message); }
            catch (NotSupportedException ex) { return NotebookReadResult.Failed(ex.Message); }
        }

        public static NotebookReadResult Read(string json)
        {
            if (string.IsNullOrEmpty(json)) return NotebookReadResult.Failed("the file is empty");

            JObject root;
            try
            {
                // DateParseHandling.None: a cell whose source or metadata contains something that
                // looks like a date must come back as the string it was, not as a DateTime that
                // then round-trips to a DIFFERENT string.
                using (var reader = new JsonTextReader(new StringReader(json))
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Double,
                })
                {
                    root = JObject.Load(reader);
                }
            }
            catch (JsonException ex) { return NotebookReadResult.Failed("not valid JSON: " + ex.Message); }

            var cellsToken = root["cells"] as JArray;
            if (cellsToken == null)
            {
                // A .py or a JSON file that is not a notebook lands here. Say which it is.
                return NotebookReadResult.Failed(
                    root["nbformat"] == null
                        ? "this file has no `cells` array — it is JSON, but not a notebook"
                        : "this notebook's `cells` is not an array");
            }

            var document = new NotebookDocument
            {
                NbFormat = ReadInt(root["nbformat"], 4),
                NbFormatMinor = ReadInt(root["nbformat_minor"], 5),
                Metadata = root["metadata"] as JObject ?? new JObject(),
                SourceObject = root,
                NewLine = DetectNewLine(json),
                EndsWithNewLine = json.Length > 0 && json[json.Length - 1] == '\n',
            };

            foreach (JToken cellToken in cellsToken)
            {
                var cellObject = cellToken as JObject;
                if (cellObject == null) continue;   // a null or scalar in `cells` is not a cell
                document.Cells.Add(ReadCell(cellObject));
            }
            return NotebookReadResult.Ok(document);
        }

        private static NotebookCell ReadCell(JObject cellObject)
        {
            var cell = new NotebookCell(ParseKind(AsString(cellObject["cell_type"])))
            {
                Source = JoinLines(cellObject["source"]),
                OriginalSource = JoinLines(cellObject["source"]),
                Id = AsString(cellObject["id"]),
                Metadata = cellObject["metadata"] as JObject ?? new JObject(),
                SourceObject = cellObject,
            };

            JToken count = cellObject["execution_count"];
            if (count != null && count.Type != JTokenType.Null)
            {
                int parsed;
                if (TryReadInt(count, out parsed)) cell.ExecutionCount = parsed;
            }

            var outputs = cellObject["outputs"] as JArray;
            if (outputs != null)
            {
                foreach (JToken outputToken in outputs)
                {
                    var outputObject = outputToken as JObject;
                    if (outputObject != null) cell.Outputs.Add(ReadOutput(outputObject));
                }
            }
            return cell;
        }

        private static NotebookOutput ReadOutput(JObject outputObject)
        {
            string type = AsString(outputObject["output_type"]);
            var output = new NotebookOutput
            {
                Kind = ParseOutputKind(type),
                RawOutputType = type,
                Metadata = outputObject["metadata"] as JObject,
                SourceObject = outputObject,
            };

            switch (output.Kind)
            {
                case NotebookOutputKind.Stream:
                    output.StreamName = AsString(outputObject["name"]) ?? "stdout";
                    output.Text = JoinLines(outputObject["text"]);
                    break;

                case NotebookOutputKind.Error:
                    output.ErrorName = AsString(outputObject["ename"]);
                    output.ErrorValue = AsString(outputObject["evalue"]);
                    var traceback = outputObject["traceback"] as JArray;
                    if (traceback != null)
                        foreach (JToken line in traceback) output.Traceback.Add(AsString(line) ?? string.Empty);
                    break;

                default:
                    ReadMimeBundle(outputObject["data"] as JObject, output.Data);
                    int count;
                    if (TryReadInt(outputObject["execution_count"], out count)) output.ExecutionCount = count;
                    // An unrecognised output_type may still carry `text`; keeping it means the user
                    // sees SOMETHING rather than a blank block.
                    if (output.Kind == NotebookOutputKind.Unknown && output.Data.Count == 0)
                        output.Text = JoinLines(outputObject["text"]);
                    break;
            }
            return output;
        }

        /// <summary>
        /// mime → payload. Text mimes arrive as a line array and are joined; a binary mime
        /// (image/png) is a single base64 string and is kept exactly as written. A mime whose value
        /// is a JSON object (application/json, the Colab bundles) is re-serialised rather than
        /// dropped, so nothing about the output is invented and nothing is lost.
        /// </summary>
        private static void ReadMimeBundle(JObject data, IDictionary<string, string> into)
        {
            if (data == null) return;
            foreach (KeyValuePair<string, JToken> pair in data)
            {
                if (pair.Value == null) continue;
                if (pair.Value.Type == JTokenType.Object || pair.Value.Type == JTokenType.Array)
                {
                    // An array here is a line array for a text mime; an object is structured data.
                    into[pair.Key] = pair.Value.Type == JTokenType.Array && IsStringArray((JArray)pair.Value)
                        ? JoinLines(pair.Value)
                        : pair.Value.ToString(Formatting.None);
                }
                else
                {
                    into[pair.Key] = pair.Value.ToString();
                }
            }
        }

        private static bool IsStringArray(JArray array)
        {
            foreach (JToken item in array)
                if (item.Type != JTokenType.String) return false;
            return true;
        }

        /// <summary>
        /// nbformat writes multi-line text as an array of lines that each keep their trailing "\n".
        /// A plain string is equally legal and several tools emit it, so both are accepted.
        /// </summary>
        internal static string JoinLines(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return string.Empty;
            if (token.Type == JTokenType.String) return token.ToString();

            var array = token as JArray;
            if (array == null) return token.ToString();

            var builder = new StringBuilder();
            foreach (JToken line in array) builder.Append(line == null ? string.Empty : line.ToString());
            return builder.ToString();
        }

        /// <summary>
        /// LF or CRLF, whichever the file actually used. Decided on the FIRST newline: a notebook
        /// with mixed endings (an editor appended to a Colab file) is written back with the one it
        /// mostly has, which is the smallest diff available.
        /// </summary>
        private static string DetectNewLine(string json)
        {
            const char lf = (char)10;
            const char cr = (char)13;
            int index = json.IndexOf(lf);
            if (index < 0) return lf.ToString();
            return index > 0 && json[index - 1] == cr ? cr.ToString() + lf : lf.ToString();
        }

        private static NotebookCellKind ParseKind(string cellType)
        {
            if (string.Equals(cellType, "markdown", StringComparison.OrdinalIgnoreCase)) return NotebookCellKind.Markdown;
            if (string.Equals(cellType, "raw", StringComparison.OrdinalIgnoreCase)) return NotebookCellKind.Raw;
            return NotebookCellKind.Code;
        }

        private static NotebookOutputKind ParseOutputKind(string outputType)
        {
            switch (outputType)
            {
                case "stream": return NotebookOutputKind.Stream;
                case "execute_result": return NotebookOutputKind.ExecuteResult;
                case "display_data": return NotebookOutputKind.DisplayData;
                case "error": return NotebookOutputKind.Error;
                // nbformat 3 spelling, still found in files converted forward by old tools.
                case "pyout": return NotebookOutputKind.ExecuteResult;
                case "pyerr": return NotebookOutputKind.Error;
                default: return NotebookOutputKind.Unknown;
            }
        }

        private static string AsString(JToken token)
        {
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static int ReadInt(JToken token, int fallback)
        {
            int value;
            return TryReadInt(token, out value) ? value : fallback;
        }

        private static bool TryReadInt(JToken token, out int value)
        {
            value = 0;
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type == JTokenType.Integer) { value = token.Value<int>(); return true; }
            // Some writers emit 3.0 for an execution count.
            if (token.Type == JTokenType.Float) { value = (int)token.Value<double>(); return true; }
            return int.TryParse(token.ToString(), out value);
        }
    }
}
