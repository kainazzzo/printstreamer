namespace PrintStreamer.Models
{
    /// <summary>
    /// Immutable snapshot of printer state from Moonraker.
    /// This is a pure data model with no knowledge of YouTube, streaming, or timelapse logic.
    /// Used as the bridge between MoonrakerPoller (data provider) and higher-level orchestrators.
    /// </summary>
    public sealed record PrinterState
    {
        /// <summary>
        /// Printer state string from Moonraker (e.g., "printing", "paused", "idle", "error").
        /// </summary>
        public string? State { get; init; }

        /// <summary>
        /// Current job filename (gcode file being printed).
        /// Null if no job is queued or printing.
        /// </summary>
        public string? Filename { get; init; }

        /// <summary>
        /// Job queue ID from Moonraker.
        /// </summary>
        public string? JobQueueId { get; init; }

        /// <summary>
        /// Current print progress as percentage (0-100).
        /// </summary>
        public double? ProgressPercent { get; init; }

        /// <summary>
        /// Estimated time remaining for current print.
        /// </summary>
        public TimeSpan? Remaining { get; init; }

        /// <summary>
        /// Current layer number (1-indexed).
        /// </summary>
        public int? CurrentLayer { get; init; }

        /// <summary>
        /// Total layer count for current print.
        /// </summary>
        public int? TotalLayers { get; init; }

        /// <summary>
        /// Timestamp when this state snapshot was created (UTC).
        /// </summary>
        public DateTime SnapshotTime { get; init; }

        /// <summary>
        /// Helper: true if state indicates active printing or related states.
        /// </summary>
        public bool IsActivelyPrinting => State != null && PrinterStateClassifier.ActiveStates.Contains(State, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Helper: true if state indicates print is finished or error.
        /// </summary>
        public bool IsDone => State != null && PrinterStateClassifier.DoneStates.Contains(State, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classification helper for printer states.
    /// Defines which states are considered "active" (printing/paused/etc) vs "done" (idle/error/etc).
    /// </summary>
    public static class PrinterStateClassifier
    {
        /// <summary>
        /// States where print is actively happening or being managed.
        /// </summary>
        public static readonly HashSet<string> ActiveStates = new(StringComparer.OrdinalIgnoreCase)
        {
            "printing",
            "paused",
            "pausing",
            "resuming",
            "resumed",
            "cancelling",
            "finishing",
            "heating",
            "preheating",
            "cooling"
        };

        /// <summary>
        /// States where print has finished (one way or another).
        /// </summary>
        public static readonly HashSet<string> DoneStates = new(StringComparer.OrdinalIgnoreCase)
        {
            "standby",
            "idle",
            "ready",
            "complete",
            "completed",
            "success",
            "cancelled",
            "canceled",
            "error"
        };
    }

    /// <summary>
    /// Event fired when printer state changes.
    /// </summary>
    /// <param name="previousState">The previous printer state (can be null if this is the first update).</param>
    /// <param name="currentState">The current printer state.</param>
    public delegate void PrintStateChangedEventHandler(PrinterState? previousState, PrinterState currentState);

    /// <summary>
    /// Event fired when a new print job starts (transition to printing state).
    /// </summary>
    /// <param name="state">The printer state when print started.</param>
    public delegate void PrintStartedEventHandler(PrinterState state);

    /// <summary>
    /// Event fired when a print job ends (transition away from printing state).
    /// </summary>
    /// <param name="state">The printer state when print ended.</param>
    public delegate void PrintEndedEventHandler(PrinterState state);
}
