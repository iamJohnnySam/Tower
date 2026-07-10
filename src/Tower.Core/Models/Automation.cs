namespace Tower.Core.Models;

public class Automation
{
    public int    Id          { get; set; }
    public string Name        { get; set; } = "";
    public string ActionsJson { get; set; } = "[]";
}
