namespace Tower.Core.Jellyfin;
public record SessionInfo(
    string SessionId, string User, string Client, string Device,
    bool Playing, string MediaId, string Media, string MediaType, string SeriesName,
    int? SeasonNumber, int? EpisodeNumber, string Container, string Method,
    string VideoCodec, string AudioCodec, IReadOnlyList<string> TranscodeReasons, long Bitrate,
    int? VideoBitDepth);
