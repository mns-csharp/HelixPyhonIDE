namespace HelixPythonNamespace.App.Coordinators
{
    /// <summary>
    /// A coordinator owns one cohesive slice of the application shell (a menu, a set
    /// of panels, the canvas, the editor, ...) together with the event handlers that
    /// drive it. <see cref="MainForm"/> hosts the WinForms surface and delegates to
    /// coordinators rather than holding the logic itself.
    ///
    /// Lifecycle: <see cref="Attach"/> is called once after the host form's controls
    /// exist; <see cref="Detach"/> unsubscribes from the event bus and releases
    /// references on form close.
    /// </summary>
    public interface ICoordinator
    {
        void Attach(MainFormContext context);

        void Detach();
    }
}
