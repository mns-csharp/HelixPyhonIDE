namespace HelixPythonNamespace.UI.Interaction
{
    public interface ICommand
    {
        string Description { get; }
        void Execute();
        void Undo();
    }
}
