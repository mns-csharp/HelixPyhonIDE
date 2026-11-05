using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Interaction;

namespace HelixPythonNamespace.UI.Tests
{
    /// <summary>
    /// SRS §8.1: commands replayed through the dispatcher may rewrite solution files (multi-file
    /// refactor edits), so Execute/Undo/Redo enter the host's WriteGuard (the file-watcher
    /// suspension scope) for the duration — otherwise an Edit ▸ Undo surfaces as a false
    /// "External Change Detected".
    /// </summary>
    [TestClass]
    public class CommandDispatcherWriteGuardTests
    {
        private sealed class LogCommand : ICommand
        {
            private readonly List<string> _log;
            public LogCommand(List<string> log) { _log = log; }
            public string Description { get { return "log"; } }
            public void Execute() { _log.Add("execute"); }
            public void Undo() { _log.Add("undo"); }
        }

        private sealed class Scope : IDisposable
        {
            private readonly List<string> _log;
            public Scope(List<string> log) { _log = log; }
            public void Dispose() { _log.Add("exit"); }
        }

        private static CommandDispatcher Guarded(List<string> log)
        {
            var d = new CommandDispatcher();
            d.WriteGuard = () => { log.Add("enter"); return new Scope(log); };
            return d;
        }

        [TestMethod]
        public void Execute_RunsInsideTheGuard()
        {
            var log = new List<string>();
            Guarded(log).Execute(new LogCommand(log));
            CollectionAssert.AreEqual(new[] { "enter", "execute", "exit" }, log);
        }

        [TestMethod]
        public void Undo_RunsInsideTheGuard()
        {
            var log = new List<string>();
            var d = Guarded(log);
            d.Execute(new LogCommand(log));
            log.Clear();

            d.Undo();
            CollectionAssert.AreEqual(new[] { "enter", "undo", "exit" }, log,
                "an Undo replay rewrites files and must be suppressed like the original Execute");
        }

        [TestMethod]
        public void Redo_RunsInsideTheGuard()
        {
            var log = new List<string>();
            var d = Guarded(log);
            d.Execute(new LogCommand(log));
            d.Undo();
            log.Clear();

            d.Redo();
            CollectionAssert.AreEqual(new[] { "enter", "execute", "exit" }, log);
        }

        [TestMethod]
        public void NullGuard_IsHarmless()
        {
            var log = new List<string>();
            var d = new CommandDispatcher();   // no guard wired
            d.Execute(new LogCommand(log));
            d.Undo();
            d.Redo();
            CollectionAssert.AreEqual(new[] { "execute", "undo", "execute" }, log);
        }
    }
}
