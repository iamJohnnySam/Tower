using System.Text.Json;
using Tower.Core.Models;

namespace Tower.Core.Tuya;

// ── DTOs from Python service ─────────────────────────────────────────────────

public record TuyaDeviceDto(
    string Id,
    string Name,
    string Ip,
    string Version,
    Dictionary<string, JsonElement> Dps);

public record ScannedDevice(
    string Id,
    string Name,
    string Ip,
    string Key,
    string Version);

// ── Commands sent TO Python service ──────────────────────────────────────────

public record TuyaCommandRequest(
    Dictionary<string, object>? Dps = null,
    AcCommandPayload?           Ac  = null);

public record AcCommandPayload(
    bool?   Power = null,
    int?    Temp  = null,
    string? Mode  = null,
    string? Fan   = null);

// ── Merged view used by the Blazor page ──────────────────────────────────────

public record TuyaDeviceView(
    int                              Id,
    string                           DeviceId,
    string                           Name,
    TuyaDeviceType                   DeviceType,
    string?                          Room,
    int                              SortOrder,
    bool                             Reachable,
    Dictionary<string, JsonElement>  Dps);
