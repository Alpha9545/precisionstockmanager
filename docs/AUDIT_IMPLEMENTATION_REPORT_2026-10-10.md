# Audit solutions — implementation report (2026-10-10)

Branch `restructure/production-workflow-v2`. **Nothing was committed, pushed or deployed. The new migration
was applied only to scratch copies. The live database was never touched; local `PlantsIMS2_Test` was only
read (sowings 653 / 936).**

## 1. Files changed and why

### New files

| File | Why |
|---|---|
| `Services/LedgerDates.cs` | Business-date rule for ledger rows; reservation release never before the reservation. |
| `Services/TrayTransferRules.cs` | Pure rules for tray adjustment, transfer and close. |
| `Data/TrayTransferRepository.cs` | Send / receive / reject / cancel, with locking and idempotency. |
| `Models/TrayTransfer.cs` | Transfer row and request. |
| `Pages/Production/MaterialStock/TrayAdjust.cshtml(.cs)` | Tray adjustment page. |
| `Pages/Production/MaterialStock/TrayTransfers.cshtml(.cs)` | Tray transfers page. |
| `Database/Migrations/2026-10-10_TrayTransfers.sql` + `_Rollback.sql` | `dbo.TrayTransfers`, 3 tray ledger types, 3 permissions, 4 grants. |
| `Database/ZZTEST_TrayUsers.sql` | Scratch-only browser-test logins (refuses non-scratch databases). |
| `docs/STOCK_DEFINITIONS.md`, `docs/PRODUCTION_PREFLIGHT.md`, this report | Documentation. |
| `tests/…/TrayTransferRulesTests.cs`, `TrayTransfersE2ETests.cs`, `TrayTransferAuthorizationTests.cs` | New tests. |
| `tests/…/IntegrationDatabase.cs` | Lets the `PSM_TEST_CONNECTION` tests run on a `PlantsIMS2_Scratch_*` copy. |

### Changed files

| File | Why |
|---|---|
| `Data/ReadyStockRepository.cs` | Movement rows take an optional business date; dispatch before Ready approval is flagged; release dating. |
| `Data/SeedlingFulfilmentRepository.cs`, `OutletSaleRepository.cs`, `OutletWastageRepository.cs`, `MotherPlantTransferRepository.cs`, `TransplantRepository.cs` | Pass their operation's business date; future dispatch dates refused. |
| `Data/TrayStockRepository.cs` | `AdjustAsync`. |
| `Data/DailyOperationsReportRepository.cs`, `Services/DailyOperationsReportRules.cs`, `Models/DailyOperationsReport.cs`, `Pages/ManagementDashboard/DailyOperationsReport.cshtml` | Owned / reserved / in-transit history; Available as the primary figure; recording-lag notes; activity by `CreatedAt`. |
| `Data/ManagementDashboardRepository.cs`, `Pages/ManagementDashboard/Index.cshtml`, `Pages/Production/ReadyStock/Index.cshtml`, `Models/DailyReport.cs` | One Ready Stock definition everywhere (WhatsApp report too). |
| `Services/MaterialStockRules.cs`, `Models/TrayStock.cs`, `Pages/Production/MaterialStock/History.cshtml.cs` | `Transplant` → Used (bug fix); new Transfer category and labels. |
| `Pages/Production/MaterialStock/Index.cshtml(.cs)` | Trays-in-transit line; links to the new pages. |
| `Authorization/FeatureAuthorizationConventions.cs`, `Authorization/MaterialStockAccess.cs`, `Pages/Shared/_Layout.cshtml`, `Program.cs` | Page rules, permission constants, menu items, DI. |
| `wwwroot/js/site.js`, `wwwroot/css/site.css` | Accessibility of phone card tables. |
| `docs/READY_STOCK_TRANSPLANTING.md`, `DAILY_OPERATIONS_REPORT.md`, `MATERIAL_STOCK.md` | 2026-10-10 sections. |
| `.claude/launch.json` | `psm-audit` preview configuration (port 5004, scratch). |
| Tests: `DailyOperationsReport{Rules,E2E}Tests`, `ReadyStockTransplantingE2ETests`, `MotherPlantSupervisorOutletSellE2ETests`, `Cutting{SowingOverage,EntryDestination,StockAreaVisibility,DeliveryHistory,ProductionFilter}E2ETests`, `DeleteIntegrationTests`, `{Pot,Tray}StockConcurrencyTests` | Model updates, new business-date tests, stale-fixture repairs (section 9). |

Vehicle Tracking, `mobile/`, `publish-live/` and the zip files are byte-identical to the Stage A snapshot.

## 2. Confirmed defects fixed

1. Ready Stock ledger rows were stamped with the recorded time, not the business date (dispatch, outlet tray sale / wastage, Mother Plant transfer, transplanting), so past-date reports put them on the wrong day.
2. Dashboard KPI and WhatsApp report showed owned stock as "Ready"; the Ready Stock page showed available stock. During a transfer they disagreed.
3. Tray history showed transplanting tray use as "Adjustment" (`MaterialStockRules.TrayCategory`).
4. There was no way to correct a tray balance or move trays between Polyhouses.
5. The role-permission test stopped at the first mismatching role, so the Sowing Supervisor and Sowing Operator differences were never checked.
6. The `PSM_TEST_CONNECTION` tests (17) could only run against `PlantsIMS2_Test`. On scratch, three of them raced other tests writing to the same copy.
7. 28 cutting tests failed on stale fixtures (missing destination Polyhouse, hard-coded pools / sowings, data growth).
8. Phone card tables lost their table semantics for screen readers (header `display:none`).

## 3. Potential issues still unresolved

* **Booking Executive** holds `Dispatch.View` and `Dispatch.Enter` in the data. They first appear between 2026-10-01 and 2026-10-04 and no script grants them. `OtherRoles_PermissionSets_AreUnaffectedByThisFix` is left failing and names them. **Owner decision.**
* The Mother Plant Supervisor can receive trays but cannot open Material Stock (no tray-view permission).
* Tray transfers may target any active, non-Outlet Polyhouse. Only the Mother Plant Supervisor role holds `TrayStock.ReceiveTransfer`, so transfers to Main Office Polyhouses can only be received by Admin.
* Ready Stock → Mother Plant **receipt** has no date field: it is dated when recorded.
* If a transfer's source pool is retired (its Polyhouse moved Areas) before a reject or cancel, the return is refused and the trays stay in transit until an Admin corrects it.
* Legacy ledger rows keep their recorded-time dates. This is by design; they are disclosed in Daily Operations notes.
* The Vehicle Tracking code is wired into this branch, but its migrations are not applied anywhere except scratch. Deployment decision needed (`PRODUCTION_PREFLIGHT.md` section 6).

## 4. Stock definitions and formulas

Full text: [STOCK_DEFINITIONS.md](STOCK_DEFINITIONS.md).

* Owned = `Quantity − Dispatched`
* Physical = `Owned − InTransit`
* Reserved = `Reserved`
* **Available for booking/dispatch** = `Physical − Reserved`

In-transit plants are counted once, in the source's Owned figure only. Browser check on scratch: the Dashboard, the Ready Stock page and Daily Operations all show 2,51,155 available = 2,51,514 physical − 359 reserved (672 in transit).

## 5. Business dates

New rows: `TransactionDate` = the business date at the recorded time of day, `CreatedAt` = when it was recorded. Future dates are refused.

* A dispatch dated before the batch's Ready approval is allowed and flagged in the remarks.
* A reservation release is never dated before its reservation.
* Legacy rows are unchanged, listed in the Daily Operations notes, and counted on the day they were recorded.

## 6. Tray adjustment and transfer

| Action | Permission and Areas | Ledger effect |
|---|---|---|
| Adjust | `TrayStock.Adjust` (Sowing Supervisor in its own Areas; Admin) | One `Adjustment` row, whole trays, reason required, never below zero, idempotent. |
| Send | `TrayStock.Transfer` (Sowing Supervisor, Office Coordinator) from Areas they may stock trays in | `TransferOut` at the date sent; trays are in transit. |
| Cancel | `TrayStock.Transfer`, same Areas as Send | `TransferReturn` to the source; reason required. |
| Receive | `TrayStock.ReceiveTransfer` (Mother Plant Supervisor) in its own Areas | `TransferIn` at the date received (not before the send); atomic, received once. |
| Reject | `TrayStock.ReceiveTransfer`, same Areas as Receive | `TransferReturn` to the source; reason required. |

## 7. Permission changes (2026-10-10 migration)

* New permissions: `TrayStock.Adjust`, `TrayStock.Transfer`, `TrayStock.ReceiveTransfer`.
* Grants:
  * Sowing Supervisor: Adjust and Transfer.
  * Office Coordinator: Transfer.
  * Mother Plant Supervisor: ReceiveTransfer.
* No other grant was changed.

**Booking Executive `TrayStock.EnterMotherPlant`: unchanged.** Its impact:

* It can add empty trays to every active Mother Plant Area Polyhouse.
* It can see Material Stock and Material History in tray-only mode (its own Areas plus all Mother Plant Areas).
* It gets no growing material.
* It gets none of the new adjust or transfer permissions.

## 8. Migrations and order

The order and dependencies are in [PRODUCTION_PREFLIGHT.md](PRODUCTION_PREFLIGHT.md) section 3b. In short:

1. BookingOrderLink Schema, then Backfill
2. DailyLabourAttendance
3. LabourOvertime
4. MaintenanceTickets
5. StoreInventory
6. SuggestionsProblems, then SuggestionsGlobalAccess
7. MaterialStock, then MaterialStockApprovals
8. ReadyStockTransplanting
9. TrayTransfers (new)

`2026-10-10_TrayTransfers.sql` was verified on scratch:

* dry run, commit, re-run (idempotent), rollback dry run, rollback, re-commit;
* the wrong-server guard refused (52003);
* the wrong-database guard refused (52001);
* the rollback refused once transfers existed (52014).

**Not applied to `PlantsIMS2_Test` or production.**

## 9. Tests

| Run | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Baseline before this work (fresh scratch) | 2203 | 2091 | 29 | 83 |
| **Final (fresh scratch `PlantsIMS2_Scratch_AuditFinal_20261009`, migrated)** | **2252** | **2185** | **1** | **66** |

* **Baseline failures:** 28 of 29 fixed.
  * 19 CuttingSowingOverage and 1 CuttingEntryDestination: the destination Polyhouse plus trays now come from the fixture.
  * 5 CuttingStockAreaVisibility, 2 CuttingDeliveryHistory and 1 CuttingProductionFilter: own-Area and other-Area checks now read from the database instead of hard-coded pools and sowings.
  * The remaining one is the permission test, now failing **only** on the Booking Executive difference, by name.
* **New failures: 0.**
* **Previously skipped, now run and pass: 17** (DeleteIntegration 14, concurrency 3, on scratch).
* **Remaining skips (66):** 63 Vehicle Tracking (paused, no migrations), 1 manual WhatsApp sender, 2 booking scenarios whose data condition is absent.
* **New tests:**
  * pure tests for dates, adjustment, transfer, close and history categories;
  * Daily Operations stock kinds and recording-lag notes;
  * E2E for adjustment, the transfer lifecycle, concurrency and idempotency;
  * business-dated transfer, transplant and dispatch;
  * HTTP authorization for both new pages.
* **Test-only correction:** one Daily Operations fixture now records `CreatedAt` at its fixture time, because activity follows the recorded time.

## 10. Mobile accessibility

Phone card tables now:

* carry explicit ARIA table roles;
* keep the header row available to screen readers (visually hidden instead of `display:none`);
* mark the visible column labels as decorative (`content: attr(data-label) / ""`);
* give validation messages their own line.

Print is unaffected (screen-only rules).

**Regression matrix:** 19 views at 375×812 in the in-app browser, as Admin, populated and empty states:

* Dashboard, Daily Operations, Ready Stock;
* Material Stock, Material History, Tray Adjust, Tray Transfers;
* Mother Plant Transfers, Transplanting and its History;
* Direct Sowing, Approvals, Cutting Stock, Stock History.

All tables stacked, all had roles, and none caused horizontal page scroll. **Emulation only — no real device was used.**

The new pages were also exercised end to end in the browser as Sowing Supervisor and Mother Plant Supervisor: refusal below zero, adjust, send, receive.

## 11. Outstanding business decisions

1. Booking Executive `Dispatch.View` / `Dispatch.Enter` (keep or remove).
2. Booking Executive `TrayStock.EnterMotherPlant` (unchanged pending approval).
3. Tray-view permission for the Mother Plant Supervisor.
4. Allowed tray-transfer destinations and who receives at Main Office Polyhouses.
5. A receipt date for Ready Stock → Mother Plant transfers.
6. Vehicle Tracking in or out of the next release.
7. Approval to apply `2026-10-10_TrayTransfers.sql` to local `PlantsIMS2_Test`. Until then, Tray Transfers shows "not set up" there, and Adjust Trays is Admin-only there because the permission does not exist yet.

## 12. Production readiness

See [PRODUCTION_PREFLIGHT.md](PRODUCTION_PREFLIGHT.md):

* stop conditions;
* environment checks;
* object-existence checks;
* ordered migrations;
* backup and restore verification;
* photo-storage settings;
* the Vehicle Tracking decision;
* smoke tests and the physical tray count;
* rollback criteria.

## 13. Confirmations

* Nothing was committed, pushed, staged or deployed.
* No production configuration or service was touched; the live database was never connected to.
* `2026-10-10_TrayTransfers.sql` ran only on `PlantsIMS2_Scratch_AuditMig_20261009`, `…AuditFull_20261009` and `…AuditFinal_20261009`.
* The Ready Stock Transplanting migration was neither re-applied nor rolled back on `PlantsIMS2_Test`.
* No opening balances were inserted.
* No historical record was corrected.
* No scratch database was deleted.
* Vehicle Tracking is untouched.
