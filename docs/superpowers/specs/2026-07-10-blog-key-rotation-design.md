# Blog API Key Rotation — Design

Date: 2026-07-10

## Problem

`BLOG_API_KEY` is a **self-issued shared bearer token** that iamJohnnySam.com's PHP
API validates. It lives in `blog_secrets.php` (a PHP file that `return`s an array)
in the parent folder of `public_html` — the same file also holds the site's DB
credentials. Client apps (NewsDigest, Editor) send the key as
`Authorization: Bearer <key>`. Today the key is static and pasted into each client.

Goal: Tower becomes the source of truth. It can rotate the key (manually or on a
schedule) by editing `blog_secrets.php` over FTP, and client apps pull the current
key from Tower at use-time so rotation causes no downtime.

## `blog_secrets.php` shape (do NOT clobber)

```php
<?php
return [
    'BLOG_API_KEY' => '<64-hex>',
    'DB_HOST' => '...',
    'DB_USER' => '...',
    'DB_PASS' => '...',
    'DB_NAME' => '...',
];
```

Rotation must preserve every other entry. **Never render the whole file from a
template** — download it, replace only the `BLOG_API_KEY` value, re-upload.

## Rotation algorithm (`BlogKeyService.RotateAsync`)

1. FTP-download the current `blog_secrets.php` text.
2. Regex-replace **only** the key value:
   pattern `('BLOG_API_KEY'\s*=>\s*')([^']*)(')` → keep g1/g3, new token in g2.
3. If the pattern is **not found**, abort with an error (do not upload) — writing a
   file without the key would break the site and drop DB creds.
4. FTP-upload the modified text back to the same remote path.
5. Only after a successful upload, persist the new key + timestamp to Settings.

Ordering guarantees no downtime: the server accepts the new key before any client
learns it; clients fetch fresh at use-time. If any step fails, the old key stays
valid everywhere.

Token format: 32 random bytes, hex (matches existing 64-hex style). Use
`RandomNumberGenerator`.

## Tower storage (Settings table, plaintext)

| Key                    | Meaning                                        |
|------------------------|------------------------------------------------|
| `blogkey.current`      | current BLOG_API_KEY value                     |
| `blogkey.rotated_at`   | ISO-8601 UTC of last rotation                  |
| `blogkey.auto_days`    | auto-rotate interval in days; empty = disabled |
| `blogkey.fetch_token`  | fixed token clients present to read the key    |
| `blogkey.remote_file`  | remote path, default `/blog_secrets.php`       |

`// ponytail: plaintext LAN blog token in Settings; tower.db is atom-only and the
key rotates. NOT the master-password vault — a background worker and the client
endpoint must read it with no human unlock, so vault encryption is impossible here.`

## Components (Tower)

- **`BlogKeyService`** (`Tower.Core/Website/`): `GenerateToken()`, `RotateAsync()`,
  `GetCurrent()`, get/set `auto_days`, ensure `fetch_token` exists (generate on
  first use if empty). Depends on `SettingsService` + `FtpSyncService`.
- **`FtpSyncService`** additions:
  - `Task<string> DownloadTextAsync(string remotePath)`
  - `Task UploadTextAsync(string remotePath, string content)`
  Reuse `CreateFtpClient(host,user,pass,acceptAnyCert)` + `GetCredentials()`.
  `remotePath` is absolute (e.g. `/blog_secrets.php`), independent of the website
  sync root.
- **`BlogKeyRotationWorker`** (`Tower.Core/Workers/`, `BackgroundService`): once a
  day, if `auto_days` set and `now - rotated_at >= auto_days`, call `RotateAsync()`.
  Log outcome. Must resolve `BlogKeyService` from a scope (SettingsService is scoped).
- **`/blog-key` Blazor page** (`Components/Pages/BlogKey.razor`): current key masked
  with reveal toggle, last-rotated, auto-rotate interval (days) editable, "Rotate
  now" button with result/error message, shows the `fetch_token` (for wiring
  clients). Match the styling/altitude of `Website.razor`. Add to nav.
- **`GET /api/blog-key`** minimal endpoint in `Program.cs`: require header
  `X-Fetch-Token` == `blogkey.fetch_token`; on match return `200 {"key":"..."}`;
  mismatch/missing → `401`; key not configured → `404`. Bound on 0.0.0.0:8888 (LAN).

## Client changes (interface contract)

Endpoint: `GET http://atom:8888/api/blog-key`, header `X-Fetch-Token: <token>`,
success `200 {"key":"<current>"}`. On any non-200, client falls back to its locally
configured key so it keeps working if Tower is down.

- **NewsDigest** (`Program.cs`): read `s["TowerKeyUrl"]` + `s["TowerFetchToken"]`
  (in gitignored `appsettings.Secrets.json`). If set, GET the key at startup and use
  it in place of `siteApiKey`; else fall back to `s["SiteApiKey"]`. Fetches fresh
  each daily run → always current.
- **Editor** (Next.js): add `lib/blogKey.ts` → `async getBlogApiKey(db): Promise<string>`
  that GETs from Tower (`process.env.TOWER_KEY_URL`, `process.env.TOWER_FETCH_TOKEN`)
  and falls back to the `api_key` row in the SQLite `settings` table. Replace the
  `api_key` reads in `app/api/publish/[url]/route.ts`,
  `app/api/publish/projects/route.ts`, `app/api/posts/[url]/route.ts` (and
  `app/api/images/upload/route.ts` if it uses the key for a Bearer call). Env in
  `.env.local` (gitignored). No long caching — fetch per publish so rotation is picked
  up immediately.

## Out of scope

- No server-side PHP changes (the site already reads `BLOG_API_KEY` from this file).
- No encryption-at-rest for `blogkey.current` (see ponytail note).
- Rotating DB creds — only `BLOG_API_KEY`.

## Verification

- `BlogKeyService`: unit-test the regex swap — given the real file shape, swapping
  preserves DB entries and changes only the key; missing-key input throws.
- End-to-end: Rotate from the page → site still serves + NewsDigest/Editor publish
  succeed with the new key.
