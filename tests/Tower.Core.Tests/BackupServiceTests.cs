using Microsoft.Data.Sqlite;
using Tower.Core.Backup;
using Xunit;

namespace Tower.Core.Tests;

public class BackupServiceTests
{
    [Fact]
    public void Snapshot_produces_readable_copy()
    {
        var src = Path.GetTempFileName() + ".db";
        var dest = Path.GetTempFileName() + ".bak";
        try
        {
            using (var c = new SqliteConnection($"Data Source={src}"))
            {
                c.Open();
                var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE t(x); INSERT INTO t VALUES(42);";
                cmd.ExecuteNonQuery();
            }

            BackupService.Snapshot(src, dest);

            Assert.True(new FileInfo(dest).Length > 0);

            using var d = new SqliteConnection($"Data Source={dest}");
            d.Open();
            var q = d.CreateCommand();
            q.CommandText = "SELECT x FROM t";
            Assert.Equal(42L, (long)q.ExecuteScalar()!);
        }
        finally
        {
            if (File.Exists(src)) File.Delete(src);
            if (File.Exists(dest)) File.Delete(dest);
        }
    }
}
