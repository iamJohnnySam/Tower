using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Tower.Core.Data;

public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
