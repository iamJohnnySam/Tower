namespace Tower.Core.Website;

public class WebsiteOptions
{
    public string LocalPath { get; set; } = "";
    public string FtpHost { get; set; } = "";
    public string FtpRemotePath { get; set; } = "/public_html";
}
