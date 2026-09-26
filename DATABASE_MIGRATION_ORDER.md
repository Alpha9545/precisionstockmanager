# Database Migration Order

All scripts live in `Database/` and are **additive, idempotent, and safe to re-run**: every `CREATE TABLE`/`ALTER TABLE`/index/constraint/function is guarded with an existence check (`IF NOT EXISTS (...) BEGIN ... END`), so running a script twice is a no-op the second time, and no script drops an existing table or deletes existing data. Take a full backup of `PlantsIMS2` before running these against a real database — this sandbox has no live SQL Server to run them against, so none have been executed; only manual/static review has been performed (see `PROJECT_DOCUMENTATION.md` for what that means and its limits).

Run them **in this exact order**, each one only after the previous one has completed successfully:

1. `Phase2_Area_MotherPlant.sql` — `dbo.Area` (Polyhouse-scoped), `dbo.BatchNumberSequences` (the generic batch-number generator every later phase reuses), `dbo.MotherPlants` (`MotherPlantCode`, `SupervisorId → IMSUsers.Id`).
2. `Phase3_CuttingPlan.sql` — `dbo.CuttingPlans` ("CUT-" prefix), FK to Mother Plants.
3. `Phase4_ActualCutting.sql` — `dbo.ActualCuttings` ("AC-" prefix), FK to Cutting Plans.
4. `Phase5_CuttingDelivery.sql` — `dbo.CuttingDeliveries` ("CD-" prefix), FK to Actual Cuttings.
5. `Phase6_PropagationBatch.sql` — `dbo.PropagationBatches` ("PROP-" prefix), FK to Cutting Deliveries.
6. `Phase7_PotProduction.sql` — `dbo.EmptyPotInventory` + `dbo.EmptyPotInventoryTransactions`, `dbo.PottedPlantStock` + `dbo.PottedPlantStockTransactions`, `dbo.PotProduction` ("POT-" prefix). First real stock-bearing entities besides the pre-existing `dbo.Inventory`/`dbo.SeedCuttingBank`, each with its own dedicated ledger per Decision 1.
7. `Phase8_InternalTransfer.sql` — adds `AreaId` to `EmptyPotInventory`/`PottedPlantStock` (a Phase 7 correction, confirmed with the business owner rather than guessed), widens `EmptyPotInventoryTransactions`' type CHECK to add `'Transfer'`, creates `dbo.InternalTransfers` ("TR-" prefix).
8. `Phase9_BookingReservation.sql` — `dbo.PottedPlantBookings` ("BK-" prefix), a **new** reservation system separate from the pre-existing `dbo.Bookings` (confirmed with the business owner — see Decision 4 in `PROJECT_DOCUMENTATION.md`).
9. `Phase10_Dispatch.sql` — widens `PottedPlantBookings`' Status CHECK to add `'Dispatched'`, creates `dbo.Dispatches` ("DIS-" prefix), one full dispatch per booking.
10. `Phase11_PurchaseOrderReceipt.sql` — `dbo.Vendors`, `dbo.PurchaseOrders`/`dbo.PurchaseOrderItems` ("PO-" prefix), `dbo.PurchaseReceipts`/`dbo.PurchaseReceiptItems`. Does **not** touch `dbo.VendorPurchases` (Decision 2).
11. `Phase12_LabRequest.sql` — widens `PottedPlantStockTransactions`' type CHECK to add `'LabSent'`/`'LabReceived'`, creates `dbo.LabRequests` ("LAB-" prefix).
12. `Phase13_LabourLog.sql` — `dbo.LabourLogs` ("LBR-" prefix). Pure record-keeping; touches no stock table.
13. `Phase14_RoleFoundation_AreaExtension.sql` — role/workflow redesign foundation (Migration Plan step 1-2 of `claude/plantstockmanager-phase2-redesign-plan.md`): loosens `dbo.Area.PolyhouseId` back to nullable (Main Office has no Polyhouse), adds `AreaType`/`SupervisorId`/`Location`/`Remarks` to `dbo.Area`, creates `dbo.Roles`/`dbo.Permissions`/`dbo.RolePermissions`/`dbo.UserRoles`, and seeds roles/permissions/default grants -- including a temporary safety-net Admin grant for every existing user so nobody is locked out once enforcement is wired up in the application. Does **not** touch `dbo.Bookings`/`dbo.PottedPlantBookings`/`dbo.VendorPurchases`.
14. `Phase15_CuttingStock_TransferConfirmation.sql` — creates `dbo.CuttingStock` + `dbo.CuttingStockTransactions` (own ledger, per Decision 1), widens `dbo.InternalTransfers`' Status CHECK to add `'PendingConfirmation'`/`'Rejected'`, adds `SourceCuttingStockId`/`PendingConfirmationAreaId`/`ConfirmedQuantity`/`ConfirmedBy`/`ConfirmedDate`/`DiscrepancyReason` to `dbo.InternalTransfers`, loosens its `DestinationAreaId` to nullable for Cutting-type rows only (destination isn't known until Main Office confirms). Does **not** touch `dbo.CuttingPlans`/`dbo.ActualCuttings`/`dbo.CuttingDeliveries`/`dbo.PropagationBatches` (Phases 3-6) — that chain is left completely separate.
15. `Phase16_CuttingWorkflow_ModelB.sql` — **supersedes Phase 15's single-step Cutting confirmation only.** Adds `InTransitQuantity` (maintained) + `AvailableQuantity` (`PERSISTED` computed, `= PhysicalQuantity - InTransitQuantity`) to `dbo.CuttingStock`, plus two CHECK constraints (`InTransitQuantity >= 0`, `InTransitQuantity <= PhysicalQuantity`); drops and recreates `CK_InternalTransfers_Status` to add `'ConfirmedAwaitingTransplant'`/`'Transplanted'`; drops and recreates `CK_CuttingStockTx_Type` to add `'Transplanted'`; creates `dbo.CuttingTransplants` (1:1 with a `Transplanted` `InternalTransfers` row: `DestinationSupervisorId → IMSUsers.Id`, `TransplantDate`, `Remarks`). Every existing EmptyPot/PottedPlant transfer row, every existing CuttingStock/CuttingStockTransactions row, and the entire Phase 3-6 chain are unaffected — `InTransitQuantity` defaults to 0 for rows that already exist, and no existing Status/TransactionType value was removed.
16. `Phase17_GrowingPartnerFoundation.sql` — creates `dbo.GrowingPartners` (new master, unrelated to `dbo.Vendors`); adds nullable `Area.GrowingPartnerId` + `FK_Area_GrowingPartner` + `IX_Area_GrowingPartnerId`. Does **not** touch `dbo.Vendors`, `dbo.UserRoles`/`Roles`/`Permissions`/`RolePermissions`, or any Phase 16 object. Every existing `dbo.Area` row is unaffected — `GrowingPartnerId` defaults to `NULL` (internal/no partner).
17. `Phase18_MainOfficeGrowingPartnerIssue.sql` — adds `PottedPlantStock.InTransitQuantity` (maintained, mirrors `CuttingStock.InTransitQuantity` from Phase 16) plus two CHECK constraints (`InTransitQuantity >= 0`, `InTransitQuantity <= PhysicalQuantity`); drops and recreates `CK_InternalTransfers_StockType` and `CK_InternalTransfers_SourceMatchesStockType` to add a fourth StockType value, `'MainOfficeIssue'` (source = `SourcePottedPlantStockId`). Does **not** widen `CK_InternalTransfers_Status`/`_DestinationRequired`/`_DifferentAreas`/`_ConfirmedQuantity` — all four already sufficient as-is for the new StockType. Does **not** touch `dbo.CuttingStock`, `dbo.CuttingStockTransactions`, `dbo.CuttingTransplants`, or any Phase 16 CHECK constraint. Every existing EmptyPot/PottedPlant/Cutting transfer row and every existing `PottedPlantStock` row are unaffected — `InTransitQuantity` defaults to 0 for rows that already exist.
18. `Phase19_CuttingToPotProduction.sql` — relaxes `PotProduction.PropagationBatchId`/`MotherPlantId` to nullable; adds nullable `SourceCuttingStockId INT NULL` + `FK_PotProduction_CuttingStock` + `IX_PotProduction_SourceCuttingStockId`, and nullable `CuttingQuantityConsumed DECIMAL(18,2) NULL`; adds `CK_PotProduction_SourceType` (exactly one of `PropagationBatchId`/`SourceCuttingStockId`, `MotherPlantId` tied to `PropagationBatchId`) and `CK_PotProduction_CuttingQuantityConsumed` (`CuttingQuantityConsumed` populated iff `SourceCuttingStockId` is, and `>= Quantity`); drops and recreates `fn_PotProduction_MatchesPropagationBatch`/`fn_PotProduction_WithinSurvivedQuantity` (and their backing `CK_PotProduction_MatchesPropagationBatch`/`CK_PotProduction_WithinSurvivedQuantity` constraints) to bypass (return 1) when `PropagationBatchId IS NULL` — every existing row is validated identically to before this script; drops and recreates `CK_CuttingStockTx_Type` to add `'ReversalReturn'` (crediting cuttings back on a cancelled Cutting-sourced production — the one new ledger type this phase needed). Does **not** touch `dbo.CuttingStock`'s own columns/CHECK constraints, `dbo.CuttingTransplants`, `dbo.PottedPlantStockTransactions`'s type CHECK (already sufficient), or any other Phase 16 object. Every existing `PotProduction` row is unaffected — `SourceCuttingStockId`/`CuttingQuantityConsumed` default to `NULL`, and both already-populated `PropagationBatchId`/`MotherPlantId` satisfy every new CHECK constraint as-is.

The Reporting/Dashboard phase (`Pages/Production/Dashboard/Index`) added **no** new database objects — it only reads existing tables through their own repositories — so it has no migration script.

**Phase E — Growing Partner Pot/Tray Production & Stock — also added no database objects and has no migration script.** Every requirement (Area-scoping, Growing Partner/production traceability on `PottedPlantStock`/`EmptyPotInventory`, tray production, Internal Movement compatibility) was met with read-time `LEFT JOIN`/`OUTER APPLY` queries and application-layer `AreaAccessService` checks against columns/tables that already existed after Phase 19 — see Decision 16 in `PROJECT_DOCUMENTATION.md` for the full reasoning, including why no `ContainerType` column was added for trays.

19. `Phase20_GrowingPartnerToOutlet.sql` — drops and recreates `CK_InternalTransfers_StockType` and `CK_InternalTransfers_SourceMatchesStockType` to add a fifth StockType value, `'GrowingPartnerToOutlet'` (source = `SourcePottedPlantStockId`, same shape as `PottedPlant`/`MainOfficeIssue`). Does **not** widen `CK_InternalTransfers_Status`/`_DestinationRequired`/`_DifferentAreas`/`_ConfirmedQuantity` — all four already sufficient as-is for the new StockType (confirmed by reading their Phase 8/15/18 definitions directly). Does **not** add any column — reuses `PottedPlantStock.InTransitQuantity` (Phase 18) exactly as-is. Does **not** touch `dbo.CuttingStock`, `dbo.CuttingStockTransactions`, `dbo.CuttingTransplants`, `dbo.PotProduction`, or any Phase 16/19 object. Every existing InternalTransfers row of any StockType is unaffected — this script only widens two CHECK constraints' allowed-value lists; no column default, no data row is touched. See Decision 17 in `PROJECT_DOCUMENTATION.md` for the full reasoning.
20. `Phase21_OutletSalesBookingDispatch.sql` — adds one new maintained column, `PottedPlantBookings.DispatchedQuantity DECIMAL(18,2) NOT NULL DEFAULT (0)` + `CK_PottedPlantBookings_DispatchedQuantity` (`>= 0 AND <= Quantity`), with a one-time backfill setting it to `Quantity` for every pre-existing `'Dispatched'` row (idempotent — a second run is a no-op since the backfill's own `WHERE` filter no longer matches once applied); drops and recreates `CK_PottedPlantBookings_Status` to add `'PartiallyDispatched'`; drops `UQ_Dispatches_Booking` (more than one Dispatch row may now reference the same Booking, enabling partial dispatch); drops and recreates `fn_Dispatches_MatchesBooking`/`CK_Dispatches_MatchesBooking`, relaxing the Quantity check from strict equality to Booking.Quantity down to a coarse `> 0 AND <= Booking.Quantity` bound (the precise "fits within what's remaining right now" check moves into `DispatchRepository.InsertAsync` under the Booking row's own lock). Does **not** add any column to `dbo.PottedPlantStock`, does **not** touch `dbo.CuttingStock`/`dbo.PotProduction`/`dbo.InternalTransfers` or any Phase 16/18/19/20 object. Every pre-existing `Pending`/`Cancelled` Booking row is unaffected beyond the one intentional `Dispatched`-row backfill described above. See Decision 18 in `PROJECT_DOCUMENTATION.md` for the full reasoning.
21. `Phase22_MainOfficeSeedIssue.sql` — creates three brand-new tables from scratch: `dbo.SeedStock` (per Species+Area+BatchNo pool — `PhysicalQuantity`/`InTransitQuantity` + a real `PERSISTED` `AvailableQuantity = PhysicalQuantity - InTransitQuantity`; `UNIQUE (SpeciesId, AreaId, BatchNo)`; FKs to the existing `dbo.PlantSpecies`/`dbo.Area`/`dbo.SeedSources`), `dbo.SeedStockTransactions` (its own dedicated ledger, `CK_SeedStockTx_Type IN ('StockIn', 'Transfer')`), and `dbo.SeedIssues` (the Main Office → Polyhouse issue header — `CK_SeedIssues_Status IN ('PendingConfirmation', 'Completed', 'Rejected')`, `IssueCode` unique, `ConfirmedQuantity`/`DiscrepancyReason`/`ConfirmedBy`/`ConfirmedDate`). Does **not** alter, widen, or drop any existing table or constraint — in particular, `dbo.InternalTransfers`' `CK_InternalTransfers_StockType`/`_SourceMatchesStockType` are untouched; no sixth `StockType` value was added there (see Decision 19 in `PROJECT_DOCUMENTATION.md` for the full reasoning on why a dedicated table set was chosen over extending `InternalTransfers` or the legacy `SeedCuttingBank`/`SeedCuttingTx`). Does **not** touch `dbo.CuttingStock`, `dbo.PotProduction`, `dbo.PottedPlantStock`, or any Phase 16/18/19/20/21 object. Since every object this script creates is brand new, there is no pre-existing row to backfill or migrate.
22. `Phase23_SeedSowing.sql` — drops and recreates `CK_SeedStockTx_Type` to add `'Sown'` and `'ReversalReturn'` (the only change to any existing table); creates one brand-new table, `dbo.SeedSowings` (the Sowing production-event header — `SourceSeedStockId`, denormalized Species/Area/BatchNo/SeedSource, `CavityType` (**post-review correction**: closed 5-value list `9/24/42/102/150 Cavity`, enforced by `CK_SeedSowings_CavityType CHECK (CavityType IN ('9 Cavity', '24 Cavity', '42 Cavity', '102 Cavity', '150 Cavity'))`, no longer free text — this constraint was added in place to the not-yet-executed script, see Decision 20), optional `NumberOfTrays`, `QuantitySown`, `SowingDate`, optional `ExpectedReadyDate`, `CK_SeedSowings_Status IN ('Sown', 'Cancelled')`, `SowingCode` unique). Does **not** touch `dbo.SeedIssues`, `dbo.InternalTransfers`, `dbo.CuttingStock`, or `dbo.PotProduction`. See Decision 20 in `PROJECT_DOCUMENTATION.md` for the full reasoning.
23. `Phase24_ReadyAlerts.sql` — **no new table** (Ready Alerts is entirely query-driven against the existing `dbo.SeedSowings`, per the spec's own "a read-only/query-driven Ready Alert page is acceptable" instruction). Adds one new nullable column to the existing plant/variety master, `dbo.PlantSpecies.ReadyStockDays INT` + `CK_PlantSpecies_ReadyStockDays CHECK (ReadyStockDays IS NULL OR ReadyStockDays > 0)` (reused master, not a new lookup table). Adds one new nullable column to `dbo.SeedSowings.ReadyStockDays INT` + `CK_SeedSowings_ReadyStockDays` (same shape) — the denormalized value actually applied at sowing time, so a later edit to `PlantSpecies.ReadyStockDays` can never rewrite an already-inserted Sowing's historical `ExpectedReadyDate`/`ReadyStockDays` (see Decision 21). Does **not** touch `CK_SeedSowings_Status`, `CK_SeedSowings_CavityType`, `CK_SeedStockTx_Type`, `dbo.SeedIssues`, `dbo.InternalTransfers`, `dbo.CuttingStock`, or `dbo.PotProduction`. Every existing `PlantSpecies`/`SeedSowings` row is unaffected — both new columns default to `NULL`. See Decision 21 in `PROJECT_DOCUMENTATION.md` for the full reasoning.
24. `Phase25_ReadyConfirmation.sql` — adds one new maintained column, `dbo.SeedSowings.ConfirmedReadyQuantity DECIMAL(18,2) NOT NULL DEFAULT (0)` + `CK_SeedSowings_ConfirmedReadyQuantity CHECK (>= 0 AND <= QuantitySown)` (mirrors `PottedPlantBookings.DispatchedQuantity` from Phase 21 — every pre-existing `SeedSowings` row defaults to `0`, the correct historical value since none has ever been Ready-Confirmed). Creates three brand-new tables: `dbo.ReadyStock` (the confirmed-ready stock pool, **one row per `SeedSowingId`**, `UNIQUE (SeedSowingId)` — deliberately not a Species+Area+Lot pool, for batch-traceability reasons, see Decision 22), `dbo.ReadyStockTransactions` (its own dedicated ledger per Decision 1, `CK_ReadyStockTx_Type IN ('Confirmed', 'ReversalRemoval')`), and `dbo.ReadyConfirmations` (the auditable confirmation event/header — `ConfirmationCode` unique ("RDY-" prefix), `CK_ReadyConfirmations_Status IN ('Confirmed', 'Cancelled')`, FKs to both `SeedSowings` and `ReadyStock`). Does **not** touch `CK_SeedSowings_Status` (stays `Sown`/`Cancelled` only — no `'Ready'`/`'PartiallyReady'` value added, a deliberate departure from Phase 21's own `DispatchedQuantity`+`'PartiallyDispatched'`-status precedent, see Decision 22), `CK_SeedSowings_CavityType`, `CK_SeedStockTx_Type`, `dbo.SeedIssues`, `dbo.InternalTransfers`, `dbo.CuttingStock`, `dbo.PotProduction`, or any Phase 16/18/19/20/21/22/23/24 object. Since `dbo.ReadyStock`/`dbo.ReadyStockTransactions`/`dbo.ReadyConfirmations` are brand new, there is no pre-existing row to backfill or migrate for them. See Decision 22 in `PROJECT_DOCUMENTATION.md` for the full reasoning.
25. `Phase26_ManagementDashboard.sql` — **no new table, no new column, no widened CHECK constraint, no changed workflow.** Adds exactly 8 guarded indexes supporting the new read-only Management Dashboard's aggregate/date-range queries (`Data/ManagementDashboardRepository.cs`): `IX_PotProduction_AreaId`, `IX_PotProduction_ProductionDate`, `IX_Dispatches_DispatchDate`, `IX_SeedSowings_SowingDate`, `IX_SeedIssues_IssueDate`, `IX_ReadyStockTransactions_TransactionDate`, `IX_CuttingStockTransactions_TransactionDate`, `IX_InternalTransfers_StockType`, `IX_PottedPlantBookings_AreaId` — each identified during inspection as a genuine gap (a column the dashboard filters/groups by that had no supporting index in any prior phase), each guarded with the standard `IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = ... AND object_id = OBJECT_ID(...))` pattern used since Phase 2/8/14/17/19. Does **not** touch `dbo.CuttingStock`, `dbo.PottedPlantStock`, `dbo.SeedStock`, `dbo.ReadyStock`, `dbo.GrowingPartners`, or any existing table's data, columns, or constraints — every existing query/workflow across every prior phase is unaffected; new indexes only ever make an existing query plan faster, never change its result. See Decision 23 in `PROJECT_DOCUMENTATION.md` for the full reasoning, including the KPI-to-authoritative-source mapping and the Administrator-only authorization design.

26. `PhaseA_RoleBasedAccess.sql` — **repairs and completes Phase 14's role system** (run after `Phase14_RoleFoundation_AreaExtension.sql`; independent of Phases 15–26). In production `dbo.Roles` pre-dated Phase 14 (`Id, RoleName`, later plus `Name`), so Phase 14's role seed failed and `RolePermissions`/`UserRoles` stayed empty. This script: guarantees both `Roles.Name` and `Roles.RoleName` exist and are in sync, and adds the missing `Description` (NULL) / `IsSystemRole` (BIT, default 0) / `CreatedDate` (default `SYSUTCDATETIME()`) columns; adds roles `Fertilizer Supervisor` and `Mother Plant Supervisor` (matching existing designations 6/7, approved by the business owner) and flags `System Administrator` as `IsSystemRole = 1`; adds 13 permission codes (`Sowing.*`, `ReadyStock.*`, `SeedStock.*`, `Fertilizer.*`, `Labour.*`, `Booking.Direct`, `Admin.ManageMasters`); backfills `dbo.UserRoles` **only for users with no role yet** (legacy `DesignationID` → role of the same name, plus the Areas whose `SupervisorId` is that user); seeds default `RolePermissions` **only for roles that have none**. No DROP, no DELETE, no change to users/passwords/designations/stock. Idempotent. `System Administrator` needs no `RolePermissions` rows — full access is granted by the application (see `Authorization/SecurityOptions.cs`).

27. `PhaseB_DirectSowing.sql` — **Seed Stock → Direct Sowing → Supervisor Approval → Ready Stock** (run after `PhaseA_RoleBasedAccess.sql` and Phases 22–25). No new table, no DROP/rename of any column, no change to stock, ledgers, sowings, Seed Issues or legacy data. Adds `Polyhouses.AreaId` (Area = site → Polyhouse inside it; the existing `Area.PolyhouseId` is **not** reversed), `SeedSowings.PolyhouseId` / `WastageQuantity` and widens its status to `Sown | Completed | Cancelled` (a Completed sowing must satisfy Ready + Wastage = Sown), `ReadyConfirmations.WastageQuantity` / `WastageReason` (closed list) / `ApprovedById`, `ReadyStock.PolyhouseId`, and the `Sowing Supervisor` role (on the run that creates it, `ReadyStock.Confirm` moves from `Sowing Operator` to it — the single RolePermissions row it removes). The batch number `YYYY-MM-DD-L-NNN` is stored in the existing unique `SeedSowings.SowingCode` and sequenced by the existing `dbo.BatchNumberSequences`. Idempotent. **After running:** assign each Polyhouse to its Area (Admin → Polyhouse) and set growing days per variety (Admin → Plant Master) — sowing is refused for a variety without growing days.

27b. `PhaseB_SeedlingRules.sql` — run **immediately after** `PhaseB_DirectSowing.sql`. Additive and idempotent: adds `SeedSowings.CreatedById` (nullable, FK → `IMSUsers`, the user who recorded the sowing — used by the self-approval rule: nobody approves a sowing they recorded) and whole-number CHECK constraints (`CK_*_WholeQuantities`) on `SeedStock`, `SeedStockTransactions`, `SeedSowings`, `ReadyConfirmations`, `ReadyStock` and `ReadyStockTransactions` (seeds and plants are whole units). No row is changed; a constraint cannot be added while an existing row breaks it.

27c. `PhaseB_ApprovalAndTrayRules.sql` — run after `PhaseB_SeedlingRules.sql` (with `QUOTED_IDENTIFIER ON`, e.g. `sqlcmd -I`). Additive and idempotent: adds `CK_SeedSowings_TrayCount` (`NumberOfTrays = FLOOR(QuantitySown / tray size)`, at least one complete tray; added only if every existing sowing already satisfies it) and the INSERT-only trigger `TR_ReadyConfirmations_AssignedSupervisor` (a Supervisor Approval is refused unless `ApprovedById` is the sowing's assigned `SupervisorId` and not its `CreatedById`). No table, column or row is changed; existing approvals are not re-validated.

28. `PhaseC_Booking_ReadyStock.sql` — **Ready Stock → seedling Booking → Reservation → Batch Allocation → Dispatch** (run after `PhaseB_DirectSowing.sql`). Additive and idempotent: no DELETE, no UPDATE of existing rows, no dropped/renamed table or column. `dbo.ReadyStock` gains `ReservedQuantity`, `DispatchedQuantity` and computed `PhysicalQuantity` / `AvailableQuantity` (`Quantity` keeps its Phase B meaning, the approved Ready quantity; CHECK Reserved + Dispatched ≤ Quantity); `dbo.ReadyStockTransactions` accepts `Reservation` / `ReservationRelease` / `Dispatch`; `dbo.Bookings` gains `ReservedQuantity`, `DispatchedQuantity`, `FulfilmentSource` (NULL / Legacy / ReadyStock), `RevisionNo`, `ParentBookingId`, cancellation and modification columns — all defaulted, existing bookings are not rewritten and the Status CHECK is untouched. New tables: `BookingBatchAllocations`, `BookingRevisions`, `SeedlingDispatches`, `SeedlingDispatchLines`. The potted-plant `PottedPlantBookings` / `Dispatches` tables are not touched. Legacy Inventory fulfilment stays available until `LegacySeedPipeline:AllowLegacyInventoryFulfilment` is set to `false` after the cutover.

28b. `PhaseB_ReadyStockTrays.sql` — run after `PhaseB_ApprovalAndTrayRules.sql` and `PhaseC_Booking_ReadyStock.sql` (with `QUOTED_IDENTIFIER ON`). Refuses to run on any database other than `PlantsIMS2_Test` until it is approved for production. Additive and idempotent: adds `SeedSowings.SeedQuantity` and `ReadyConfirmations.ActualTrayQuantity` (both NULL for existing rows), `CK_SeedSowings_SeedQuantity` (Seeds Used = trays × cavity, Remaining Seeds < one tray), `CK_ReadyConfirmations_ActualTrays`, the filtered unique index `UX_ReadyConfirmations_OneConfirmedPerSowing`, `UQ_SeedSowings_IdCavity` + `FK_ReadyStock_SowingCavity` (Ready Stock cavity = sowing cavity), and the triggers `TR_SeedSowings_RequireSeedQuantity`, `TR_SeedSowings_ImmutableTrayData`, `TR_ReadyConfirmations_TrayQuantity` (seedlings = Actual Ready Trays × sowing cavity; wastage = seeds sown − seedlings) and `TR_ReadyStock_WholeTrays` (a new or changed Ready Stock quantity is whole trays). No existing row is changed; rows from before the tray rule are not re-validated. **The application code of this branch needs these columns — deploy the code and this script together.**

29. `PhaseD_ProductionRestructure.sql` — **one consistent production + stock flow** (run after `PhaseB_ReadyStockTrays.sql`, with `QUOTED_IDENTIFIER ON`). Refuses to run on any database other than `PlantsIMS2_Test`. Additive and idempotent; no existing business row is changed (the only UPDATE copies the old `Area.PolyhouseId` links into `Polyhouses.AreaId`):
    * **Masters:** new `dbo.PotSizes` (seeded with the pot sizes already stored, verbatim) + a FK from every `PotSize` column to it; `PlantSpecies.Color` (NULL).
    * **Cutting:** new `dbo.CuttingProductions` (Mother Plant → cuttings → Cutting Stock `'Harvest'`); `CuttingStockTransactions` accepts `'Sown'` and `'TransitLoss'`; a completed Cutting delivery must record the received quantity and destination (`CK_InternalTransfers_CuttingCompleted`). Main Office confirmation now moves the received cuttings into Main Office Cutting Stock in one step (the separate Transplant step is retired).
    * **Cutting tray sowing:** `SeedSowings.SourceType` ('Seed' for every existing row) + `SourceCuttingStockId`; `SourceSeedStockId` becomes NULLable (its FK and index are dropped and recreated identically); `CK_SeedSowings_Source`; `TR_SeedSowings_ImmutableTrayData` now also freezes the source, the **assigned supervisor** and the recorder; `TR_SeedSowings_SupervisorRole` (a new sowing's supervisor is an active Sowing Supervisor other than the recorder).
    * **Pot production:** new `dbo.EmptyPotPurchases`, `dbo.PotProductionBatches`, `dbo.PotProductionEntries` with CHECKs, composite FKs (a batch can only use its own Area's empty pots; READY stock goes to the batch's variety/pot size/Area) and triggers (assigned Mother Plant Supervisor of the Area confirms READY, never the creator; production never exceeds the cuttings allocated; entries are immutable; a closed batch never changes).
    * **Integrity:** whole-number CHECKs on cutting / empty-pot / potted-plant stock and ledgers; `TR_MotherPlants_AreaAndSupervisor` (new Mother Plants have an Area; a supervisor set or changed is a Mother Plant Supervisor **of that Area**; the Polyhouse belongs to that Area).
    * **Complete loss:** a pot batch can be closed with 0 ready plants (status `Lost`, loss reason mandatory, nothing added to Potted Plant Stock); READY / Lost is re-checked against the confirmer's current Mother Plant Supervisor role for the Area.
    * **Area → Polyhouse:** `Polyhouses.AreaId` is the only link. The old `Area.PolyhouseId` values are copied once (only unambiguous ones); the column becomes NULLable (FK + 3 indexes dropped and recreated identically) and `TR_Area_PolyhouseIdRetired` stops new values being written. Nothing is dropped.
    * **Roles (created with no users):** Main Office Store Keeper, Purchase Officer, Pot Production Operator, Outlet Sales, each with only the permissions its job needs. No existing role is changed.
    * **Ready Stock → Main Office / Outlet:** new `InternalTransfers.SourceReadyStockId` + `FK_InternalTransfers_ReadyStock`; `CK_InternalTransfers_StockType` and `CK_InternalTransfers_SourceMatchesStockType` recreated to add `'ReadyStock'`; `CK_ReadyStockTx_Type` recreated to add `'Transfer'`. A batch (one row per `SeedSowingId`) moves as a whole to another Area — never split — the same immediate, no-confirmation shape `'PottedPlant'` already has; only allowed before anything is reserved or dispatched from it (checked by the application under the row lock, not a DB trigger).
    * **Mother Plant Supervisor** is granted `InternalTransfer.Enter` — the Send/Move screens already required it, but the role had never actually been given it.
    **The application code of this branch needs these objects — deploy the code and this script together.**

## Verifying a run

After running all twenty-five scripts, a quick sanity check:

```sql
SELECT name FROM sys.tables WHERE name IN (
  'Area','BatchNumberSequences','MotherPlants','CuttingPlans','ActualCuttings',
  'CuttingDeliveries','PropagationBatches','EmptyPotInventory','EmptyPotInventoryTransactions',
  'PottedPlantStock','PottedPlantStockTransactions','PotProduction','InternalTransfers',
  'PottedPlantBookings','Dispatches','Vendors','PurchaseOrders','PurchaseOrderItems',
  'PurchaseReceipts','PurchaseReceiptItems','LabRequests','LabourLogs',
  'Roles','Permissions','RolePermissions','UserRoles',
  'CuttingStock','CuttingStockTransactions','CuttingTransplants','GrowingPartners',
  'SeedStock','SeedStockTransactions','SeedIssues','SeedSowings',
  'ReadyStock','ReadyStockTransactions','ReadyConfirmations'
);
```

should now return all 36 names (Phase 18/19/20/21/24 added no new table, only columns/constraints on existing ones; Phase 22 added the three Seed Issue tables; Phase 23 adds exactly one, `SeedSowings`; Phase 25/K adds the last three, `ReadyStock`/`ReadyStockTransactions`/`ReadyConfirmations` -- see item 24 below; Phase 26/L adds no table either -- indexes only, see item 25 below). The count stays 36 after Phase 26. `dbo.VendorPurchases` should still exist, completely unchanged, alongside them.

After Phase 14 specifically, also confirm:

```sql
SELECT AreaType, SupervisorId, Location, Remarks FROM dbo.Area; -- new columns exist, all NULL for existing rows
SELECT COUNT(*) FROM dbo.UserRoles ur INNER JOIN dbo.Roles r ON ur.RoleId = r.Id WHERE r.Name = 'Admin';
  -- should equal the number of active dbo.IMSUsers rows (the temporary safety-net grant)
```

After Phase 15/16 specifically, also confirm:

```sql
-- Phase 16 columns/constraints exist on the Phase 15 table:
SELECT InTransitQuantity, AvailableQuantity FROM dbo.CuttingStock; -- InTransitQuantity = 0, AvailableQuantity = PhysicalQuantity for every pre-existing row
SELECT definition FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_Status';
  -- should list PendingConfirmation, Rejected, ConfirmedAwaitingTransplant, Transplanted, Completed, Cancelled
SELECT definition FROM sys.check_constraints WHERE name = 'CK_CuttingStockTx_Type';
  -- should list Harvest, Transfer, Potted, Adjustment, ReversalRemoval, Transplanted
SELECT COUNT(*) FROM dbo.CuttingTransplants; -- 0 on a fresh run; one row per Transplanted Cutting transfer thereafter
```

After Phase 17 specifically, also confirm:

```sql
SELECT GrowingPartnerId FROM dbo.Area; -- NULL for every pre-existing row
SELECT COUNT(*) FROM dbo.GrowingPartners; -- 0 on a fresh run
SELECT name FROM sys.foreign_keys WHERE name = 'FK_Area_GrowingPartner'; -- exists
SELECT name FROM sys.indexes WHERE name = 'IX_Area_GrowingPartnerId'; -- exists
```

After Phase 18 specifically, also confirm:

```sql
SELECT InTransitQuantity FROM dbo.PottedPlantStock; -- 0 for every pre-existing row
SELECT definition FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_StockType';
  -- should list EmptyPot, PottedPlant, Cutting, MainOfficeIssue
SELECT definition FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_SourceMatchesStockType';
  -- should include a MainOfficeIssue clause requiring SourcePottedPlantStockId
SELECT COUNT(*) FROM dbo.InternalTransfers WHERE StockType = 'MainOfficeIssue'; -- 0 on a fresh run
-- Phase 16 unaffected:
SELECT definition FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_Status';
  -- unchanged from Phase 16 -- still lists PendingConfirmation, Rejected, ConfirmedAwaitingTransplant, Transplanted, Completed, Cancelled
```

After Phase 19 specifically, also confirm:

```sql
SELECT PropagationBatchId, MotherPlantId, SourceCuttingStockId, CuttingQuantityConsumed FROM dbo.PotProduction;
  -- every pre-existing row: PropagationBatchId/MotherPlantId still populated, SourceCuttingStockId/CuttingQuantityConsumed both NULL
SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PotProduction') AND name IN ('PropagationBatchId', 'MotherPlantId');
  -- both 1 (nullable) now
SELECT definition FROM sys.check_constraints WHERE name IN ('CK_PotProduction_SourceType', 'CK_PotProduction_CuttingQuantityConsumed');
  -- both exist
SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.fn_PotProduction_MatchesPropagationBatch'));
  -- contains "IF @PropagationBatchId IS NULL RETURN 1"
SELECT definition FROM sys.check_constraints WHERE name = 'CK_CuttingStockTx_Type';
  -- should list Harvest, Transfer, Potted, Adjustment, ReversalRemoval, Transplanted, ReversalReturn
SELECT COUNT(*) FROM dbo.PotProduction WHERE SourceCuttingStockId IS NOT NULL; -- 0 on a fresh run
-- Phase 16 unaffected:
SELECT definition FROM sys.check_constraints WHERE name = 'CK_CuttingStock_InTransitNotExceedPhysical'; -- unchanged, still exists
```

After Phase 20 specifically, also confirm:

```sql
SELECT definition FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_StockType';
  -- should list EmptyPot, PottedPlant, Cutting, MainOfficeIssue, GrowingPartnerToOutlet
SELECT definition FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_SourceMatchesStockType';
  -- should include a GrowingPartnerToOutlet clause requiring SourcePottedPlantStockId
SELECT COUNT(*) FROM dbo.InternalTransfers WHERE StockType = 'GrowingPartnerToOutlet'; -- 0 on a fresh run
-- Phase 16/18 unaffected:
SELECT definition FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_Status';
  -- unchanged from Phase 16 -- still lists PendingConfirmation, Rejected, ConfirmedAwaitingTransplant, Transplanted, Completed, Cancelled
SELECT InTransitQuantity FROM dbo.PottedPlantStock; -- unchanged column, still 0 for every pre-Phase-18 row
```

After Phase 21 specifically, also confirm:

```sql
SELECT DispatchedQuantity FROM dbo.PottedPlantBookings; -- 0 for every Pending/Cancelled row; = Quantity for every pre-existing Dispatched row (backfilled)
SELECT definition FROM sys.check_constraints WHERE name = 'CK_PottedPlantBookings_Status';
  -- should list Pending, PartiallyDispatched, Dispatched, Cancelled
SELECT name FROM sys.key_constraints WHERE name = 'UQ_Dispatches_Booking'; -- should return NO rows (dropped)
SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.fn_Dispatches_MatchesBooking'));
  -- should contain "@Quantity > 0" and "@Quantity <= Quantity", NOT a strict "= @Quantity" equality check
SELECT COUNT(*) FROM dbo.Dispatches WHERE Status = 'Completed' GROUP BY PottedPlantBookingId HAVING COUNT(*) > 1;
  -- 0 rows on a fresh run; > 1 per BookingId is now legal (partial dispatch) where it was impossible before Phase 21
-- Phase 16/18/19/20 unaffected:
SELECT definition FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_StockType';
  -- unchanged from Phase 20 -- still lists EmptyPot, PottedPlant, Cutting, MainOfficeIssue, GrowingPartnerToOutlet
```

After Phase 22 specifically, also confirm:

```sql
SELECT name FROM sys.tables WHERE name IN ('SeedStock', 'SeedStockTransactions', 'SeedIssues'); -- all 3 exist
SELECT COUNT(*) FROM dbo.SeedStock; -- 0 on a fresh run
SELECT COUNT(*) FROM dbo.SeedIssues; -- 0 on a fresh run
SELECT definition FROM sys.check_constraints WHERE name = 'CK_SeedIssues_Status';
  -- should list PendingConfirmation, Completed, Rejected
SELECT definition FROM sys.check_constraints WHERE name = 'CK_SeedStockTx_Type';
  -- should list StockIn, Transfer
SELECT is_persisted FROM sys.computed_columns WHERE object_id = OBJECT_ID('dbo.SeedStock') AND name = 'AvailableQuantity'; -- 1
-- Phase 16/18/19/20/21 unaffected:
SELECT definition FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_StockType';
  -- unchanged from Phase 20 -- still lists EmptyPot, PottedPlant, Cutting, MainOfficeIssue, GrowingPartnerToOutlet (no sixth SeedIssue-related value added)
SELECT DispatchedQuantity FROM dbo.PottedPlantBookings; -- unchanged column from Phase 21
```

After Phase 23 specifically, also confirm:

```sql
SELECT name FROM sys.tables WHERE name = 'SeedSowings'; -- exists
SELECT COUNT(*) FROM dbo.SeedSowings; -- 0 on a fresh run
SELECT definition FROM sys.check_constraints WHERE name = 'CK_SeedSowings_Status';
  -- should list Sown, Cancelled
SELECT definition FROM sys.check_constraints WHERE name = 'CK_SeedSowings_CavityType';
  -- POST-REVIEW CORRECTION: should list exactly
  -- '9 Cavity', '24 Cavity', '42 Cavity', '102 Cavity', '150 Cavity'
SELECT definition FROM sys.check_constraints WHERE name = 'CK_SeedStockTx_Type';
  -- should now list StockIn, Transfer, Sown, ReversalReturn
-- Phase 22 unaffected:
SELECT name FROM sys.tables WHERE name IN ('SeedStock', 'SeedIssues'); -- both still exist, unchanged
SELECT COUNT(*) FROM dbo.SeedIssues; -- unaffected by this script
```

After Phase 24 specifically, also confirm:

```sql
-- No new table was created by this script -- confirm the count stays 33
-- (the same query from the top-level sanity check above):
SELECT COUNT(*) FROM sys.tables WHERE name IN (
  'Area','BatchNumberSequences','MotherPlants','CuttingPlans','ActualCuttings',
  'CuttingDeliveries','PropagationBatches','EmptyPotInventory','EmptyPotInventoryTransactions',
  'PottedPlantStock','PottedPlantStockTransactions','PotProduction','InternalTransfers',
  'PottedPlantBookings','Dispatches','Vendors','PurchaseOrders','PurchaseOrderItems',
  'PurchaseReceipts','PurchaseReceiptItems','LabRequests','LabourLogs',
  'Roles','Permissions','RolePermissions','UserRoles',
  'CuttingStock','CuttingStockTransactions','CuttingTransplants','GrowingPartners',
  'SeedStock','SeedStockTransactions','SeedIssues','SeedSowings'
); -- still 33 -- Phase 24 added no table

SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PlantSpecies') AND name = 'ReadyStockDays';
  -- exists, nullable INT
SELECT definition FROM sys.check_constraints WHERE name = 'CK_PlantSpecies_ReadyStockDays';
  -- ReadyStockDays IS NULL OR ReadyStockDays > 0

SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'ReadyStockDays';
  -- exists, nullable INT (the value applied AT THE TIME OF SOWING -- never re-derived from PlantSpecies on read)
SELECT definition FROM sys.check_constraints WHERE name = 'CK_SeedSowings_ReadyStockDays';
  -- ReadyStockDays IS NULL OR ReadyStockDays > 0

-- Confirm Phase 23's own CavityType/Status CHECKs are untouched by this script:
SELECT definition FROM sys.check_constraints WHERE name = 'CK_SeedSowings_Status';
  -- should still list only Sown, Cancelled -- no 'Ready' value added
```

After Phase 25 specifically, also confirm:

```sql
-- Three new tables now exist -- confirm the count reaches 36:
SELECT COUNT(*) FROM sys.tables WHERE name IN (
  'Area','BatchNumberSequences','MotherPlants','CuttingPlans','ActualCuttings',
  'CuttingDeliveries','PropagationBatches','EmptyPotInventory','EmptyPotInventoryTransactions',
  'PottedPlantStock','PottedPlantStockTransactions','PotProduction','InternalTransfers',
  'PottedPlantBookings','Dispatches','Vendors','PurchaseOrders','PurchaseOrderItems',
  'PurchaseReceipts','PurchaseReceiptItems','LabRequests','LabourLogs',
  'Roles','Permissions','RolePermissions','UserRoles',
  'CuttingStock','CuttingStockTransactions','CuttingTransplants','GrowingPartners',
  'SeedStock','SeedStockTransactions','SeedIssues','SeedSowings',
  'ReadyStock','ReadyStockTransactions','ReadyConfirmations'
); -- now 36

SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'ConfirmedReadyQuantity';
  -- exists, DECIMAL(18,2) NOT NULL DEFAULT 0
SELECT definition FROM sys.check_constraints WHERE name = 'CK_SeedSowings_ConfirmedReadyQuantity';
  -- ConfirmedReadyQuantity >= 0 AND ConfirmedReadyQuantity <= QuantitySown
SELECT ConfirmedReadyQuantity FROM dbo.SeedSowings; -- 0 for every pre-existing row

SELECT name FROM sys.indexes WHERE name = 'UQ_ReadyStock_SeedSowing'; -- exists (one ReadyStock row per Sowing)
SELECT definition FROM sys.check_constraints WHERE name = 'CK_ReadyStockTx_Type';
  -- should list Confirmed, ReversalRemoval
SELECT definition FROM sys.check_constraints WHERE name = 'CK_ReadyConfirmations_Status';
  -- should list Confirmed, Cancelled
SELECT COUNT(*) FROM dbo.ReadyStock; -- 0 on a fresh run
SELECT COUNT(*) FROM dbo.ReadyConfirmations; -- 0 on a fresh run

-- Confirm no earlier phase's object was widened/altered by this script:
SELECT definition FROM sys.check_constraints WHERE name = 'CK_SeedSowings_Status';
  -- should still list only Sown, Cancelled -- no new value added (see Decision 22)
SELECT definition FROM sys.check_constraints WHERE name = 'CK_SeedSowings_CavityType';
  -- unchanged from Phase 23/its post-review correction
-- Phase 22/23/24 unaffected:
SELECT name FROM sys.tables WHERE name IN ('SeedStock', 'SeedIssues', 'SeedSowings'); -- all still exist, unchanged
```

After Phase 26 specifically, also confirm:

```sql
SELECT name FROM sys.indexes WHERE name IN (
  'IX_PotProduction_AreaId', 'IX_PotProduction_ProductionDate',
  'IX_Dispatches_DispatchDate', 'IX_SeedSowings_SowingDate',
  'IX_SeedIssues_IssueDate', 'IX_ReadyStockTransactions_TransactionDate',
  'IX_CuttingStockTransactions_TransactionDate', 'IX_InternalTransfers_StockType',
  'IX_PottedPlantBookings_AreaId'
); -- all 9 rows returned (8 new indexes from this script, listed above)

-- No new table was created by this script -- confirm the count stays 36
-- (the same query from the top-level sanity check above returns 36):
SELECT COUNT(*) FROM sys.tables WHERE name IN (
  'Area','BatchNumberSequences','MotherPlants','CuttingPlans','ActualCuttings',
  'CuttingDeliveries','PropagationBatches','EmptyPotInventory','EmptyPotInventoryTransactions',
  'PottedPlantStock','PottedPlantStockTransactions','PotProduction','InternalTransfers',
  'PottedPlantBookings','Dispatches','Vendors','PurchaseOrders','PurchaseOrderItems',
  'PurchaseReceipts','PurchaseReceiptItems','LabRequests','LabourLogs',
  'Roles','Permissions','RolePermissions','UserRoles',
  'CuttingStock','CuttingStockTransactions','CuttingTransplants','GrowingPartners',
  'SeedStock','SeedStockTransactions','SeedIssues','SeedSowings',
  'ReadyStock','ReadyStockTransactions','ReadyConfirmations'
); -- still 36 -- Phase 26 added no table

-- Confirm no existing constraint/column was touched by this script:
SELECT definition FROM sys.check_constraints WHERE name = 'CK_SeedSowings_Status';
  -- unchanged from Phase 25 -- still lists only Sown, Cancelled
SELECT definition FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_StockType';
  -- unchanged from Phase 20 -- still lists EmptyPot, PottedPlant, Cutting, MainOfficeIssue, GrowingPartnerToOutlet
SELECT ConfirmedReadyQuantity FROM dbo.SeedSowings; -- unchanged column from Phase 25

-- Confirm the Administrator-only permission grant this dashboard relies on
-- (no NEW permission code was added -- Phase 26 reuses "Admin.ManageAreas",
-- seeded back in Phase 14):
SELECT COUNT(*) FROM dbo.RolePermissions rp
  INNER JOIN dbo.Roles r ON rp.RoleId = r.Id
  INNER JOIN dbo.Permissions p ON rp.PermissionId = p.Id
  WHERE p.Code = 'Admin.ManageAreas' AND r.Name <> 'Admin';
  -- should be 0 -- only the Admin role holds this permission code (unchanged from Phase 14)
```
