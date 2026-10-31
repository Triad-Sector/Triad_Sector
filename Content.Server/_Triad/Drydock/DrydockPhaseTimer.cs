using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Phase stopwatch for the store and retrieve pipelines, so a hitch is attributed to a phase rather
/// than guessed at. <see cref="Mark"/> closes the running phase and opens the next, so the caller
/// only names boundaries.
///
/// <para>Wall clock, not game-thread time, and under slicing that stops being a footnote about two
/// phases and becomes the whole picture. Every phase now spans ticks the server spent running
/// normally, so <c>serialize=900ms</c> no longer means anyone waited 900 ms: it means the phase took
/// 900 ms of clock to get through a few milliseconds at a time. The number that says whether other
/// players felt anything is the worst single slice, which the five-argument
/// <see cref="Format(string,Guid,int,double,int)"/> puts on the same line.</para>
///
/// <para>The flat <c>phase=Nms</c> line Loki pattern-matches also gained keys beyond the original
/// phase roster - <c>freeze</c>, <c>purge</c>, <c>strip</c> and <c>unwind</c> from the slicing
/// change, and <c>validate_parse</c> from splitting the store's validate reload into an off-thread
/// parse and an atomic build-and-compare - so anything matching the old key set exactly will see
/// them.</para>
///
/// <para>Emitted at Info: a store or retrieve is a rare deliberate player action, so one line each
/// costs nothing and reaches the server log without anyone enabling debug first.</para>
/// </summary>
public sealed class DrydockPhaseTimer
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<(string Phase, long Ms)> _phases = new();
    private long _last;

    public void Mark(string phase)
    {
        var now = _watch.ElapsedMilliseconds;
        _phases.Add((phase, now - _last));
        _last = now;
    }

    public long TotalMs => _watch.ElapsedMilliseconds;

    /// <summary>
    /// One key=value line in execution order. Flat and delimiter-free so Loki can pattern-match the
    /// phases out of it without a parser.
    /// </summary>
    public string Format(string operation, Guid shipId, int entities)
    {
        var sb = new StringBuilder();
        sb.Append("Drydock timing: ").Append(operation).Append(' ').Append(shipId);
        sb.Append(" entities=").Append(entities);

        foreach (var (phase, ms) in _phases)
            sb.Append(' ').Append(phase).Append('=').Append(ms).Append("ms");

        return sb.Append(" total=").Append(TotalMs).Append("ms").ToString();
    }

    /// <summary>
    /// The same line with the two figures that actually answer "did anyone else feel this": the worst
    /// single span the pipeline held the main thread for, and how many spans it took.
    /// </summary>
    /// <remarks>
    /// The three-argument form is kept rather than replaced. A pipeline running unsliced has no slice
    /// figures to report, and a zero there would read as a claim rather than as an absence.
    /// </remarks>
    public string Format(string operation, Guid shipId, int entities, double worstSliceMs, int slices)
    {
        return Format(operation, shipId, entities)
               + $" slices={slices} worst_slice={worstSliceMs:F1}ms";
    }
}
