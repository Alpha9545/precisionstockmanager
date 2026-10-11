# Ready Stock Transfer to Mother Plant Areas & Transplanting

Status (2026-10-09): implemented and verified on scratch databases only. **Not** applied to
`PlantsIMS2_Test`, not deployed, not committed. Migration required before the code can run anywhere
(see *Migration*).

## Business workflow

```
Main Area sowing ─► Ready Stock (Main Area batch)
                      ├─ Option A: customer booking ─► dispatch            (existing, unchanged)
                      └─ Option B: Transfer to Mother Plant Area
                            ─► In transit (held at the Main Area batch)
                            ─► Mother Plant Supervisor receives (or rejects; sender may cancel)
                            ─► NEW Ready Stock batch in the Mother Plant polyhouse
                            ─► Transplanting into larger cavity trays
                            ─► NEW Ready Stock batch in the target cavity
                            ─► grows on / booked / dispatched through the existing workflow
```

No second stock system: received and transplanted stock are ordinary `dbo.ReadyStock` batches
(bookable and dispatchable like any other), each pointing back to the batch it came from.

## Pages and navigation

| Page | Route | Permission | Menu |
|------|-------|-----------|------|
| Ready Stock (existing) — Option B button per Main Area batch, *Transplant* button per Mother Plant batch | `/Production/ReadyStock/Index` | `ReadyStock.View` | Seed Production › Ready Seedling Stock |
| Transfer to Mother Plant Area | `/Production/ReadyStock/TransferToMotherPlant/{id}` | `ReadyStock.TransferToMotherPlant` | (from Ready Stock) |
| Transfers / Incoming Ready Stock | `/Production/MotherPlantTransfers/Index` | `ReadyStock.TransferToMotherPlant` or `ReadyStock.ReceiveTransfer` (each POST re-checks its own) | Seed Production › Transfers to Mother Plant (senders); Mother Plant & Cutting › Incoming Ready Stock (receivers) |
| Transplanting (eligible batches + filters) | `/Production/Transplanting/Index` | `Transplant.Enter` | Mother Plant & Cutting › Transplanting |
| Transplant form | `/Production/Transplanting/Create/{id}` | `Transplant.Enter` | (from Transplanting) |
| Transplanting History | `/Production/Transplanting/History` | `Transplant.View` | Mother Plant & Cutting › Transplanting History |
| Transplant details + reversal | `/Production/Transplanting/Details/{id}` | `Transplant.View`; POST `Transplant.Reverse` | (from History) |
| Batch lineage | `/Production/ReadyStock/Lineage/{id}` | `ReadyStock.View` or `Transplant.View` | (links everywhere) |
| Management Dashboard card "Mother Plant seedling flow" | `/ManagementDashboard/Index` | unchanged | — |

## Permissions (least privilege)

| Permission | Granted to | Purpose |
|-----------|-----------|---------|
| `ReadyStock.TransferToMotherPlant` | Sowing Supervisor, Office Coordinator | send / cancel (own Main Areas) |
| `ReadyStock.ReceiveTransfer` | Mother Plant Supervisor | receive / reject (own Mother Plant Areas) |
| `Transplant.Enter` | Mother Plant Supervisor | record transplanting (own Mother Plant Areas) |
| `Transplant.View` | Mother Plant Supervisor | history, details, lineage |
| `Transplant.Reverse` | **no role** — full-access (System Administrator) only | reverse an untouched transplanting |
| `TrayStock.EnterMotherPlant` | Office Coordinator, Booking Executive | add EMPTY trays to any active Mother Plant polyhouse (Add Material › Trays); nothing else |

Owner decisions 2026-10-09: senders = Sowing Supervisor + Office Coordinator; Mother Plant trays are
stocked by Office Coordinator, Booking Executive and the Administrator (their users are assigned to the
Main Office only, hence the narrow permission instead of `TrayStock.Enter` + Area assignment).
No other role changed. The Mother Plant Supervisor gains no administration / master-data access.

**Area scope.** Mother-Plant-side work (receive, reject, transplant, history, details) uses
`AreaAccessService` — always enforced, like the Mother Plant module. The Main-Area send / cancel side
and the Ready Stock pages use `SeedlingAreaScope`, which `appsettings.Development.json` deliberately
switches off (`SeedlingWorkflow:EnforceAreaScope = false`); production enforces it.

## Stock rules (units: plants, `DECIMAL(18,2)`, whole numbers)

`ReadyStock` gains `InTransitQuantity`:

* **Physical** = `Quantity − Dispatched − InTransit`; **Available** = `Quantity − Reserved − Dispatched − InTransit`
  (both computed columns redefined); DB check `Reserved + Dispatched + InTransit ≤ Quantity`.
* **Send**: Main Area *sowing* batches only, **whole trays** of the batch's cavity, ≤ available,
  to an active Mother Plant Area + one of its polyhouses. `InTransit += q` (`TransferHold`); `Quantity` unchanged.
  In-transit plants are not available for booking, dispatch or another transfer, and are not physical stock
  at either place (they still count once in company-wide totals).
* **Receive** (all or part; a shortfall needs a reason): `InTransit −= q` (`TransferHoldRelease`),
  source `Quantity −= received` (`TransferOut`) and `−= shortfall` (`TransferLoss`) in one change;
  a **new** batch at the destination `+received` (`TransferIn`).
* **Reject** (receiver) / **Cancel** (sender), only while pending: `InTransit −= q`; nothing else moves.
* Every action is one transaction: the transfer row and batch are locked (`UPDLOCK, HOLDLOCK`) and
  re-read; a `SubmissionToken` (unique) makes a repeated send return the first transfer; `UX_ReadyStock_Origin`
  allows exactly one received batch per transfer, so a repeated or concurrent *receive* adds nothing.

## Transplanting calculations and cavities

Cavities are the nursery's existing closed list (`DirectSowingRules.CavityTypes`, also the Tray Stock
sizes): **9, 24, 42, 102, 150 Cavity** — no second list.

```
Selected (source consumed) = Successfully transplanted + Lost/damaged
Output trays               = CEILING(Transplanted / target cavity capacity)
Full trays                 = FLOOR(Transplanted / capacity);  last tray holds the remainder
```

* Exact plant counts; the last tray may be partly filled; no plant is invented to fill a tray.
* Source = any batch with available plants in an active Mother Plant Area the user is assigned to.
  If the source is a *sowing* batch (kept in whole trays) the selected quantity must be whole source trays.
* Source: `−transplanted` (`TransplantOut`) `−lost` (`TransplantLoss`) in one change. Output: a **new**
  batch in the target cavity / Area / Polyhouse `+transplanted` (`TransplantIn`). The remaining source
  plants stay available. Losses are separate ledger rows; nothing is deleted.
* Stock equation (tested): `source before = source after + transplanted + lost`; output = transplanted.
* Server re-validates everything under lock; the browser preview is display only.

## Empty trays and growing material

* **Trays**: the target polyhouse's own `TrayStock` pool (Area + Polyhouse + cavity) loses
  `OutputTrays` (`TrayStockTransactions` type `Transplant`); refused if the pool has too few.
  Never taken from another Area or the Main Office. All five Mother Plant Areas hold **0 trays** on
  the current data — they must be stocked first (Add Material › Trays) or every transplant is refused.
* **Growing material (cocopeat / pitmos): none is deducted** (owner decision 2026-10-09). Material Stock
  is restricted to Main Office polyhouses by `TR_MaterialStock_Rules` anyway.

## Lineage

`ReadyStock.OriginType` (`Sowing` | `Transfer` | `Transplant`), `ParentReadyStockId`,
`OriginReferenceId` (InternalTransfers.Id / ReadyStockTransplants.Id). Derived batches keep the original
`SeedSowingId`, so the chain is always: original Main Area sowing batch → transfer → received batch →
transplanting → output batch (target cavity) → later bookings / dispatches (ledger). Shown on
*Batch lineage* and *Transplant details*.

## Cancellation and reversal

* Transfers: reject / cancel only while pending (above). A received transfer is not undone; its stock is
  ordinary Ready Stock from then on.
* Transplanting: **Reverse** (`Transplant.Reverse`, full access only, with a reason) is a compensating
  operation, allowed **only while the output batch is untouched** (no reservation, dispatch, transfer,
  further transplant or other change). It writes `ReversalRemoval` on the output, `ReversalReturn`
  (transplanted + lost) on the source, and `ReversalReturn` on Tray Stock; status → `Reversed`.
  Ledger rows are never deleted. A replayed reversal is refused (status check under lock).

## Reports and dashboard

* Management Dashboard: new card — in transit now, Ready Stock in Mother Plant Areas, transplanted / lost
  today, transit loss today, output by cavity today. Existing per-Area Ready Stock excludes in-transit
  plants; company totals count them once (tested: per-Area physical + in transit = company stock).
* Daily Operations Report: ledger reconstruction treats `TransferHold*` like reservations (they move
  in-transit, not plants); historical totals stay exact.

## Migration

`Database/Migrations/2026-10-09_ReadyStockTransplanting.sql` — guarded to `PlantsIMS2_Test` or a
`PlantsIMS2_Scratch_*` copy and to `-v TARGET_SERVER=<@@SERVERNAME>`; one transaction; idempotent; `MODE=DRYRUN|COMMIT`.
Rollback: `..._Rollback.sql` — **refuses** while any transfer, transplanting, derived batch, in-transit
quantity or new ledger type exists.

```
sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i Database/Migrations/2026-10-09_ReadyStockTransplanting.sql
```

Verified on scratch copies of today's `PlantsIMS2_Test` backup: dry-run (no change), commit, re-run
(no-op, 0 grants), rollback refused with history (Msg 51944), rollback on a clean copy restores the exact
structure and data (verified by column-wise checksum), re-apply after rollback.
Run order: after `2026-10-08_MaterialStockApprovals.sql`; independent of the (paused) Vehicle Tracking migrations.

## Tests

| File | What |
|------|------|
| `TransplantRulesTests.cs` | pure transfer / transplant rules |
| `ReadyStockTransplantingE2ETests.cs` | scratch DB: hold / receive / reject / cancel, idempotency and concurrency, every cavity (9/24/42/102/150) incl. partial last tray, loss, trays, atomic rollback on an injected failure, reversal, company totals, and the full acceptance scenario (sow 1,050 in 150-cavity → reserve 150 → send 600 → in-transit not bookable → receive → 485 = 480 + 5 lost into 20 × 24-cavity trays → lineage → dispatch 100 → reversal refused) |
| `TransplantAuthorizationTests.cs` | tray-area rule; every new page: anonymous, wrong roles refused, right roles admitted |

Results 2026-10-09 (fresh migrated scratch): targeted 74/74 passed; full suite 2091 passed / 29 failed /
83 skipped — the 29 failures are the known, pre-existing baseline (identical test names).

## Open items / not determinable from the code

* Who physically stocks Mother Plant trays day to day (permission granted; process is the owner's).
* Whether transplanting ever needs growing material in future (would need a Mother Plant material-stock design).
* `MotherPlantSupervisorOutletSellE2ETests.OtherRoles_PermissionSets_AreUnaffectedByThisFix` was failing before
  this feature (Booking Executive count); only this migration's deltas were added to it.

## 2026-10-10 changes (audit follow-up)

* **Business dates.** The transfer hold (`TransferHold`) is dated at the Transfer Date; transplanting
  rows (source `TransplantOut` / `TransplantLoss`, output `TransplantIn`, tray `Transplant`) at the
  Transplanting Date. Receipt, rejection and cancellation rows are dated when recorded. `CreatedAt` is always
  the recorded time. Rule and legacy treatment: [STOCK_DEFINITIONS.md](STOCK_DEFINITIONS.md).
* **Definitions.** Plants in transit are counted once: owned by the source batch, in neither location's
  physical or available figure. Dashboard KPI, Ready Stock page, Daily Operations and the WhatsApp report all
  lead with "Available for booking/dispatch".
* **Tray history.** `Transplant` tray rows are now shown as "Used for Transplanting" (category Used),
  not "Adjustment".
* **Permission test.** The role-permission test now compares exact sets and lists every role that
  differs; the only remaining difference is Booking Executive (`Dispatch.View`, `Dispatch.Enter`, granted in the
  data between 2026-10-01 and 2026-10-04 by no script) -- left failing until the owner decides.
