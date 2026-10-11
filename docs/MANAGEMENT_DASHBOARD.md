# Management Dashboard — data definitions

Page: `/ManagementDashboard/Index`. Administrator only: it requires the permission `Admin.ManageAreas`,
enforced server-side for every request by `Authorization/FeatureAuthorizationConventions.cs`. No
ordinary role holds that permission, so only full-access users (System Administrator) can open the
page; everyone else gets Access Denied, whether or not the menu item is shown.

Code: `Pages/ManagementDashboard/Index.cshtml(.cs)`, `Data/ManagementDashboardRepository.cs`
(`GetOverviewAsync` + the Phase L section methods), `Services/ManagementDashboardCalculations.cs`,
`Models/ManagementOverview.cs`. Tests: `ManagementDashboardCalculationsTests`, `ManagementDashboardE2ETests`.

## Scope

* **NOW** — current state. The period selector never changes it (tested across every preset).
* **PERIOD** — events dated inside the selected period: Today, Last 7 days (today + 6 days before),
  Last 30 days (default), This month, or a custom From–To range. Both ends inclusive.
* **TODAY** — today's labour only.
* The top overview is always company-wide (all Areas). The Area / Variety / Pot Size filters apply
  only to the "Production detail" section at the bottom.
* No caching: every figure is read live on each request.

## Time zone (business time zone: Asia/Kolkata)

"Today" and every period boundary are India dates (`AppClock.Today`). How each stored value is
turned into an India date:

| Stored as | Columns | Handling |
|---|---|---|
| Business DATE (entered by the user, already an India date) | SeedSowings.SowingDate (always stored at 00:00), CuttingProductions.CuttingDate, PotProductionEntries.ProductionDate, SeedlingDispatches.DispatchDate, Bookings.DeliveryDate, DailyLabourAttendance.LabourDate, LabourOvertime.LabourDate | compared directly with the India date |
| UTC (`SYSUTCDATETIME()`) | ReadyStockTransactions.TransactionDate, Dispatches.DispatchDate / CreatedDate, SeedlingDispatches.CreatedDate, InternalTransfers.ConfirmedDate, CuttingStockTransactions.TransactionDate, MaintenanceTickets.RequestedAt / ClosedAt, StoreRequests.RequestedAt, SuggestionPosts.CreatedAt | filtered between `AppClock.IndiaToUtc(from)` and `AppClock.IndiaToUtc(to + 1 day)`; grouped by `CAST(DATEADD(MINUTE, 330, col) AS DATE)`; displayed with `AppClock.ToIndiaTime` |
| SQL Server local time (`GETDATE()`) | Bookings.BookingDate | the UTC bounds above are shifted by SQL Server's own exact offset, `DATEPART(TZOFFSET, SYSDATETIMEOFFSET())`, so the result does not depend on the web or SQL server's time zone; recent-activity times are converted the same way |

Example: 2026-10-06 20:30 UTC = 2026-10-07 02:00 IST and counts on **7 Oct**; 2026-10-06 18:29 UTC
= 2026-10-06 23:59 IST and counts on **6 Oct**. Both directions are tested for Ready Stock approvals,
potted-plant dispatches (Dashboard and Daily Report) and seedling bookings (Dashboard and Daily Report).

### Historical assumption for dbo.Bookings.BookingDate (verified 7 Oct 2026, local copy)

`BookingDate` has no time zone of its own: it is whatever clock `GETDATE()` read on the SQL Server
that wrote the row. The dashboard converts it with the CURRENT SQL Server offset, i.e. it assumes
every row was written by a server on today's time zone. Read-only check of the local data
(`LAPTOP-R87BKQGH\MSSQLSERVER02`, offset +330, 4,947 rows, Nov 2025 – Oct 2026):

* **Apr 2026 onwards** — consistent with India time. Recent rows match the UTC `CreatedDate` of
  reservations made in the same minute exactly 5 h 30 min apart (329 whole minutes + seconds).
* **Nov 2025 – Mar 2026 (2,135 rows)** — roughly half are stamped 03:00–08:59 and none after 18:00.
  As India time that would be hundreds of bookings at 4–6 AM; as UTC it is 08:30–14:30 IST. These
  rows were most likely written by a server running on UTC. This cannot be proven from the data
  alone and was NOT changed.
* **Impact on the dashboard: none at day level.** A UTC-written row lands on a different India date
  only if stamped at or after 18:30; 0 of the 2,135 older rows are. Only 1 of 2,812 newer rows is
  stamped before 05:30. Day/period booking counts are therefore the same under either assumption;
  open/overdue orders use `DeliveryDate` (a plain date) and are unaffected. Only the clock time of
  an old booking would be 5 h 30 min early in a display, and the activity feed shows only the newest.
* No data or schema was changed. If exact historical times are ever needed, the fix is a one-off,
  reviewed data correction of the pre-April rows, which is a business decision.

To check the LIVE server (read-only; run on the VPS):

```sql
SELECT @@SERVERNAME, DATEPART(TZOFFSET, SYSDATETIMEOFFSET()) AS OffsetMinutes, CURRENT_TIMEZONE() AS TimeZone;
SELECT FORMAT(BookingDate, 'yyyy-MM') AS Month, COUNT(*) AS Bookings,
       SUM(CASE WHEN DATEPART(HOUR, BookingDate) BETWEEN 3 AND 8 THEN 1 ELSE 0 END) AS Stamped_03_08,
       SUM(CASE WHEN CAST(BookingDate AS TIME) >= '18:30' THEN 1 ELSE 0 END) AS Stamped_After_1830
FROM dbo.Bookings GROUP BY FORMAT(BookingDate, 'yyyy-MM') ORDER BY Month;
```

If live reports an offset other than 330, the dashboard still filters correctly for rows written
from now on (it uses that live offset), but historical rows written while the server was on a
different zone carry the same caveat as above.

Not changed (shared with other modules): the Ready Alerts page and the Daily Report still classify
ready alerts against the server date (`DateTime.Today`), and the Daily Report's "today" is the server
date. The dashboard passes the India date. On a server that runs on India time these are identical.

## Terminology

| Term | Table | Example |
|---|---|---|
| Plant Type (crop group) | `dbo.PlantTypes` | MERI GOLD, CHRYSANTHEMUM, ZINNIA, SEASONAL VARITIES |
| Variety | `dbo.PlantSpecies` | Lakhani Yellow, Antirrhinum Twinny |
| Booking order | `ISNULL(dbo.Bookings.OrderBookingId, Id)` | one customer booking, any number of varieties |
| Booking line | one `dbo.Bookings` row | one variety on an order |

**Orders are always COUNT(DISTINCT order); quantities are always SUM over lines.** A multi-variety
order is one order.

## KPI cards

| Name | Scope | Source | Calculation | Counts |
|---|---|---|---|---|
| Ready seedling stock | NOW | `dbo.ReadyStock` | physical = SUM(Quantity − DispatchedQuantity); reserved = SUM(ReservedQuantity); available = SUM(Quantity − ReservedQuantity − DispatchedQuantity) (= its computed `AvailableQuantity`) | plants |
| Open seedling orders | NOW | `dbo.Bookings`, Status Pending | COUNT(DISTINCT order); "still to dispatch" = SUM(max(0, Quantity − DispatchedQuantity − legacy-fulfilled)) (`SeedlingBookingSummary.RemainingQuantity`) | orders; plants |
| Seedlings dispatched | PERIOD | `dbo.SeedlingDispatchLines` via `SeedlingFulfilmentRepository.GetDispatchRegisterAsync` | SUM(line Quantity); dispatches = COUNT(DISTINCT dispatch) — same as the Dispatch Register and the Daily Report | plants; dispatches |
| Seedling orders received | PERIOD | `dbo.Bookings` by BookingDate, every status | `BookingRepository.GetBookingCountByBookingDateAsync`: COUNT(DISTINCT order), SUM(Quantity) — same as the Daily Report | orders; plants |
| Potted plant stock | NOW | `dbo.PottedPlantStock` | SUM(PhysicalQuantity); available = physical − reserved | plants |
| Open maintenance tickets | NOW | `dbo.MaintenanceTickets` not Closed/Cancelled | count; critical/urgent by Priority; overdue = `MaintenanceRules.OverdueReason` (Open longer than 4/24/72 h by priority, or Accepted with the expected visit date passed) | tickets |
| Store requests pending | NOW | `dbo.StoreRequests` | Status Pending; also Approved (not issued); low stock = active `dbo.StoreItems` with AvailableQuantity < MinimumStock (`StoreItem.IsLow`) | requests; items |
| Today's labour | TODAY | `dbo.DailyLabourAttendance` (+ legacy `DailyLabourCounts`) via `DailyLabourAttendanceRepository.SearchDaysAsync` | headcount at 9:00 AM; Full/Half Day by `DailyLabourRules.Classify`, only for locations with both headcounts | workers |

## Sections

| Section | Scope | Definition |
|---|---|---|
| Attention required | NOW | Only conditions the application already defines, shown when above zero: critical tickets, overdue tickets, Pending seedling orders past their expected delivery date (with plants still to dispatch), Pending orders due today, sowings overdue for Ready approval (`SeedSowingRepository.ClassifyReadyAlert`, India date), overdue pot batches (`PotBatchRules`), cutting deliveries awaiting confirmation, store requests pending / approved, store low stock, active problems. No new thresholds. |
| Stock overview | NOW | Ready seedlings and potted plants side by side; different products, never added together. |
| Top 3 varieties by Ready Stock | NOW | Ready Stock physical per Variety; highest first, ties by Variety name; % = share of all Ready Stock physical. |
| Top 3 varieties by open booking quantity | NOW | Still-to-dispatch quantity of Pending booking lines per Variety; same ordering; % = share of all still-to-dispatch. |
| Ready Stock by variety (chart) | NOW | Top 10 varieties + one "Others (n varieties)" bar; the bars add up to the Ready Stock total. |
| Ready Stock by Plant Type | NOW | Same rows grouped by Plant Type ("(No Plant Type)" when blank); Varieties = distinct varieties holding stock. |
| Stock by Area and Polyhouse | NOW | Ready Stock grouped by its own AreaId, then PolyhouseId (each Area = the sum of its Polyhouse rows). Potted stock per Area (no Polyhouse). |
| Area/Polyhouse master-data mismatch | NOW | Ready Stock recorded under one Area on a Polyhouse that `dbo.Polyhouses` assigns to another Area: recorded Area, Polyhouse, the Polyhouse's actual Area, quantity. Counted under the recorded Area as stored; nothing is moved. |
| Bookings and dispatch | NOW + PERIOD | Seedling orders (distinct orders, summed lines), Pending past expected delivery date / due today, received and dispatched in the period; potted-plant bookings (`GetOutletSalesSummaryAsync`, company-wide). |
| Production | PERIOD | Sown = `dbo.SeedSowings` (not Cancelled) by SowingDate; approved into Ready Stock = `dbo.ReadyStockTransactions` 'Confirmed'; cuttings = `dbo.CuttingProductions` by CuttingDate; pots = `dbo.PotProductionEntries` by ProductionDate. |
| Maintenance / Store / Labour / Suggestions | NOW (+ tickets closed in PERIOD) | As in the KPI table; suggestion counts from `SuggestionRepository.GetCountsAsync`. |
| Recent activity | NOW | The latest 5 of each kind (booking orders, seedling and potted dispatches, Ready Stock approvals, maintenance tickets, store requests, suggestion/problem posts), merged newest first, 12 shown, India time. |

## Business-data issues the dashboard reports but does not change

* **Old Pending seedling bookings.** Most Pending orders are past their expected delivery date
  (local data, 7 Oct 2026: about 2,700 of 2,700+ open orders, delivery dates January–September 2026).
  They are real Pending bookings, so the dashboard counts them under the existing booking rules and
  shows a note. Cancelling, completing or re-dating them is a separate management decision, done in
  Booking Fulfilment, not in code.
* **Area/Polyhouse master data.** Ready Stock is recorded under Main-Office-Areas on Polyhouse "P-1",
  which belongs to Samarth Ropvatika. Correcting the master data or the stock record is a separate
  data-cleanup task.

## Performance

One page load runs a fixed 38 SQL commands, the same with an empty period or a busy one (tested
with a SqlClient diagnostic listener). The overview is one batched command with several result sets;
there are no per-row queries. When the production detail is not filtered, its Ready / Cutting /
Outlet summaries are reused by the overview instead of being queried twice.
