using System.Diagnostics;
using System.Text;
using HelixPythonNamespace.Terminal.Shell.Builtins;
using HelixPythonNamespace.Terminal.Shell.Expansion;
using HelixPythonNamespace.Terminal.Shell.Lexing;
using HelixPythonNamespace.Terminal.Shell.Parsing;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Execution;

/// <summary>Walks the AST and runs it.</summary>
public sealed class Interpreter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly HashSet<string> _aliasesInProgress = new(StringComparer.Ordinal);

    public BuiltinRegistry Builtins { get; } = BuiltinRegistry.CreateDefault();

    // --------------------------------------------------------- entry points

    /// <summary>Parses and runs a script, returning the exit status of the last command.</summary>
    public int Run(string source, ShellContext context)
    {
        CompoundList program;

        try
        {
            program = Parser.ParseProgram(source);
        }
        catch (ShellSyntaxException ex)
        {
            WriteError(context, ex.Message);
            context.LastExitStatus = 2;
            return 2;
        }

        try
        {
            return Execute(program, context);
        }
        catch (ShellExitException ex)
        {
            context.ExitRequested = true;
            context.ExitCode = ex.ExitCode;
            return ex.ExitCode;
        }
        catch (FunctionReturnException ex)
        {
            return ex.ExitCode;
        }
        catch (LoopBreakException)
        {
            return context.LastExitStatus;
        }
        catch (LoopContinueException)
        {
            return context.LastExitStatus;
        }
        catch (ShellException ex)
        {
            WriteError(context, ex.Message);
            context.LastExitStatus = ex.ExitCode;
            return ex.ExitCode;
        }
        finally
        {
            context.Io.Flush();
            context.RunDeferredActions();
        }
    }

    /// <summary>Runs a script in a subshell and returns everything it wrote to stdout — this is <c>$(...)</c>.</summary>
    public string RunCapture(string script, ShellContext parent)
    {
        using var output = new MemoryStream();
        var child = parent.Fork();
        var io = new ShellIo(parent.Io.Input, output, parent.Io.Error);
        child.Io = io;

        try
        {
            child.LastExitStatus = Run(script, child);
        }
        finally
        {
            io.Flush();
        }

        parent.LastExitStatus = child.LastExitStatus;
        return Utf8NoBom.GetString(output.ToArray());
    }

    /// <summary>Runs a script in a subshell with the given stream as its standard input.</summary>
    public void RunWithInput(string script, ShellContext parent, Stream input)
    {
        var child = parent.Fork();
        var io = new ShellIo(input, parent.Io.Output, parent.Io.Error);
        child.Io = io;

        try { Run(script, child); }
        finally { io.Flush(); }
    }

    // ------------------------------------------------------------ AST walk

    public int Execute(CompoundList list, ShellContext context)
    {
        var status = context.LastExitStatus;

        foreach (var item in list.Items)
        {
            if (context.ExitRequested) break;
            ThrowIfCancelled(context);

            status = item.Background ? StartBackground(item, context) : ExecuteAndOr(item, context);
            context.LastExitStatus = status;

            if (status != 0 && context.Options.ExitOnError && !item.Background)
                throw new ShellExitException(status);
        }

        return status;
    }

    private int ExecuteAndOr(AndOrList list, ShellContext context)
    {
        var status = ExecutePipeline(list.First, context);
        context.LastExitStatus = status;

        foreach (var (op, pipeline) in list.Rest)
        {
            if (context.ExitRequested) break;

            var shouldRun = op == AndOrOperator.And ? status == 0 : status != 0;
            if (!shouldRun) continue;

            status = ExecutePipeline(pipeline, context);
            context.LastExitStatus = status;
        }

        return status;
    }

    private int StartBackground(AndOrList list, ShellContext context)
    {
        var job = context.Jobs.Add(Describe(list));
        var child = context.Fork();
        child.CancellationToken = job.Cancellation.Token;
        child.IsBackgroundJob = true;

        var thread = new Thread(() =>
        {
            try
            {
                var status = ExecuteAndOr(list, child);
                job.ExitStatus = status;
                job.State = status == 0 ? JobState.Done : JobState.Failed;
            }
            catch (Exception)
            {
                job.State = JobState.Failed;
                job.ExitStatus = 1;
            }
            finally
            {
                child.Io.Flush();
            }
        })
        { IsBackground = true, Name = "stt-job-" + job.Id };

        job.Worker = thread;
        thread.Start();

        context.LastBackgroundPid = job.PrimaryProcessId;
        context.Io.ErrorOut.WriteLine($"[{job.Id}] {job.Command}");
        context.Io.ErrorOut.Flush();
        return 0;
    }

    private int ExecutePipeline(Pipeline pipeline, ShellContext context)
    {
        var stopwatch = pipeline.Timed ? Stopwatch.StartNew() : null;
        int status;

        if (pipeline.Elements.Count == 1)
        {
            status = ExecuteCommand(pipeline.Elements[0].Command, context);
        }
        else
        {
            status = ExecuteMultiStagePipeline(pipeline, context);
        }

        if (stopwatch is not null)
        {
            stopwatch.Stop();
            var elapsed = stopwatch.Elapsed;
            context.Io.ErrorOut.WriteLine();
            context.Io.ErrorOut.WriteLine($"real\t{(int)elapsed.TotalMinutes}m{elapsed.Seconds}.{elapsed.Milliseconds:000}s");
            context.Io.ErrorOut.Flush();
        }

        return pipeline.Negated ? status == 0 ? 1 : 0 : status;
    }

    private int ExecuteMultiStagePipeline(Pipeline pipeline, ShellContext context)
    {
        var count = pipeline.Elements.Count;
        var pipes = new BlockingPipeStream[count - 1];
        for (var i = 0; i < pipes.Length; i++) pipes[i] = new BlockingPipeStream();

        var statuses = new int[count];
        var threads = new Thread[count];

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var element = pipeline.Elements[index];
            var input = index == 0 ? context.Io.Input : pipes[index - 1];
            var output = index == count - 1 ? context.Io.Output : pipes[index];
            var previous = index == 0 ? null : pipeline.Elements[index - 1];
            var error = index < count - 1 && element.PipeStandardError ? output : context.Io.Error;

            threads[index] = new Thread(() =>
            {
                var child = context.Fork();
                var io = new ShellIo(input, output, error);
                child.Io = io;

                try
                {
                    statuses[index] = ExecuteCommand(element.Command, child);
                }
                catch (ShellExitException ex)
                {
                    statuses[index] = ex.ExitCode;
                }
                catch (ShellException ex)
                {
                    WriteError(child, ex.Message);
                    statuses[index] = ex.ExitCode;
                }
                catch (Exception ex)
                {
                    WriteError(child, ex.Message);
                    statuses[index] = 1;
                }
                finally
                {
                    io.Flush();
                    child.RunDeferredActions();

                    // Closing the write end tells the next stage there is no more input.
                    if (index < count - 1) pipes[index].CloseWrite();

                    // Draining our input keeps an upstream writer from blocking forever.
                    if (index > 0) pipes[index - 1].CloseRead();
                }
            })
            { IsBackground = true, Name = $"stt-pipe-{index}" };
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        if (context.Options.PipeFail)
        {
            for (var i = count - 1; i >= 0; i--)
                if (statuses[i] != 0) return statuses[i];
        }

        return statuses[count - 1];
    }

    // ------------------------------------------------------------ commands

    public int ExecuteCommand(Command command, ShellContext context)
    {
        ThrowIfCancelled(context);

        var expander = new Expander(context);
        var baseIo = context.Io;
        ShellIo? applied = null;

        try
        {
            applied = Redirector.Apply(baseIo, command.Redirections, context, expander);
            context.Io = applied;

            return command switch
            {
                SimpleCommand simple => ExecuteSimple(simple, context, expander),
                SubshellCommand subshell => ExecuteSubshell(subshell, context),
                GroupCommand group => Execute(group.Body, context),
                IfCommand ifCommand => ExecuteIf(ifCommand, context),
                WhileCommand whileCommand => ExecuteWhile(whileCommand, context),
                ForCommand forCommand => ExecuteFor(forCommand, context, expander),
                ForArithmeticCommand forArithmetic => ExecuteForArithmetic(forArithmetic, context),
                CaseCommand caseCommand => ExecuteCase(caseCommand, context, expander),
                FunctionDefinition definition => DefineFunction(definition, context),
                ArithmeticCommand arithmetic => ExecuteArithmetic(arithmetic, context),
                ConditionalCommand conditional => ConditionalEvaluator.Evaluate(conditional.Expression, context) ? 0 : 1,
                _ => throw new ShellException("unsupported command"),
            };
        }
        finally
        {
            context.Io = baseIo;
            if (applied is not null && !ReferenceEquals(applied, baseIo)) applied.Dispose();
        }
    }

    private int ExecuteSubshell(SubshellCommand command, ShellContext context)
    {
        var child = context.Fork();
        child.Io = context.Io;

        try
        {
            return Execute(command.Body, child);
        }
        catch (ShellExitException ex)
        {
            return ex.ExitCode;
        }
        finally
        {
            child.Io.Flush();
            child.RunDeferredActions();
        }
    }

    private int ExecuteIf(IfCommand command, ShellContext context)
    {
        foreach (var (condition, body) in command.Branches)
        {
            if (Execute(condition, context) == 0) return Execute(body, context);
        }

        return command.ElseBody is null ? 0 : Execute(command.ElseBody, context);
    }

    private int ExecuteWhile(WhileCommand command, ShellContext context)
    {
        var status = 0;

        while (true)
        {
            ThrowIfCancelled(context);
            if (context.ExitRequested) break;

            var conditionStatus = Execute(command.Condition, context);
            var keepGoing = command.Until ? conditionStatus != 0 : conditionStatus == 0;
            if (!keepGoing) break;

            if (!RunLoopBody(command.Body, context, ref status)) break;
        }

        return status;
    }

    private int ExecuteFor(ForCommand command, ShellContext context, Expander expander)
    {
        var items = command.Items is null
            ? context.PositionalParameters.ToList()
            : expander.ExpandWords(command.Items);

        var status = 0;

        foreach (var item in items)
        {
            ThrowIfCancelled(context);
            if (context.ExitRequested) break;

            context.SetValue(command.Variable, item);
            if (!RunLoopBody(command.Body, context, ref status)) break;
        }

        return status;
    }

    private int ExecuteForArithmetic(ForArithmeticCommand command, ShellContext context)
    {
        var status = 0;

        if (command.Initialiser.Length > 0) ArithmeticEvaluator.Evaluate(command.Initialiser, context);

        while (true)
        {
            ThrowIfCancelled(context);
            if (context.ExitRequested) break;

            if (command.Condition.Length > 0 && ArithmeticEvaluator.Evaluate(command.Condition, context) == 0) break;

            if (!RunLoopBody(command.Body, context, ref status)) break;

            if (command.Update.Length > 0) ArithmeticEvaluator.Evaluate(command.Update, context);
        }

        return status;
    }

    /// <summary>Runs one loop iteration. Returns false when the loop should stop.</summary>
    private bool RunLoopBody(CompoundList body, ShellContext context, ref int status)
    {
        try
        {
            status = Execute(body, context);
            return true;
        }
        catch (LoopBreakException ex)
        {
            if (ex.Levels > 1) { ex.Levels--; throw; }
            return false;
        }
        catch (LoopContinueException ex)
        {
            if (ex.Levels > 1) { ex.Levels--; throw; }
            return true;
        }
    }

    private int ExecuteCase(CaseCommand command, ShellContext context, Expander expander)
    {
        var subject = expander.ExpandToString(command.Subject);
        var status = 0;
        var matching = false;

        foreach (var clause in command.Clauses)
        {
            if (!matching)
            {
                matching = clause.Patterns.Any(pattern => ShellPattern.IsMatch(subject, expander.ExpandToPattern(pattern)));
                if (!matching) continue;
            }

            status = Execute(clause.Body, context);

            switch (clause.Terminator)
            {
                case CaseTerminator.Break: return status;
                case CaseTerminator.FallThrough: continue;      // run the next body unconditionally
                case CaseTerminator.ContinueTesting: matching = false; continue;
            }
        }

        return status;
    }

    private static int DefineFunction(FunctionDefinition definition, ShellContext context)
    {
        context.Functions[definition.Name] = definition;
        return 0;
    }

    private static int ExecuteArithmetic(ArithmeticCommand command, ShellContext context)
    {
        var expander = new Expander(context);
        var expression = command.Expression.Contains('$') || command.Expression.Contains('`')
            ? expander.ExpandToString(Lexer.SubWord(command.Expression))
            : command.Expression;

        return ArithmeticEvaluator.Evaluate(expression, context) != 0 ? 0 : 1;
    }

    // ----------------------------------------------------- simple commands

    private int ExecuteSimple(SimpleCommand command, ShellContext context, Expander expander)
    {
        var words = expander.ExpandWords(command.Words);

        if (words.Count == 0)
        {
            // A bare assignment list (or a bare redirection) changes the shell and stops there.
            // `x=$(false)` still reports the substitution's status, which RunCapture propagates.
            context.LastExitStatus = 0;
            foreach (var assignment in command.Assignments) ApplyAssignment(assignment, context, expander, local: false);
            return context.LastExitStatus;
        }

        if (context.Options.Trace) WriteTrace(context, words);

        if (TryExpandAlias(words, command, context, out var aliasStatus)) return aliasStatus;

        // `D:` on its own is how cmd changes drive, and there is no program by that name to find.
        if (words.Count == 1 && words[0].Length == 2 && ShellContext.IsDriveRelative(words[0]))
            words = new List<string> { "cd", words[0] };

        var name = words[0];
        var arguments = words;

        // Function definitions and shell state live in this process, so assignments that prefix a
        // builtin or function are applied to the context and rolled back afterwards.
        if (context.Functions.TryGetValue(name, out var function))
        {
            using var scope = new TemporaryAssignments(command.Assignments, context, expander);
            return CallFunction(function, arguments, context);
        }

        if (Builtins.TryGet(name, out var builtin))
        {
            using var scope = new TemporaryAssignments(command.Assignments, context, expander);

            try
            {
                return builtin!(context, arguments, this);
            }
            catch (ShellException ex)
            {
                WriteError(context, $"{name}: {ex.Message}");
                return ex.ExitCode;
            }
            catch (IOException)
            {
                // The downstream end of a pipe closed early (`yes | head`), which is not an error.
                return 0;
            }
            finally
            {
                context.Io.Flush();
            }
        }

        var executable = ProcessRunner.Resolve(name, context);
        var operands = arguments.Skip(1).ToList();

        // cmd keeps a handful of its commands to itself rather than shipping them as programs, so
        // there is nothing on the path to find; those are handed to cmd to run.
        if (executable is null && ProcessRunner.TryUseCmdIntrinsic(name, operands, context, out var host, out var hosted))
        {
            executable = host;
            operands = hosted;
        }

        if (executable is null)
        {
            WriteError(context, $"{name}: command not found");
            return 127;
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in command.Assignments)
            environment[assignment.Name] = expander.ExpandAssignmentValue(assignment.Value);

        var status = ProcessRunner.Run(executable, operands, context, context.Io, environment, null);
        context.RunDeferredActions();
        return status;
    }

    /// <summary>Re-runs the command with the alias body substituted for its first word.</summary>
    private bool TryExpandAlias(List<string> words, SimpleCommand command, ShellContext context, out int status)
    {
        status = 0;
        var name = words[0];

        if (!context.Aliases.TryGetValue(name, out var body)) return false;
        if (!_aliasesInProgress.Add(name)) return false;

        try
        {
            var script = new StringBuilder(body);
            foreach (var argument in words.Skip(1)) script.Append(' ').Append(SingleQuote(argument));

            var program = Parser.ParseProgram(script.ToString());

            // The alias body inherits the original command's assignments and redirections.
            foreach (var item in program.Items)
            {
                foreach (var element in item.First.Elements)
                {
                    if (element.Command is not SimpleCommand simple) continue;
                    simple.Assignments.InsertRange(0, command.Assignments);
                }
            }

            status = Execute(program, context);
            return true;
        }
        catch (ShellSyntaxException ex)
        {
            WriteError(context, $"{name}: bad alias: {ex.Message}");
            status = 2;
            return true;
        }
        finally
        {
            _aliasesInProgress.Remove(name);
        }
    }

    public int CallFunction(FunctionDefinition function, IReadOnlyList<string> arguments, ShellContext context)
    {
        var savedPositional = context.PositionalParameters;
        context.PositionalParameters = arguments.Skip(1).ToList();
        context.PushScope();

        try
        {
            return ExecuteCommand(function.Body, context);
        }
        catch (FunctionReturnException ex)
        {
            return ex.ExitCode;
        }
        finally
        {
            context.PopScope();
            context.PositionalParameters = savedPositional;
        }
    }

    private static void ApplyAssignment(Assignment assignment, ShellContext context, Expander expander, bool local)
    {
        if (assignment.ArrayItems is not null)
        {
            context.SetArray(assignment.Name, expander.ExpandWords(assignment.ArrayItems), local);
            return;
        }

        var value = expander.ExpandAssignmentValue(assignment.Value);

        if (assignment.Subscript is { Length: > 0 } subscript)
        {
            var index = (int)ArithmeticEvaluator.Evaluate(subscript, context);
            context.SetArrayElement(assignment.Name, index, value);
            return;
        }

        if (assignment.Append)
        {
            var existing = context.GetValue(assignment.Name) ?? string.Empty;
            value = existing + value;
        }

        context.SetValue(assignment.Name, value, exported: context.Options.AllExport, local: local);
    }

    /// <summary>Applies <c>NAME=value cmd</c> prefixes for the duration of one builtin or function call.</summary>
    private sealed class TemporaryAssignments : IDisposable
    {
        private readonly ShellContext _context;
        private readonly List<(string Name, string? Previous, bool Existed)> _saved = new();

        public TemporaryAssignments(List<Assignment> assignments, ShellContext context, Expander expander)
        {
            _context = context;

            foreach (var assignment in assignments)
            {
                var existing = context.Lookup(assignment.Name);
                _saved.Add((assignment.Name, existing?.Value, existing is not null));
                ApplyAssignment(assignment, context, expander, local: false);
                if (context.Lookup(assignment.Name) is { } variable) variable.Exported = true;
            }
        }

        public void Dispose()
        {
            for (var i = _saved.Count - 1; i >= 0; i--)
            {
                var (name, previous, existed) = _saved[i];

                try
                {
                    if (existed && previous is not null) _context.SetValue(name, previous);
                    else if (!existed) _context.Unset(name);
                }
                catch (ShellException)
                {
                    // A readonly variable cannot be restored; leave it as it is.
                }
            }
        }
    }

    // ------------------------------------------------------------- helpers

    public static string SingleQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    private static void WriteTrace(ShellContext context, IReadOnlyList<string> words)
    {
        context.Io.ErrorOut.WriteLine("+ " + string.Join(' ', words));
        context.Io.ErrorOut.Flush();
    }

    public static void WriteError(ShellContext context, string message)
    {
        context.Io.ErrorOut.WriteLine($"{context.ShellName}: {message}");
        context.Io.ErrorOut.Flush();
    }

    private static void ThrowIfCancelled(ShellContext context)
    {
        if (context.CancellationToken.IsCancellationRequested) throw new ShellExitException(130);
    }

    /// <summary>A short label for the jobs table, rebuilt from the literal words of the first command.</summary>
    private static string Describe(AndOrList list)
    {
        var command = list.First.Elements.FirstOrDefault()?.Command;

        if (command is SimpleCommand simple && simple.Words.Count > 0)
        {
            var words = simple.Words.Select(w => w.LiteralText is { Length: > 0 } t ? t : "...");
            return string.Join(' ', words);
        }

        return command?.GetType().Name.Replace("Command", string.Empty).ToLowerInvariant() ?? "job";
    }
}
