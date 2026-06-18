namespace Tower.Core.Projects;

/// <summary>
/// Core-side mirror of the web-layer ProjectDef.
/// Used by ProjectsWorker and ProjectsOptions so Tower.Core never references Tower (the web project).
/// </summary>
public record ProjectDefCore(
    string  Name,
    string? Service,
    int?    Port,
    string? DbPath,
    string? LogDir,
    string? Url);

/// <summary>
/// Singleton options populated from TowerConfig.Projects in Program.cs (Task 9).
/// </summary>
public class ProjectsOptions
{
    public List<ProjectDefCore> Projects { get; set; } = new();
}
