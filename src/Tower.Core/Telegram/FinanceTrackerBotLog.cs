using System.Globalization;
using Microsoft.Data.Sqlite;
using Tower.Core.Projects;

namespace Tower.Core.Telegram;

public record FtBotLogEntry(DateTime CreatedAt, string ChatId, string? UserId,
    string? Sender, string Kind, string? Summary, bool Authorized);

/// <summary>
/// Reads FinanceTracker's own Telegram audit log (its <c>TelegramLogs</c> table) directly from its
/// SQLite file — FinanceTracker records, Tower only reads. The path is the FinanceTracker project's
/// DbPath from appsettings, so there is nothing extra to configure.
/// </summary>
public class FinanceTrackerBotLog
{
    private readonly string? _dbPath;

    public FinanceTrackerBotLog(ProjectsOptions projects)
    {
        _dbPath = projects.Projects
            .FirstOrDefault(p => string.Equals(p.Name, "FinanceTracker", StringComparison.OrdinalIgnoreCase))
            ?.DbPath;
    }

    public (List<FtBotLogEntry> Entries, string? Error) Read(int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(_dbPath))
            return (new(), "FinanceTracker DbPath is not configured in appsettings.");
        if (!File.Exists(_dbPath))
            return (new(), $"FinanceTracker database not found at {_dbPath}.");

        try
        {
            var entries = new List<FtBotLogEntry>();
            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT CreatedAt, ChatId, UserId, Sender, Kind, Summary, Authorized
                                FROM TelegramLogs ORDER BY Id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                entries.Add(new FtBotLogEntry(
                    ParseUtc(r.IsDBNull(0) ? null : r.GetString(0)),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? "" : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    !r.IsDBNull(6) && r.GetInt64(6) != 0));
            }
            return (entries, null);
        }
        catch (Exception ex)
        {
            return (new(), ex.Message);
        }
    }

    // FinanceTracker stores UTC as "yyyy-MM-dd HH:mm:ss[.fff]" (EF Core / SQLite datetime('now')).
    private static DateTime ParseUtc(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt : DateTime.MinValue;
}
