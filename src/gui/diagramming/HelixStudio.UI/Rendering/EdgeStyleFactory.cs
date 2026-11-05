namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.UI.Rendering.Styles;

    public static class EdgeStyleFactory
    {
        private static readonly ImplementsEdgeStyle ImplementsStyle = new ImplementsEdgeStyle();
        private static readonly InheritsEdgeStyle InheritsStyle = new InheritsEdgeStyle();
        private static readonly DependsOnEdgeStyle DependsOnStyle = new DependsOnEdgeStyle();

        public static IEdgeStyleStrategy GetStyle(RelationshipKind kind)
        {
            switch (kind)
            {
                case RelationshipKind.Implements:
                    return ImplementsStyle;
                case RelationshipKind.Inherits:
                    return InheritsStyle;
                case RelationshipKind.DependsOn:
                    return DependsOnStyle;
                default:
                    throw new ArgumentOutOfRangeException("kind",
                        string.Format("Unsupported RelationshipKind: {0}", kind));
            }
        }
    }
}
