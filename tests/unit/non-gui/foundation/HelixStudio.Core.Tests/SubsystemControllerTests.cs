using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Execution;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class SubsystemControllerTests
    {
        // A minimal ISubsystem test double that records how many times Stop() ran.
        private sealed class FakeSubsystem : ISubsystem
        {
            private int _stopCount;
            private bool _running = true;
            private readonly bool _throwOnStop;

            public FakeSubsystem(SubsystemKind kind, bool throwOnStop = false)
            {
                Kind = kind;
                _throwOnStop = throwOnStop;
            }

            public SubsystemKind Kind { get; }
            public string DisplayName { get { return Kind.ToString(); } }
            public bool IsRunning { get { return _running; } }
            public int StopCount { get { return _stopCount; } }
            public event EventHandler StateChanged;

            public void Stop()
            {
                Interlocked.Increment(ref _stopCount);
                _running = false;
                EventHandler h = StateChanged;
                if (h != null) h(this, EventArgs.Empty);
                if (_throwOnStop) throw new InvalidOperationException("boom");
            }
        }

        [TestMethod]
        public void RegisterMarksRunningAndDisposeClears()
        {
            SubsystemController c = new SubsystemController();
            Assert.IsFalse(c.IsAnyRunning);

            FakeSubsystem sub = new FakeSubsystem(SubsystemKind.Linter);
            IDisposable reg = c.Register(sub);

            Assert.IsTrue(c.IsAnyRunning);
            Assert.IsTrue(c.IsRunning(SubsystemKind.Linter));
            Assert.IsFalse(c.IsRunning(SubsystemKind.Profiler));
            CollectionAssert.AreEqual(new[] { SubsystemKind.Linter }, c.RunningKinds.ToArray());

            reg.Dispose();
            Assert.IsFalse(c.IsAnyRunning);
            Assert.IsFalse(c.IsRunning(SubsystemKind.Linter));
        }

        [TestMethod]
        public void RunningChangedFiresOnRegisterAndUnregister()
        {
            SubsystemController c = new SubsystemController();
            int changes = 0;
            c.RunningChanged += delegate { changes++; };

            IDisposable reg = c.Register(new FakeSubsystem(SubsystemKind.Analyzer));
            Assert.AreEqual(1, changes);
            reg.Dispose();
            Assert.AreEqual(2, changes);
        }

        [TestMethod]
        public void RegisterSameInstanceTwiceIsNoOp()
        {
            SubsystemController c = new SubsystemController();
            FakeSubsystem sub = new FakeSubsystem(SubsystemKind.Linker);
            int changes = 0;
            c.RunningChanged += delegate { changes++; };

            c.Register(sub);
            c.Register(sub);   // duplicate — must not add again nor re-raise

            Assert.AreEqual(1, changes);
            Assert.AreEqual(1, c.RunningKinds.Count);
        }

        [TestMethod]
        public void StopAllStopsEveryRunningSubsystem()
        {
            SubsystemController c = new SubsystemController();
            FakeSubsystem a = new FakeSubsystem(SubsystemKind.VirtualCompiler);
            FakeSubsystem b = new FakeSubsystem(SubsystemKind.TestRunner);
            c.Register(a);
            c.Register(b);

            int stopped = c.StopAll();

            Assert.AreEqual(2, stopped);
            Assert.AreEqual(1, a.StopCount);
            Assert.AreEqual(1, b.StopCount);
        }

        [TestMethod]
        public void StopKindStopsOnlyMatchingKind()
        {
            SubsystemController c = new SubsystemController();
            FakeSubsystem compiler = new FakeSubsystem(SubsystemKind.VirtualCompiler);
            FakeSubsystem tests = new FakeSubsystem(SubsystemKind.TestRunner);
            c.Register(compiler);
            c.Register(tests);

            int n = c.StopKind(SubsystemKind.TestRunner);

            Assert.AreEqual(1, n);
            Assert.AreEqual(0, compiler.StopCount);
            Assert.AreEqual(1, tests.StopCount);
        }

        [TestMethod]
        public void StopAllSwallowsExceptionsFromMisbehavingEngine()
        {
            SubsystemController c = new SubsystemController();
            FakeSubsystem bad = new FakeSubsystem(SubsystemKind.Profiler, throwOnStop: true);
            FakeSubsystem good = new FakeSubsystem(SubsystemKind.Linter);
            c.Register(bad);
            c.Register(good);

            // A throwing Stop() must not prevent the other engine from being stopped.
            int stopped = c.StopAll();

            Assert.AreEqual(2, stopped);
            Assert.AreEqual(1, good.StopCount);
        }

        [TestMethod]
        public void RegisterNullThrows()
        {
            SubsystemController c = new SubsystemController();
            Assert.ThrowsException<ArgumentNullException>(delegate { c.Register(null); });
        }

        [TestMethod]
        public void DisposeIsIdempotent()
        {
            SubsystemController c = new SubsystemController();
            IDisposable reg = c.Register(new FakeSubsystem(SubsystemKind.Linter));
            reg.Dispose();
            reg.Dispose();   // second dispose must not throw nor double-fire
            Assert.IsFalse(c.IsAnyRunning);
        }

        [TestMethod]
        public void ConcurrentRegisterAndStopIsThreadSafe()
        {
            SubsystemController c = new SubsystemController();
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < 50; i++)
            {
                SubsystemKind kind = (i % 2 == 0) ? SubsystemKind.Linter : SubsystemKind.Analyzer;
                tasks.Add(Task.Run(delegate
                {
                    IDisposable reg = c.Register(new FakeSubsystem(kind));
                    c.StopAll();
                    reg.Dispose();
                }));
            }
            Task.WaitAll(tasks.ToArray());
            Assert.IsFalse(c.IsAnyRunning);
        }
    }

    [TestClass]
    public class DelegateSubsystemTests
    {
        [TestMethod]
        public void StopInvokesDelegateExactlyOnce()
        {
            int calls = 0;
            DelegateSubsystem sub = new DelegateSubsystem(
                SubsystemKind.VirtualCompiler, "Compile",
                delegate { calls++; }, delegate { return true; });

            sub.Stop();
            sub.Stop();   // idempotent

            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public void IsRunningReflectsProbe()
        {
            bool running = true;
            // ReSharper disable once AccessToModifiedClosure
            DelegateSubsystem sub = new DelegateSubsystem(
                SubsystemKind.Profiler, "Profile",
                delegate { }, delegate { return running; });

            Assert.IsTrue(sub.IsRunning);
            running = false;
            Assert.IsFalse(sub.IsRunning);
        }

        [TestMethod]
        public void StopSwallowsDelegateException()
        {
            DelegateSubsystem sub = new DelegateSubsystem(
                SubsystemKind.Linter, "Lint",
                delegate { throw new InvalidOperationException(); },
                delegate { return true; });

            sub.Stop();   // must not propagate
            Assert.IsFalse(sub.IsRunning || false);
        }

        [TestMethod]
        public void DisplayNameFallsBackToKind()
        {
            DelegateSubsystem sub = new DelegateSubsystem(
                SubsystemKind.Analyzer, null, delegate { }, null);
            Assert.AreEqual(SubsystemKind.Analyzer.ToString(), sub.DisplayName);
            Assert.IsFalse(sub.IsRunning);   // null probe => false
        }
    }
}
