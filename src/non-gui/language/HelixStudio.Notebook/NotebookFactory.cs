using System;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Notebooks
{
    /// <summary>
    /// A brand-new `.ipynb` — the other end of <see cref="NotebookReader"/>.
    ///
    /// <para>
    /// Everything else in this engine exists to preserve what a previous tool wrote. A new notebook
    /// has no previous tool, so this is the one place that decides what HelixPython's own notebooks
    /// look like, and the answer is "what Jupyter would have written": nbformat 4.5, one empty code
    /// cell, a `python3` kernelspec, and `language_info.name = "python"`. A reader that sees
    /// anything else — a missing kernelspec, a language it does not know — has to start guessing,
    /// and <see cref="NotebookDocument.IsPython"/> is one of the things doing the guessing.
    /// </para>
    ///
    /// <para>
    /// <b>4.5, and therefore cell ids.</b> nbformat 4.5 REQUIRES an id on every cell, and every
    /// cell in a new notebook is one this IDE created, so a factory that did not mint ids would
    /// produce a file `jupyter nbconvert` refuses to validate — a notebook the user cannot hand to
    /// anyone. <see cref="NotebookDocument.InsertCell"/> mints them for the same reason; the writer
    /// still strips them from a 4.0 document, which forbids them.
    /// </para>
    /// </summary>
    public static class NotebookFactory
    {
        /// <summary>nbformat 4.5 — the version that carries cell ids, and what Jupyter writes today.</summary>
        public const int DefaultNbFormat = 4;
        public const int DefaultNbFormatMinor = 5;

        /// <summary>A new notebook with one empty code cell, ready to save.</summary>
        public static NotebookDocument CreateNew()
        {
            return CreateNew(null);
        }

        /// <summary>
        /// A new notebook with one empty code cell.
        ///
        /// <paramref name="pythonVersion"/> is written to `language_info.version` when the caller
        /// knows it — the shell resolves an interpreter for Run and can say "3.11". When it is
        /// null the key is OMITTED rather than guessed: a wrong version in a notebook's metadata
        /// is worse than an absent one, because the next tool believes it.
        /// </summary>
        public static NotebookDocument CreateNew(string pythonVersion)
        {
            var document = new NotebookDocument
            {
                NbFormat = DefaultNbFormat,
                NbFormatMinor = DefaultNbFormatMinor,
                // LF and a trailing newline: what every other tool writes, and what keeps the first
                // `git diff` after somebody else opens it empty.
                NewLine = "\n",
                EndsWithNewLine = true,
                Metadata = BuildMetadata(pythonVersion),
            };

            // One empty code cell, because an empty notebook has nowhere to type. Jupyter and Colab
            // both do this.
            document.InsertCell(0, NotebookCellKind.Code, string.Empty);
            return document;
        }

        /// <summary>
        /// A fresh nbformat 4.5 cell id: 8 characters of <c>[a-z0-9]</c>, which is the shape
        /// Jupyter mints and is well inside the spec's 1–64 characters of
        /// <c>[a-zA-Z0-9-_]</c>. Uniqueness is per notebook, and a GUID prefix is far more than
        /// that needs.
        /// </summary>
        public static string NewCellId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static JObject BuildMetadata(string pythonVersion)
        {
            var kernelspec = new JObject();
            kernelspec["display_name"] = "Python 3";
            kernelspec["language"] = "python";
            kernelspec["name"] = "python3";

            var languageInfo = new JObject();
            languageInfo["name"] = "python";
            if (!string.IsNullOrEmpty(pythonVersion)) languageInfo["version"] = pythonVersion;

            var metadata = new JObject();
            metadata["kernelspec"] = kernelspec;
            metadata["language_info"] = languageInfo;
            return metadata;
        }
    }
}
