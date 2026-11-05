using System;
using System.Collections.Generic;
using System.Drawing;

namespace HelixTextEditorNET.Rendering
{
    // Appearance of one style index (the style byte stored per character).
    // Port of view/view_style.h Style.
    internal sealed class StyleSpec
    {
        public string FontName = "Consolas";
        public float SizeF = 10f;
        public Color ForeColor = Color.Black;
        public Color BackColor = Color.White;
        public bool Bold;
        public bool Italic;
        public bool Underline;

        public StyleSpec Clone()
        {
            return (StyleSpec)MemberwiseClone();
        }

        public void CopyFrom(StyleSpec other)
        {
            FontName = other.FontName;
            SizeF = other.SizeF;
            ForeColor = other.ForeColor;
            BackColor = other.BackColor;
            Bold = other.Bold;
            Italic = other.Italic;
            Underline = other.Underline;
        }
    }

    // Appearance of one marker number (0..31). Port of MarkerStyle.
    internal sealed class MarkerSpec
    {
        public int Symbol = (int)MarkerSymbol.Circle;
        public Color ForeColor = Color.Black;
        public Color BackColor = Color.Black;
        public int Alpha = 256;  // 256 == opaque; <256 blends background markers
    }

    // One margin's configuration. Port of MarginSpec.
    internal sealed class MarginSpecData
    {
        public int Type = (int)MarginType.Symbol;
        public int Width;
        public uint Mask = ~Marker.MaskFolders;
        public bool Sensitive;
    }

    // One indicator's appearance. Port of IndicatorStyle (the view part).
    internal sealed class IndicatorSpec
    {
        public int Style = (int)IndicatorStyle.Plain;
        public Color ForeColor = Color.Black;
        public int Alpha = 30;          // fill alpha (box styles), 0..255
        public int OutlineAlpha = 50;   // outline alpha (box styles)
        public bool Under;              // draw beneath the text
    }

    // The full visual configuration: the style table plus editor chrome colours.
    // Port of view/view_style.h ViewStyle, with SciLexer's STYLE_DEFAULT=32
    // convention: style 32 is the base an explicit StyleClearAll copies around.
    internal sealed class ViewStyleModel
    {
        public const int MaxMargins = 5;
        public const int MaxMarkers = 32;
        public const int MaxIndicators = 32;
        public const int StyleDefaultIndex = 32;  // STYLE_DEFAULT
        public const int MaxStyles = 256;

        public readonly StyleSpec[] Styles = new StyleSpec[MaxStyles];
        public readonly MarginSpecData[] Margins = new MarginSpecData[MaxMargins];
        public readonly MarkerSpec[] Markers = new MarkerSpec[MaxMarkers];
        public readonly IndicatorSpec[] Indicators = new IndicatorSpec[MaxIndicators];

        public Color CaretColour = Color.Black;
        public double CaretWidth = 1.0;
        public Color SelectionBack = Color.FromArgb(173, 214, 255);
        public Color SelectionFore = Color.Black;
        public bool SelectionForeSet;
        public bool SelectionBackSet;
        public Color CurrentLineBack = Color.FromArgb(245, 245, 255);
        public bool CaretLineVisible;

        public int IndentWidth = 4;
        public int TabWidth = 4;
        public int IndentationGuides;  // IndentView
        public Color IndentGuideColour = Color.FromArgb(200, 200, 200);
        public int EdgeMode;           // EdgeMode
        public int EdgeColumn = 80;
        public Color EdgeColour = Color.FromArgb(224, 224, 224);
        public Color BraceLightFore = Color.Blue;
        public Color BraceLightBack = Color.FromArgb(200, 220, 255);
        public Color BraceBadFore = Color.Red;

        public double MarginInnerPad = 2.0;   // gap between margins and text
        public Color MarginBack = Color.FromArgb(240, 240, 240);
        public Color MarginNumberFore = Color.FromArgb(120, 120, 120);
        public Color FoldMarginColour = Color.FromArgb(230, 230, 230);
        public Color FoldBoxColour = Color.FromArgb(120, 120, 120);

        public ViewStyleModel()
        {
            for (int i = 0; i < MaxStyles; i++) Styles[i] = new StyleSpec();
            for (int i = 0; i < MaxMargins; i++) Margins[i] = new MarginSpecData();
            for (int i = 0; i < MaxMarkers; i++) Markers[i] = new MarkerSpec();
            for (int i = 0; i < MaxIndicators; i++) Indicators[i] = new IndicatorSpec();
        }

        public Color DefaultBack
        {
            get { return Styles[StyleDefaultIndex].BackColor; }
        }

        public Color DefaultFore
        {
            get { return Styles[StyleDefaultIndex].ForeColor; }
        }

        public StyleSpec StyleAt(int index)
        {
            if (index < 0 || index >= MaxStyles) return Styles[StyleDefaultIndex];
            return Styles[index];
        }

        // SCI_STYLECLEARALL: copy STYLE_DEFAULT to every other style.
        public void StyleClearAll()
        {
            StyleSpec baseStyle = Styles[StyleDefaultIndex];
            for (int i = 0; i < MaxStyles; i++)
            {
                if (i == StyleDefaultIndex) continue;
                Styles[i].CopyFrom(baseStyle);
            }
        }

        // SCI_STYLERESETDEFAULT.
        public void StyleResetDefault()
        {
            Styles[StyleDefaultIndex] = new StyleSpec();
        }

        // Total pixel width of all visible margins.
        public double MarginsWidth()
        {
            double w = 0;
            for (int i = 0; i < MaxMargins; i++) w += Margins[i].Width;
            if (w > 0) w += MarginInnerPad;
            return w;
        }
    }
}
