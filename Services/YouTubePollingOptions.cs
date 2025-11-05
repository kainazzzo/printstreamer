namespace PrintStreamer.Services
{
    /// <summary>
    /// Configuration options for YouTube API polling behavior
    /// </summary>
    public class YouTubePollingOptions
    {
        public const string SectionName = "YouTube:Polling";

        /// <summary>
        /// Enable polling manager (set false to revert to direct calls for troubleshooting)
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Base polling interval in seconds (default: 15)
        /// </summary>
        public int BaseIntervalSeconds { get; set; } = 15;

        /// <summary>
        /// Minimum polling interval in seconds when urgent (default: 10)
        /// </summary>
        public int MinIntervalSeconds { get; set; } = 10;

        /// <summary>
        /// Maximum polling interval during idle periods (default: 60)
        /// </summary>
        public int MaxIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// Minutes of inactivity before entering idle mode (default: 5)
        /// </summary>
        public int IdleThresholdMinutes { get; set; } = 5;

        /// <summary>
        /// Exponential backoff multiplier for retries (default: 1.5)
        /// </summary>
        public double BackoffMultiplier { get; set; } = 1.5;

        /// <summary>
        /// Maximum random jitter in seconds to prevent thundering herd (default: 5)
        /// </summary>
        public int MaxJitterSeconds { get; set; } = 5;

        /// <summary>
        /// Rate limit: maximum requests per minute (default: 100, YouTube allows ~1000)
        /// </summary>
        public int RequestsPerMinute { get; set; } = 100;

        /// <summary>
        /// Cache duration for identical requests in seconds (default: 5)
        /// </summary>
        public int CacheDurationSeconds { get; set; } = 5;
    }
}
