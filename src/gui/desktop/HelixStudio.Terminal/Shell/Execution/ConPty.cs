using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Execution;

/// <summary>
/// Runs a child program attached to a Windows pseudo-console instead of anonymous pipes.
///
/// This is what makes a program believe it is talking to a terminal. With plain pipes a child
/// asking "am I interactive?" — <c>isatty</c>, <c>GetConsoleMode</c>, Node's <c>stdin.isTTY</c> —
/// is told no, and well-behaved programs then refuse to prompt: they read a script off standard
/// input instead, or exit with an error. A pseudo-console gives the child a real console handle,
/// so it prompts, draws and reads keys exactly as it would under Windows Terminal, and everything
/// it draws arrives here as a stream of VT sequences for the screen to interpret.
/// </summary>
public static class ConPty
{
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    // Without this a console application briefly gets a window of its own on the way to the
    // pseudo-console. It is visible for only a few frames, but long enough to take the
    // foreground and leave our window buried behind whatever was underneath it.
    private const uint CreateNoWindow = 0x08000000;

    private const uint Infinite = 0xFFFFFFFF;

    private static readonly bool Available = ResolveAvailability();

    /// <summary>False on Windows builds older than 1809, where there is no pseudo-console API.</summary>
    public static bool IsSupported => Available;

    /// <summary>
    /// True when this process already owns a console of its own.
    ///
    /// It matters because a child created with a pseudo-console attribute still joins an
    /// inherited console when the parent has one: it attaches, but its output goes to that
    /// console rather than down our pipe, and the screen stays empty. The tray application has
    /// no console, so this is normally false; it is true when the executable is started from
    /// one, and under a test runner. Pipes are the honest answer in that case — the program
    /// loses its terminal, but its output still reaches the person who ran it.
    /// </summary>
    public static bool WouldInheritAConsole() => GetConsoleWindow() != IntPtr.Zero;

    private static bool ResolveAvailability()
    {
        try
        {
            return NativeLibrary.TryLoad("kernel32.dll", out var library)
                   && NativeLibrary.TryGetExport(library, "CreatePseudoConsole", out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts <paramref name="executable"/> on a pseudo-console and runs it to completion.
    /// Returns false without side effects when the console could not be created, which lets the
    /// caller fall back to pipes; once the child has started, all outcomes are reported through
    /// <paramref name="status"/>.
    /// </summary>
    public static bool TryRun(
        string executable,
        IReadOnlyList<string> arguments,
        ShellContext context,
        ShellIo io,
        IReadOnlyDictionary<string, string> environment,
        Job? job,
        out int status)
    {
        status = 0;
        if (!Available || WouldInheritAConsole()) return false;

        var handles = new PtyHandles();

        // Every program starts on a console the size of the window. The rows the one before it was
        // given to hold its history in were its own, and handing them on would open this one on a
        // console taller than anything it has to put in it.
        context.Screen.SetConsoleRows(context.TerminalRows);
        context.Screen.SetProgramKeepsHistory(context.KeepsConsoleHistory(executable));

        try
        {
            if (!handles.Create(context.TerminalColumns, context.ConsoleRows)) return false;
        }
        catch (Exception)
        {
            handles.Dispose();
            return false;
        }

        ProcessInformation processInfo;

        try
        {
            processInfo = StartChild(executable, arguments, context, environment, handles.PseudoConsole);
        }
        catch (ShellException)
        {
            handles.Dispose();
            throw;
        }
        catch (Exception)
        {
            handles.Dispose();
            return false;
        }

        // The write end of the input pipe and the read end of the output pipe stay with us; the
        // console owns the other two, and until our copies are closed the pipe never reports the
        // end of the child's output.
        handles.CloseConsoleSides();

        status = Supervise(processInfo, handles, context, io, job);
        return true;
    }

    private static int Supervise(
        ProcessInformation processInfo,
        PtyHandles handles,
        ShellContext context,
        ShellIo io,
        Job? job)
    {
        var process = AttachToJob(processInfo, job);
        var rawInput = io.Input as IRawInputSource;
        void Resize(int columns, int rows) => handles.Resize(columns, rows);

        rawInput?.BeginRawInput();
        context.Screen.Changed += Resize;

        var outputPump = PumpToStream(handles.OutputRead, io.Output);
        var inputPump = PumpToPipe(io.Input, handles.InputWrite);

        try
        {
            while (WaitForSingleObject(processInfo.Process, 100) == 258 /* WAIT_TIMEOUT */)
            {
                if (!context.CancellationToken.IsCancellationRequested && job?.Cancellation.IsCancellationRequested != true)
                    continue;

                KillTree(process, processInfo.Process);
                break;
            }

            WaitForSingleObject(processInfo.Process, Infinite);
            return GetExitCodeProcess(processInfo.Process, out var code) ? (int)code : 1;
        }
        finally
        {
            context.Screen.Changed -= Resize;
            context.Screen.SetProgramKeepsHistory(false);

            (io.Input as IUnblockableInput)?.UnblockPendingReads();
            handles.CloseInputWrite();
            inputPump.Join(200);

            // Order matters, in both directions. ClosePseudoConsole does not return until the
            // console's remaining output has been consumed, so the pump has to still be running
            // when it is called — joining first would deadlock. Equally, the read handle cannot
            // be closed until the pump has drained to end of stream, or the last of the child's
            // output is thrown away underneath it: for a command as short as `git --version`,
            // that is the entire output.
            WaitForOutputToSettle(outputPump);
            handles.CloseConsole();
            outputPump.Thread.Join(2000);
            handles.Dispose();

            io.Flush();

            // Only now. The screen is still the child's until the last frame it drew has been
            // through the pump: giving it back any earlier would place that frame against the
            // window's rows instead of the child's, which is the one thing this is here to stop.
            rawInput?.EndRawInput();

            CloseHandle(processInfo.Process);
            process?.Dispose();
        }
    }

    /// <summary>Publishes the child to the job table so <c>kill</c> and Ctrl+C can reach it.</summary>
    private static Process? AttachToJob(ProcessInformation processInfo, Job? job)
    {
        try
        {
            var process = Process.GetProcessById(processInfo.ProcessId);
            job?.Processes.Add(process);
            return process;
        }
        catch (Exception)
        {
            // The child exited before we could look it up; the raw handle still tells us the rest.
            return null;
        }
    }

    private static void KillTree(Process? process, IntPtr handle)
    {
        try
        {
            if (process is not null) { process.Kill(entireProcessTree: true); return; }
        }
        catch (Exception)
        {
            // Fall through to the handle we know is still valid.
        }

        try { TerminateProcess(handle, 1); } catch (Exception) { /* already exiting */ }
    }

    private static ProcessInformation StartChild(
        string executable,
        IReadOnlyList<string> arguments,
        ShellContext context,
        IReadOnlyDictionary<string, string> environment,
        IntPtr pseudoConsole)
    {
        var attributes = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;

        try
        {
            attributes = BuildAttributeList(pseudoConsole);
            environmentBlock = BuildEnvironmentBlock(environment);

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() },
                AttributeList = attributes,
            };

            var commandLine = (BuildCommandLine(executable, arguments) + '\0').ToCharArray();

            var started = CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                environmentBlock,
                context.WorkingDirectory,
                ref startupInfo,
                out var processInfo);

            if (!started)
            {
                var error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                throw new ShellException($"{Path.GetFileName(executable)}: {error.Message}", 126);
            }

            CloseHandle(processInfo.Thread);
            return processInfo;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
        }
    }

    private static IntPtr BuildAttributeList(IntPtr pseudoConsole)
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        var list = Marshal.AllocHGlobal(size);

        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(list);
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed");
        }

        if (!UpdateProcThreadAttribute(list, 0, (IntPtr)ProcThreadAttributePseudoConsole, pseudoConsole,
                (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            throw new InvalidOperationException("UpdateProcThreadAttribute failed");
        }

        return list;
    }

    /// <summary>CreateProcess wants one NUL-separated, NUL-NUL-terminated, sorted block of names.</summary>
    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();

        foreach (var pair in environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Key.Length == 0 || pair.Key.Contains('=')) continue;
            block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        // StringToHGlobalUni adds the terminator that closes the block.
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    /// <summary>Joins the arguments back into the single string CreateProcess takes, quoted its way.</summary>
    public static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder();
        AppendArgument(line, executable);

        foreach (var argument in arguments)
        {
            line.Append(' ');
            AppendArgument(line, argument);
        }

        return line.ToString();
    }

    private static void AppendArgument(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '\n' or '\v' or '"'))
        {
            line.Append(argument);
            return;
        }

        line.Append('"');

        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\') { backslashes++; i++; }

            if (i == argument.Length)
            {
                // Backslashes before the closing quote have to be doubled or they escape it.
                line.Append('\\', backslashes * 2);
                break;
            }

            line.Append('\\', argument[i] == '"' ? backslashes * 2 + 1 : backslashes);
            line.Append(argument[i]);
        }

        line.Append('"');
    }

    /// <summary>
    /// Carries the child's output to the screen, and remembers when it last saw any, so the
    /// caller can tell the difference between "finished" and "not finished yet".
    /// </summary>
    private sealed class OutputPump
    {
        private long _lastActivity = Environment.TickCount64;

        public Thread Thread { get; init; } = null!;

        public bool Finished { get; private set; }

        public long IdleMilliseconds => Environment.TickCount64 - Interlocked.Read(ref _lastActivity);

        public void MarkActivity() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

        public void MarkFinished() => Finished = true;
    }

    /// <summary>
    /// Where to append every byte a hosted program writes, or null when nothing is being traced.
    ///
    /// What a program sends is never what you would predict, and every terminal fix in this
    /// codebase came from reading a real one rather than from the specification. Set
    /// <c>STT_PTY_TRACE</c> to a file, run the program, and inventory the escape sequences in
    /// what lands there.
    /// </summary>
    private static string? TracePath()
    {
        var path = Environment.GetEnvironmentVariable("STT_PTY_TRACE");
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static void Trace(string? path, byte[] buffer, int count)
    {
        if (path is null) return;

        try
        {
            using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            file.Write(buffer, 0, count);
        }
        catch (Exception)
        {
            // Tracing is a debugging aid; never let it take the session down with it.
        }
    }

    private static OutputPump PumpToStream(IntPtr source, Stream destination)
    {
        OutputPump pump = null!;

        // The console opens by clearing the screen it thinks it owns. On a screen that already
        // has the session's history on it, that has to go.
        var filtered = new ConsoleStartupFilter(destination);
        var trace = TracePath();

        var thread = new Thread(() =>
        {
            var buffer = new byte[8192];
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);

            try
            {
                while (ReadFile(source, pinned.AddrOfPinnedObject(), buffer.Length, out var read, IntPtr.Zero) && read > 0)
                {
                    pump.MarkActivity();
                    Trace(trace, buffer, read);
                    filtered.Write(buffer, 0, read);
                    filtered.Flush();
                    pump.MarkActivity();
                }
            }
            catch (Exception)
            {
                // The console closed underneath us; nothing useful to report.
            }
            finally
            {
                try { filtered.Complete(); filtered.Flush(); } catch (Exception) { }
                pump.MarkFinished();
                pinned.Free();
            }
        })
        { IsBackground = true, Name = "stt-pty-out" };

        pump = new OutputPump { Thread = thread };
        thread.Start();
        return pump;
    }

    /// <summary>
    /// Waits for the console to stop producing output before the session is closed.
    ///
    /// A pseudo-console renders on its own schedule, a frame at a time, and closing it does not
    /// flush what has not been drawn yet. A command that finishes as fast as it starts — the
    /// usual <c>git --version</c> — therefore exits before its own output has been rendered, and
    /// closing the console at that moment loses the lot. Waiting for a short quiet spell is the
    /// only signal available that the last frame has been and gone.
    /// </summary>
    private static void WaitForOutputToSettle(OutputPump pump)
    {
        const int quietMilliseconds = 120;
        const int capMilliseconds = 2000;

        var deadline = Environment.TickCount64 + capMilliseconds;

        while (!pump.Finished && Environment.TickCount64 < deadline)
        {
            if (pump.IdleMilliseconds >= quietMilliseconds) return;
            Thread.Sleep(15);
        }
    }

    private static Thread PumpToPipe(Stream source, PipeEnd destination)
    {
        var thread = new Thread(() =>
        {
            var buffer = new byte[4096];
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);

            try
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (!destination.Write(pinned.AddrOfPinnedObject(), read)) break;
                }
            }
            catch (Exception)
            {
                // The child closed its input, or the shell released our blocked read.
            }
            finally
            {
                pinned.Free();
            }
        })
        { IsBackground = true, Name = "stt-pty-in" };

        thread.Start();
        return thread;
    }

    /// <summary>The write end of the console's input pipe, guarded so the pump cannot use it after it closes.</summary>
    private sealed class PipeEnd
    {
        private readonly object _gate = new();
        private IntPtr _handle;

        public PipeEnd(IntPtr handle) => _handle = handle;

        public bool Write(IntPtr buffer, int count)
        {
            lock (_gate)
            {
                if (_handle == IntPtr.Zero) return false;

                var offset = 0;

                while (offset < count)
                {
                    if (!WriteFile(_handle, buffer + offset, count - offset, out var written, IntPtr.Zero) || written <= 0)
                        return false;

                    offset += written;
                }

                return true;
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                if (_handle == IntPtr.Zero) return;
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    /// <summary>The pipes and console handle belonging to one child, closed in the order the API needs.</summary>
    private sealed class PtyHandles : IDisposable
    {
        private IntPtr _inputRead = IntPtr.Zero;
        private IntPtr _outputWrite = IntPtr.Zero;
        private IntPtr _outputRead = IntPtr.Zero;

        public IntPtr PseudoConsole { get; private set; } = IntPtr.Zero;

        public IntPtr OutputRead => _outputRead;

        public PipeEnd InputWrite { get; private set; } = new(IntPtr.Zero);

        public bool Create(int columns, int rows)
        {
            if (!CreatePipe(out _inputRead, out var inputWrite, IntPtr.Zero, 0)) return false;

            InputWrite = new PipeEnd(inputWrite);

            if (!CreatePipe(out _outputRead, out _outputWrite, IntPtr.Zero, 0)) return false;

            var size = new Coord { X = (short)Math.Clamp(columns, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) };

            if (CreatePseudoConsole(size, _inputRead, _outputWrite, 0, out var console) != 0) return false;

            PseudoConsole = console;
            return true;
        }

        public void CloseConsoleSides()
        {
            CloseAndClear(ref _inputRead);
            CloseAndClear(ref _outputWrite);
        }

        public void CloseInputWrite() => InputWrite.Close();

        public void Resize(int columns, int rows)
        {
            if (PseudoConsole == IntPtr.Zero) return;

            var size = new Coord { X = (short)Math.Clamp(columns, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) };
            try { ResizePseudoConsole(PseudoConsole, size); } catch (Exception) { /* the console is going away */ }
        }

        /// <summary>Ends the session and lets the child's remaining output reach the read pipe.</summary>
        public void CloseConsole()
        {
            if (PseudoConsole == IntPtr.Zero) return;

            ClosePseudoConsole(PseudoConsole);
            PseudoConsole = IntPtr.Zero;
        }

        public void Dispose()
        {
            CloseConsole();
            InputWrite.Close();
            CloseAndClear(ref _inputRead);
            CloseAndClear(ref _outputWrite);
            CloseAndClear(ref _outputRead);
        }

        private static void CloseAndClear(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }
    }

    // --------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int attributeCount, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? applicationName,
        char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr file, IntPtr buffer, int count, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr file, IntPtr buffer, int count, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
