// tests/Tower.Core.Tests/TodoTelegramHandlerTests.cs
using Tower.Core.Todo;

namespace Tower.Core.Tests;

public class TodoTelegramHandlerTests
{
    // Tests for the static date/title parser

    [Fact]
    public void ParseDeadline_no_by_returns_null_and_full_title()
    {
        var result = TodoTelegramHandler.ParseDeadline("Buy milk", out var title);
        Assert.Null(result);
        Assert.Equal("Buy milk", title);
    }

    [Fact]
    public void ParseDeadline_absolute_iso_date()
    {
        var result = TodoTelegramHandler.ParseDeadline("Submit report by 2026-08-01", out var title);
        Assert.Equal("Submit report", title);
        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), result!.Value.Date == DateTime.MinValue.Date ? result : new DateTime(result!.Value.Year, result.Value.Month, result.Value.Day, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseDeadline_month_name_date()
    {
        var result = TodoTelegramHandler.ParseDeadline("Call dentist by Dec 25", out var title);
        Assert.Equal("Call dentist", title);
        Assert.NotNull(result);
        Assert.Equal(12, result!.Value.Month);
        Assert.Equal(25, result.Value.Day);
    }

    [Fact]
    public void ParseDeadline_weekday_resolves_to_future()
    {
        // Monday is a known day name — result should be a Monday in the future
        var result = TodoTelegramHandler.ParseDeadline("Do laundry by Monday", out var title);
        Assert.Equal("Do laundry", title);
        Assert.NotNull(result);
        Assert.Equal(DayOfWeek.Monday, result!.Value.DayOfWeek);
        Assert.True(result.Value.Date >= DateTime.UtcNow.Date);
    }

    [Fact]
    public void ParseDeadline_unparseable_date_treats_whole_text_as_title()
    {
        var result = TodoTelegramHandler.ParseDeadline("Do X by whenever", out var title);
        Assert.Null(result);
        Assert.Equal("Do X by whenever", title);
    }
}
