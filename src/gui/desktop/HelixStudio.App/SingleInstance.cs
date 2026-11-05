namespace HelixPythonNamespace.App
{
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;

    /// <summary>
    /// One IDE per session. A second launch hands its work to the first and exits.
    ///
    /// <para><b>Why this is not a preference.</b> Two HelixPython windows are not two independent
    /// editors: they share <c>%APPDATA%\HelixPython</c> — the settings file, the tab layout, the
    /// recent list, the breakpoint store — and both write it on shutdown. Whichever quits last
    /// wins, so the loser's session silently disappears. They also share the per-solution sidecars
    /// the designers write, so the same design open in two windows is two editors overwriting one
    /// file with no merge and no warning.</para>
    ///
    /// <para><b>Per session, not per machine.</b> The mutex is <c>Local\</c>, so two users signed
    /// in to the same machine each get their own IDE — their settings directories are separate and
    /// nothing is shared between them. A <c>Global\</c> mutex would let one user's IDE block
    /// another's, which is a different bug.</para>
    ///
    /// <para><b>The running instance is told, not searched for.</b> A broadcast of a registered
    /// window message reaches the first instance wherever it is — including when it is hidden in
    /// the notification area, which is exactly when a user is most likely to launch it again and
    /// think nothing happened. Enumerating windows would not find a hidden one.</para>
    ///
    /// <para>This is the EXE's entry path only. The tests compose <c>MainForm</c> directly and
    /// never go through <c>Program.Main</c>, so a suite that opens a dozen shells is unaffected.</para>
    /// </summary>
    internal static class SingleInstance
    {
        private const string MutexName = @"Local\HelixPython.SingleInstance";

        /// <summary>The broadcast a second launch sends. Registered by name, so the value is the
        /// same in both processes without either knowing about the other.</summary>
        internal static readonly int ActivateMessage =
            RegisterWindowMessage("HelixPython.Activate");

        private static Mutex _held;

        /// <summary>
        /// True when this process is the one that gets to run.
        ///
        /// <para>The mutex is held for the life of the process and deliberately never released:
        /// the OS drops it when the process ends, including on a crash, which is the case a manual
        /// release would get wrong.</para>
        /// </summary>
        internal static bool Claim()
        {
            bool created;
            _held = new Mutex(true, MutexName, out created);
            if (created) return true;

            // Abandoned by a crashed instance: the handle is signalled and this process may take
            // it. WaitOne throws AbandonedMutexException in that case, which MEANS acquired.
            try
            {
                if (_held.WaitOne(TimeSpan.Zero)) return true;
            }
            catch (AbandonedMutexException)
            {
                return true;
            }

            _held.Dispose();
            _held = null;
            return false;
        }

        /// <summary>
        /// Wake the instance that is already running, and give it whatever this launch was asked to
        /// open.
        ///
        /// <para>The path goes through a file rather than the message, because a broadcast carries
        /// two integers and nothing else. <c>WM_COPYDATA</c> would carry a string but cannot be
        /// broadcast — it needs the target window, which is the thing that cannot be found when the
        /// IDE is hidden.</para>
        /// </summary>
        internal static void HandOver(string pathToOpen)
        {
            if (!string.IsNullOrEmpty(pathToOpen)) PendingOpen.Write(pathToOpen);
            PostMessage(HwndBroadcast, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
        }

        private static readonly IntPtr HwndBroadcast = new IntPtr(0xFFFF);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>
    /// The one path a second launch wants opened, left where the running instance will find it.
    ///
    /// <para>A file in the user's own temp directory, written by the launching process and deleted
    /// by the reading one. Not a queue and not a protocol: a user double-clicking two files in
    /// quick succession gets the second one, which is the same thing that happens if they
    /// double-click twice in one Explorer window.</para>
    /// </summary>
    internal static class PendingOpen
    {
        private static string Path
        {
            get
            {
                return System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "HelixPython", "pending-open.txt");
            }
        }

        internal static void Write(string path)
        {
            try
            {
                string directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
                System.IO.File.WriteAllText(Path, path);
            }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Takes the path if there is one, and clears it. Returns null otherwise.</summary>
        internal static string Take()
        {
            try
            {
                if (!System.IO.File.Exists(Path)) return null;
                string path = System.IO.File.ReadAllText(Path).Trim();
                System.IO.File.Delete(Path);
                return path.Length == 0 ? null : path;
            }
            catch (System.IO.IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
