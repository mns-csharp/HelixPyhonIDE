namespace HelixPythonNamespace.GuiDesigner.Model
{
    using System.Collections.Generic;

    /// <summary>Framework-neutral transaction history for a design surface.</summary>
    public sealed class GuiFormHistory
    {
        private readonly Stack<GuiForm> _undo = new Stack<GuiForm>();
        private readonly Stack<GuiForm> _redo = new Stack<GuiForm>();
        public bool CanUndo { get { return _undo.Count > 0; } }
        public bool CanRedo { get { return _redo.Count > 0; } }

        public void Record(GuiForm current) { _undo.Push(Clone(current)); _redo.Clear(); }
        public bool Undo(GuiForm current) { if (!CanUndo) return false; _redo.Push(Clone(current)); Copy(_undo.Pop(), current); return true; }
        public bool Redo(GuiForm current) { if (!CanRedo) return false; _undo.Push(Clone(current)); Copy(_redo.Pop(), current); return true; }

        public static GuiForm Clone(GuiForm source)
        {
            GuiForm copy = new GuiForm { FormClassName = source.FormClassName, Title = source.Title, Width = source.Width, Height = source.Height, Framework = source.Framework, SchemaVersion = source.SchemaVersion };
            copy.Viewport = new GuiViewport { Zoom = source.Viewport.Zoom, PanX = source.Viewport.PanX, PanY = source.Viewport.PanY, GridSize = source.Viewport.GridSize, SnapToGrid = source.Viewport.SnapToGrid };
            foreach (GuiControl c in source.Controls)
            {
                GuiControl cc = new GuiControl { Id = c.Id, Kind = c.Kind, Name = c.Name, ParentId = c.ParentId, X = c.X, Y = c.Y, Width = c.Width, Height = c.Height };
                foreach (KeyValuePair<string, string> p in c.Props) cc.Props[p.Key] = p.Value;
                foreach (GuiEvent e in c.Events) cc.Events.Add(new GuiEvent { EventName = e.EventName, HandlerName = e.HandlerName });
                copy.Controls.Add(cc);
            }
            return copy;
        }

        private static void Copy(GuiForm source, GuiForm target)
        {
            GuiForm copy = Clone(source);
            target.FormClassName = copy.FormClassName; target.Title = copy.Title; target.Width = copy.Width; target.Height = copy.Height;
            target.Framework = copy.Framework; target.SchemaVersion = copy.SchemaVersion; target.Viewport = copy.Viewport;
            target.Controls.Clear(); target.Controls.AddRange(copy.Controls);
        }
    }
}
