using System.Diagnostics;

namespace HelixPythonNamespace.Terminal.Shell.Execution;

public enum JobState
{
    Running,
    Done,
    Failed,
    Stopped,
}

public sealed class Job
{
    public int Id { get; init; }

    public string Command { get; init; } = string.Empty;

    public JobState State { get; set; } = JobState.Running;

    public int ExitStatus { get; set; }

    /// <summary>The thread running the background pipeline.</summary>
    public Thread? Worker { get; set; }

    /// <summary>External processes started by the job, so <c>kill</c> can reach them.</summary>
    public List<Process> Processes { get; } = new();

    public CancellationTokenSource Cancellation { get; } = new();

    public int? PrimaryProcessId
    {
        get
        {
            foreach (var process in Processes)
            {
                try { if (!process.HasExited) return process.Id; }
                catch (Exception) { /* already gone */ }
            }

            return null;
        }
    }
}

public sealed class JobTable
{
    private readonly object _gate = new();
    private readonly List<Job> _jobs = new();
    private int _nextId = 1;

    public Job Add(string command)
    {
        lock (_gate)
        {
            var job = new Job { Id = _nextId++, Command = command };
            _jobs.Add(job);
            return job;
        }
    }

    public IReadOnlyList<Job> All()
    {
        lock (_gate) return _jobs.ToList();
    }

    public Job? Find(int id)
    {
        lock (_gate) return _jobs.FirstOrDefault(j => j.Id == id);
    }

    public Job? Current()
    {
        lock (_gate) return _jobs.LastOrDefault(j => j.State == JobState.Running) ?? _jobs.LastOrDefault();
    }

    /// <summary>Drops finished jobs and returns them so the shell can report "[1]+ Done".</summary>
    public List<Job> Reap()
    {
        lock (_gate)
        {
            var finished = _jobs.Where(j => j.State is JobState.Done or JobState.Failed).ToList();
            foreach (var job in finished) _jobs.Remove(job);
            if (_jobs.Count == 0) _nextId = 1;
            return finished;
        }
    }

    public void KillAll()
    {
        foreach (var job in All())
        {
            job.Cancellation.Cancel();

            foreach (var process in job.Processes.ToList())
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception) { /* best effort */ }
            }
        }
    }
}
