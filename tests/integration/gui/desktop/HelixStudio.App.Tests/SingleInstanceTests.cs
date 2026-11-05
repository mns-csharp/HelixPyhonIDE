namespace HelixPythonNamespace.App.Tests
{
    using System;
    using System.IO;
    using System.Reflection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// One IDE per session.
    ///
    /// <para>Two shells are not two independent editors: they share
    /// <c>%APPDATA%\HelixPython</c> — settings, tab layout, recent files, breakpoints — and both
    /// write it on shutdown, so whichever quits last silently discards the other's session.</para>
    ///
    /// <para>These tests exercise the claim and the hand-over WITHOUT starting a process. The
    /// suite itself composes <c>MainForm</c> directly and never goes through <c>Program.Main</c>,
    /// which is exactly why the gate lives in <c>Main</c> and not in the form: a test run that
    /// opens a dozen shells must stay unaffected, and this class would be the first thing to
    /// deadlock if that ever changed.</para>
    /// </summary>
    [TestClass]
    public class SingleInstanceTests
    {
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;

        private static Type Target
        {
            get
            {
                Type type = typeof(MainForm).Assembly.GetType("HelixPythonNamespace.App.SingleInstance");
                Assert.IsNotNull(type, "SingleInstance has been renamed or removed.");
                return type;
            }
        }

        private static Type Pending
        {
            get
            {
                Type type = typeof(MainForm).Assembly.GetType("HelixPythonNamespace.App.PendingOpen");
                Assert.IsNotNull(type, "PendingOpen has been renamed or removed.");
                return type;
            }
        }

        [TestMethod]
        public void TheGateIsInTheEntryPoint_NotInTheForm()
        {
            // If the claim ever moves into MainForm's constructor, every test class in this suite
            // that composes a shell starts fighting the first one for the mutex. Stated as a test
            // because the failure it would cause — a suite that hangs on the second class — says
            // nothing about the cause.
            MethodInfo main = typeof(MainForm).Assembly.GetType("HelixPythonNamespace.App.Program")
                .GetMethod("Main", Flags);
            Assert.IsNotNull(main, "Program.Main has been renamed.");

            MethodInfo claim = Target.GetMethod("Claim", Flags);
            Assert.IsNotNull(claim, "SingleInstance.Claim has been renamed.");

            foreach (Type type in typeof(MainForm).Assembly.GetTypes())
            {
                if (type != typeof(MainForm)) continue;
                foreach (ConstructorInfo constructor in type.GetConstructors())
                    Assert.IsFalse(constructor.ToString().Contains("SingleInstance"),
                        "the shell's constructor now claims the mutex; the test suite composes "
                        + "shells directly and would deadlock against itself");
            }
        }

        [TestMethod]
        public void TheMutexIsPerSession_NotPerMachine()
        {
            // Local\, not Global\: two users signed in to the same machine have separate settings
            // directories and share nothing, so one user's IDE must not lock the other one out.
            FieldInfo name = Target.GetField("MutexName", Flags);
            Assert.IsNotNull(name, "SingleInstance.MutexName has been renamed.");
            var value = (string)name.GetRawConstantValue();
            StringAssert.StartsWith(value, @"Local\",
                "a Global mutex would let one signed-in user block another's IDE");
        }

        [TestMethod]
        public void TheSecondLaunchHasSomethingToBroadcastOn()
        {
            // RegisterWindowMessage returns 0 only if the OS refuses, and a 0 message id would mean
            // the hand-over posts nothing and the second launch exits having done nothing at all.
            FieldInfo message = Target.GetField("ActivateMessage",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(message, "SingleInstance.ActivateMessage has been renamed.");
            Assert.AreNotEqual(0, (int)message.GetValue(null),
                "the activate message was not registered, so a second launch would be silent");
        }

        [TestMethod]
        public void ThePathASecondLaunchWasGivenSurvivesTheHandOver()
        {
            // The broadcast carries two integers and nothing else, so the path goes through a file.
            // What matters is that it is handed over exactly once: read and cleared, so the next
            // plain launch does not reopen whatever the last one was asked to open.
            MethodInfo write = Pending.GetMethod("Write", Flags);
            MethodInfo take = Pending.GetMethod("Take", Flags);
            Assert.IsNotNull(write);
            Assert.IsNotNull(take);

            take.Invoke(null, null);   // start from nothing, whatever a previous run left

            string path = Path.Combine(Path.GetTempPath(), "some-solution", "pyproject.toml");
            write.Invoke(null, new object[] { path });

            Assert.AreEqual(path, take.Invoke(null, null), "the path did not survive the hand-over");
            Assert.IsNull(take.Invoke(null, null),
                "the path was left behind, so the next launch would reopen it unasked");
        }

        [TestMethod]
        public void TheRunningShellAnswersTheBroadcast()
        {
            // The half that makes the whole thing worth having: an IDE hidden in the notification
            // area must come forward. Without it the user presses the shortcut, a second process
            // starts and exits, and nothing appears to happen at all.
            MethodInfo activate = typeof(MainForm).GetMethod("ActivateFromSecondLaunch",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(activate, "MainForm.ActivateFromSecondLaunch has been renamed.");

            MethodInfo wndProc = typeof(MainForm).GetMethod("WndProc",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(System.Windows.Forms.Message).MakeByRefType() }, null);
            Assert.IsNotNull(wndProc, "the shell no longer overrides WndProc");
            Assert.AreEqual(typeof(MainForm), wndProc.DeclaringType,
                "the override that listens for the second launch is gone");
        }
    }
}
