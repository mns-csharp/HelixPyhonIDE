namespace HelixPythonNamespace.App.Events
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Default <see cref="IAppEventBus"/> implementation. Handlers are stored per
    /// event type and invoked synchronously on the caller's thread (the UI thread,
    /// in practice). A snapshot of the handler list is taken before dispatch so a
    /// handler may subscribe/unsubscribe during delivery without corrupting iteration.
    /// </summary>
    public sealed class AppEventBus : IAppEventBus
    {
        // Event CLR type -> its ordered list of subscriber delegates. Delegate is used
        // as the storage type; each is cast back to Action<TEvent> at publish time.
        private readonly Dictionary<Type, List<Delegate>> _handlers =
            new Dictionary<Type, List<Delegate>>();

        public void Subscribe<TEvent>(Action<TEvent> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException("handler");
            }

            // Lazily create the per-event handler list on first subscription.
            List<Delegate> list;
            if (!_handlers.TryGetValue(typeof(TEvent), out list))
            {
                list = new List<Delegate>();
                _handlers[typeof(TEvent)] = list;
            }

            list.Add(handler);
        }

        public void Unsubscribe<TEvent>(Action<TEvent> handler)
        {
            // Unsubscribing a null (or never-registered) handler is a harmless no-op.
            if (handler == null)
            {
                return;
            }

            List<Delegate> list;
            if (_handlers.TryGetValue(typeof(TEvent), out list))
            {
                list.Remove(handler);
            }
        }

        public void Publish<TEvent>(TEvent evt)
        {
            // No subscribers for this event type: nothing to dispatch.
            List<Delegate> list;
            if (!_handlers.TryGetValue(typeof(TEvent), out list) || list.Count == 0)
            {
                return;
            }

            // Snapshot so subscribe/unsubscribe during delivery is safe.
            Delegate[] snapshot = list.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                ((Action<TEvent>)snapshot[i])(evt);
            }
        }
    }
}
