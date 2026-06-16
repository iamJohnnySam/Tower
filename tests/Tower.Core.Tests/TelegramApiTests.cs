using System.IO;
using Tower.Core.Telegram;

namespace Tower.Core.Tests;

public class TelegramApiTests
{
    private static string LoadFixture(string name)
        => File.ReadAllText(Path.Combine("Fixtures", name));

    // ─── ParseUpdates happy path ──────────────────────────────────────────────

    [Fact]
    public void ParseUpdates_ReturnsCorrectCount()
    {
        var json = LoadFixture("telegram_getupdates.json");
        var (_, updates) = UpdateParser.ParseUpdates(json);
        Assert.Equal(2, updates.Count);
    }

    [Fact]
    public void ParseUpdates_NewOffset_IsMaxUpdateIdPlusOne()
    {
        var json = LoadFixture("telegram_getupdates.json");
        var (newOffset, _) = UpdateParser.ParseUpdates(json);
        // update_ids are 1000001 and 1000002 → max+1 = 1000003
        Assert.Equal(1000003L, newOffset);
    }

    [Fact]
    public void ParseUpdates_Message_HasCorrectFields()
    {
        var json = LoadFixture("telegram_getupdates.json");
        var (_, updates) = UpdateParser.ParseUpdates(json);

        var msg = updates.First(u => !u.IsCallback);
        Assert.False(msg.IsCallback);
        Assert.Equal(111222333L, msg.ChatId);
        Assert.Equal("/status", msg.Text);
        Assert.Equal("johndoe", msg.Username);
        Assert.Equal("John", msg.FirstName);
        Assert.Equal("Doe", msg.LastName);
    }

    [Fact]
    public void ParseUpdates_Callback_HasCorrectFields()
    {
        var json = LoadFixture("telegram_getupdates.json");
        var (_, updates) = UpdateParser.ParseUpdates(json);

        var cb = updates.First(u => u.IsCallback);
        Assert.True(cb.IsCallback);
        Assert.Equal(111222333L, cb.ChatId);
        Assert.Equal("callback-abc-123", cb.CallbackId);
        Assert.Equal("ms:next", cb.CallbackData);
        Assert.Equal(99, cb.MessageId);
    }

    // ─── ParseUpdates defensive / edge cases ─────────────────────────────────

    [Fact]
    public void ParseUpdates_MalformedJson_ReturnsEmptyWithoutThrowing()
    {
        var (newOffset, updates) = UpdateParser.ParseUpdates("not valid json {{{{");
        Assert.Equal(0L, newOffset);
        Assert.Empty(updates);
    }

    [Fact]
    public void ParseUpdates_OkFalse_ReturnsEmpty()
    {
        const string json = """{"ok":false,"error_code":401,"description":"Unauthorized"}""";
        var (newOffset, updates) = UpdateParser.ParseUpdates(json);
        Assert.Equal(0L, newOffset);
        Assert.Empty(updates);
    }

    [Fact]
    public void ParseUpdates_EmptyResult_ReturnsEmpty()
    {
        const string json = """{"ok":true,"result":[]}""";
        var (newOffset, updates) = UpdateParser.ParseUpdates(json);
        Assert.Equal(0L, newOffset);
        Assert.Empty(updates);
    }

    [Fact]
    public void ParseUpdates_UpdateWithNoMessageOrCallback_IsSkipped()
    {
        const string json = """
        {
          "ok": true,
          "result": [
            { "update_id": 5, "edited_message": { "message_id": 1 } }
          ]
        }
        """;
        var (newOffset, updates) = UpdateParser.ParseUpdates(json);
        Assert.Equal(6L, newOffset);   // offset still advances
        Assert.Empty(updates);
    }
}
