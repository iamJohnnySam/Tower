# Tower UI — "Cool Slate Console" redesign

A drop-in dark redesign for the Tower home-lab dashboard. Three hi-fi screens are in
**`Tower Console.dc.html`** (Home, Devices → System, Projects); the production CSS library is
**`app.css`**. This doc covers (1) the rationale + tokens, (2) how the mockup maps back to your
Blazor components, and (3) how to make the live app feel interactive on top of the new system.

---

## 1. Design rationale

**Goal:** keep the dark, GitHub-rooted, utilitarian feel but make it *calm, dense, and crafted* —
an "ops console," not a generic AI dashboard. Three moves:

- **Cooler slate surfaces.** The neutrals shift slightly blue-grey (`#0b0e14 → #11151e → #161b26`)
  instead of GitHub's warmer greys. Reads as crisper and more "instrument panel."
- **Color is information, never decoration.** One accent (`--accent #4dabf7`) for interactive/selected
  state only. Status is the three semantic hues (green/amber/red) plus a violet (`--info`) reserved
  for the *secondary* series in charts (e.g. memory vs cpu). Nothing is colored just to look nice.
- **A real elevation + token system** (below) so every surface, border, and status reads consistently
  and the whole thing can be re-themed from `:root`.

### Token set (all in `app.css :root`)
- **Surfaces:** `--bg`, `--surface-1/2/3`, `--surface-sunken` (table headers). Low → high elevation.
- **Borders:** `--border` (hairline), `--border-2` (interactive), `--line-faint` (table dividers).
- **Text:** `--text-strong` (numbers/headings) · `--text` · `--muted` · `--faint` (units/meta).
- **Accent:** `--accent` + `--accent-fg/-bg/-bd`. **Status:** `--ok / --warn / --err` each with `-bg/-bd`.
- **Type scale:** `--fs-display 26` · `h1 19` · `h2 15` · `body 14` · `sm 12.5` · `xs 11.5` · `2xs 10.5`.
  System UI stack for chrome; `--mono` for every number, ID, port, and metric.
- **Spacing:** 4-pt scale `--s1..s8`. **Radii:** `--r 10` (cards) · `--r-sm 6` (controls) · `--r-xs 4` · pill.
- **Elevation:** `--shadow-1/2`, plus a `--focus` ring token for keyboard accessibility.

---

## 2. Mapping the mockup → your Blazor components

The golden rule from your brief: **shared classes live in global `app.css`; per-component
`.razor.css` only holds layout unique to that page.** Here's the split.

### `app.css` (replace wholesale)
Already written as a drop-in. Provides the full shared component library as plain classes:
`.card`, `.badge--ok/warn/err`, `.dot--*`, `.bar`, `.btn / .btn--primary / .btn--danger / .confirm`,
`.pill`, `.table`, `.alert--*`, `.banner`, `.toggle`, `.input / .field`, `.todo`, `.empty`, `.log`,
plus `.tower-rail / .rail-link`, `.page-header / .pills`, and the `.grid-2/3/4` helpers.

### `Components/Layout/MainLayout.razor`
Keep `.tower-layout` + `.tower-main`. Wrap the `@Body` scroll region in `<div class="tower-scroll">`
and pages in `<div class="tower-page">` (or `grid-*`). The top **page header** (title + sub + live
indicator + clock) should become a small `PageHeader.razor` component using `.page-header`,
`.page-title`, `.page-sub` — it repeats on every screen.

### `Components/Layout/NavMenu.razor` (+ .css)
Swap the link markup to `.rail-link` / `.rail-link.active` (active border-bar via `::before`).
The brand mark is the little 3-bar glyph. The footer status block (`3 hosts · 11 services up`,
version, port) maps to the existing rail footer. NavMenu.razor.css now only needs the rail width
and the footer — everything else is global.

### `Components/Pages/Home.razor`  ← biggest change (new features)
New top section: **quick-action widgets** (`grid-3` of `.card`s):
- *Shutdown all Pis* — `.btn--danger` that swaps to the `.confirm` row (two-step).
- *Backup all DBs* — `.btn--primary` "Run backup now".
- *Wake / ping devices* — per-host `.dot--ok/err` rows with a "Wake" `.btn--sm`.

Then your existing blocks restyled: **Devices** status list (`.card` + `.dot` + inline sparkline),
**key metrics** (CPU/Mem area charts + multi-series Network), **alerts** (`.alert--err/warn`),
**services grid** (`.dot` chips), **public-IP/DDNS/Tailscale** strip.

New **To-Do** card (medium tier you picked): inline add-row (`.input` + date chip + Add), then
`.todo` items with `.todo-pri--high/med/low` color tabs, `.todo--overdue` red treatment, and
`.todo--done` strike-through. Back it with a tiny `TodoItem` record + a JSON file or a SQLite table
(see §3). This is the one piece that needs new persistence.

### `Components/Devices/AtomSystem.razor`
Device rail (Atom/AtomTV/AtomMiniTV) + sub-view `.pills` (System/Maintenance/Configuration; add
Storage for the server). Body: big **CPU** area chart + **per-core** mini-bar grid, **memory/swap**
as radial gauges, **temp/GPU** cards, **per-NIC** network rows, **services** grid, and the
**top-process table** (`.table` with `grid-template-columns: 2fr 1fr 1fr 1fr 1fr`). When a host is
unreachable, show the `.banner` "showing last snapshot" state instead of blanking the page.

### `Components/Pages/Projects.razor`
Status strip (counts + "Backup all DBs"), then a `grid-3` of project `.card`s — each with a status
`.badge`, port `.dot`, CPU/Mem stats, an inline sparkline, and a control row of `.btn`s
(Restart/Stop/Open/Logs; failed → Restart+Logs; stopped → Start+Logs). Below, a compact
project-process `.table`.

> The same library covers MediaBox, Communication, Jellyfin, and Settings without new primitives —
> pills + cards + tables + badges + the `.toggle` (Telegram active) + `.log` viewer + `.field`
> key/value forms. Those screens are stubbed in the mockup; say the word and I'll build them out.

---

## 3. Making it feel interactive (Blazor Server, minimal JS)

### Live updates without flicker (your hard constraint)
- **Reserve space + tabular numerals.** Everything numeric already uses `--mono` +
  `font-variant-numeric: tabular-nums`, so `33%` → `100%` never reflows. Give stat containers a
  fixed min-width where a value can grow by a digit.
- **Mutate values, not DOM.** On each SignalR push, only change the bound number / bar width /
  chart path. Don't re-render the card. The `.bar > i { transition: width .4s }` and the chart path
  give you smooth motion for free. Keep `@key` stable on list items so Blazor diffs in place.
- **Throttle to the cadence you have** (2–5 s). For sparklines, keep a fixed-length ring buffer of
  the last N samples and re-emit the SVG `path` `d` — cheap, no layout shift.

### Sparklines / gauges with no chart library
The mockup's charts are hand-built SVG (see the `spark`, `netChart`, `donut`, `coreBars` helpers in
`Tower Console.dc.html`). Port them as a `Sparkline.razor` / `Gauge.razor` that take a `double[]`
and emit the same `<svg>` (`<path>` for line+area, `stroke-dasharray/-offset` for the donut). That's
the only "JS-ish" work and it's pure markup — no JS needed.

### The interaction patterns to wire
- **Danger confirm:** keep a `bool _armed` per danger action. First click arms (show `.confirm`),
  second click runs. Auto-disarm after ~4 s or on blur. Use for Shutdown/Reboot/Stop.
- **Optimistic control buttons:** on Start/Stop/Restart, immediately flip the badge to a pending
  `.badge--warn "…"` state, then settle to ok/err when the systemd result returns. Disable the row
  while in flight.
- **To-Do:** `record TodoItem(Guid Id, string Text, DateOnly? Due, Priority Pri, bool Done)`. Persist
  to a `todos.json` (you're single-user) or a SQLite table. Derive `--todo--overdue` from
  `Due < Today && !Done`; sort overdue → today → upcoming → done. The add-row is a plain
  `@bind` input + date + `@onclick`.
- **Toggles** (Telegram active): bind `.toggle.is-on` to the bool; flip optimistically.
- **Offline state:** when a host's last-seen exceeds the threshold, render the `.banner` + freeze the
  last snapshot (dim it) rather than showing zeros — much calmer than flapping to 0%.

### Focus / a11y / keyboard
`:focus-visible` already gives every control the `--focus` ring. Make danger confirms reachable by
keyboard and the To-Do add-row submit on Enter.

### Light theme (optional)
Everything reads from `:root` tokens, so a light mode is a `[data-theme="light"]` block overriding
~12 surface/border/text vars — no component changes. Dark stays the default.

---

## How to push these further to pixel-perfect, if you want

Prompt me (or Claude in the repo) with: *"Build out MediaBox / Communication / Jellyfin / Settings
in the same Cool Slate system"*, or *"Generate Sparkline.razor and Gauge.razor from the SVG helpers
in Tower Console.dc.html"*, or *"Produce the per-component .razor.css for Home/AtomSystem/Projects
given app.css."* Each is a clean, self-contained next step.
