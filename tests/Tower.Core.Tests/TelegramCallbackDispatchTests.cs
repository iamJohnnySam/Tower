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
    static TelegramHub BuildHub()
    {
        // Minimal DI for TelegramHub: needs IServiceScopeFactory, TelegramApi, LiveState, ILogger
        var services = new ServiceCollection();
        var dbOpts = new DbContextOptionsBuilder<TowerDbContext>()
            .UseSqlite("DataSource=:memory:").Options;
        services.AddSingleton(dbOpts);
        services.AddScoped<TowerDbContext>(sp =>
        {
            var db = new TowerDbContext(sp.GetRequiredService<DbContextOptions<TowerDbContext>>());
            db.Database.OpenConnection();
            db.Database.EnsureCreated();
            return db;
        });
        services.AddScoped<SettingsService>();
        services.AddScoped<SubscriberService>();
        services.AddSingleton<LiveState>();
        services.AddHttpClient<TelegramApi>();
        services.AddSingleton<TelegramApi>();
        var sp = services.BuildServiceProvider();
        return new TelegramHub(
            sp.GetRequiredService<TelegramApi>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<LiveState>(),
            NullLogger<TelegramHub>.Instance);
    }

    [Fact]
    public async Task Registered_handler_is_invoked_for_matching_prefix()
    {
        var hub = BuildHub();
        string? captured = null;
        hub.RegisterCallbackHandler("conv:convert:", (data, chatId, cbId, ct) =>
        {
            captured = data;
            return Task.CompletedTask;
        });

        var update = new ParsedUpdate(
            IsCallback: true, ChatId: 100, Text: "", Username: "", FirstName: "John",
            LastName: "", CallbackId: "cb1", CallbackData: "conv:convert:42", MessageId: 5);

        await hub.HandleIncomingAsync(update, CancellationToken.None);

        Assert.Equal("conv:convert:42", captured);
    }

    [Fact]
    public async Task Non_matching_callback_does_not_invoke_handler()
    {
        var hub = BuildHub();
        bool invoked = false;
        hub.RegisterCallbackHandler("conv:convert:", (_, _, _, _) => { invoked = true; return Task.CompletedTask; });

        var update = new ParsedUpdate(
            IsCallback: true, ChatId: 100, Text: "", Username: "", FirstName: "John",
            LastName: "", CallbackId: "cb1", CallbackData: "other:data", MessageId: 5);

        await hub.HandleIncomingAsync(update, CancellationToken.None);

        Assert.False(invoked);
    }

    [Fact]
    public async Task First_matching_prefix_wins_when_multiple_registered()
    {
        var hub = BuildHub();
        var invoked = new List<string>();
        hub.RegisterCallbackHandler("conv:", (_, _, _, _) => { invoked.Add("short"); return Task.CompletedTask; });
        hub.RegisterCallbackHandler("conv:convert:", (_, _, _, _) => { invoked.Add("long"); return Task.CompletedTask; });

        var update = new ParsedUpdate(
            IsCallback: true, ChatId: 100, Text: "", Username: "", FirstName: "John",
            LastName: "", CallbackId: "cb1", CallbackData: "conv:convert:42", MessageId: 5);

        await hub.HandleIncomingAsync(update, CancellationToken.None);

        Assert.Single(invoked);
        Assert.Equal("short", invoked[0]); // first registered wins
    }
}
