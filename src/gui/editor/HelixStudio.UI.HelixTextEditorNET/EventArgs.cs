using System;

namespace HelixTextEditorNET
{
    // Event argument types matching ScintillaNET 3.6.3's shapes (only the members
    // consumers use are guaranteed; the rest follow the original semantics).

    // SCN_MODIFIED + SC_MOD_INSERTCHECK: handlers may REPLACE the text about to be
    // inserted by assigning Text.
    public class InsertCheckEventArgs : EventArgs
    {
        public InsertCheckEventArgs(int position, string text)
        {
            Position = position;
            Text = text;
        }

        public int Position { get; private set; }

        public string Text { get; set; }
    }

    // SCN_CHARADDED.
    public class CharAddedEventArgs : EventArgs
    {
        public CharAddedEventArgs(int ch)
        {
            Char = ch;
        }

        public int Char { get; private set; }
    }

    // SCN_UPDATEUI.
    public class UpdateUIEventArgs : EventArgs
    {
        public UpdateUIEventArgs(UpdateChange change)
        {
            Change = change;
        }

        public UpdateChange Change { get; private set; }
    }

    // SCN_MARGINCLICK.
    public class MarginClickEventArgs : EventArgs
    {
        public MarginClickEventArgs(int margin, int position, System.Windows.Forms.Keys modifiers)
        {
            Margin = margin;
            Position = position;
            Modifiers = modifiers;
        }

        public int Margin { get; private set; }

        public int Position { get; private set; }

        public System.Windows.Forms.Keys Modifiers { get; private set; }
    }

    // SCN_DWELLSTART / SCN_DWELLEND.
    public class DwellEventArgs : EventArgs
    {
        public DwellEventArgs(int position, int x, int y)
        {
            Position = position;
            X = x;
            Y = y;
        }

        public int Position { get; private set; }

        public int X { get; private set; }

        public int Y { get; private set; }
    }

    // SCN_AUTOCSELECTION.
    public class AutoCSelectionEventArgs : EventArgs
    {
        public AutoCSelectionEventArgs(int position, string text)
        {
            Position = position;
            Text = text;
        }

        public int Position { get; private set; }

        public string Text { get; private set; }
    }

    // SCN_MODIFIED (subset: what changed and where).
    public class ModificationEventArgs : EventArgs
    {
        public ModificationEventArgs(int position, string text, bool isInsert)
        {
            Position = position;
            Text = text;
            IsInsert = isInsert;
        }

        public int Position { get; private set; }

        public string Text { get; private set; }

        public bool IsInsert { get; private set; }
    }
}
