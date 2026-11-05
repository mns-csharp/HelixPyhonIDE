namespace HelixPythonNamespace.UI.Rendering.Styles
{
    using System.Drawing;
    using System.Drawing.Drawing2D;

    public sealed class InterfaceNodeStyle : INodeStyleStrategy
    {
        public NodeStyleInfo GetStyle(bool isSelected)
        {
            NodeStyleInfo style;
            style.HeaderFillColor = Color.FromArgb(225, 240, 255);
            style.BodyFillColor = Color.FromArgb(245, 250, 255);
            style.TextColor = Color.FromArgb(30, 30, 30);
            style.BorderDashStyle = DashStyle.Dash;

            if (isSelected)
            {
                style.BorderColor = Color.FromArgb(0, 120, 215);
                style.BorderWidth = 2f;
            }
            else
            {
                style.BorderColor = Color.FromArgb(80, 140, 200);
                style.BorderWidth = 1.5f;
            }

            return style;
        }
    }
}
