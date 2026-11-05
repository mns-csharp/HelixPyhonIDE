namespace HelixPythonNamespace.UI.Rendering
{
    using System.Drawing;

    public interface IRenderable
    {
        void Render(Graphics g, ViewportTransform viewport);
        int ZOrder { get; }
    }
}
