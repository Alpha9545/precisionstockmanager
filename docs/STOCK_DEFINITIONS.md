# Stock definitions and ledger dates

Last updated 2026-10-10. This page sets out the single meaning of every Ready Stock and
empty-tray figure the app shows, and how ledger rows are dated. The code comments in
`Services/LedgerDates.cs`, `Services/DailyOperationsReportRules.cs`,
`Data/ManagementDashboardRepository.cs` and `Services/TrayTransferRules.cs` refer here.

## 1. Ready Stock (seedlings approved Ready)

Columns on `dbo.ReadyStock`, per batch:

| Column | Meaning |
|---|---|
| `Quantity` | Plants approved Ready into the batch, net of later corrections, transplant out/in and transfer losses. |
| `ReservedQuantity` | Plants held for bookings / outlet orders, still physically in the batch. |
| `DispatchedQuantity` | Plants that have left the business (dispatch, outlet tray sale, wastage). |
| `InTransitQuantity` | Plants sent to a Mother Plant Area and not yet received. They are still owned by the source batch. |

### Definitions (all figures in plants)

| Name | Formula | Where shown |
|---|---|---|
| **Owned** | `Quantity − Dispatched` | Management Dashboard: "Ready Stock — total owned"; Daily Operations "totals" line (via physical + in transit). |
| **In transit** | `InTransit` | Dashboard sub-line; Daily Operations totals; Ready Stock page totals. |
| **Physical** | `Owned − InTransit` = computed column `PhysicalQuantity` | Ready Stock page; Dashboard area figures; Daily Operations totals. |
| **Reserved** | `Reserved` | Ready Stock page; Dashboard sub-line; Daily Operations totals. |
| **Available for booking/dispatch** (the primary figure) | `Physical − Reserved` = computed column `AvailableQuantity` = `Quantity − Reserved − Dispatched − InTransit` | Dashboard KPI card, Ready Stock page (first total), Daily Operations per plant type, the WhatsApp Daily Report ("Ready Stock by Plant Type"). |

Rules:

* A plant in transit is counted **once**: in the source batch's Owned figure, never in the
  destination's, and never in Physical or Available. On receipt it leaves the source
  (`TransferOut`) and arrives in a new destination batch (`TransferIn`); a shortfall is a
  `TransferLoss` on the source.
* Before 2026-10-10 the Dashboard KPI and the WhatsApp report showed `Quantity − Dispatched`
  (Owned) labelled "Ready", while the Ready Stock page showed Available. They now all lead
  with **Available for booking/dispatch** and show physical / reserved / in transit beside it.

### Ledger (`dbo.ReadyStockTransactions`)

| Kind | Transaction types | Changes |
|---|---|---|
| Owned (O) | every type except the four below | `Quantity` or `DispatchedQuantity` |
| Reserved (R) | `Reservation`, `ReservationRelease` | `ReservedQuantity` only |
| In transit (T) | `TransferHold`, `TransferHoldRelease` | `InTransitQuantity` only |

Reconciliation (Daily Operations coverage check and the E2E tests):
`SUM(O) = Quantity − Dispatched`, `SUM(R) = Reserved`, `SUM(T) = InTransit` for every batch.

## 2. Ledger dates: business date vs recorded time

Every stock ledger row has two timestamps:

* `TransactionDate` — **when the movement happened in business terms**.
* `CreatedAt` — **when it was recorded** (always the server time of the save).

From 2026-10-10 (`Services/LedgerDates.cs`), a movement that carries its own business date is
written with `TransactionDate` = that India date at the time of day it was recorded (or "now"
when the business date is today). Future dates are refused.

| Operation | Business date used |
|---|---|
| Seedling dispatch | Dispatch Date |
| Outlet tray sale | Sale Date |
| Outlet tray wastage | Wastage Date |
| Ready Stock → Mother Plant transfer (hold) | Transfer Date |
| Ready Stock → Mother Plant transfer (receipt: hold release, source out / loss, destination in) | **Date received** (2026-10-11): default today; not in the future; not before the Transfer Date; an earlier date needs a reason. Rows are dated no earlier than the hold, so the plants are never at both places or neither. Stored in `InternalTransfers.ReceivedDate` / `ReceiptDateReason`; `ConfirmedDate` keeps the recorded time. The received batch is usable (e.g. for transplanting) from that date. |
| Transplanting (source out / loss, output in, tray consumption) | Transplanting Date |
| Tray transfer sent | Transfer Date |
| Tray transfer received | Date received (not before the send, not in the future) |
| Tray adjustment | Date of the count |

Movements without a business date of their own (approvals, reservations, receipts of Ready
Stock transfers, rejections, cancellations, reversals) keep `TransactionDate` = recorded time.

**Reservation release** rows of a dispatch are dated at the later of the dispatch's business
time and the time the plants were reserved (`LedgerDates.ReleaseInstant`), so a historical
"reserved" figure never goes negative when a dispatch is entered with an earlier date.

**Dispatch dated before Ready approval** (owner decision 2026-10-10): allowed, and the ledger
remark is flagged `[dated before this batch was approved Ready on dd/MM/yyyy]`.

### History that cannot be reconstructed reliably (2026-10-11)

The Daily Operations Report flags a past day's Ready Stock for a Plant Type, without replacing the figures, when:

* a ledger row is dated before its batch first held stock (e.g. a dispatch or transfer dated before the
  batch was approved Ready) — query 18 of `Data/DailyOperationsReportRepository.cs`; or
* a figure rebuilds to a negative number (safety net).

Receipts recorded before 2026-10-11 have no business receipt date; they stay on the day they were recorded.

### Legacy rows (before 2026-10-10)

Rows written earlier were stamped with the recorded time even when the operation had an earlier
business date. They are **not changed** — no historical record is rewritten. Instead:

* The Daily Operations Report counts them on the day they were recorded and lists every such
  difference in its notes ("… dated 2026-10-01 were recorded later (2026-10-03) …"), using
  query 17 of `Data/DailyOperationsReportRepository.cs`.
* Software Activity in that report follows `CreatedAt` (the day the employee recorded the work).

## 3. Empty trays (`dbo.TrayStock`, `dbo.TrayStockTransactions`)

A pool is Area + Polyhouse + Cavity; `PhysicalQuantity` is the count of empty trays in it and
can never be negative (`CK_TrayStock_NonNegative`, re-checked under the pool lock).

| Type | Meaning | History category |
|---|---|---|
| `Allocation` | Trays added (Add Material > Tray) | Added |
| `Sowing` | Used by a seed / cutting sowing (or its approval overage) | Used |
| `Transplant` | Used by transplanting (fixed 2026-10-10: was shown as "Adjustment") | Used |
| `ReversalReturn` | Returned by a cancelled sowing / approval / transplanting | Returned |
| `Adjustment` | Count correction (`ReferenceType 'TrayAdjustment'`) or a historical migration row | Adjustment |
| `TransferOut` | Sent to another Polyhouse (2026-10-10) | Transfer |
| `TransferIn` | Received from another Polyhouse | Transfer |
| `TransferReturn` | Returned to the source (transfer rejected or cancelled) | Transfer |

**Who sees which trays:** users with the tray-view permissions see their own Areas; `TrayStock.EnterMotherPlant`
adds every active Mother Plant Area; `TrayStock.ViewMotherPlant` (Mother Plant Supervisor, 2026-10-11) is
read-only and covers only the user's own active Mother Plant Areas (`Authorization/TrayAreaScope.cs`).

**Who receives tray transfers** (location-aware, 2026-10-10): Mother Plant Areas -- `TrayStock.ReceiveTransfer`
(Mother Plant Supervisor); Main Office Areas -- `TrayStock.ReceiveTransferMainArea` (Sowing Supervisor); only in
the receiver's own Areas; full-access users anywhere. Destinations without such a receiver are refused for
non-administrators (`Services/TrayTransferRules.ReceivePermissionFor`, `Authorization/TrayAreaScope.IsReceivableBy`).

**Back-dated tray deductions** (2026-10-10): a transplanting, tray transfer or tray adjustment dated earlier than
today may only use trays that were in the pool **on that date and at every later point** (receipts, transfers and
other consumption counted in business-date order -- `TrayStockRepository.AvailableFromAsync`). So the tray history
by date can never go below zero. Older rows that already do are flagged on Material History, never changed.

**Trays in transit** = the quantities of `Pending` tray transfers. They are in neither
Polyhouse's pool; Material Stock shows them as a separate line.

### Opening balances

No opening balance is ever created automatically. To start or correct a pool: count the empty
trays of each cavity in each Polyhouse **once**, then enter the difference as a Tray Adjustment
with the reason (e.g. "Physical count 10/10/2026"). Each adjustment is one new ledger row; earlier
rows are never edited or deleted.

## 4. Bed Sowing (2026-10-12, `dbo.BedSowings`, `dbo.BedSowingOperations`)

Seed sown into a BED at the Bed Sowing Polyhouse (`dbo.BedSowingLocations`; locally "Facility 1 Main
office", Main-Office-Areas). Figures in seeds / seedlings.

| Figure | Meaning |
|---|---|
| Original | `BedSowings.Quantity` -- seeds taken from the Main Office seed lot when the bed was sown (`SeedStockTransactions` 'Sown', ReferenceType `BedSowing`) |
| Transplanted | sum of the bed's ACTIVE transplant operations; each one is one growing tray batch (`dbo.SeedSowings`, SourceType `BedSowing`) |
| Lost | sum of the bed's ACTIVE loss operations (reason required) |
| Remaining | Original - Transplanted - Lost; never negative (`CK_BedSowings_Quantities`) |

* **Conservation:** Original = Remaining + Transplanted + Lost, enforced by `TR_BedSowings_Guard`
  (the totals must equal the active operations).
* **No stock at bed stage:** no trays, no growing material, no Ready Stock.
* **Dates (2026-10-12 review):** every Bed Sowing ledger row is on its BUSINESS date, with the
  recorded time kept for audit -- the seed deduction on the bed's sowing date
  (`SeedStockTransactions.TransactionDate`), the trays and the Cocopeat / Pitmos of a transplant on the
  transplant date (`TrayStockTransactions.TransactionDate`, new `MaterialTransactions.TransactionDate`);
  `CreatedAt` stays the recorded time. A back-dated deduction must fit each balance on that date and at
  every later point (seed lot, tray pool, material balance) -- a history can never go below zero.
  Reversals and cancellations are dated when recorded (section 2). Material History, its KPIs and its
  consumption summary use `COALESCE(TransactionDate, CreatedAt)` and show the recorded time of a
  back-dated row.
* **Bed form (2026-10-13):** sowing date, variety, seed lot, quantity, notes. No Ready Date at the bed
  stage (`BedSowings.ReadyStockDays` / `ExpectedReadyDate` stay NULL; old rows keep theirs).
* **One transplant closes the bed (2026-10-14):** the Transplanter enters the **Quantity to Transplant**, a
  positive multiple of the chosen cavity (at most the remaining N); trays = quantity / cavity, and EVERY other
  remaining seedling (N - quantity) is recorded in the SAME transaction as an ordinary bed Loss operation,
  dated at the transplant date, reason "Untransplanted remainder after complete-tray transplant" (e.g. 500
  remaining, 450 into 150 Cavity = 3 trays + 50 waste; 500 = 0 + 450 + 50). There is no separate "Record bed
  loss" form any more. **Waste-only closure:** when all remaining seedlings are dead / unusable, the
  Transplanter ticks the confirmation and gives a reason -- all N are one bed loss with that reason, no tray
  batch, no trays or material, and the bed closes. The auto-loss is reversed separately like any bed loss.
* **Transplant:** complete trays only (Quantity = trays x cavity). Target: ONLY the Bed Sowing Polyhouse
  of the bed's own Main Office Area (Cocopeat / Pitmos at the existing kg-per-tray rule); Mother Plant Areas
  are no longer a Bed transplant target (owner decision 2026-10-11). Empty trays leave the
  target Polyhouse's Tray Stock dated at the transplant's business date (it must fit the pool's
  history, section 3). The tray batch keeps the bed's Sowing Date (the original date) and stores
  **Ready Date = actual transplant date + the variety's Ready Days** (IST business date), with the Ready
  Days snapshotted on the batch: a later change of the variety never rewrites it. Missing / zero Ready
  Days: the transplant is refused (the bed itself can still be sown).
* **Ready Stock** is created only by the existing Sowing Approval of a tray batch, at most the
  seedlings in its trays; never by the bed or the transplant. A bed tray batch is approved only FROM
  its Ready Date (India date; `TR_ReadyConfirmations_BedReadyDate` repeats it). Other sowings unchanged.
* **Who:** transplanting and bed losses only by the ONE Bed Sowing Transplanter (Maya Shinde) of the
  bed's Area, identified from the signed-in user (no supervisor is chosen; the tray batch has none and
  any user with `ReadyStock.Confirm` or a System Administrator approves it, never its recorder) -- nobody else, a System Administrator included; there is no exception path
  (`TR_BedSowingOperations_Transplanter` refuses any other recorder). Administrators view, cancel
  untouched beds and reverse operations; their reversals are logged in `dbo.BedSowingAuditLog`. The
  role cannot be given, removed or edited on the role pages, and the database allows one holder only.
* **Reports** count a bed sowing once, on its sowing date; its tray batches are not sowings again
  (`BedSowingSchema.SowingActivitySql`). Active bed losses appear in Wastage as "Bed sowing (bed loss)".
* **Corrections:** operations are never edited or deleted; a transplant (while its tray batch has no
  approval) or a loss is REVERSED, keeping the row with who / when / why. An untouched bed can be
  cancelled; its seed returns to the lot ('ReversalReturn').

## 5. Transplant into Trays from received Ready Stock (2026-10-13, `2026-10-13_TraysToSowingApprovals.sql`)

Main Area Ready Stock -> Mother Plant transfer -> receipt -> **Transplant into Trays** (offered on the
receipt and on every received transfer). The user chooses the cavity (no default) and the plants
transplanted (+ any lost); the target is the received batch's own Mother Plant Area and Polyhouse.

* The source batch gives up transplanted + lost (`TransplantOut` / `TransplantLoss`); the plants become
  ONE growing tray batch (`dbo.SeedSowings`, SourceType `Transplant`, linked by
  `ReadyStockTransplants.OutputSeedSowingId`) -- complete trays only; the rest stays in the source batch.
  Empty trays leave the Polyhouse's Tray Stock (`Transplant`) on the transplant date. No growing material.
* Ready Date = transplant date + the variety's Ready Days (snapshot), as for a bed. Sowing Date = the
  original sowing's date. Ready Stock is created only by the Sowing Approval (any `ReadyStock.Confirm`
  holder or System Administrator; never more than transplanted), once, and **not before the Ready
  Date** -- checked inside the approval transaction and by `TR_ReadyConfirmations_BedReadyDate` (52208),
  shown as "Approve from <date>" on Sowing Approvals. Direct Seed / Cutting Sowing are not gated. Lineage continues from the
  approved batch to the received batch and the Main Area sowing.
* Reversal only while the tray batch has no approval: the batch is cancelled, plants and trays return.
* Without the 2026-10-13 migration the earlier behaviour remains (immediate Ready Stock batch, partial
  last tray allowed); Bed transplanting is refused until it is applied.
* Sowing reports never count `BedSowing` / `Transplant` tray batches as sowings.

## 6. Mother Plant Areas: Area-level trays, transfer purpose, pots from Ready Stock (2026-10-14 / 2026-10-15)

* **Empty trays and pots per Area** (`2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql`): a Mother Plant
  (or Cutting) Area holds ONE tray pool per cavity with no Polyhouse (`TR_TrayStock_PoolLocation`), like its
  empty pots. Mother Plant & Cutting > **Add Empty Trays** / **Add Empty Pots** -- Area only, the user's own
  authorised Areas. Main Office keeps its Polyhouse pools. Pot Production no longer offers "Add Empty Pot Stock".
* **Ready Stock transfer to a Mother Plant Area**: to the AREA (no Polyhouse), with a required **purpose**
  (`InternalTransfers.TransferPurpose`): **Pot Production** or **Transplant into a different cavity tray**.
  Older transfers keep their Polyhouse and have no purpose. Only "Transplant" stock is offered Transplant into
  Trays; only "Pot Production" stock is offered Start Pot Production.
* **Pot Production from Ready Stock** (`2026-10-15_PotProductionFromReadyStock.sql`): received Pot Production
  seedlings start a pot batch (`PotProductionBatches.SourceReadyStockId`, exactly one source per batch) in the
  same Area with the Area's empty pots: seedlings leave the Ready Stock batch ('PotProductionOut', dated the
  production start); daily production uses the empty pots; READY adds pots to Potted Plant Stock; seedlings not
  potted return to the batch ('ReversalReturn') or are wastage ("Pot production (seedlings not potted)");
  cancelling before production returns all of them. Cutting -> Pots is unchanged.
