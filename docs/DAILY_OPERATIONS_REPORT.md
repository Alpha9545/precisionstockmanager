# Daily Operations Report

Route: `/ManagementDashboard/DailyOperationsReport` (link: **Daily Operations Report** button in the
Management Dashboard header). **System Administrator only** — explicit `FullAccessOnlyRule` in
`Authorization/FeatureAuthorizationConventions.cs`, enforced server-side for every handler
(`OnGetAsync`, `OnGetTextAsync` .txt export). No permission code grants it; no role was changed.
The dashboard link is rendered through the same rule (`FeatureAccessService`), so a user who can open
the dashboard via `Admin.ManageAreas` but is not a full-access user does not see the link.

Read-only. No new tables, no snapshots, no migration, no background job.

## Files

| File | Role |
|------|------|
| `Data/DailyOperationsReportRepository.cs` | One batched, parameterized command for any date range (1 round trip) |
| `Services/DailyOperationsReportRules.cs` | Pure rules: per-day build, availability, employee categories, Indian numbers, plain text |
| `Models/DailyOperationsReport.cs` | Range data + per-day report model |
| `Pages/ManagementDashboard/DailyOperationsReport.cshtml(.cs)` | Page, date navigation, history, copy, print, .txt export |
| `Data/WastageRepository.cs` | Loss-source SQL extracted to `SourcesSql(indiaDays)`; the Wasted Stock report's own query text is byte-identical (guarded by a SHA-256 test) |

## Dates (India, Asia/Kolkata)

Every figure is bucketed by the **India calendar day**, independent of the web/SQL server time zone:

| Column kind | Handling |
|-------------|----------|
| `DATE`, or business date stored at midnight (`SowingDate`, `IssueDate`, `SaleDate`, `SeedlingDispatches.DispatchDate`) | used as is |
| `SYSUTCDATETIME()` timestamps (ledgers, `LoginAt`, `Dispatches.DispatchDate`, `ReadyConfirmations.ConfirmationDate`, …) | bounded by the UTC instants of the India midnights, bucketed with `DATEADD(MINUTE, +330, …)` |
| `dbo.Bookings.BookingDate` (`GETDATE()`, SQL Server local) | shifted by SQL Server's own offset (same as `BookingRepository`) |
| `dbo.PottedPlantBookings.BookingDate` (mix of UTC timestamps and midnight dates) | +05:30 never moves a midnight date, so both kinds land on the right day |

Future dates are refused (today is shown with a notice). History range ≤ 62 days; full reports ≤ 31 days.

## Figures

"Not available" = the source holds **no records before** the selected date (or none at all), so the system
cannot tell "nothing happened" from "not recorded yet". **0** = records exist and none fell on that day.

| Figure | Source | Date field | Rule | Historical reliability |
|--------|--------|-----------|------|------------------------|
| Sowing | `dbo.SeedSowings` | `SowingDate` | `SUM(QuantitySown)`, `Status <> 'Cancelled'`; Direct Seed **and** Cutting Sowing (one row per sowing → no double count) | Reliable. A sowing cancelled later drops out of its original day (net view). |
| Ready Stock | `dbo.ReadyStockTransactions` (+ `ReadyStock`, `PlantSpecies`, `PlantTypes`) | `TransactionDate` (UTC) | Physical balance (`Quantity − DispatchedQuantity`, reserved included) per Plant Type at the **end of the India day** = sum of all non-reservation ledger movements up to then. Non-zero Plant Types, largest first. | Exact while the ledger explains every batch's current balance (checked live on every request). If any batch does not reconcile: past dates → *cannot be reconstructed*; today → current table balance, with a note. Before the first ledger row → not available. Today's stock is **never** shown under a past date. |
| Bookings – Seedling | `dbo.Bookings` | `BookingDate` | Distinct orders (`ISNULL(OrderBookingId, Id)`) dated by the order's **first** line; a revision line added later never re-counts the order. Lines/plants = that order's lines booked the same day. Any status (as the existing Daily Report). | Reliable. |
| Bookings – Potted | `dbo.PottedPlantBookings` | `BookingDate` | Rows (one booking code each), any status | Reliable. Outlet customer bookings (`dbo.OutletBookings`) are a separate flow and not included (same as the existing Daily Report). |
| Dispatch – Seedling | `dbo.SeedlingDispatches` (+ lines for quantity) | `DispatchDate` | Completed dispatch **events**, not lines | Reliable from the first `SeedlingDispatches` row; legacy dispatches are not in this table → not available before it. |
| Dispatch – Potted | `dbo.Dispatches` | `DispatchDate` (UTC) | `Status = 'Completed'` (cancelled excluded) | Net view: a dispatch cancelled later drops out of its day. |
| Outlet Sales | `dbo.OutletSales` | `SaleDate` | Number of sales (headers) | Reliable. |
| Fertilizer Usage | `dbo.FertilizerUsage` → `FertilizerStock.UnitId` → `UnitMaster` | `IssueDate` | `SUM(UsedQuantity)` **per unit** — units (Bag, KG, Nos…) are never added together | Reliable. |
| Wastage | `WastageRepository` loss sources (ReadyConfirmations, Cutting/Potted/Ready/Seed ledgers, Pot batches) | each source's date, India day | Plants/cuttings/pots summed; seed-lot losses shown separately in the Seed Stock unit | Reliable from the first production record. |

## Software Activity

| Category | Definition |
|----------|------------|
| Total Employees | Active users (`IMSUsers.IsActive = 1`) — the existing Daily Report definition. **Limitation:** no joining/leaving dates exist, so past dates use today's list. |
| Logged In | ≥ 1 successful login (`dbo.UserLoginHistory`) on the India day. Login *events* are shown separately. |
| Active Users | Logged In **and** ≥ 1 qualifying recorded action that day (the approved list from `UserLoginHistoryRepository.GetTodaysActivityAsync`: stock ledger `UserId` on Seed/Cutting/Ready/Potted/EmptyPot transactions, `Bookings.BookedById`, `PottedPlantBookings.BookedById`, `FertilizerUsage.EnteredById`). Page views/sessions are not activity. |
| No Activity Recorded | Logged In and no qualifying action. Active + No Activity = Logged In. |
| Not Logged In | No login that day. Logged In + Not Logged In = Total. |
| Activity Without New Login | Subset of Not Logged In who still recorded work (a login cookie lasts 7 days). Shown only when non-empty. |

Login evidence starts with the first `UserLoginHistory` row (locally 2026-10-01 10:14 IST). Before that
India day the four login categories are **Not available** (never "Not logged in"); on the first day a
partial-day note is shown. Names are de-duplicated by user id, not by name (two people with the same
name both appear).

## Copy / print / export

* **Copy Report** copies the server-built plain text (same layout as the WhatsApp template; Indian
  digit grouping `1,02,546`). Unavailable figures read `Not available` and are explained under `Notes:`.
* **Print / Save as PDF** uses browser printing; `@media print` hides the sidebar, header, buttons,
  date navigation and history.
* **Download .txt** (`?handler=Text&Date=…`) returns the same text.

## Differences from the existing WhatsApp Daily Report (`DailyReportService`, unchanged)

That service is today-only and was left untouched. For past dates it would be wrong, and this report differs on purpose:
Ready Stock is reconstructed as of the date (not current); fertilizer is per unit (not summed across
units); wastage uses India days and keeps seed lots separate; employee activity uses India-day bounds
(`GetTodaysActivityAsync` compares server-day bounds with UTC `LoginAt`/ledger timestamps); seedling
orders are dated by their first line.

## 2026-10-10 changes

* **Ready Stock** per plant type now shows **Available for booking/dispatch** (physical − reserved), with a
  totals line: physical, reserved, in transit. History is rebuilt per stock kind (owned / reserved / in
  transit) from the ledger; the coverage check reconciles all three per batch.
* **Business dates.** New ledger rows carry their business date (dispatch, outlet sale and wastage,
  transfer, transplanting). Older rows were stamped when recorded; they are not changed. The report lists
  them in the notes ("… dated D were recorded later (R) …") -- query 17 in the repository.
* **Software activity** follows `CreatedAt` (when the employee recorded the work), not the business date.
* Definitions: [STOCK_DEFINITIONS.md](STOCK_DEFINITIONS.md).
