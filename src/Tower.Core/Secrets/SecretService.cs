using Microsoft.EntityFrameworkCore;
using Tower.Core.Data;
using Tower.Core.Models;

namespace Tower.Core.Secrets;

// Simple project-scoped password/secret store. Values are plaintext (see Secret.cs).
public class SecretService(TowerDbContext db)
{
    public async Task<List<Secret>> AllAsync() =>
        await db.Secrets
            .OrderBy(s => s.Project).ThenBy(s => s.Label)
            .ToListAsync();

    public async Task<Secret> UpsertAsync(int id, string project, string label, string value, string? notes)
    {
        var s = id > 0 ? await db.Secrets.FindAsync(id) : null;
        if (s is null)
        {
            s = new Secret();
            db.Secrets.Add(s);
        }
        s.Project   = project.Trim();
        s.Label     = label.Trim();
        s.Value     = value;
        s.Notes     = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return s;
    }

    public async Task DeleteAsync(int id)
    {
        var s = await db.Secrets.FindAsync(id);
        if (s is null) return;
        db.Secrets.Remove(s);
        await db.SaveChangesAsync();
    }
}
