using System;
using System.Drawing;

namespace HelixTextEditorNET
{
    // One indicator's appearance. API-compatible with ScintillaNET 3.6.3's Indicator.
    public class Indicator
    {
        private readonly Scintilla _scintilla;
        private readonly int _index;

        internal Indicator(Scintilla scintilla, int index)
        {
            _scintilla = scintilla;
            _index = index;
        }

        public int Index
        {
            get { return _index; }
        }

        public IndicatorStyle Style
        {
            get { return (IndicatorStyle)_scintilla.ViewModel.Indicators[_index].Style; }
            set
            {
                _scintilla.ViewModel.Indicators[_index].Style = (int)value;
                _scintilla.InvalidateView();
            }
        }

        public Color ForeColor
        {
            get { return _scintilla.ViewModel.Indicators[_index].ForeColor; }
            set
            {
                _scintilla.ViewModel.Indicators[_index].ForeColor = value;
                _scintilla.InvalidateView();
            }
        }

        public int Alpha
        {
            get { return _scintilla.ViewModel.Indicators[_index].Alpha; }
            set
            {
                _scintilla.ViewModel.Indicators[_index].Alpha = value;
                _scintilla.InvalidateView();
            }
        }

        public int OutlineAlpha
        {
            get { return _scintilla.ViewModel.Indicators[_index].OutlineAlpha; }
            set
            {
                _scintilla.ViewModel.Indicators[_index].OutlineAlpha = value;
                _scintilla.InvalidateView();
            }
        }

        public bool Under
        {
            get { return _scintilla.ViewModel.Indicators[_index].Under; }
            set
            {
                _scintilla.ViewModel.Indicators[_index].Under = value;
                _scintilla.InvalidateView();
            }
        }

        // Start/end of the indicator run containing `position`.
        public int Start(int position)
        {
            return _scintilla.Document.IndicatorStart(_index, position);
        }

        public int End(int position)
        {
            return _scintilla.Document.IndicatorEnd(_index, position);
        }

        public int ValueAt(int position)
        {
            return _scintilla.Document.IndicatorValueAt(_index, position);
        }
    }

    public class IndicatorCollection
    {
        private readonly Scintilla _scintilla;
        private readonly Indicator[] _indicators;

        internal IndicatorCollection(Scintilla scintilla)
        {
            _scintilla = scintilla;
            _indicators = new Indicator[Rendering.ViewStyleModel.MaxIndicators];
        }

        public Indicator this[int index]
        {
            get
            {
                if (index < 0) index = 0;
                if (index >= _indicators.Length) index = _indicators.Length - 1;
                if (_indicators[index] == null) _indicators[index] = new Indicator(_scintilla, index);
                return _indicators[index];
            }
        }

        public int Count
        {
            get { return _indicators.Length; }
        }
    }
}
