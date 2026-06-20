using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Models;
namespace Tower.Core.Jellyfin;
public record MediaCount(string Name, int Count);
public record UserCount(string User, int Count);
public class JellyfinStats(TowerDbContext db) {
    public List<MediaCount> TopMedia(int n) =>
        db.PlayHistory
          .GroupBy(p => p.MediaName)
          .Select(g => new { Name = g.Key, Count = g.Count() })
          .OrderByDescending(x => x.Count).Take(n)
          .AsEnumerable()
          .Select(x => new MediaCount(x.Name, x.Count))
          .ToList();
    public List<UserCount> PerUser(int n) =>
        db.PlayHistory
          .Where(p => p.UserName != null && p.UserName != "")
          .GroupBy(p => p.UserName!)
          .Select(g => new { User = g.Key, Count = g.Count() })
          .OrderByDescending(x => x.Count).Take(n)
          .AsEnumerable()
          .Select(x => new UserCount(x.User, x.Count))
          .ToList();
    public int TranscodeCount() => db.PlayHistory.Count(p => p.PlayMethod == "Transcode");
    public int TotalPlays() => db.PlayHistory.Count();
    public List<Models.PlayHistory> Recent(int n) =>
        db.PlayHistory.OrderByDescending(p => p.StartedAt).Take(n).ToList();

    public List<ConversionJob> ConversionQueue() =>
        db.ConversionJobs
          .OrderByDescending(j => j.CreatedAt)
          .ToList();

    public void RequeueJob(int jobId)
    {
        var job = db.ConversionJobs.Find(jobId);
        if (job is null) return;
        job.Status       = ConversionStatus.Queued;
        job.StartedAt    = null;
        job.CompletedAt  = null;
        job.ErrorMessage = null;
        job.TestPath     = null;
        db.SaveChanges();
    }

    public void DeleteJob(int jobId)
    {
        var job = db.ConversionJobs.Find(jobId);
        if (job is null) return;
        db.ConversionJobs.Remove(job);
        db.SaveChanges();
    }
}
