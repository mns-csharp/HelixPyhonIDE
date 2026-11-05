namespace HelixPythonNamespace.UI.Rendering.Styles
{
    using System.Drawing;
    using System.Drawing.Drawing2D;

    public sealed class AbstractClassNodeStyle : INodeStyleStrategy
    {
        public NodeStyleInfo GetStyle(bool isSelected)
        {
            NodeStyleInfo style;
            style.HeaderFillColor = Color.FromArgb(240, 235, 255);
            style.BodyFillColor = Color.FromArgb(250, 248, 255);
            style.TextColor = Color.FromArgb(30, 30, 30);
            style.BorderDashStyle = DashStyle.Solid;

            if (isSelected)
            {
                style.BorderColor = Color.FromArgb(0, 120, 215);
                style.BorderWidth = 2f;
            }
            else
            {
                style.BorderColor = Color.FromArgb(130, 100, 190);
                style.BorderWidth = 1.5f;
            }

            return style;
        }
    }
}
