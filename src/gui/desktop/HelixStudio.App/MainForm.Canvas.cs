namespace HelixPythonNamespace.App
{
    using System;
    using System.IO;
    using HelixPythonNamespace.Core.IO;

    /// <summary>
    /// Opening a <c>*.canvas</c> file (Task 10i): the kind in its header picks the designer.
    ///
    /// <para>The whole file is read and checked here, BEFORE any designer tab opens or changes, so a
    /// damaged file never reaches a canvas: nothing is half-loaded and the design already on screen
    /// stays as it is.</para>
    ///
    /// <para>Dispatch table (kind → designer): the rows are registered through
    /// <see cref="HelixPythonNamespace.AddIns.IAddInHost.RegisterCanvasKind"/>, because the canvases
    /// are add-ins and this assembly must not reference them (book Chapter 20, §20.2.2). Today:
    /// <c>cg-mc</c> → the CG MC Designer Canvas add-in (Task 10i). A kind no loaded add-in
    /// registered is refused.</para>
    /// </summary>
    public partial class MainForm
    {
        internal void OpenCanvas(string path)
        {
            CanvasContent content;
            try { content = CanvasFile.Read(path); }
            catch (CanvasFileException ex) { RefuseCanvas(ex.Message); return; }
            catch (IOException ex) { RefuseCanvas("Could not open \"" + Path.GetFileName(path) + "\": " + ex.Message); return; }
            catch (UnauthorizedAccessException ex) { RefuseCanvas("Could not open \"" + Path.GetFileName(path) + "\": " + ex.Message); return; }

            foreach (LoadedAddIn addIn in _addIns.ToArray())
                if (addIn.Host.TryOpenCanvas(content.Kind, path)) return;

            RefuseCanvas(new CanvasFileException(Path.GetFileName(path), CanvasFile.UnknownKindReason(content.Kind)).Message);
        }

        /// <summary>Says why in the Output pane and the status bar. Never a message box: this runs from
        /// File > Open, the Solution Explorer and Windows Explorer, and a modal here would also stop
        /// every test that opens a file (book Chapter 4, §4.6.4).</summary>
        private void RefuseCanvas(string message)
        {
            AppendBuildOutputLine(message);
            if (_statusLabel != null) _statusLabel.Text = message;
        }
    }
}
