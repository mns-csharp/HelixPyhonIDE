namespace HelixTextEditorNET
{
    /// <summary>When a scrollbar is on screen. See <see cref="Scintilla.HScrollBarVisibility"/>.</summary>
    public enum ScrollBarVisibility
    {
        /// <summary>Always present, so the text area never reflows because a bar appeared.</summary>
        Always = 0,

        /// <summary>Present only while there is something off screen in that direction.</summary>
        WhenNeeded = 1,

        /// <summary>Never — the host sizes the control to its content.</summary>
        Never = 2,
    }
}
