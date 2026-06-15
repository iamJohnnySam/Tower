using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
namespace Tower.Core.Jellyfin;
public record MediaCount(string Name, int Count);
public record UserCount(string User, int Count);
public class JellyfinStats(TowerDbContext db) {
    public List<MediaCount> TopMedia(int n) =>
        db.PlayHistory.GroupBy(p => p.MediaName).AsEnumerable()
          .Select(g => new MediaCount(g.Key, g.Count()))
          .OrderByDescending(x => x.Count).Take(n).ToList();
    public List<UserCount> PerUser(int n) =>
        db.PlayHistory.Where(p => p.UserName != null && p.UserName != "")
          .GroupBy(p => p.UserName!).AsEnumerable()
          .Select(g => new UserCount(g.Key, g.Count()))
          .OrderByDescending(x => x.Count).Take(n).ToList();
    public int TranscodeCount() => db.PlayHistory.Count(p => p.PlayMethod == "Transcode");
    public int TotalPlays() => db.PlayHistory.Count();
    public List<Models.PlayHistory> Recent(int n) =>
        db.PlayHistory.OrderByDescending(p => p.StartedAt).Take(n).ToList();
}
