namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.UI.Rendering.Styles;

    public static class NodeStyleFactory
    {
        private static readonly InterfaceNodeStyle InterfaceStyle = new InterfaceNodeStyle();
        private static readonly AbstractClassNodeStyle AbstractClassStyle = new AbstractClassNodeStyle();
        private static readonly ConcreteClassNodeStyle ConcreteClassStyle = new ConcreteClassNodeStyle();

        public static INodeStyleStrategy GetStyle(TypeKind kind)
        {
            switch (kind)
            {
                case TypeKind.Interface:
                case TypeKind.Delegate:
                case TypeKind.Event:
                    return InterfaceStyle;
                case TypeKind.AbstractClass:
                    return AbstractClassStyle;
                case TypeKind.ConcreteClass:
                case TypeKind.Struct:
                case TypeKind.Enum:
                case TypeKind.Record:
                case TypeKind.Static:
                    return ConcreteClassStyle;
                default:
                    return ConcreteClassStyle;
            }
        }
    }
}
