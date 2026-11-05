using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Core.Operations;
using HelixPythonNamespace.UI.Projection;

namespace HelixPythonNamespace.UI.Interaction
{
    /// <summary>
    /// Result of executing an operation.
    /// </summary>
    public sealed class OperationResult
    {
        public OperationResult(bool success, string message = null)
        {
            Success = success;
            Message = message;
        }

        public bool Success { get; }
        public string Message { get; }

        public static OperationResult Ok()
        {
            return new OperationResult(true);
        }

        public static OperationResult Fail(string message)
        {
            return new OperationResult(false, message);
        }
    }

    /// <summary>
    /// Executed command with undo capability.
    /// </summary>
    internal sealed class ExecutedCommand
    {
        public ISolutionCommand Command { get; set; }
        public SolutionEditOperation ExecutedOperation { get; set; }
    }

    /// <summary>
    /// Dispatcher for solution commands with undo/redo support.
    /// </summary>
    public sealed class SolutionCommandDispatcher
    {
        private readonly Stack<ExecutedCommand> _undoStack;
        private readonly Stack<ExecutedCommand> _redoStack;
        private readonly ISolutionEditService _editService;
        private readonly SolutionProjectionBuilder _projectionBuilder;

        private const int MaxUndoLevels = 50;

        public SolutionCommandDispatcher(
            ISolutionEditService editService,
            SolutionProjectionBuilder projectionBuilder)
        {
            _editService = editService ?? throw new ArgumentNullException("editService");
            _projectionBuilder = projectionBuilder ?? throw new ArgumentNullException("projectionBuilder");
            _undoStack = new Stack<ExecutedCommand>();
            _redoStack = new Stack<ExecutedCommand>();
        }

        /// <summary>
        /// Execute a command.
        /// </summary>
        public OperationResult Execute(ISolutionCommand command)
        {
            if (command == null)
            {
                return OperationResult.Fail("Command is null");
            }

            try
            {
                var operation = command.CreateEditOperation();
                var result = _editService.Apply(operation);

                if (!result.Success)
                {
                    return OperationResult.Fail(result.Message);
                }

                // Clear redo stack on new command
                _redoStack.Clear();

                // Add to undo stack
                var executed = new ExecutedCommand
                {
                    Command = command,
                    ExecutedOperation = operation
                };
                _undoStack.Push(executed);

                // Trim undo stack if too large
                while (_undoStack.Count > MaxUndoLevels)
                {
                    var items = new List<ExecutedCommand>();
                    while (_undoStack.Count > 0)
                    {
                        items.Add(_undoStack.Pop());
                    }
                    items.RemoveAt(items.Count - 1); // Remove oldest
                    items.Reverse();
                    foreach (var item in items)
                    {
                        _undoStack.Push(item);
                    }
                }

                return OperationResult.Ok();
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(ex.Message);
            }
        }

        /// <summary>
        /// Undo the last command.
        /// </summary>
        public OperationResult Undo()
        {
            if (_undoStack.Count == 0)
            {
                return OperationResult.Fail("Nothing to undo");
            }

            try
            {
                var executed = _undoStack.Pop();
                var undoOperation = executed.Command.CreateUndoOperation();
                var result = _editService.Apply(undoOperation);

                if (!result.Success)
                {
                    // Put it back if undo failed
                    _undoStack.Push(executed);
                    return OperationResult.Fail(result.Message);
                }

                _redoStack.Push(executed);
                return OperationResult.Ok();
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(ex.Message);
            }
        }

        /// <summary>
        /// Redo the last undone command.
        /// </summary>
        public OperationResult Redo()
        {
            if (_redoStack.Count == 0)
            {
                return OperationResult.Fail("Nothing to redo");
            }

            try
            {
                var executed = _redoStack.Pop();
                var operation = executed.Command.CreateEditOperation();
                var result = _editService.Apply(operation);

                if (!result.Success)
                {
                    // Put it back if redo failed
                    _redoStack.Push(executed);
                    return OperationResult.Fail(result.Message);
                }

                _undoStack.Push(executed);
                return OperationResult.Ok();
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(ex.Message);
            }
        }

        /// <summary>
        /// Check if undo is available.
        /// </summary>
        public bool CanUndo
        {
            get { return _undoStack.Count > 0; }
        }

        /// <summary>
        /// Check if redo is available.
        /// </summary>
        public bool CanRedo
        {
            get { return _redoStack.Count > 0; }
        }

        /// <summary>
        /// Get description of next undo action.
        /// </summary>
        public string NextUndoDescription
        {
            get
            {
                if (_undoStack.Count == 0)
                {
                    return null;
                }
                return _undoStack.Peek().Command.Description;
            }
        }

        /// <summary>
        /// Get description of next redo action.
        /// </summary>
        public string NextRedoDescription
        {
            get
            {
                if (_redoStack.Count == 0)
                {
                    return null;
                }
                return _redoStack.Peek().Command.Description;
            }
        }

        /// <summary>
        /// Clear all undo/redo history.
        /// </summary>
        public void ClearHistory()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }

    /// <summary>
    /// Interface for applying edit operations to the solution.
    /// </summary>
    public interface ISolutionEditService
    {
        /// <summary>
        /// Apply an edit operation.
        /// </summary>
        OperationResult Apply(SolutionEditOperation operation);
    }
}
