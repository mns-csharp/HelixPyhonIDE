namespace HelixPythonNamespace.UI.Interaction
{
    using System;
    using System.Collections.Generic;

    public sealed class CommandDispatcher
    {
        private readonly Stack<ICommand> _undoStack;
        private readonly Stack<ICommand> _redoStack;

        public CommandDispatcher()
        {
            _undoStack = new Stack<ICommand>();
            _redoStack = new Stack<ICommand>();
        }

        public event EventHandler<CommandExecutedEventArgs> CommandExecuted;

        /// <summary>
        /// Optional host-supplied scope entered around Execute/Undo/Redo. Commands may write
        /// solution files (multi-file refactor edits), so the host returns its file-watcher
        /// suspension scope here — otherwise an Undo/Redo replay surfaces as an external change.
        /// </summary>
        public Func<IDisposable> WriteGuard { get; set; }

        private IDisposable EnterWriteGuard()
        {
            try { Func<IDisposable> g = WriteGuard; return g != null ? g() : null; }
            catch { return null; }
        }

        public bool CanUndo
        {
            get { return _undoStack.Count > 0; }
        }

        public bool CanRedo
        {
            get { return _redoStack.Count > 0; }
        }

        public void Execute(ICommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }

            IDisposable guard = EnterWriteGuard();
            try
            {
                command.Execute();
            }
            finally
            {
                if (guard != null) { try { guard.Dispose(); } catch { } }
            }
            _undoStack.Push(command);
            _redoStack.Clear();

            OnCommandExecuted(command);
        }

        private void OnCommandExecuted(ICommand command)
        {
            var handler = CommandExecuted;
            if (handler != null)
            {
                handler(this, new CommandExecutedEventArgs(command));
            }
        }

        public void PushWithoutExecute(ICommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }

            _undoStack.Push(command);
            _redoStack.Clear();
        }

        public void Undo()
        {
            if (_undoStack.Count == 0)
            {
                return;
            }

            ICommand command = _undoStack.Pop();
            IDisposable guard = EnterWriteGuard();
            try
            {
                command.Undo();
            }
            finally
            {
                if (guard != null) { try { guard.Dispose(); } catch { } }
            }
            _redoStack.Push(command);
        }

        public void Redo()
        {
            if (_redoStack.Count == 0)
            {
                return;
            }

            ICommand command = _redoStack.Pop();
            IDisposable guard = EnterWriteGuard();
            try
            {
                command.Execute();
            }
            finally
            {
                if (guard != null) { try { guard.Dispose(); } catch { } }
            }
            _undoStack.Push(command);

            OnCommandExecuted(command);
        }
    }

    public class CommandExecutedEventArgs : EventArgs
    {
        public CommandExecutedEventArgs(ICommand command)
        {
            Command = command;
        }

        public ICommand Command { get; private set; }
    }
}
