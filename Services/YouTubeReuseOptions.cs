using System;

namespace PrintStreamer.Services
{
    internal class YouTubeReuseOptions
    {
        public const string SectionName = "YouTube:Reuse";

        public bool Enabled { get; set; } = true;
        public string StoreFile { get; set; } = "youtube_reuse_store.json";
        public int TtlMinutes { get; set; } = 24 * 60;
        public bool OnlyUnlistedOrPrivateForReuse { get; set; } = true;
    }
}
