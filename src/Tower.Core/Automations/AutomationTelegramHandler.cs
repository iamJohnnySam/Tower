using Microsoft.Extensions.DependencyInjection;
using Tower.Core.Telegram;

namespace Tower.Core.Automations;

public sealed class AutomationTelegramHandler(
    TelegramHub hub,
    IServiceScopeFactory scopes)
{
    public void Register()
    {
        hub.RegisterCommandHandler("run", HandleRunAsync);   // plain "run Bedtime"
        hub.RegisterCommandHandler("/run", HandleRunAsync);  // "/run Bedtime"
    }

    private async Task HandleRunAsync(string text, long chatId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AutomationService>();

        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[1].Length == 0)
        {
            var names = (await svc.ListAsync()).Select(a => a.Name).ToList();
            await hub.SendAsync(TgAudience.Chat, chatId,
                names.Count == 0
                    ? "No automations set up yet. Create one on the Tower Automations page."
                    : $"Usage: run <name>\nAvailable: {string.Join(", ", names)}",
                null, ct);
            return;
        }

        var result = await svc.RunByNameAsync(parts[1]);
        await hub.SendAsync(TgAudience.Chat, chatId, result, null, ct);
    }
}
