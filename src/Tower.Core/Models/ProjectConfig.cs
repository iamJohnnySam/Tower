namespace Tower.Core.Models;
public class ProjectConfig {
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Service { get; set; }
    public int? Port { get; set; }
    public string? DbPath { get; set; }
    public string? LogDir { get; set; }
    public string? Url { get; set; }
}
