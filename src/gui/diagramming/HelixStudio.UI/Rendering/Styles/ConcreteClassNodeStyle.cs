namespace HelixPythonNamespace.UI.Rendering.Styles
{
    using System.Drawing;
    using System.Drawing.Drawing2D;

    public sealed class ConcreteClassNodeStyle : INodeStyleStrategy
    {
        public NodeStyleInfo GetStyle(bool isSelected)
        {
            NodeStyleInfo style;
            style.HeaderFillColor = Color.FromArgb(230, 245, 230);
            style.BodyFillColor = Color.FromArgb(248, 255, 248);
            style.TextColor = Color.FromArgb(30, 30, 30);
            style.BorderDashStyle = DashStyle.Solid;

            if (isSelected)
            {
                style.BorderColor = Color.FromArgb(0, 120, 215);
                style.BorderWidth = 2f;
            }
            else
            {
                style.BorderColor = Color.FromArgb(80, 160, 80);
                style.BorderWidth = 1.5f;
            }

            return style;
        }
    }
}
