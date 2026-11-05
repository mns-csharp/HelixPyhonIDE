namespace HelixPythonNamespace.Core.Model
{
    public sealed class DiagramPoint
    {
        public DiagramPoint(float x, float y)
        {
            X = x;
            Y = y;
        }

        public float X { get; private set; }
        public float Y { get; private set; }
    }
}
