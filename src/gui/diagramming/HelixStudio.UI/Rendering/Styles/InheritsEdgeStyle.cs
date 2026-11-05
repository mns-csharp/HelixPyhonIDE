namespace HelixPythonNamespace.UI.Rendering.Styles
{
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using HelixPythonNamespace.Core.Model.Enums;

    public sealed class InheritsEdgeStyle : IEdgeStyleStrategy
    {
        public RelationshipKind Kind
        {
            get { return RelationshipKind.Inherits; }
        }

        public EdgeStyleInfo GetStyle(bool isSelected)
        {
            EdgeStyleInfo style;
            style.LineDashStyle = DashStyle.Solid;
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
                style.LineColor = Color.FromArgb(41, 121, 255); // Blue for inheritance
                style.LineWidth = 1.8f;
            }

            return style;
        }
    }
}
