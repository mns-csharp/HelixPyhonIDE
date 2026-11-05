namespace HelixPythonNamespace.UI.Rendering.Styles
{
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using HelixPythonNamespace.Core.Model.Enums;

    public sealed class DependsOnEdgeStyle : IEdgeStyleStrategy
    {
        public RelationshipKind Kind
        {
            get { return RelationshipKind.DependsOn; }
        }

        public EdgeStyleInfo GetStyle(bool isSelected)
        {
            EdgeStyleInfo style;
            style.LineDashStyle = DashStyle.Dot;
            style.DrawArrowhead = true;
            style.DrawHollowTriangle = false;
            style.UseSpline = false;

            if (isSelected)
            {
                style.LineColor = Color.FromArgb(0, 120, 215);
                style.LineWidth = 2.5f;
            }
            else
            {
                style.LineColor = Color.FromArgb(255, 109, 0); // Orange for depends-on
                style.LineWidth = 1.8f;
            }

            return style;
        }
    }
}
