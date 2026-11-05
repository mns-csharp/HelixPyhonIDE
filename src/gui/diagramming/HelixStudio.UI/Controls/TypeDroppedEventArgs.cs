namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Drawing;
    using HelixPythonNamespace.Core.Model;

    public sealed class TypeDroppedEventArgs : EventArgs
    {
        public TypeDroppedEventArgs(TypeModel type, PointF worldLocation)
        {
            Type = type;
            WorldLocation = worldLocation;
        }

        public TypeModel Type { get; }
        public PointF WorldLocation { get; }
    }
}
