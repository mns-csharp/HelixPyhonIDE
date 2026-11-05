using System;
using System.IO;
using HelixPythonNamespace.Notebooks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Creating a notebook — the other end of the engine from <see cref="NotebookReader"/>.
    ///
    /// The IDE could open a `.ipynb` and could not make one. The Solution Explorer's "Add File..."
    /// forces a `.py` extension onto whatever the user types, so a name ending in `.ipynb` came
    /// back as a Python module, and nothing else offered to create one at all.
    ///
    /// The bar for these is not "a file appeared". A notebook the IDE writes is a file the user
    /// will hand to Jupyter, to Colab, and to a git diff, so what is asserted is that the file
    /// READS BACK as a notebook, carries the metadata those tools look for, and is stable across
    /// saves.
    /// </summary>
    [TestClass]
    public class NotebookCreationTests
    {
        private static JObject Parse(string json) { return JObject.Parse(json); }

        [TestMethod]
        public void ANewNotebook_ReadsBackAsANotebookWithOneEmptyCodeCell()
        {
            NotebookReadResult result = NotebookReader.Read(NotebookWriter.Write(NotebookFactory.CreateNew()));

            Assert.IsTrue(result.Succeeded, result.Error);
            Assert.AreEqual(1, result.Document.Cells.Count, "Jupyter and Colab both start you with one cell");
            Assert.AreEqual(NotebookCellKind.Code, result.Document.Cells[0].Kind);
            Assert.AreEqual(string.Empty, result.Document.Cells[0].Source);
        }

        /// <summary>
        /// Without a kernelspec and a `language_info`, every reader downstream has to guess what
        /// the notebook is — including this one's own <see cref="NotebookDocument.IsPython"/>,
        /// which decides whether the IDE will offer to run it.
        /// </summary>
        [TestMethod]
        public void ANewNotebook_DeclaresItselfPython()
        {
            NotebookDocument document = NotebookReader.Read(NotebookWriter.Write(NotebookFactory.CreateNew())).Document;

            Assert.IsTrue(document.IsPython);
            Assert.AreEqual("python3", document.KernelName);
            Assert.AreEqual("python", document.LanguageName);
        }

        [TestMethod]
        public void ThePythonVersionIsWrittenWhenKnown_AndOmittedWhenNotGuessed()
        {
            JObject known = Parse(NotebookWriter.Write(NotebookFactory.CreateNew("3.11")));
            Assert.AreEqual("3.11", known["metadata"]["language_info"]["version"].ToString());

            JObject unknown = Parse(NotebookWriter.Write(NotebookFactory.CreateNew(null)));
            Assert.IsNull(unknown["metadata"]["language_info"]["version"],
                "a wrong version is worse than an absent one — the next tool believes it");
        }

        /// <summary>
        /// nbformat 4.5 REQUIRES an id on every cell, and every cell in a new notebook is one this
        /// IDE created. Nothing minted ids before this: a notebook saved after "+ Code" carried an
        /// id-less cell, which `jupyter nbconvert` refuses to validate.
        /// </summary>
        [TestMethod]
        public void EveryCellOfANewNotebook_HasAnId()
        {
            var cells = (JArray)Parse(NotebookWriter.Write(NotebookFactory.CreateNew()))["cells"];

            Assert.AreEqual(4, (int)Parse(NotebookWriter.Write(NotebookFactory.CreateNew()))["nbformat"]);
            Assert.AreEqual(5, (int)Parse(NotebookWriter.Write(NotebookFactory.CreateNew()))["nbformat_minor"]);
            foreach (JToken cell in cells)
            {
                JToken id = cell["id"];
                Assert.IsNotNull(id, "nbformat 4.5 requires a cell id");
                Assert.AreNotEqual(string.Empty, id.ToString());
            }
        }

        [TestMethod]
        public void ACellAddedInTheIde_AlsoGetsAnId()
        {
            var document = new NotebookDocument { NbFormat = 4, NbFormatMinor = 5 };
            NotebookCell added = document.InsertCell(0, NotebookCellKind.Code, "x = 1\n");

            Assert.IsFalse(string.IsNullOrEmpty(added.Id), "'+ Code' must produce a valid 4.5 cell");
            StringAssert.Contains(NotebookWriter.Write(document), "\"id\": \"" + added.Id + "\"");
        }

        /// <summary>
        /// The id is minted on insert, not on write. An id regenerated per save would mark the cell
        /// as deleted-and-recreated in every single diff — the exact class of noise the whole
        /// patch-in-place writer exists to avoid.
        /// </summary>
        [TestMethod]
        public void ACellsId_DoesNotChangeBetweenSaves()
        {
            NotebookDocument document = NotebookFactory.CreateNew();
            string first = Parse(NotebookWriter.Write(document))["cells"][0]["id"].ToString();
            string second = Parse(NotebookWriter.Write(document))["cells"][0]["id"].ToString();

            Assert.AreEqual(first, second);
        }

        [TestMethod]
        public void TwoNewNotebooks_DoNotShareACellId()
        {
            string a = Parse(NotebookWriter.Write(NotebookFactory.CreateNew()))["cells"][0]["id"].ToString();
            string b = Parse(NotebookWriter.Write(NotebookFactory.CreateNew()))["cells"][0]["id"].ToString();

            Assert.AreNotEqual(a, b);
        }

        /// <summary>
        /// The round-trip rule the whole engine is built on, applied to a file it created itself:
        /// write it, open it, save it untouched, and not one byte moves. If a created notebook did
        /// not satisfy this, the user's first commit after opening their own new notebook would be
        /// a diff.
        /// </summary>
        [TestMethod]
        public void ACreatedNotebook_SavesBackByteIdentically()
        {
            string directory = Path.Combine(Path.GetTempPath(), "helix-nbnew-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "Untitled.ipynb");
                NotebookWriter.WriteFile(NotebookFactory.CreateNew("3.12"), path);
                string asCreated = File.ReadAllText(path);

                NotebookReadResult reopened = NotebookReader.ReadFile(path);
                Assert.IsTrue(reopened.Succeeded, reopened.Error);
                NotebookWriter.WriteFile(reopened.Document, path);

                Assert.AreEqual(asCreated, File.ReadAllText(path),
                    "opening and saving a notebook the IDE just wrote must not move a byte");
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch (IOException) { }
            }
        }

        /// <summary>
        /// LF and a trailing newline. The corpus is CRLF because Colab wrote it, but a file this
        /// IDE authors has no such history to preserve, and LF is what every other tool writes.
        /// </summary>
        [TestMethod]
        public void ACreatedNotebook_UsesLfAndEndsWithANewline()
        {
            string text = NotebookWriter.Write(NotebookFactory.CreateNew());

            Assert.IsFalse(text.Contains("\r\n"), "a notebook this IDE authors is LF");
            Assert.IsTrue(text.EndsWith("\n"), "POSIX text files end with a newline");
        }
    }
}
