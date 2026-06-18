namespace Tower.Core.Models;
public class PlayHistory {
    public int Id { get; set; }
    public DateTime StartedAt { get; set; }
    public string? SessionKey { get; set; }
    public string? MediaId { get; set; }
    public string MediaName { get; set; } = "";
    public string? MediaType { get; set; }
    public string? SeriesName { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? UserName { get; set; }
    public string? PlayMethod { get; set; }
    public string? TranscodeReasons { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? Container { get; set; }
    public string? ClientName { get; set; }
    public string? DeviceName { get; set; }
}
