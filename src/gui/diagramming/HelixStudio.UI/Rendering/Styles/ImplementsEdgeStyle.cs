namespace HelixPythonNamespace.UI.Rendering.Styles
{
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using HelixPythonNamespace.Core.Model.Enums;

    public sealed class ImplementsEdgeStyle : IEdgeStyleStrategy
    {
        public RelationshipKind Kind
        {
            get { return RelationshipKind.Implements; }
        }

        public EdgeStyleInfo GetStyle(bool isSelected)
        {
            EdgeStyleInfo style;
            style.LineDashStyle = DashStyle.Dash;
            style.DrawArrowhead = false;
            style.DrawHollowTriangle = true;
            style.UseSpline = false;

            if (isSelected)
            {
                style.LineColor = Color.FromArgb(0, 120, 215);
                style.LineWidth = 2.5f;
            }
            else
            {
                style.LineColor = Color.FromArgb(0, 200, 83); // Green for implements
                style.LineWidth = 1.8f;
            }

            return style;
        }
    }
}
