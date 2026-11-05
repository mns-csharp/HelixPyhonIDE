namespace HelixPythonNamespace.UI.Interaction
{
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Rendering;

    public interface IInteractionState
    {
        void OnMouseDown(CanvasMouseEventArgs e);
        void OnMouseMove(CanvasMouseEventArgs e);
        void OnMouseUp(CanvasMouseEventArgs e);
        void OnMouseWheel(CanvasMouseWheelEventArgs e);
        void OnKeyDown(KeyEventArgs e);
        void OnPaint(Graphics g, ViewportTransform viewport);
        Cursor GetCursor();
    }
}
