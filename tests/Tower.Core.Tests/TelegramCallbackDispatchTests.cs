// tests/Tower.Core.Tests/TelegramCallbackDispatchTests.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tower.Core.Data;
using Tower.Core.Settings;
using Tower.Core.State;
using Tower.Core.Telegram;

namespace Tower.Core.Tests;

public class TelegramCallbackDispatchTests
{
    // ChatId used as the admin in all dispatch tests
    const long AdminChatId = 100;

    static (TelegramHub hub, IServiceProvider sp) BuildHub()
    {
        // Use a shared SQLite connection so all scopes see the same in-memory data.
        var sharedConn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        sharedConn.Open();
        var dbOpts = new DbContextOptionsBuilder<TowerDbContext>()
            .UseSqlite(sharedConn).Options;
        // Ensure schema is created once on the shared connection.
        using (var seedDb = new TowerDbContext(dbOpts))
            seedDb.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton(dbOpts);
        services.AddScoped<TowerDbContext>(sp => new TowerDbContext(sp.GetRequiredService<DbContextOptions<TowerDbContext>>()));
        services.AddScoped<SettingsService>();
        services.AddScoped<SubscriberService>();
        services.AddSingleton<LiveState>();
        services.AddHttpClient<TelegramApi>();
        services.AddSingleton<TelegramApi>();
        var sp = services.BuildServiceProvider();
        var hub = new TelegramHub(
            sp.GetRequiredService<TelegramApi>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<LiveState>(),
            NullLogger<TelegramHub>.Instance);
        return (hub, sp);
    }

    /// <summary>Seeds the admin chat id so the admin-check in HandleIncomingAsync passes.</summary>
    static void SeedAdmin(IServiceProvider sp, long chatId)
    {
        using var scope = sp.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        settings.Set("telegram.admin_chat", chatId.ToString());
    }

    [Fact]
    public async Task Registered_handler_is_invoked_for_matching_prefix()
    {
        var (hub, sp) = BuildHub();
        SeedAdmin(sp, AdminChatId);
        string? captured = null;
        hub.RegisterCallbackHandler("conv:convert:", (data, chatId, cbId, ct) =>
        {
            captured = data;
            return Task.CompletedTask;
        });

        var update = new ParsedUpdate(
            IsCallback: true, ChatId: AdminChatId, Text: "", Username: "", FirstName: "John",
            LastName: "", CallbackId: "cb1", CallbackData: "conv:convert:42", MessageId: 5);

        await hub.HandleIncomingAsync(update, CancellationToken.None);

        Assert.Equal("conv:convert:42", captured);
    }

    [Fact]
    public async Task Non_matching_callback_does_not_invoke_handler()
    {
        var (hub, sp) = BuildHub();
        SeedAdmin(sp, AdminChatId);
        bool invoked = false;
        hub.RegisterCallbackHandler("conv:convert:", (_, _, _, _) => { invoked = true; return Task.CompletedTask; });

        var update = new ParsedUpdate(
            IsCallback: true, ChatId: AdminChatId, Text: "", Username: "", FirstName: "John",
            LastName: "", CallbackId: "cb1", CallbackData: "other:data", MessageId: 5);

        await hub.HandleIncomingAsync(update, CancellationToken.None);

        Assert.False(invoked);
    }

    [Fact]
    public async Task First_matching_prefix_wins_when_multiple_registered()
    {
        var (hub, sp) = BuildHub();
        SeedAdmin(sp, AdminChatId);
        var invoked = new List<string>();
        hub.RegisterCallbackHandler("conv:", (_, _, _, _) => { invoked.Add("short"); return Task.CompletedTask; });
        hub.RegisterCallbackHandler("conv:convert:", (_, _, _, _) => { invoked.Add("long"); return Task.CompletedTask; });

        var update = new ParsedUpdate(
            IsCallback: true, ChatId: AdminChatId, Text: "", Username: "", FirstName: "John",
            LastName: "", CallbackId: "cb1", CallbackData: "conv:convert:42", MessageId: 5);

        await hub.HandleIncomingAsync(update, CancellationToken.None);

        Assert.Single(invoked);
        Assert.Equal("short", invoked[0]); // first registered wins
    }
}
