using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.CPUJob.JobQueues;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The non-generic surface of <see cref="DrydockPipelineJob{TContext,TOutcome}"/>: the members a
/// caller can read without knowing which pipeline is running.
/// </summary>
public interface IDrydockPipelineJob
{
    /// <summary>The id <see cref="DrydockSystem.RegisterJob"/> stamped on this job, or zero until then.</summary>
    int JobId { get; }

    double SecondsSinceProgress { get; }

    /// <summary>Worst single run span in milliseconds, a lower bound on the worst tick. Sampled at each suspension.</summary>
    double WorstSliceMs { get; }

    int Slices { get; }
}

/// <summary>
/// A drydock pipeline, run a few milliseconds at a time on the main thread. The fork's first
/// <see cref="Job{T}"/>: the engine's pathfinder and the dungeon generator use the same machinery to
/// spread expensive work over ticks, and a drydock store or retrieve is the same shape of problem - a
/// couple of seconds of main-thread work that nobody but the captain who asked for it should ever feel.
///
/// <para>The job is also the pipeline's <see cref="IDrydockSlice"/>. That pairing is deliberate: the
/// only way to suspend is through methods on this object, so a pipeline written against the
/// interface cannot accidentally take a bare await, which would leave <see cref="Job{T}"/> with no
/// resume handle and hang the job forever in release.</para>
/// </summary>
public sealed class DrydockPipelineJob<TContext, TOutcome> : Job<TOutcome>, IDrydockSlice, IDrydockPipelineJob
{
    private readonly DrydockSystem _system;

    /// <summary>The pipeline this job drives: <c>RunStorePipeline</c> or <c>RunRetrievePipeline</c>.</summary>
    private readonly Func<TContext, IDrydockSlice, Task<TOutcome>> _pipeline;

    /// <summary>How many items pass between stopwatch reads. Never less than one.</summary>
    private readonly int _stride;

    /// <summary>
    /// Real time since the last <see cref="Begin"/> or <see cref="Step"/>. Wall clock rather than
    /// the job's own stopwatch, because the thing the watchdog is looking for is a job that stopped
    /// being run at all, which the job's stopwatch cannot see.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _sinceProgress = System.Diagnostics.Stopwatch.StartNew();

    public DrydockPipelineJob(
        DrydockSystem system,
        TContext context,
        double maxTime,
        int stride,
        DrydockProgressCallback? onProgress,
        CancellationToken cancellation,
        IReadOnlyList<DrydockPhase> phases,
        Func<TContext, IDrydockSlice, Task<TOutcome>> pipeline)
        : base(maxTime, cancellation)
    {
        _system = system;
        _stride = Math.Max(1, stride);
        _pipeline = pipeline;
        Context = context;
        Progress = new DrydockProgress(phases, onProgress);
    }

    public TContext Context { get; }

    public DrydockProgress Progress { get; }

    /// <summary>The id <see cref="DrydockSystem.RegisterJob"/> returned for this job. Set right after registration.</summary>
    public int JobId { get; set; }

    /// <summary>Worst single run span in milliseconds, a lower bound on the worst tick. Sampled at each suspension.</summary>
    public double WorstSliceMs { get; private set; }

    public int Slices { get; private set; }

    /// <summary>
    /// The phase that owns the span currently being timed: whichever one was open when this run
    /// started, not whichever one is open when it ends. <c>Begin</c> calls
    /// <c>Progress.BeginPhase</c> before it suspends, so reading the phase off Progress at a
    /// suspension would file every phase's tail under the phase that follows it, misattributing an
    /// expensive phase's cost to its neighbour. Null until the first phase opens.
    /// </summary>
    private DrydockPhase? _spanPhase;

    /// <summary>Per-tick, per-phase main-thread cost. See <see cref="DrydockSpanMeter"/>.</summary>
    public DrydockSpanMeter Meter { get; } = new();

    public double SecondsSinceProgress => _sinceProgress.Elapsed.TotalSeconds;

    // Explicit, because Job<T>.Cancellation is protected and a public one of the same name would
    // collide with the inherited member.
    CancellationToken IDrydockSlice.Cancellation => Cancellation;

    public async Task Begin(DrydockPhase phase, int items)
    {
        Progress.BeginPhase(phase, items);
        _sinceProgress.Restart();

        // Unconditional, not budget-conditional. A phase that starts with most of a tick's budget
        // already spent by the phase before it would otherwise overrun on its very first items,
        // and the phases whose first items are expensive are exactly the ones worth protecting.
        Sample();

        // After the sample, so the span just closed is credited to the phase that ran it.
        _spanPhase = phase;
        await SuspendNow();
    }

    public async Task Step(int index)
    {
        Progress.Advance(index);
        _sinceProgress.Restart();

        if (index % _stride != 0)
            return;

        // The engine's SuspendIfOutOfTime would do this, but it cannot tell us whether it actually
        // suspended, and the worst-slice figure is only meaningful when sampled at a real one.
        if (StopWatch.Elapsed.TotalSeconds <= MaxTime)
            return;

        Sample();
        await SuspendNow();
    }

    // No `new` modifier: Job<T> has no member named Await - it has WaitAsyncTask - so `new` would
    // be an error rather than a hide.
    public async Task<T> Await<T>(Task<T> task)
    {
        Sample();
        _sinceProgress.Restart();

        var result = await WaitAsyncTask(task);

        _sinceProgress.Restart();
        return result;
    }

    public async Task Await(Task task)
    {
        Sample();
        _sinceProgress.Restart();

        await WaitAsyncTask(task);

        _sinceProgress.Restart();
    }

    protected override async Task<TOutcome?> Process()
    {
        try
        {
            return await _pipeline(Context, this);
        }
        finally
        {
            // The last run of a pipeline ends at its return, and nothing suspends there, so without
            // this the tail is never sampled. The guard is the engine's own (Job.cs:193-200): a job
            // still Waiting reached here because an awaited task faulted, which means WaitAsyncTask
            // never got to its resume park and the stopwatch is still holding a span that contains
            // the whole off-thread wait. The fault path's own main-thread work is simply not
            // measured; there is no restart to price it against.
            if (Status != JobStatus.Waiting)
                Sample(suspending: false);

            Meter.Flush();
        }
    }

    /// <summary>
    /// Records the run span that is about to end. The engine restarts the stopwatch at the top of
    /// every run, so its elapsed time at a suspension is how long this one run held the main thread.
    /// A tick can still hold more than one run, because the queue re-runs a suspended job while its
    /// own clock has room, so this is a lower bound on the worst tick rather than the worst tick.
    /// </summary>
    private void Sample(bool suspending = true)
    {
        var ms = StopWatch.Elapsed.TotalMilliseconds;
        if (ms > WorstSliceMs)
            WorstSliceMs = ms;

        Meter.Add(_system.CurTickValue, _spanPhase, ms);

        // Only a real suspension is a slice. The pipeline's closing sample is not one, and counting
        // it would change what the shipped timing line's slices= has always meant.
        if (suspending)
            Slices++;
    }
}
