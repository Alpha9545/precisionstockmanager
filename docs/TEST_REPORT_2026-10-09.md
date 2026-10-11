# Plant Stock Manager — Full Test & Mobile Report (2026-10-09)

Scope: every feature and page of the application, on scratch copies of today's (migrated)
`PlantsIMS2_Test`. Vehicle Tracking is paused and was not tested or changed. Nothing was committed,
pushed or deployed; `PlantsIMS2_Test` and production were not modified by the testing.

## 1. Automated test suite

Fresh copy of `PlantsIMS2_Test` (after the 2026-10-09 transplanting migration), full suite:
**2,203 tests — 2,091 passed, 29 failed, 83 skipped.** The 29 failures are the known, pre-existing
baseline (test names identical to the baseline run before today's work); **no new failures**.

| Feature area | Test classes | Tests | Passed | Failed (known baseline) | Skipped |
|---|---:|---:|---:|---:|---:|
| Seed Stock, Direct Sowing, Ready Stock & approvals | 12 | 192 | 192 | - | - |
| Ready Stock transfer to Mother Plant & Transplanting (new) | 3 | 74 | 74 | - | - |
| Trays & Material Stock (cocopeat / pitmos / trays) | 10 | 235 | 234 | - | 1 |
| Cuttings (production, delivery, sowing) | 14 | 322 | 294 | 28 | - |
| Mother Plants | 3 | 14 | 13 | 1 | - |
| Seedling bookings, fulfilment & dispatch | 11 | 204 | 202 | - | 2 |
| Pot production, empty pots & potted plants | 8 | 177 | 175 | - | 2 |
| Outlet (sales, bookings, wastage) | 4 | 55 | 55 | - | - |
| Fertilizer | 4 | 85 | 85 | - | - |
| Labour (attendance, overtime) | 6 | 112 | 112 | - | - |
| Maintenance tickets | 2 | 54 | 54 | - | - |
| Inventory Store | 2 | 39 | 39 | - | - |
| Suggestions & Problems | 2 | 61 | 61 | - | - |
| Dashboards, Daily Operations Report, WhatsApp daily report, login history | 19 | 305 | 304 | - | 1 |
| Security: authorization & deletion rules | 3 | 94 | 80 | - | 14 |
| Vehicle Tracking (PAUSED - not tested here) | 7 | 180 | 117 | - | 63 |
| **Total** | **110** | **2203** | **2091** | **29** | **83** |

Known baseline failures (pre-existing, unrelated to today's work):
* 19 × `CuttingSowingOverageE2ETests` — stale test fixture ("Please select a Main Area / Polyhouse.").
* 5 × `CuttingStockAreaVisibilityE2ETests`, 2 × `CuttingDeliveryHistoryE2ETests`,
  1 × `CuttingEntryDestinationE2ETests`, 1 × `CuttingProductionFilterE2ETests` — cutting fixtures.
* 1 × `MotherPlantSupervisorOutletSellE2ETests.OtherRoles_PermissionSets_AreUnaffectedByThisFix` —
  pins role permission counts; already wrong before today (Booking Executive count).

Skipped (83): 63 Vehicle Tracking (paused; its migrations are not on this copy); 17 tests that only run
against the real `PlantsIMS2_Test` because they write to it (`DeleteIntegrationTests` 14, stock
concurrency 3) — deliberately not run; 3 opt-in / manual (WhatsApp manual send, 2 data-dependent E2E).

## 2. Page smoke test (every page, as System Administrator)

145 URLs (all 149 pages except the 7 paused Vehicle Tracking pages and Logout; detail pages opened
with real record ids): **0 server errors (no HTTP 500, no exception pages).**
Expected non-200 results only: Access Denied page (403), `/Error` page, and 4 detail pages opened
without an id (404 by design). 16 redirects, all intended (missing records, retired Tray Stock URLs
forwarding to Material Stock, add-user forwarding to Users).

## 3. Mobile (phone, 375 px wide)

Measured on 124 content pages in a 375 × 812 viewport.

| | Before | After |
|---|---:|---:|
| Pages wider than the screen (whole page scrolls sideways) | 0 | 0 |
| Tables wider than the screen (must be swiped sideways) | 73 tables on 60 pages | **0** |
| Tables shown as phone cards | 0 | 103 of 119 (the other 16 already fit) |

**What changed** (two static files, applied to every page automatically):
* `wwwroot/js/site.js` — on every page, data tables with a plain or two-row grouped header get each
  cell labelled with its column name (e.g. "9:00 AM · Male"); rows added later by page scripts
  (e.g. "Add another item") are labelled too.
* `wwwroot/css/site.css` — on screens up to 576 px those tables show each row as a card of
  "Column: value" lines; empty cells are hidden in card view (a month grid shows only days with
  sowing); form fields get the label above and the full card width; action buttons wrap.
* Unchanged: tablets (768 px) and desktops show the normal tables; printing / PDF unchanged; tables
  marked `no-stack`, and pages' own existing mobile layouts, are left alone.

Verified visually on phone emulation: Ready Stock (was 1,918 px, 22 columns), Sowing Monthly Report
(35-column grid, card height 877 → 148 px), Attendance History (grouped header), Outlet Direct Sale
(data-entry table, rows added by script), Transplanting pages. No JavaScript or server errors.

## 4. Features verified end-to-end today

* Daily Operations Report (any date, admin only) — 64 tests + browser check.
* Ready Stock → Mother Plant transfer → receive → transplanting → dispatch — 74 tests, full acceptance
  scenario, and a browser walk-through with three roles; stock conserved (8,700 = 7,650 + 565 + 480 + 5 lost).
* Migration `2026-10-09_ReadyStockTransplanting.sql` — applied to local `PlantsIMS2_Test` after backup
  `PlantsIMS2_Test_PreReadyStockTransplanting_20261009_174511.bak`; **not on live**.

## 5. Open items

* Live (VPS) does not have the 2026-10-09 migration (or several earlier ones — see deploy notes);
  do not deploy current code there until they are applied.
* Mother Plant polyhouses hold 0 empty trays; transplanting is refused until trays are added.
* The 29 baseline failures are long-standing test-fixture problems, not product defects found today;
  they need their own fix.
* `DeleteIntegrationTests` / concurrency tests can be run on request (they write test rows to `PlantsIMS2_Test`).
