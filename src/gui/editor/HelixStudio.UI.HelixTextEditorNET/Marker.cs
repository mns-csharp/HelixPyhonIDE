using System;
using System.Drawing;

namespace HelixTextEditorNET
{
    // One marker definition (symbol + colours), plus the folding marker index
    // constants. API-compatible with ScintillaNET 3.6.3's Marker class.
    public class Marker
    {
        public const int FolderEnd = 25;      // SC_MARKNUM_FOLDEREND
        public const int FolderOpenMid = 26;  // SC_MARKNUM_FOLDEROPENMID
        public const int FolderMidTail = 27;  // SC_MARKNUM_FOLDERMIDTAIL
        public const int FolderTail = 28;     // SC_MARKNUM_FOLDERTAIL
        public const int FolderSub = 29;      // SC_MARKNUM_FOLDERSUB
        public const int Folder = 30;         // SC_MARKNUM_FOLDER
        public const int FolderOpen = 31;     // SC_MARKNUM_FOLDEROPEN
        public const uint MaskFolders = 0xFE000000;  // SC_MASK_FOLDERS

        private readonly Scintilla _scintilla;
        private readonly int _index;

        internal Marker(Scintilla scintilla, int index)
        {
            _scintilla = scintilla;
            _index = index;
        }

        public int Index
        {
            get { return _index; }
        }

        public MarkerSymbol Symbol
        {
            get { return (MarkerSymbol)_scintilla.ViewModel.Markers[_index].Symbol; }
            set
            {
                _scintilla.ViewModel.Markers[_index].Symbol = (int)value;
                _scintilla.InvalidateView();
            }
        }

        public void SetForeColor(Color color)
        {
            _scintilla.ViewModel.Markers[_index].ForeColor = color;
            _scintilla.InvalidateView();
        }

        public void SetBackColor(Color color)
        {
            _scintilla.ViewModel.Markers[_index].BackColor = color;
            _scintilla.InvalidateView();
        }

        public void SetAlpha(int alpha)
        {
            _scintilla.ViewModel.Markers[_index].Alpha = alpha;
            _scintilla.InvalidateView();
        }
    }

    public class MarkerCollection
    {
        private readonly Scintilla _scintilla;
        private readonly Marker[] _markers;

        internal MarkerCollection(Scintilla scintilla)
        {
            _scintilla = scintilla;
            _markers = new Marker[Rendering.ViewStyleModel.MaxMarkers];
        }

        public Marker this[int index]
        {
            get
            {
                if (index < 0) index = 0;
                if (index >= _markers.Length) index = _markers.Length - 1;
                if (_markers[index] == null) _markers[index] = new Marker(_scintilla, index);
                return _markers[index];
            }
        }

        public int Count
        {
            get { return _markers.Length; }
        }
    }
}
