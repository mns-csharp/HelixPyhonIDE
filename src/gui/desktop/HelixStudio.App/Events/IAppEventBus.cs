namespace HelixPythonNamespace.App.Events
{
    using System;

    /// <summary>
    /// Typed, in-process publish/subscribe bus used to decouple the coordinators
    /// that make up the application shell. Coordinators publish intent/notification
    /// events and subscribe to those they care about, instead of holding direct
    /// references to one another or reaching back into <see cref="MainForm"/>.
    /// </summary>
    public interface IAppEventBus
    {
        /// <summary>Register <paramref name="handler"/> to receive events of type <typeparamref name="TEvent"/>.</summary>
        void Subscribe<TEvent>(Action<TEvent> handler);

        /// <summary>Remove a previously registered handler.</summary>
        void Unsubscribe<TEvent>(Action<TEvent> handler);

        /// <summary>Synchronously dispatch <paramref name="evt"/> to all current subscribers.</summary>
        void Publish<TEvent>(TEvent evt);
    }
}
