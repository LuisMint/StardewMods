using System;
using System.Diagnostics;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. A temporary diagnostic aid for comparing event-based automation against the old
/// fixed-interval polling — tracks how often automation actually runs and how much real time it costs,
/// split by which kind of pass did the work:
/// <list type="bullet">
/// <item><b>Full scans</b> — one whole-group <see cref="IMachineGroup.Automate"/> call from
/// <c>ModEntry.TryRunAutomationPass</c> (interval mode's entire polling mechanism, event-based mode's rare
/// periodic backstop, and the one-shot pass after a day starts or the config changes) — but ONLY while
/// <see cref="Models.ModConfig.ActionDelaySeconds"/> is 0. Once it's greater than zero, that same bulk
/// trigger stops calling <see cref="IMachineGroup.Automate"/> directly and instead queues each ready/empty
/// machine into its group's own FIFO queue (see <c>ModEntry.RunGroupBatch</c>, below), so this bucket goes
/// quiet and the flagged-machine batches bucket picks up everything instead.</item>
/// <item><b>Flagged-machine batches</b> — despite the name (kept for continuity with this metric's
/// history), this covers every individual <c>ModEntry.AutomateMachine</c> call made while draining a
/// group's queue (see <c>ModEntry.RunGroupBatch</c>), regardless of which trigger originally queued the
/// machine (<see cref="Patches.MachineReadyPatches"/> reporting it ready, a chest restock making it
/// feedable, or the bulk scan finding it Done/Empty): pushes its output if Done, AND feeds it fresh input if
/// Empty. Each group drains up to <see cref="Models.ModConfig.ActionsPerDelayWindow"/> machines from the
/// FRONT of its queue every <see cref="Models.ModConfig.ActionDelaySeconds"/> if configured.</item>
/// </list>
/// Separately, <b>flagged machines</b> is a plain counter of individual <see cref="Patches.MachineReadyPatches"/>
/// detections (output-ready only) — lower than the flagged-machine batch count above whenever feeds or the
/// bulk scan are also contributing, kept as its own counter mainly for historical comparison against
/// earlier designs.
///
/// Recording only accumulates while <see cref="IsRecording"/> is on, so starting/stopping (tied to the
/// perf overlay's own toggle key) always gives a clean, comparable window rather than a lifetime total.
/// </summary>
internal static class AutomationPerfTracker
{
    /*********
    ** Fields
    *********/
    /// <summary>How long the current recording window has been running.</summary>
    private static readonly Stopwatch RecordingStopwatch = new();

    /// <summary>The number of full, single-group scans recorded so far this window.</summary>
    private static long FullScanCount;

    /// <summary>The cumulative time spent in full, single-group scans so far this window, in milliseconds.</summary>
    private static double FullScanTotalMs;

    /// <summary>The number of flagged-machine batches recorded so far this window.</summary>
    private static long FlaggedBatchCount;

    /// <summary>The cumulative time spent in flagged-machine batches so far this window, in milliseconds.</summary>
    private static double FlaggedBatchTotalMs;

    /// <summary>The number of individual machine-ready detections folded into flagged-machine batches so far this window.</summary>
    private static long FlaggedMachineCount;


    /*********
    ** Accessors
    *********/
    /// <summary>Whether a recording window is currently active.</summary>
    public static bool IsRecording { get; private set; }

    /// <summary>How long the current recording window has been running.</summary>
    public static TimeSpan Elapsed => AutomationPerfTracker.RecordingStopwatch.Elapsed;

    /// <summary>The number of full, single-group scans recorded so far this window.</summary>
    public static long FullScans => AutomationPerfTracker.FullScanCount;

    /// <summary>The average time per full scan so far this window, in milliseconds.</summary>
    public static double FullScanAverageMs => AutomationPerfTracker.FullScanCount > 0 ? AutomationPerfTracker.FullScanTotalMs / AutomationPerfTracker.FullScanCount : 0;

    /// <summary>The cumulative time spent in full scans so far this window, in milliseconds.</summary>
    public static double FullScanTotalMilliseconds => AutomationPerfTracker.FullScanTotalMs;

    /// <summary>The number of flagged-machine batches recorded so far this window.</summary>
    public static long FlaggedBatches => AutomationPerfTracker.FlaggedBatchCount;

    /// <summary>The average time per flagged-machine batch so far this window, in milliseconds.</summary>
    public static double FlaggedBatchAverageMs => AutomationPerfTracker.FlaggedBatchCount > 0 ? AutomationPerfTracker.FlaggedBatchTotalMs / AutomationPerfTracker.FlaggedBatchCount : 0;

    /// <summary>The cumulative time spent in flagged-machine batches so far this window, in milliseconds.</summary>
    public static double FlaggedBatchTotalMilliseconds => AutomationPerfTracker.FlaggedBatchTotalMs;

    /// <summary>The number of individual machine-ready detections folded into flagged-machine batches so far this window.</summary>
    public static long FlaggedMachines => AutomationPerfTracker.FlaggedMachineCount;

    /// <summary>The total number of passes (of any kind) recorded so far this window.</summary>
    public static long TotalPasses => AutomationPerfTracker.FullScanCount + AutomationPerfTracker.FlaggedBatchCount;

    /// <summary>The total cumulative time spent in passes (of any kind) so far this window, in milliseconds.</summary>
    public static double TotalMilliseconds => AutomationPerfTracker.FullScanTotalMs + AutomationPerfTracker.FlaggedBatchTotalMs;

    /// <summary>What percentage of the recording window's elapsed real time was spent inside automation passes.</summary>
    public static double PercentOfElapsedTime => AutomationPerfTracker.Elapsed.TotalMilliseconds > 0 ? AutomationPerfTracker.TotalMilliseconds / AutomationPerfTracker.Elapsed.TotalMilliseconds * 100 : 0;


    /*********
    ** Public methods
    *********/
    /// <summary>Start a fresh recording window, discarding any previous counts.</summary>
    public static void StartRecording()
    {
        AutomationPerfTracker.FullScanCount = 0;
        AutomationPerfTracker.FullScanTotalMs = 0;
        AutomationPerfTracker.FlaggedBatchCount = 0;
        AutomationPerfTracker.FlaggedBatchTotalMs = 0;
        AutomationPerfTracker.FlaggedMachineCount = 0;
        AutomationPerfTracker.RecordingStopwatch.Restart();
        AutomationPerfTracker.IsRecording = true;
    }

    /// <summary>Stop the current recording window — the last-recorded counts remain readable until the next <see cref="StartRecording"/> call.</summary>
    public static void StopRecording()
    {
        AutomationPerfTracker.IsRecording = false;
        AutomationPerfTracker.RecordingStopwatch.Stop();
    }

    /// <summary>Record one full, single-group scan's elapsed time, if a recording window is active.</summary>
    /// <param name="elapsedMs">How long the scan took, in milliseconds.</param>
    public static void RecordFullScan(double elapsedMs)
    {
        if (!AutomationPerfTracker.IsRecording)
            return;

        AutomationPerfTracker.FullScanCount++;
        AutomationPerfTracker.FullScanTotalMs += elapsedMs;
    }

    /// <summary>Record one flagged-machine batch's elapsed time, if a recording window is active.</summary>
    /// <param name="elapsedMs">How long the batch took, in milliseconds.</param>
    public static void RecordFlaggedBatch(double elapsedMs)
    {
        if (!AutomationPerfTracker.IsRecording)
            return;

        AutomationPerfTracker.FlaggedBatchCount++;
        AutomationPerfTracker.FlaggedBatchTotalMs += elapsedMs;
    }

    /// <summary>Record one individual machine-ready detection, if a recording window is active.</summary>
    public static void RecordFlaggedMachine()
    {
        if (!AutomationPerfTracker.IsRecording)
            return;

        AutomationPerfTracker.FlaggedMachineCount++;
    }
}
