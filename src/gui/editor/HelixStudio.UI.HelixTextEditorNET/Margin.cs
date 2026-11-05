using System;

namespace HelixTextEditorNET
{
    // One margin's configuration. API-compatible with ScintillaNET 3.6.3's Margin.
    public class Margin
    {
        private readonly Scintilla _scintilla;
        private readonly int _index;

        internal Margin(Scintilla scintilla, int index)
        {
            _scintilla = scintilla;
            _index = index;
        }

        public int Index
        {
            get { return _index; }
        }

        public MarginType Type
        {
            get { return (MarginType)_scintilla.ViewModel.Margins[_index].Type; }
            set
            {
                _scintilla.ViewModel.Margins[_index].Type = (int)value;
                _scintilla.InvalidateView();
            }
        }

        public int Width
        {
            get { return _scintilla.ViewModel.Margins[_index].Width; }
            set
            {
                _scintilla.ViewModel.Margins[_index].Width = Math.Max(0, value);
                _scintilla.InvalidateView();
            }
        }

        public uint Mask
        {
            get { return _scintilla.ViewModel.Margins[_index].Mask; }
            set
            {
                _scintilla.ViewModel.Margins[_index].Mask = value;
                _scintilla.InvalidateView();
            }
        }

        public bool Sensitive
        {
            get { return _scintilla.ViewModel.Margins[_index].Sensitive; }
            set { _scintilla.ViewModel.Margins[_index].Sensitive = value; }
        }
    }

    public class MarginCollection
    {
        private readonly Scintilla _scintilla;
        private readonly Margin[] _margins;

        internal MarginCollection(Scintilla scintilla)
        {
            _scintilla = scintilla;
            _margins = new Margin[Rendering.ViewStyleModel.MaxMargins];
        }

        public Margin this[int index]
        {
            get
            {
                if (index < 0) index = 0;
                if (index >= _margins.Length) index = _margins.Length - 1;
                if (_margins[index] == null) _margins[index] = new Margin(_scintilla, index);
                return _margins[index];
            }
        }

        public int Count
        {
            get { return _margins.Length; }
        }
    }
}
