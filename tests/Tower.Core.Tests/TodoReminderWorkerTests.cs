using Tower.Core.Workers;

namespace Tower.Core.Tests;

public class TodoReminderWorkerTests
{
    [Fact]
    public void IsReminderTime_true_at_0900_colombo()
    {
        // 2026-06-20 03:30 UTC == 09:00 Sri Lanka (UTC+5:30)
        var utc = new DateTime(2026, 6, 20, 3, 30, 0, DateTimeKind.Utc);
        Assert.True(TodoReminderWorker.IsReminderTime(utc));
    }

    [Fact]
    public void IsReminderTime_false_at_0901_colombo()
    {
        var utc = new DateTime(2026, 6, 20, 3, 31, 0, DateTimeKind.Utc);
        Assert.False(TodoReminderWorker.IsReminderTime(utc));
    }

    [Fact]
    public void IsReminderTime_false_at_0800_colombo()
    {
        var utc = new DateTime(2026, 6, 20, 2, 30, 0, DateTimeKind.Utc);
        Assert.False(TodoReminderWorker.IsReminderTime(utc));
    }

    [Fact]
    public void IsReminderTime_false_at_1000_colombo()
    {
        var utc = new DateTime(2026, 6, 20, 4, 30, 0, DateTimeKind.Utc);
        Assert.False(TodoReminderWorker.IsReminderTime(utc));
    }
}
