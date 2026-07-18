# Bill Mail Importer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Scan a Gmail "Bills" folder on a schedule, turn recognized bill emails into FinanceTracker expense transactions with the raw `.eml` attached, then Trash the email — all deterministically (no AI at runtime).

**Architecture:** A Tower `BackgroundService` (`BillMailWorker`, mirrors the existing `SolarMailWorker`) reuses Tower's Gmail OAuth (`GmailReader`/`GmailTokenService`) to read the Bills label. Code-defined `BillProfile`s match sender+subject and regex-extract the paid amount. A thin `FinanceTrackerClient` POSTs to FinanceTracker's existing external REST API; FinanceTracker gains one attachment endpoint and an `.eml` viewer page.

**Tech Stack:** .NET 10, C#, EF Core (SQLite), Blazor Server (FinanceTracker), xUnit, MimeKit (new, FinanceTracker only).

## Global Constraints

- **Two repos:** Tower = `/home/atom/dev/Tower` (service `tower`, deploy `bash /home/atom/dev/Tower/deploy.sh`). FinanceTracker = `/home/atom/dev/FinanceTracker` (service `financetracker`, deploy `bash /home/atom/dev/FinanceTracker/deploy.sh`). FinanceTracker project file lives at `FinanceTracker/FinanceTracker/FinanceTracker/`.
- **Tower schema:** Tower uses `db.Database.EnsureCreated()` + manual `CREATE TABLE IF NOT EXISTS` in `src/Tower/Program.cs` for new tables — **no EF migrations**. New entities need both a DbSet/index and a raw CREATE TABLE block.
- **FinanceTracker schema:** the `Attachment` table already exists — **no migration** for these changes.
- **Amounts:** expense = **negative** `Value`. Extracted amount is positive; the worker negates it.
- **Currency:** `LKR`.
- **Secrets:** FinanceTracker base URL + API key live in Tower's **`Settings` table (plaintext)** as `financetracker.base_url` / `financetracker.api_key` — NOT the encrypted vault (a background worker can't unlock it).
- **Do not** commit/deploy until a task's tests pass. Commit per task. Deploy both services once at the end.

---

### Task 1: FinanceTracker — attachment upload endpoint

**Files:**
- Modify: `FinanceTracker/FinanceTracker/FinanceTracker/Controllers/ExternalApiController.cs`

**Interfaces:**
- Produces: `POST /api/external/transactions/{id:int}/attachment` — multipart form, field name `file`; auth via `X-Api-Key`; returns `{ attachmentId }`. Consumed by Task 6's `FinanceTrackerClient.PostAttachmentAsync`.

- [ ] **Step 1: Add the endpoint.** Append this action inside the `ExternalApiController` class (after `AddTransaction`). It reuses the existing `AuthAsync` and the `IAttachmentService` (inject it via the constructor — see Step 2). The transaction must belong to the key's user.

```csharp
    // ── Attach a file (e.g. the source bill email .eml) to a transaction ─────
    [HttpPost("transactions/{id:int}/attachment")]
    public async Task<IActionResult> AddAttachment(int id, IFormFile file)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync();
        var auth = await AuthAsync(ctx);
        if (auth == null) return Unauthorized();
        if (file == null || file.Length == 0) return BadRequest(new { error = "No file." });

        var tx = await ctx.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.UserId == auth.UserId);
        if (tx == null) return NotFound(new { error = "Transaction not found for this user." });

        await using var stream = file.OpenReadStream();
        var att = await _attachments.SaveAsync(
            "Transaction", tx.Id, auth.UserId, stream,
            file.FileName, file.ContentType ?? "application/octet-stream",
            AttachmentSource.Gmail, tx.Date);

        return Ok(new { attachmentId = att.Id });
    }
```

- [ ] **Step 2: Inject `IAttachmentService`.** Update the constructor and fields at the top of the class.

```csharp
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IFxRateService _fxRates;
    private readonly IAttachmentService _attachments;

    public ExternalApiController(IDbContextFactory<ApplicationDbContext> dbFactory,
        IFxRateService fxRates, IAttachmentService attachments)
    {
        _dbFactory = dbFactory;
        _fxRates = fxRates;
        _attachments = attachments;
    }
```

- [ ] **Step 3: Build.**

Run: `dotnet build /home/atom/dev/FinanceTracker/FinanceTracker/FinanceTracker/FinanceTracker.csproj`
Expected: Build succeeded (0 errors). `IAttachmentService` is already registered in `Program.cs`.

- [ ] **Step 4: Smoke-test the route** (optional, needs the API key). If a `financetracker.api_key` value is known, `curl -F file=@/etc/hostname -H "X-Api-Key: <key>" http://localhost:5500/api/external/transactions/999999/attachment` should return `404` (transaction not found) — proving auth + routing work without a valid tx. Skip if the service isn't running.

- [ ] **Step 5: Commit.**

```bash
cd /home/atom/dev/FinanceTracker && git add -A && git commit -m "External API: attachment upload endpoint for transactions"
```

---

### Task 2: FinanceTracker — .eml viewer

**Files:**
- Modify: `FinanceTracker/FinanceTracker/FinanceTracker/FinanceTracker.csproj` (add MimeKit)
- Create: `FinanceTracker/FinanceTracker/FinanceTracker/Components/Pages/EmlViewer.razor`
- Modify: `FinanceTracker/FinanceTracker/FinanceTracker/Components/Pages/Transactions.razor:151`
- Modify: `FinanceTracker/FinanceTracker/FinanceTracker/Components/Pages/FileAttach.razor:17`

**Interfaces:**
- Consumes: `IAttachmentService.GetForAsync` is not needed; the viewer looks the attachment up directly by id via the DbContext factory. A `.eml` attachment link points to `/attachments/eml/{id}`.

- [ ] **Step 1: Add MimeKit.** Add to the `<ItemGroup>` of PackageReferences in `FinanceTracker.csproj`:

```xml
    <PackageReference Include="MimeKit" Version="4.7.0" />
```

Run: `dotnet restore /home/atom/dev/FinanceTracker/FinanceTracker/FinanceTracker/FinanceTracker.csproj`
Expected: restore succeeds and resolves MimeKit.

- [ ] **Step 2: Create the viewer page.** It loads the attachment row, resolves the file path (same logic `AttachmentService` uses: `Storage:UploadPath` config or wwwroot), parses the `.eml` with MimeKit, and renders the HTML body inside a **sandboxed** iframe so untrusted email markup can't run scripts.

Create `Components/Pages/EmlViewer.razor`:

```razor
@page "/attachments/eml/{Id:int}"
@using FinanceTracker.Data
@using FinanceTracker.Models
@using Microsoft.EntityFrameworkCore
@inject IDbContextFactory<ApplicationDbContext> DbFactory
@inject IConfiguration Config
@inject IWebHostEnvironment Env
@rendermode InteractiveServer

<PageTitle>@_subject</PageTitle>

@if (_error != null)
{
    <p style="padding:1rem;color:#b00">@_error</p>
}
else
{
    <div style="font-family:system-ui;padding:12px 16px;border-bottom:1px solid #ddd;background:#fafafa">
        <div><strong>@_subject</strong></div>
        <div style="color:#666;font-size:.9em">@_from — @_date</div>
    </div>
    <iframe sandbox="" srcdoc="@_html" style="width:100%;height:calc(100vh - 64px);border:0"></iframe>
}

@code {
    [Parameter] public int Id { get; set; }
    private string _subject = "Email", _from = "", _date = "", _html = "";
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        await using var ctx = await DbFactory.CreateDbContextAsync();
        var att = await ctx.Attachments.FirstOrDefaultAsync(a => a.Id == Id);
        if (att == null) { _error = "Attachment not found."; return; }

        var root = Config["Storage:UploadPath"] ?? Env.WebRootPath;
        var path = Path.Combine(root, att.FilePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) { _error = "File missing on disk."; return; }

        try
        {
            var msg = await MimeKit.MimeMessage.LoadAsync(path);
            _subject = string.IsNullOrWhiteSpace(msg.Subject) ? "Email" : msg.Subject;
            _from = msg.From.ToString();
            _date = msg.Date.ToString("yyyy-MM-dd HH:mm");
            _html = msg.HtmlBody ?? System.Net.WebUtility.HtmlEncode(msg.TextBody ?? "(no body)");
        }
        catch (Exception ex) { _error = $"Could not parse email: {ex.Message}"; }
    }
}
```

- [ ] **Step 3: Route `.eml` links to the viewer.** In `Transactions.razor` line ~151, the current link is:

```razor
<a class="btn-icon" href="/@attPath" target="_blank" title="View attachment">📎</a>
```

Replace with a conditional so `.eml` opens the viewer, everything else opens raw (PDF renders inline as before). `att` is the attachment in scope there; confirm the variable names by reading the surrounding `@foreach`. General form:

```razor
@if (attPath.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
{
    <a class="btn-icon" href="/attachments/eml/@att.Id" target="_blank" title="View email">📧</a>
}
else
{
    <a class="btn-icon" href="/@attPath" target="_blank" title="View attachment">📎</a>
}
```

- [ ] **Step 4: Same treatment in `FileAttach.razor` line ~17.** Current:

```razor
<a class="doc-chip" href="/@d.FilePath" target="_blank" title="@d.OriginalFileName">📎 @Label</a>
```

Replace with:

```razor
@if (d.FilePath.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
{
    <a class="doc-chip" href="/attachments/eml/@d.Id" target="_blank" title="@d.OriginalFileName">📧 @Label</a>
}
else
{
    <a class="doc-chip" href="/@d.FilePath" target="_blank" title="@d.OriginalFileName">📎 @Label</a>
}
```

- [ ] **Step 5: Build.**

Run: `dotnet build /home/atom/dev/FinanceTracker/FinanceTracker/FinanceTracker/FinanceTracker.csproj`
Expected: Build succeeded.

- [ ] **Step 6: Commit.**

```bash
cd /home/atom/dev/FinanceTracker && git add -A && git commit -m "Attachments: .eml viewer (MimeKit, sandboxed iframe); route .eml links to it"
```

---

### Task 3: Tower — GmailReader: From header + raw message

**Files:**
- Modify: `src/Tower.Core/Gmail/GmailReader.cs`

**Interfaces:**
- Produces: `GetMessageAsync(id)` now returns `(string From, string Subject, string Body, DateTime Date)?`; new `GetRawMessageAsync(id) → byte[]?` (the RFC822 `.eml`). Consumed by Task 7.

- [ ] **Step 1: Add `From` to `GetMessageAsync`.** Change its return type and extract the `From` header alongside `Subject`. Replace the method's signature and header-parsing block:

```csharp
    public async Task<(string From, string Subject, string Body, DateTime Date)?> GetMessageAsync(string id, CancellationToken ct = default)
    {
        using var req = await AuthGet($"{Api}/messages/{id}?format=full", ct);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var payload = root.GetProperty("payload");

        string subject = "", from = "";
        if (payload.TryGetProperty("headers", out var headers))
            foreach (var h in headers.EnumerateArray())
            {
                var name = h.GetProperty("name").GetString() ?? "";
                if (name.Equals("Subject", StringComparison.OrdinalIgnoreCase))
                    subject = h.GetProperty("value").GetString() ?? "";
                else if (name.Equals("From", StringComparison.OrdinalIgnoreCase))
                    from = h.GetProperty("value").GetString() ?? "";
            }

        var date = DateTime.UtcNow;
        if (root.TryGetProperty("internalDate", out var idt) &&
            long.TryParse(idt.GetString(), out var ms))
            date = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

        var body = ExtractText(payload);
        return (from, subject, body, date);
    }
```

- [ ] **Step 2: Add `GetRawMessageAsync`.** Add this method to the class:

```csharp
    // Full RFC822 message bytes (the .eml), for archiving as an attachment.
    public async Task<byte[]?> GetRawMessageAsync(string id, CancellationToken ct = default)
    {
        using var req = await AuthGet($"{Api}/messages/{id}?format=raw", ct);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("raw", out var raw)) return null;
        var b64 = raw.GetString();
        if (string.IsNullOrEmpty(b64)) return null;
        return Convert.FromBase64String(b64.Replace('-', '+').Replace('_', '/')
            .PadRight((b64.Length + 3) / 4 * 4, '='));
    }
```

- [ ] **Step 3: Fix the one existing caller.** `SolarMailWorker` uses named tuple access (`msg.Value.Subject`, `.Body`, `.Date`) so it still compiles — but build to confirm.

Run: `dotnet build /home/atom/dev/Tower/src/Tower.Core/Tower.Core.csproj`
Expected: Build succeeded, no errors in `SolarMailWorker.cs`.

- [ ] **Step 4: Commit.**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "GmailReader: expose From header + GetRawMessageAsync (.eml bytes)"
```

---

### Task 4: Tower — ImportedBill entity + table

**Files:**
- Create: `src/Tower.Core/Models/ImportedBill.cs`
- Modify: `src/Tower.Core/Data/TowerDbContext.cs`
- Modify: `src/Tower/Program.cs` (CREATE TABLE block near the `ConversionJobs` one)

**Interfaces:**
- Produces: `ImportedBill` entity; `TowerDbContext.ImportedBills` DbSet. Consumed by Task 7.

- [ ] **Step 1: Create the entity.**

```csharp
namespace Tower.Core.Models;

public class ImportedBill
{
    public int Id { get; set; }
    public string GmailMessageId { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public int? TransactionId { get; set; }
    public DateTime ImportedAt { get; set; }
    public string? Error { get; set; }
}
```

- [ ] **Step 2: Register the DbSet + unique index** in `TowerDbContext.cs`. Add the DbSet next to the others:

```csharp
    public DbSet<Tower.Core.Models.ImportedBill> ImportedBills => Set<Tower.Core.Models.ImportedBill>();
```

And in `OnModelCreating`, add:

```csharp
        b.Entity<Tower.Core.Models.ImportedBill>().HasIndex(x => x.GmailMessageId).IsUnique();
```

- [ ] **Step 3: Add the CREATE TABLE block** in `src/Tower/Program.cs`, immediately after the existing `ConversionJobs` `ExecuteSqlRaw` block (EnsureCreated won't add tables to an existing DB):

```csharp
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ImportedBills (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            GmailMessageId TEXT NOT NULL,
            Profile TEXT NOT NULL,
            Category TEXT NOT NULL,
            Amount TEXT NOT NULL,
            Currency TEXT NOT NULL,
            TransactionId INTEGER,
            ImportedAt TEXT NOT NULL,
            Error TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_ImportedBills_GmailMessageId ON ImportedBills (GmailMessageId);");
```

(EF stores `decimal` as TEXT in SQLite by default, matching `Amount TEXT`.)

- [ ] **Step 4: Build.**

Run: `dotnet build /home/atom/dev/Tower/src/Tower/Tower.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit.**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "Add ImportedBill entity + table (bill import dedup ledger)"
```

---

### Task 5: Tower — BillProfiles + BillParser (+ test project)

**Files:**
- Create: `src/Tower.Core/Bills/BillProfiles.cs`
- Create: `tests/Tower.Tests/Tower.Tests.csproj`
- Create: `tests/Tower.Tests/BillParserTests.cs`

**Interfaces:**
- Produces: `BillProfiles.All` (list of `BillProfile`); `BillParser.TryParse(string from, string subject, string body) → (BillProfile Profile, decimal Amount)?` where `Amount` is **positive**. Consumed by Task 7.

- [ ] **Step 1: Create the test project.**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Tower.Core/Tower.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the failing tests** using the real receipt snippets (tag-stripped, entities intact — exactly what `GmailReader` yields). Create `BillParserTests.cs`:

```csharp
using Tower.Core.Bills;
using Xunit;

public class BillParserTests
{
    // Trip receipt: Estimated Fare + Surge near the top must be ignored; "Paid Amount" is the charge (incl. tip).
    const string TripBody =
        "Trip ID - 1458530325 Estimated Fare LKR 308.88 Surge LKR +100.00 " +
        "Total Trip Fare LKR 408.88 Paid Amount LKR 408.88 FriMi";

    // Delivery receipt uses the &nbsp; HTML entity between LKR and the number.
    const string DeliveryBody =
        "Order ID - 178671638 Sub Total LKR&nbsp;2970.00 Delivery Fee +LKR&nbsp;159.00 " +
        "Total LKR&nbsp;3158.00 Paid by LKR&nbsp;3158.00";

    [Fact]
    public void Trip_uses_paid_amount_not_estimate()
    {
        var r = BillParser.TryParse("support@pickme.lk", "PickMe | Email Receipt for Trip ID 1458530325", TripBody);
        Assert.NotNull(r);
        Assert.Equal("PickMe Trip", r!.Value.Profile.Name);
        Assert.Equal("Transportation", r.Value.Profile.Category);
        Assert.Equal(408.88m, r.Value.Amount);
    }

    [Fact]
    public void Delivery_handles_nbsp_and_maps_to_food()
    {
        var r = BillParser.TryParse("support@pickme.lk", "PickMe | Delivery Email Receipt for - 178671638", DeliveryBody);
        Assert.NotNull(r);
        Assert.Equal("PickMe Delivery", r!.Value.Profile.Name);
        Assert.Equal("Food", r.Value.Profile.Category);
        Assert.Equal(3158.00m, r.Value.Amount);
    }

    [Fact]
    public void Unrecognized_subject_returns_null()
    {
        var r = BillParser.TryParse("support@pickme.lk", "PickMe | Promo of the week", TripBody);
        Assert.Null(r);
    }

    [Fact]
    public void Wrong_sender_returns_null()
    {
        var r = BillParser.TryParse("noreply@uber.com", "PickMe | Email Receipt for Trip ID 1", TripBody);
        Assert.Null(r);
    }
}
```

- [ ] **Step 3: Run — expect FAIL** (type `BillParser` doesn't exist yet).

Run: `dotnet test /home/atom/dev/Tower/tests/Tower.Tests/Tower.Tests.csproj`
Expected: compile error / FAIL — `BillParser` not found.

- [ ] **Step 4: Implement `BillProfiles.cs`.**

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace Tower.Core.Bills;

public record BillProfile(
    string Name,
    string FromContains,
    Regex SubjectRegex,
    string Category,
    Regex AmountRegex,
    string Currency);

public static class BillProfiles
{
    private static Regex Rx(string p) =>
        new(p, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static readonly IReadOnlyList<BillProfile> All =
    [
        new BillProfile("PickMe Trip", "pickme.lk",
            Rx(@"^PickMe \| Email Receipt for Trip"),
            "Transportation",
            Rx(@"Paid Amount\s*LKR\s*([\d,]+\.\d{2})"),
            "LKR"),
        new BillProfile("PickMe Delivery", "pickme.lk",
            Rx(@"^PickMe \| Delivery Email Receipt"),
            "Food",
            Rx(@"Paid by\s*LKR\s*([\d,]+\.\d{2})"),
            "LKR"),
    ];
}

public static class BillParser
{
    /// <summary>Matches an email to a profile and extracts the positive paid amount, or null.</summary>
    public static (BillProfile Profile, decimal Amount)? TryParse(string from, string subject, string body)
    {
        var profile = BillProfiles.All.FirstOrDefault(p =>
            from.Contains(p.FromContains, StringComparison.OrdinalIgnoreCase) &&
            p.SubjectRegex.IsMatch(subject));
        if (profile is null) return null;

        var text = Normalize(body);
        var m = profile.AmountRegex.Match(text);
        if (!m.Success) return null;
        if (!decimal.TryParse(m.Groups[1].Value.Replace(",", ""),
                NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            return null;
        return (profile, amount);
    }

    // GmailReader yields tag-stripped HTML that still contains entities like &nbsp;. Decode the
    // couple that appear in amounts and collapse whitespace so the regexes are simple.
    private static string Normalize(string body) =>
        Regex.Replace(body.Replace("&nbsp;", " ").Replace("&amp;", "&"), @"\s+", " ");
}
```

- [ ] **Step 5: Run — expect PASS.**

Run: `dotnet test /home/atom/dev/Tower/tests/Tower.Tests/Tower.Tests.csproj`
Expected: Passed! 4 tests.

- [ ] **Step 6: Commit.**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "Bill profiles + parser (PickMe Trip→Transportation, Delivery→Food) + tests"
```

---

### Task 6: Tower — FinanceTrackerClient

**Files:**
- Create: `src/Tower.Core/Bills/FinanceTrackerClient.cs`

**Interfaces:**
- Consumes: Task 1's endpoints; `SettingsService.Get(key)`.
- Produces: `FinanceTrackerClient.IsConfigured`, `PostTransactionAsync(decimal value, string category, string? description, DateTime date, string currency, CancellationToken) → int?` (transactionId or null), `PostAttachmentAsync(int transactionId, byte[] content, string fileName, CancellationToken) → bool`. Consumed by Task 7.

- [ ] **Step 1: Create the client.**

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tower.Core.Settings;

namespace Tower.Core.Bills;

/// <summary>Thin client for FinanceTracker's external REST API. Base URL + API key come from the
/// Settings table (plaintext) — a background worker can't unlock the encrypted secrets vault.</summary>
public class FinanceTrackerClient(HttpClient http, IServiceScopeFactory scopes)
{
    private record TxReq(decimal Value, string Category, string? Description, DateTime? Date, string? Currency);

    private (string? BaseUrl, string? ApiKey) Config()
    {
        using var scope = scopes.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<SettingsService>();
        return (s.Get("financetracker.base_url"), s.Get("financetracker.api_key"));
    }

    public bool IsConfigured
    {
        get { var (b, k) = Config(); return !string.IsNullOrWhiteSpace(b) && !string.IsNullOrWhiteSpace(k); }
    }

    public async Task<int?> PostTransactionAsync(decimal value, string category, string? description,
        DateTime date, string currency, CancellationToken ct = default)
    {
        var (baseUrl, apiKey) = Config();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/external/transactions");
        req.Headers.Add("X-Api-Key", apiKey);
        req.Content = JsonContent.Create(new TxReq(value, category, description, date, currency));
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return doc.TryGetProperty("transactionId", out var id) ? id.GetInt32() : null;
    }

    public async Task<bool> PostAttachmentAsync(int transactionId, byte[] content, string fileName,
        CancellationToken ct = default)
    {
        var (baseUrl, apiKey) = Config();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)) return false;

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        form.Add(file, "file", fileName);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/api/external/transactions/{transactionId}/attachment");
        req.Headers.Add("X-Api-Key", apiKey);
        req.Content = form;
        using var resp = await http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }
}
```

- [ ] **Step 2: Build.**

Run: `dotnet build /home/atom/dev/Tower/src/Tower.Core/Tower.Core.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit.**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "FinanceTrackerClient: post transactions + attachments via external API"
```

---

### Task 7: Tower — BillMailWorker + registration

**Files:**
- Create: `src/Tower.Core/Workers/BillMailWorker.cs`
- Modify: `src/Tower/Program.cs` (DI registration near the SolarMailWorker block, lines ~150-156)

**Interfaces:**
- Consumes: `GmailReader` (Task 3), `ImportedBill`/`TowerDbContext` (Task 4), `BillParser` (Task 5), `FinanceTrackerClient` (Task 6), `GmailTokenService`, `SettingsService`.

- [ ] **Step 1: Create the worker.** Mirrors `SolarMailWorker` structure exactly.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tower.Core.Bills;
using Tower.Core.Data;
using Tower.Core.Gmail;
using Tower.Core.Models;
using Tower.Core.Settings;

namespace Tower.Core.Workers;

public class BillMailWorker(IServiceScopeFactory scopes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { await Console.Error.WriteLineAsync($"[BillMailWorker] {ex.Message}"); }

            var hours = 6;
            using (var s = scopes.CreateScope())
            {
                var v = s.ServiceProvider.GetRequiredService<SettingsService>().Get("bills.interval_hours");
                if (int.TryParse(v, out var h) && h > 0) hours = h;
            }
            await Task.Delay(TimeSpan.FromHours(hours), stoppingToken);
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var tokens = scope.ServiceProvider.GetRequiredService<GmailTokenService>();
        var reader = scope.ServiceProvider.GetRequiredService<GmailReader>();
        var ft = scope.ServiceProvider.GetRequiredService<FinanceTrackerClient>();
        if (!tokens.IsConnected || !ft.IsConfigured) return 0;

        var labelName = settings.Get("bills.label_name");
        if (string.IsNullOrWhiteSpace(labelName)) labelName = "Bills";
        var labels = await reader.ListLabelsAsync(ct);
        var labelId = labels.FirstOrDefault(l => l.Name.Equals(labelName, StringComparison.OrdinalIgnoreCase)).Id;
        if (string.IsNullOrEmpty(labelId))
        {
            settings.Set("bills.mail.last_error", $"Gmail label '{labelName}' not found");
            return 0;
        }

        var db = scope.ServiceProvider.GetRequiredService<TowerDbContext>();
        var known = db.ImportedBills.Select(x => x.GmailMessageId).ToHashSet();

        var ids = await reader.ListMessageIdsAsync(labelId, null, ct);
        int imported = 0;
        string? lastError = null;

        foreach (var id in ids)
        {
            try
            {
                if (known.Contains(id)) { await reader.TrashMessageAsync(id, ct); continue; }

                var msg = await reader.GetMessageAsync(id, ct);
                if (msg == null) continue;

                var parsed = BillParser.TryParse(msg.Value.From, msg.Value.Subject, msg.Value.Body);
                if (parsed == null) continue;                    // not a known bill — leave in place
                var (profile, amount) = parsed.Value;

                // Post the expense (negative). On failure leave the email untouched to retry next sweep.
                var txId = await ft.PostTransactionAsync(-amount, profile.Category,
                    $"{profile.Name} — {msg.Value.Subject}", msg.Value.Date, profile.Currency, ct);
                if (txId == null) { lastError = $"POST transaction failed for {id}"; continue; }

                // ponytail: dedup barrier is this local row; a crash in the gap between the remote
                // POST succeeding and this commit can duplicate one transaction. Acceptable.
                db.ImportedBills.Add(new ImportedBill
                {
                    GmailMessageId = id, Profile = profile.Name, Category = profile.Category,
                    Amount = amount, Currency = profile.Currency, TransactionId = txId,
                    ImportedAt = DateTime.UtcNow
                });
                db.SaveChanges();
                imported++;

                // Best-effort: attach the raw .eml, then delete (trash) the email.
                var raw = await reader.GetRawMessageAsync(id, ct);
                if (raw != null)
                    await ft.PostAttachmentAsync(txId.Value, raw, $"{profile.Name}-{id}.eml", ct);
                await reader.TrashMessageAsync(id, ct);
            }
            catch (Exception ex) { lastError = ex.Message; }
        }

        settings.Set("bills.mail.last_run", DateTime.UtcNow.ToString("O"));
        settings.Set("bills.mail.last_count", imported.ToString());
        settings.Set("bills.mail.last_error", lastError);
        return imported;
    }
}
```

- [ ] **Step 2: Register in `src/Tower/Program.cs`.** After the `SolarMailWorker` registration (line ~156), add:

```csharp
builder.Services.AddHttpClient<Tower.Core.Bills.FinanceTrackerClient>();
builder.Services.AddSingleton<Tower.Core.Workers.BillMailWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Tower.Core.Workers.BillMailWorker>());
```

- [ ] **Step 3: Build the whole solution.**

Run: `dotnet build /home/atom/dev/Tower/src/Tower/Tower.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit.**

```bash
cd /home/atom/dev/Tower && git add -A && git commit -m "BillMailWorker: scan Bills label, import expenses + .eml, trash; register in DI"
```

---

### Task 8: Configure, deploy, verify

**Files:** none (ops).

- [ ] **Step 1: Set Tower settings** (once). The API key is in the `/secrets` vault under FinanceTracker; ask John for the value or read it from the FinanceTracker DB (`SELECT ApiKey FROM AppSettings WHERE ApiKey IS NOT NULL;` on `/home/atom/FinanceTracker/...db` if the column is plaintext there). Then:

```bash
sqlite3 /home/atom/Tower/tower.db \
  "INSERT INTO Settings(Key,Value) VALUES('financetracker.base_url','http://192.168.1.30:5500') ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
   INSERT INTO Settings(Key,Value) VALUES('financetracker.api_key','<KEY>') ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;"
```

- [ ] **Step 2: Deploy FinanceTracker, then Tower.**

```bash
bash /home/atom/dev/FinanceTracker/deploy.sh
bash /home/atom/dev/Tower/deploy.sh
```

- [ ] **Step 3: Gmail must be reconnected first.** The refresh token is currently expired (`invalid_grant`) — until John reconnects at Tower `/gmail`, both SolarMailWorker and BillMailWorker no-op. Confirm `gmail.refresh_token` works, then watch a run:

```bash
journalctl -u tower -f | grep -i BillMailWorker
sqlite3 /home/atom/Tower/tower.db "SELECT Key,Value FROM Settings WHERE Key LIKE 'bills.mail.%';"
sqlite3 /home/atom/Tower/tower.db "SELECT GmailMessageId,Profile,Amount,TransactionId,Error FROM ImportedBills ORDER BY Id DESC LIMIT 10;"
```

- [ ] **Step 4: Verify in FinanceTracker.** Open `/transactions`, confirm PickMe expenses appear under Transportation/Food with a 📧 link that opens the `.eml` viewer, and that the source emails moved to Gmail Trash.

---

## Notes for the implementer

- **Do the tasks in order.** FinanceTracker Tasks 1–2 are independent of Tower Tasks 3–7; Task 7 depends on 3–6; Task 8 is last.
- **Verification before "done":** never claim a task passes without running its build/test command and seeing the expected output.
- The Gmail token expiry (Task 8 Step 3) is a separate operational issue (John re-auths + should move the Google OAuth consent screen to "In production" to stop the 7-day refresh-token expiry). It is **not** a code task here.
