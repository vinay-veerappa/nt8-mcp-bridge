# MCP Data-Model Endpoint — Tracked Follow-up

**Status:** 📝 Planned (not yet implemented)
**Created:** 2026-08-04
**Owner:** vveerappa
**Depends on:** `NtDrawingCore.cs` (committed) — the `NtLevelRecord` data model + `NtTagRenderer` snapshot.

---

## Goal

Expose the indicator's **data model** (semantic level data) to the NT8 MCP so an agent can read levels off a chart without scraping geometry. This is richer and cleaner than reading the canvas, and it is independent of the SharpDX rendering path.

## Why this approach

- The MCP already computes indicator values directly (`/api/indicator/values`) rather than reading draws.
- SharpDX `OnRender` drawings are invisible to `ChartControl.ChartObjects`, so canvas-scraping would not work for the new visual system.
- Reading the in-memory data model returns semantic data (`category`, `label`, `state`, `date`) — not raw pixels.
- Viewing the actual picture uses the existing chart snapshot (`/api/chart/snapshot`).

## Endpoint design

```
GET /api/indicator/levels?symbol=<instrument>
```

**Response** — array of semantic level records (matches `NtLevelRecord`):

```json
[
  {
    "key": "PDH_2026_08_03",
    "label": "PDH",
    "price": 4200.5,
    "category": "price_level",
    "scheme_color": "#38BD8A",
    "state": "active",
    "date": "2026-08-03"
  }
]
```

## Implementation notes

- **Where:** `scripts/ninjatrader/addons/McpBridgeAddOn.cs` — add a `case "/api/indicator/levels"` route alongside the existing `/api/indicator/values`.
- **Data source:** the live indicator instance's `NtTagRenderer.Snapshot()` (returns `List<NtLevelRecord>`). The MCP must locate the running indicator instance for the requested symbol (same pattern as `FindChartControl`).
- **Tag correlation:** the record `key` matches the render tag's `instance_key` (`LIBRARY_ARCHITECTURE.md §6.4`), so a rendered object correlates to its semantic record.
- **Historical retention:** the history list is configurable per template (not infinite). The endpoint returns the current snapshot; historical depth is controlled by the indicator's retention setting.

## Acceptance criteria

- [ ] `GET /api/indicator/levels?symbol=NQ 09-26` returns the live indicator's level records.
- [ ] Each record has `key`, `label`, `price`, `category`, `scheme_color`, `state`, `date`.
- [ ] `label` uses the canonical compact code from `scripts/config/abbreviations.json` (e.g. `PDH`, `NYH`, `P12L`).
- [ ] Works regardless of SharpDX rendering (data model is render-independent).
- [ ] Covered by a Python integration test in `nt8-mcp-bridge/mcp/`.

## Related

- `docs/indicators/DailyNYLevels/VISUAL_SYSTEM.md §8` — data model & history.
- `docs/indicators/DailyNYLevels/LIBRARY_ARCHITECTURE.md §5.5` — MCP data-model access.
- `scripts/ninjatrader/indicators/vinay/NtDrawingCore.cs` — `NtLevelRecord`, `NtTagRenderer`.
- `scripts/config/abbreviations.json` — canonical label codes.
