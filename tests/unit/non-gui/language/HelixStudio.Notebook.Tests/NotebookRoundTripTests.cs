using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HelixPythonNamespace.Notebooks;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Reading and writing real `.ipynb` files.
    ///
    /// The round-trip claim is deliberately strict: read → write → read must be IDENTICAL JSON,
    /// because the alternative is an editor that silently rewrites a colleague's file the moment it
    /// opens it.
    /// </summary>
    [TestClass]
    public class NotebookRoundTripTests
    {
        private static JObject Parse(string json) { return JObject.Parse(json); }

        // ------------------------------------------------------------------ the shapes in the wild

        [TestMethod]
        public void SourceIsAcceptedAsEitherALineArrayOrAString()
        {
            const string asArray = "{\"cells\":[{\"cell_type\":\"code\",\"source\":[\"a = 1\\n\",\"b = 2\"]}],"
                                 + "\"metadata\":{},\"nbformat\":4,\"nbformat_minor\":5}";
            const string asString = "{\"cells\":[{\"cell_type\":\"code\",\"source\":\"a = 1\\nb = 2\"}],"
                                  + "\"metadata\":{},\"nbformat\":4,\"nbformat_minor\":5}";

            Assert.AreEqual("a = 1\nb = 2", NotebookReader.Read(asArray).Document.Cells[0].Source);
            Assert.AreEqual("a = 1\nb = 2", NotebookReader.Read(asString).Document.Cells[0].Source,
                "several tools write source as one string, and it is legal");
        }

        [TestMethod]
        public void MultiLineTextIsWrittenBackAsLines_SoADiffStaysReadable()
        {
            var notebook = new NotebookDocument();
            notebook.InsertCell(0, NotebookCellKind.Code, "import os\nprint(os.name)\n");

            var cells = (JArray)Parse(NotebookWriter.Write(notebook))["cells"];
            var source = cells[0]["source"] as JArray;

            Assert.IsNotNull(source, "source must be an array of lines, not one string");
            Assert.AreEqual(2, source.Count);
            Assert.AreEqual("import os\n", source[0].ToString(), "the newline belongs to its own line");
            Assert.AreEqual("print(os.name)\n", source[1].ToString());
        }

        [TestMethod]
        public void ATextWithNoTrailingNewline_DoesNotGainAnEmptyLastLine()
        {
            var notebook = new NotebookDocument();
            notebook.InsertCell(0, NotebookCellKind.Code, "x = 1");
            var source = (JArray)((JArray)Parse(NotebookWriter.Write(notebook))["cells"])[0]["source"];

            Assert.AreEqual(1, source.Count, "an empty trailing element is a diff on every save");
            Assert.AreEqual("x = 1", source[0].ToString());
        }

        [TestMethod]
        public void SomethingThatIsNotANotebook_IsRefusedWithTheReason()
        {
            NotebookReadResult notJson = NotebookReader.Read("def main():\n    pass\n");
            Assert.IsFalse(notJson.Succeeded);
            StringAssert.Contains(notJson.Error, "JSON");

            NotebookReadResult jsonButNotANotebook = NotebookReader.Read("{\"name\": \"package.json\"}");
            Assert.IsFalse(jsonButNotANotebook.Succeeded);
            StringAssert.Contains(jsonButNotANotebook.Error, "cells");

            Assert.IsFalse(NotebookReader.Read(string.Empty).Succeeded,
                "an empty file is not an empty notebook");
        }

        [TestMethod]
        public void ADataFrameOutputKeepsBothItsHtmlAndItsPlainText()
        {
            // The single most common output in notebooks, and the one where picking a
            // representation at read time would throw the other away.
            const string json = "{\"cells\":[{\"cell_type\":\"code\",\"execution_count\":3,\"source\":[\"df\"],"
                + "\"outputs\":[{\"output_type\":\"execute_result\",\"execution_count\":3,"
                + "\"data\":{\"text/plain\":[\"   a  b\\n\",\"0  1  2\"],"
                + "\"text/html\":[\"<table>\\n\",\"</table>\"]},\"metadata\":{}}]}],"
                + "\"metadata\":{},\"nbformat\":4,\"nbformat_minor\":5}";

            NotebookOutput output = NotebookReader.Read(json).Document.Cells[0].Outputs[0];

            Assert.AreEqual(NotebookOutputKind.ExecuteResult, output.Kind);
            Assert.AreEqual(3, output.ExecutionCount);
            Assert.AreEqual("   a  b\n0  1  2", output.Data["text/plain"]);
            Assert.AreEqual("<table>\n</table>", output.Data["text/html"]);
            Assert.AreEqual("   a  b\n0  1  2", output.PlainText(), "plain text is what the author saw");
        }

        [TestMethod]
        public void AnImageOutputSurvivesAsBase64()
        {
            const string png = "iVBORw0KGgoAAAANSUhEUg==";
            string json = "{\"cells\":[{\"cell_type\":\"code\",\"source\":[\"plot()\"],"
                + "\"outputs\":[{\"output_type\":\"display_data\",\"data\":{\"image/png\":\"" + png + "\","
                + "\"text/plain\":[\"<Figure>\"]},\"metadata\":{}}]}],"
                + "\"metadata\":{},\"nbformat\":4,\"nbformat_minor\":5}";

            NotebookOutput output = NotebookReader.Read(json).Document.Cells[0].Outputs[0];
            string mime;
            Assert.AreEqual(png, output.ImageBase64(out mime));
            Assert.AreEqual("image/png", mime);

            // …and comes back out as one string, not chopped into "lines".
            JToken data = ((JArray)Parse(NotebookWriter.Write(NotebookReader.Read(json).Document))["cells"])[0]["outputs"][0]["data"];
            Assert.AreEqual(JTokenType.String, data["image/png"].Type);
        }

        [TestMethod]
        public void AnErrorOutputKeepsItsTraceback()
        {
            const string json = "{\"cells\":[{\"cell_type\":\"code\",\"source\":[\"1/0\"],"
                + "\"outputs\":[{\"output_type\":\"error\",\"ename\":\"ZeroDivisionError\","
                + "\"evalue\":\"division by zero\",\"traceback\":[\"Traceback\",\"ZeroDivisionError\"]}]}],"
                + "\"metadata\":{},\"nbformat\":4,\"nbformat_minor\":5}";

            NotebookOutput output = NotebookReader.Read(json).Document.Cells[0].Outputs[0];

            Assert.IsTrue(output.IsError);
            Assert.AreEqual("ZeroDivisionError", output.ErrorName);
            Assert.AreEqual(2, output.Traceback.Count);
            StringAssert.Contains(output.PlainText(), "ZeroDivisionError");
        }

        [TestMethod]
        public void CellIdsAreWrittenOnlyWhenTheFormatAllowsThem()
        {
            var modern = new NotebookDocument { NbFormat = 4, NbFormatMinor = 5 };
            modern.InsertCell(0, NotebookCellKind.Code, "x").Id = "abc123";
            StringAssert.Contains(NotebookWriter.Write(modern), "\"id\": \"abc123\"");

            // nbformat 4.0 — which is what every notebook in notebooks is — FORBIDS cell ids, and
            // a file carrying one fails `jupyter nbconvert`'s validation.
            var legacy = new NotebookDocument { NbFormat = 4, NbFormatMinor = 0 };
            legacy.InsertCell(0, NotebookCellKind.Code, "x").Id = "abc123";
            Assert.IsFalse(NotebookWriter.Write(legacy).Contains("\"id\""),
                "a 4.0 notebook must not gain a cell id it is not allowed to have");
        }

        [TestMethod]
        public void SavingWritesThroughATemporaryFile_SoAKilledSaveCannotTruncateTheNotebook()
        {
            string directory = Path.Combine(Path.GetTempPath(), "helix-nb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "work.ipynb");
                var notebook = new NotebookDocument();
                notebook.InsertCell(0, NotebookCellKind.Markdown, "# Title\n");

                NotebookWriter.WriteFile(notebook, path);

                Assert.IsTrue(File.Exists(path));
                Assert.AreEqual(0, Directory.GetFiles(directory, "*.helix-tmp").Length,
                    "the temporary file is moved into place, never left behind");
                Assert.AreEqual("# Title\n", NotebookReader.ReadFile(path).Document.Cells[0].Source);
            }
            finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
        }
    }
}
