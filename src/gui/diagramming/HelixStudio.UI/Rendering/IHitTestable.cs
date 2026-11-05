namespace HelixPythonNamespace.UI.Rendering
{
    using System.Drawing;

    public interface IHitTestable
    {
        bool HitTest(PointF worldPoint, float tolerance);
        RectangleF GetWorldBounds();
    }
}
