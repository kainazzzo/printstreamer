namespace printstreamer.Components.Models;

public class TimelapseInfo
{
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public int FrameCount { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? LastFrameTime { get; set; }
    public List<string>? VideoFiles { get; set; }
    public string? YouTubeUrl { get; set; }
}
