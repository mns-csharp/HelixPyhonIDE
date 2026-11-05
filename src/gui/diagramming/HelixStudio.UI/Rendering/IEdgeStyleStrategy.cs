namespace HelixPythonNamespace.UI.Rendering
{
    using HelixPythonNamespace.Core.Model.Enums;

    public interface IEdgeStyleStrategy
    {
        RelationshipKind Kind { get; }
        EdgeStyleInfo GetStyle(bool isSelected);
    }
}
