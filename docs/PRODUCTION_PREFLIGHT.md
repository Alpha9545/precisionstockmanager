# Production preflight checklist

Prepared 2026-10-10 for branch `restructure/production-workflow-v2`. **Nothing in this document
has been run against production.** Live = the VPS database (also named `PlantsIMS2_Test` — always
check `@@SERVERNAME`; the laptop is `LAPTOP-R87BKQGH\MSSQLSERVER02`). Every step below needs the
owner's go-ahead at the time it is done.

## 1. Stop conditions (do not deploy if any is true)

- [ ] Any migration in section 3 is missing on live and the code that needs it would be deployed.
- [ ] The backup in section 4 has not been restored and checked on another server.
- [ ] The release is not built from the Vehicle-Tracking-free scope (section 6, `docs/RELEASE_SCOPE_PLAN.md`).
- [ ] The full test suite on a fresh scratch restore shows a failure that is not in the accepted
      baseline list of the latest test report.

## 2. Environment checks (read-only)

```sql
SELECT @@SERVERNAME AS ServerName, DB_NAME() AS DatabaseName, SERVERPROPERTY('ProductVersion') AS Version;
SELECT name, compatibility_level, is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME();
```

- [ ] `@@SERVERNAME` is the VPS instance (NOT `LAPTOP-R87BKQGH\MSSQLSERVER02`). Write it down: every
      migration is run with `-v TARGET_SERVER=<that exact value>` and refuses to run elsewhere.
- [ ] Database name is the one the live `ConnectionStrings__DefaultConnection` points at.
- [ ] Free disk space for the backup and for the log growth of the migrations.
- [ ] Empty-tray history by business date is never below zero (read-only; expect 0 rows; any row is shown as a
      warning on Material History and is NOT changed):
      ```sql
      SELECT TrayStockId, MIN(Running) AS Lowest FROM (SELECT TrayStockId, SUM(Quantity) OVER (PARTITION BY TrayStockId
             ORDER BY TransactionDate, Id ROWS UNBOUNDED PRECEDING) AS Running FROM dbo.TrayStockTransactions) x
      GROUP BY TrayStockId HAVING MIN(Running) < 0;
      ```
- [ ] `sqlcmd` available on the machine that runs the scripts; run with `-I` (QUOTED_IDENTIFIER ON —
      several scripts create filtered indexes) and `-b` (stop on error).

## 3. Migrations and their order

Each script: guarded by the database-name allowlist and `TARGET_SERVER`; `MODE=DRYRUN` (rolled back)
then `MODE=COMMIT`; one transaction; safe to re-run (idempotent). Rollback scripts refuse while
dependent history exists.

```
sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <file>
sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=COMMIT TARGET_SERVER=<@@SERVERNAME> -i <file>
```

### 3a. Check what live already has

Earlier scripts (2026-09-21 … 2026-10-04) were deployed with previous releases; confirm rather than
assume. Read-only object checks:

```sql
SELECT 'TrayStock (10-02/10-04)',            OBJECT_ID('dbo.TrayStock')
UNION ALL SELECT 'TrayStock.PolyhouseId',     COL_LENGTH('dbo.TrayStock','PolyhouseId')
UNION ALL SELECT 'DailyLabourCounts (10-04)', OBJECT_ID('dbo.DailyLabourCounts')
UNION ALL SELECT 'CuttingProductions.DestinationType (09-28)', COL_LENGTH('dbo.CuttingProductions','DestinationType')
UNION ALL SELECT 'UserLoginHistory (09-30)',  OBJECT_ID('dbo.UserLoginHistory')
UNION ALL SELECT 'Bookings.OrderBookingId (10-05)', COL_LENGTH('dbo.Bookings','OrderBookingId')
UNION ALL SELECT 'DailyLabourAttendance (10-06)', OBJECT_ID('dbo.DailyLabourAttendance')
UNION ALL SELECT 'LabourOvertime (10-06)',    OBJECT_ID('dbo.LabourOvertime')
UNION ALL SELECT 'MaintenanceTickets (10-06)', OBJECT_ID('dbo.MaintenanceTickets')
UNION ALL SELECT 'StoreItems (10-06)',        OBJECT_ID('dbo.StoreItems')
UNION ALL SELECT 'SuggestionPosts (10-07)',   OBJECT_ID('dbo.SuggestionPosts')
UNION ALL SELECT 'MaterialStock (10-08)',     OBJECT_ID('dbo.MaterialStock')
UNION ALL SELECT 'SeedSowings.GrowingMaterialTypeId (10-08)', COL_LENGTH('dbo.SeedSowings','GrowingMaterialTypeId')
UNION ALL SELECT 'ReadyStock.InTransitQuantity (10-09)', COL_LENGTH('dbo.ReadyStock','InTransitQuantity')
UNION ALL SELECT 'ReadyStockTransplants (10-09)', OBJECT_ID('dbo.ReadyStockTransplants')
UNION ALL SELECT 'TrayTransfers (10-10)',     OBJECT_ID('dbo.TrayTransfers')
UNION ALL SELECT 'RolePermissionChangeLog (10-11)', OBJECT_ID('dbo.RolePermissionChangeLog')
UNION ALL SELECT 'InternalTransfers.ReceivedDate (10-11)', COL_LENGTH('dbo.InternalTransfers','ReceivedDate');
```

(A NULL means "not applied". Some table names above are the scripts' main objects; confirm each
against the script's own "After" check before relying on it.)

### 3b. Order (dependencies verified from the scripts' own guards)

| # | Script | Depends on (guard) | Notes |
|---|---|---|---|
| 1 | `2026-10-05_BookingOrderLink_Schema.sql` | — | Live booking pages already failed once without it ("Invalid column name OrderBookingId"). |
| 2 | `2026-10-05_BookingOrderLink_Backfill.sql` | #1 (THROW 52104) | Links historical multi-variety orders. |
| 3 | `2026-10-06_DailyLabourAttendance.sql` | `2026-10-04_DailyLabourCounts.sql` (THROW 51724) | Needs `-I`. |
| 4 | `2026-10-06_LabourOvertime.sql` | #3 (same feature) | |
| 5 | `2026-10-06_MaintenanceTickets.sql` | supervisor roles, `Dashboard.View` (51764/51765) | Photo folder — section 5. |
| 6 | `2026-10-06_StoreInventory.sql` | roles incl. Main Office Store Keeper (51804/51805) | Re-running it updates its triggers in place. |
| 7 | `2026-10-07_SuggestionsProblems.sql` | — | Photo folder — section 5. |
| 8 | `2026-10-07_SuggestionsGlobalAccess.sql` | #7 (51856/51857) | |
| 9 | `2026-10-08_MaterialStock.sql` | role Sowing Supervisor (51904); TrayStock Polyhouse pools | Sowing pages INSERT the new SeedSowings columns: deploying code without it breaks Direct Seed Sowing and Confirm Cutting Sowing. |
| 10 | `2026-10-08_MaterialStockApprovals.sql` | #9 (51954) | Without it, approving extra trays of a Main Office cutting sowing fails. |
| 11 | `2026-10-09_ReadyStockTransplanting.sql` | required roles (51934); TrayStock ledger | Every Ready Stock / booking / dashboard page reads `InTransitQuantity`. Mother Plant polyhouses start with 0 trays. |
| 12 | `2026-10-10_TrayTransfers.sql` | #11 (52004), roles (52005) | Adds `dbo.TrayTransfers`, tray types TransferOut/In/Return, permissions TrayStock.Adjust / Transfer / ReceiveTransfer and their 4 grants. **Applied to local `PlantsIMS2_Test` 2026-10-10 12:24** (owner-approved; backup `PlantsIMS2_Test_PreTrayTransfers_20261010_122437.bak`, test-restored). Do not run its rollback once transfers exist. |
| 13 | `2026-10-11_TrayAccessByLocation.sql` | #12 (52144); roles (52145) | ADDITIVE: `TrayStock.ViewMotherPlant` (Mother Plant Supervisor, read-only trays of own Mother Plant Areas) and `TrayStock.ReceiveTransferMainArea` (Sowing Supervisor, receives into own Main Office Areas). Logged in `dbo.RolePermissionChangeLog`. Rollback refuses while a transfer to a Main Office Area is pending (52154). |
| 14 | `2026-10-11_RolePermissionAlignment.sql` | role Booking Executive (52106) | **Changes existing permission data -- explicit owner approval required per database.** Removes Booking Executive `Dispatch.Enter` and `Dispatch.View` (both granted by hand through the Roles page; logged; the rollback restores exactly them). |
| 15 | `2026-10-11_ReadyStockReceiptDate.sql` | #11 (52124) | `InternalTransfers.ReceivedDate` / `ReceiptDateReason` + CHECK. No existing row changed; no receipt date invented. Without it the code accepts only today's receipt date. |
| 16 | `2026-10-12_BedSowing.sql` | #12-#15 (52165, 52169); `CK_SeedSowings_Source` exactly as before (52168) | Bed Sowing: `dbo.BedSowingLocations` / `BedSowings` / `BedSowingOperations`, `SeedSowings.BedSowingId` + SourceType `BedSowing`, permission `BedSowing.Transplant`, role "Bed Sowing Transplanter" assigned to ONE user for the polyhouse's Area. **Needs `-v BED_POLYHOUSE_ID=<id> BED_TRANSPLANTER_USER_ID=<id>`** -- the Ids of THAT database (local: 6 and 9), checked against the expected names "Facility 1 Main office" (an active MainOffice Area) and Maya / Maya Shinde (active); any mismatch changes nothing (52166 / 52167). Refuses if another user already holds the role (52170). Also: `MaterialTransactions.TransactionDate` (NULL for every existing row), append-only `dbo.BedSowingAuditLog`, triggers `TR_BedSowingOperations_Transplanter` (Maya-only: transplants / losses recorded only by the Transplanter of the bed's Area, no administrator exception), `TR_UserRoles_BedTransplanter` (one holder; logged), `TR_RolePermissions_BedTransplant` (that role only; logged), `TR_ReadyConfirmations_BedReadyDate` (fires first). Rollback refuses once any bed sowing exists (52184) or any material movement carries a business date (52186). |
| 17 | `2026-10-13_TraysToSowingApprovals.sql` | #16; both triggers exactly as #16 left them (SHA-256 checked) and `CK_SeedSowings_Source` as #16 (else nothing changes) | Bed rows: Ready Days / Ready Date become optional (no Ready Date at the bed stage). `CK_SeedSowings_Source` gains SourceType `Transplant`; `ReadyStockTransplants.OutputSeedSowingId` (FK, unique) so a Mother Plant "Transplant into Trays" makes a tray batch in Sowing Approvals. `TR_SeedSowings_SupervisorRole`: bed / transplant tray batches have no supervisor. `TR_ReadyConfirmations_AssignedSupervisor`: those batches are approved by a `ReadyStock.Confirm` holder or System Administrator, a bed batch never by its recorder; seed and cutting rules unchanged. `TR_ReadyConfirmations_BedReadyDate` (re-created, fires first again, also hash-checked): a `Transplant` tray batch, like a bed one, is never approved before its Ready Date (52208). **Code deployed without it:** Bed transplanting is refused with a message; Mother Plant transplanting keeps the earlier immediate-Ready-Stock behaviour. Rollback refuses once any `Transplant` tray batch, tray-batch output, supervisor-less bed batch or bed without a Ready Date exists. |
| 18 | `2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql` | #17 | Mother Plant Areas use ONE tray pool per Area + cavity (any active Polyhouse pool of a Mother Plant Area is merged into it with ledger rows, nothing created or lost); `InternalTransfers.TransferPurpose` (Pot Production / Transplant) required for new Mother Plant transfers, which no longer need a destination Polyhouse; `ReadyStockTransplants.PolyhouseId` optional. Code without it keeps the Polyhouse behaviour. |
| 19 | `2026-10-15_PotProductionFromReadyStock.sql` | #18 (52354); `TR_PotBatches_Update` and `CK_ReadyStockTx_Type` exactly as before (SHA-256, 52355/52356) | Pot batches from received Pot Production Ready Stock: `PotProductionBatches.SourceReadyStockId` (+ FK, one-source CHECK), ledger type `PotProductionOut`, `TR_PotBatches_ReadyStockSource`. Rollback restores both objects byte-for-byte; refuses once used. Code without it: the Pot Production purpose is recorded but the pot page refuses with a "needs the update" message. |
| 20 | `2026-10-16_MotherPlantTransplantWaste.sql` | #18 (52434); `CK_RsTransplants_Quantities` / `CK_RsTransplants_Status` exactly as before (52435/52436); every existing transplant row transplants and has a tray pool (52438) | Mother Plant transplanting records the untransplanted remainder as waste: `ReadyStockTransplants.WasteReason`, `TrayStockId` NULLable (waste-only closure, no trays), checks widened for waste-only rows, `CK_RsTransplants_WasteOnly`; `TR_RsTransplants_NewRow`: waste needs a reason, complete trays only, and stock received for Pot Production (any lineage) is never transplanted (52441). Rollback refuses once a waste reason or waste-only row exists. Code without it: same processing rule, the reason is kept in Notes, waste-only closure refused with a "needs the update" message, Wastage / Daily Operations do not list transplant waste. |
| 21 | `2026-10-16_PotProductionSubmissionToken.sql` | #19 (52464) | `PotProductionBatches.SubmissionToken` + filtered unique index: a pot batch start form saves once. Rollback drops both (no business data in them). Code without it: no duplicate-submit guard (earlier behaviour). |
| 22 | `2026-10-16_BedSowingApprover.sql` (`-v BED_APPROVER_USER_ID=<Id>`; local 9) | #17; `TR_ReadyConfirmations_AssignedSupervisor` exactly the #17 version (SHA-256, 52406); the Id must be the ONLY active "Maya Shinde", Username `Maya` (52409/52410) -- **check the Id on live first** | `dbo.BedSowingApprover` (one row, audited): Bed Sowing tray batches are approved ONLY by that user, who may approve a batch she transplanted (Bed Sowing exception); other batch types unchanged; every Bed Sowing approval audited (`TR_ReadyConfirmations_BedApprovalAudit`). Rollback restores the #17 trigger byte-for-byte. Code without it: the #17 rule (any approver, never the recorder). |
| 23 | `2026-10-16_MotherPlantSupervisorTrayEnter.sql` (owner approval 2026-10-12) | `dbo.RolePermissionChangeLog`; every user holding the Mother Plant Supervisor role is assigned ONLY to active Mother Plant Areas (52487) -- **re-check on live** | Grants the existing `TrayStock.Enter` to the Mother Plant Supervisor role (logged). Area scope is the existing rule (`TrayAreaScope`: the user's assigned Areas, re-checked on POST and inside `AddStockAsync`). Rollback removes it (logged) only if this script added it. |
| 24 | `2026-10-17_BedSowingApprovalAnyDate.sql` (APPLIED to local PlantsIMS2_Test 2026-10-12, owner-approved; rows 24-26 together) | #17 + #22; `TR_ReadyConfirmations_BedReadyDate` exactly the #17 version (SHA-256, 52506) | A Bed Sowing tray batch may be approved on ANY date (its Ready Date stays recorded); the Transplant Ready Date rule (52208) is kept; the trigger still fires first. Who approves is unchanged (#22). Rollback restores the #17 trigger byte-for-byte. Code without it: Bed Sowing batches only from their Ready Date. |
| 25 | `2026-10-17_TransplantApprovalAreaScope.sql` (APPLIED to local PlantsIMS2_Test 2026-10-12) | #22; `TR_ReadyConfirmations_AssignedSupervisor` exactly the #22 version (SHA-256, 52525) | A tray batch transplanted in a Mother Plant Area (SourceType `Transplant`) is approved -- and becomes that Area's Ready Stock -- only by an active user holding `ReadyStock.Confirm` through a role assigned to THAT Area, or a System Administrator. Seed / Cutting / Bed Sowing unchanged. Rollback restores the #22 trigger byte-for-byte. Code without it: any approver (earlier rule). |
| 26 | `2026-10-17_PottedDirectSaleSubmissionToken.sql` (APPLIED to local PlantsIMS2_Test 2026-10-12) | `dbo.Dispatches` | `Dispatches.SubmissionToken` + filtered unique index: a potted-plant customer sale form records once. Rollback drops both. Code without it: no duplicate-submit guard on that sale (earlier behaviour). Ready Stock / Potted Plant transfers use the existing `InternalTransfers.SubmissionToken` (no migration). |
| 27 | `2026-10-18_SeedlingDirectSaleSubmissionToken.sql` (APPLIED to local PlantsIMS2_Test 2026-10-12, owner-approved) | `dbo.SeedlingDispatches` | `SeedlingDispatches.SubmissionToken` + filtered unique index: a direct tray sale (Mother Plant & Cutting > Ready Stock > Sell to customer) records once. Rollback drops both. **Code without it: direct tray sales are switched off** (the page says the update is needed); nothing else changes. |
| 28 | `2026-10-19_Cavity70.sql` (APPLIED to local PlantsIMS2_Test 2026-10-12 20:22 IST, owner-approved; backups `PlantsIMS2_Test_PreApply1019Cavity70_20261012_202147.bak` / `PostApply1019Cavity70_20261012_202322.bak`; **NOT on live**) | 2026-10-08 MaterialStock, 2026-10-09 ReadyStockTransplanting, 2026-10-10 TrayTransfers, 2026-10-12 BedSowing, #20; the 3 triggers exactly their current versions (SHA-256, 52585) and the 10 constraints exactly their current definitions (52586) | Adds `70 Cavity` (70 plants per tray) to the closed cavity list in 10 CHECK constraints (re-added WITH CHECK) and 3 triggers (`TR_ReadyStock_WholeTrays`, `TR_ReadyConfirmations_TrayQuantity`, `TR_RsTransplants_NewRow`); adds Cocopeat / Pitmos consumption rows for 70 Cavity at 1.5 kg / tray (the value every cavity uses -- change it in Material Stock > Settings if needed). The other five cavities are untouched. Rollback restores every object byte-for-byte and is REFUSED while any 70-cavity row exists. **Code without it: 70 Cavity is not offered on any form and Tray Stock refuses 70-cavity trays** (`WorkflowCorrectionsSchema.Cavity70ProbeSql`); everything else unchanged. |
| 29 | `2026-10-20_TransplantApprovalAnyDate.sql` (APPLIED to local PlantsIMS2_Test 2026-10-12 21:55 IST, owner-approved; backups `PlantsIMS2_Test_PreApply1020TransplantAnyDate_20261012_215314.bak` / `PostApply1020TransplantAnyDate_20261012_215639.bak`; **NOT on live**) | #24 + #25; `TR_ReadyConfirmations_BedReadyDate` exactly the #24 version (SHA-256 `0DBAF1E4...`, 52615) | A Mother Plant Area transplant tray batch (SourceType `Transplant`) may be approved on ANY date -- before, on or after its Ready Date, which is not changed -- still only by an Area Supervisor of its Area or a System Administrator (#25), still only if it has a Ready Date. Bed Sowing, Seed and Cutting rules unchanged. Rollback restores the #24 trigger byte-for-byte (approvals already made stay). **Code without it: a Transplant batch is approved only from its Ready Date** (`WorkflowCorrectionsSchema.TransplantAnyDateProbeSql`). |
| 30 | `2026-10-25_MaterialStockTransfers.sql` (APPLIED to local PlantsIMS2_Test 2026-10-23 ~11:48 IST, owner-approved; backups `PlantsIMS2_Test_PreApply1025_1026_20261023_113838.bak` / `PostApply1025_1026_20261023_114909.bak`; **NOT on live**) | 2026-10-08 MaterialStock + Approvals, 2026-10-12 BedSowing, 2026-10-11 TrayAccessByLocation; TR_MaterialTransactions_Ledger / TR_MaterialStock_Rules exactly their 2026-10-08 versions (SHA-256, 53408), the 3 CHECK constraints their current definitions (53409) | Cocopeat / Pitmos transfers between Polyhouses: `dbo.MaterialTransfers`, ledger types TRANSFER_OUT / TRANSFER_IN, balances allowed at Mother Plant Polyhouses (transfers only), permission `MaterialStock.Transfer` -> Sowing Supervisor. No balance or ledger row changes. Rollback restores both triggers byte-for-byte and is REFUSED once any transfer exists (53440). **Code without it: the Material Transfers page says the update is missing; everything else unchanged** (`MaterialTransferSchema`). |
| 31 | `2026-10-26_LabourAttendanceOutlet.sql` (APPLIED to local PlantsIMS2_Test 2026-10-23 right after #30, owner-approved; same backups; **NOT on live**) | 2026-10-06 DailyLabourAttendance, 2026-10-11 TrayAccessByLocation; TR_DailyLabourAttendance_Location exactly its 2026-10-06 version (SHA-256, 53458) | Daily Labour **Attendance** accepts Outlet Areas (Area-wise); counts and overtime still exclude Outlet. New permission `Labour.Attendance` (opens Daily Attendance only) -> Outlet Sales, in their own Areas. Rollback restores the trigger byte-for-byte and is REFUSED once Outlet attendance exists (53483). **Code without it: Outlet stays excluded** (`LabourAttendanceOutletSchema`). |
| 32 | `2026-10-31_CuttingPartialTraySowing.sql` (APPLIED to local PlantsIMS2_Test 2026-10-31 ~11:39 IST, owner-approved; backups `PlantsIMS2_Test_PreApply1031PartialTray_20261031_113924.bak` / `PostApply1031PartialTray_20261031_114012.bak`; rehearsed first on scratch `PlantsIMS2_Scratch_PartialTray_20261031`: DRYRUN, COMMIT, rollback, re-apply; **NOT on live**). NOTE: the 10-21 / 10-22 / 10-23 / 10-24 scripts still have no row here; add them before any live deploy | #28 (Cavity70); `CK_SeedSowings_TrayCount`, `CK_SeedSowings_SeedQuantity`, `TR_ReadyConfirmations_TrayQuantity`, `TR_ReadyStock_WholeTrays` exactly the versions #28 leaves (SHA-256, 53709; #28 missing = 53708) -- **live may refuse if its schema differs** | A CUTTING tray sowing below one complete tray (e.g. 62 at 150 Cavity) is recorded as ONE partial tray (1 tray deducted, no sub-tray wastage), approved as exactly 1 Actual Ready Tray, and its Ready Stock batch holds 1..QuantitySown plants. Seed / Bed / Transplant batches and every quantity of one tray or more unchanged. Rollback restores the four objects byte-for-byte. **Code without it: a cutting sowing under one tray is refused as before** (`WorkflowCorrectionsSchema.CuttingPartialTrayAsync`). Deploy the code before or with the script. |
| 33 | `2026-10-31_AllSeedlingsDamagedApproval.sql` (APPLIED to local PlantsIMS2_Test 2026-10-31 13:03:46 IST, owner-approved; backups `PlantsIMS2_Test_PreApply1031AllDamaged_20261031_130316.bak` / `PostApply1031AllDamaged_20261031_130419.bak`; rehearsed on scratch `PlantsIMS2_Scratch_DmgMig_20261031` (DRYRUN, COMMIT, re-run no-op, rollback DRYRUN, rollback COMMIT from a CRLF copy, re-apply) and on `PlantsIMS2_Scratch_PreApply1031AllDamaged_20261031`; **NOT on live**) | #32 (refuses with 53748 without it); `CK_ReadyConfirmations_ActualTrays` exactly its current definition (SHA-256), `TR_ReadyConfirmations_TrayQuantity` exactly the #32 version (SHA-256 or whitespace-insensitive SHA-256) (53749) | Supervisor Approval "All Seedlings Damaged": an approval may store **0 Actual Ready Trays**, only with 0 seedlings and the reason `Damaged plants`; the trigger then requires Wastage = the sowing's still-expected quantity. Every approval of 1 tray or more unchanged. Rollback restores both objects and is REFUSED once any 0-tray approval exists (Confirmed or Cancelled) (53768). **Code without it: the option is not offered and is refused** (`WorkflowCorrectionsSchema.AllSeedlingsDamagedAsync`); ordinary approvals unchanged. |
| — | `2026-10-08_VehicleTracking_*.sql` (4) | each on the previous | **Paused module — see section 6. Not part of this release unless decided.** |

Scratch verification of #12 (2026-10-10, `PlantsIMS2_Scratch_AuditMig_20261009`): dry run (nothing
kept), commit, re-run (no duplicate grants), rollback dry run, rollback commit, re-commit; wrong
`TARGET_SERVER` refused (52003); wrong database refused (52001); rollback refused once transfers
exist (52014).

Scratch verification of #13, #14 and #15 (2026-10-10, `PlantsIMS2_Scratch_R3Mig_20261010`; #15 also
`R2Mig`/`R2Full_20261011`): #13 refused before #12 (52144); dry run, commit, re-run (0 changes), rollback dry
run, rollback, second rollback (no-op), re-commit; wrong server and wrong database refused for each; #13's
rollback refused with a pending Main Office transfer (52154); #15's rollback refused once receipt dates
existed (52134).

Scratch verification of #16 as revised (2026-10-10, `PlantsIMS2_Scratch_Bed2Mig_` and `Bed3Mig_20261010`, restored
from a copy-only backup of local `PlantsIMS2_Test` taken that day): dry run, commit, re-run, rollback (every object
removed, CHECK restored), re-commit, rollback refused with data. Earlier verification of the first version
(`PlantsIMS2_Scratch_BedMig_20261010`): dry run (nothing kept),
commit, re-run (no-op), rollback (restored `CK_SeedSowings_Source` to its exact earlier definition),
re-commit; wrong polyhouse Id (52166), wrong user Id (52167) and wrong server (52163) refused; rollback
refused once bed sowings existed (52184, `PlantsIMS2_Scratch_BedTgt_20261010`).

**Local `PlantsIMS2_Test` status (2026-10-10):** #12 applied 12:24 (verified: TrayStock 13 rows / 11,913 trays and
TrayStockTransactions 55 rows unchanged with identical checksums; exactly the 4 grants added, none removed).
#13, #14 and #15 applied 13:04 with owner approval (docs/LOCAL_VERIFICATION_REPORT_2026-10-10.md).
#16 applied 2026-10-10 21:16 with owner approval (backups `PlantsIMS2_Test_PreBedSowingApply_20261010_211603.bak`
and `..._PostBedSowingApply_20261010_211733.bak`). #17 applied 2026-10-11 16:14 with owner approval. #18 and #19 NOT applied -- await explicit approval.

Scratch verification of #19 (2026-10-11, `PlantsIMS2_Scratch_Pots15Mig_20261011`, from the post-#17 backup): refused
before #18 (52354); dry run (nothing kept); commit; re-run (no-op); rollback (trigger and CHECK restored byte-identical,
verified by SHA-256); re-commit.

Scratch verification of #17 (2026-10-11, `PlantsIMS2_Scratch_Tray13Mig_20261011`, restored from the post-#16 backup):
dry run (nothing kept), commit, re-run (no-op), rollback (both triggers restored byte-identical, verified by
SHA-256), re-commit. Revised with the Transplant Ready Date gate and re-verified the same way on
`PlantsIMS2_Scratch_T13GateMig_20261011` (all three triggers restored byte-identical; the gate trigger fires first
after commit and after rollback). Full test suite on fresh copies with and without #17: 0 failures.

### 3c. After each COMMIT

- [ ] Read the script's printed "After" section; compare the counts with the dry run.
- [ ] Spot-check the matching page (section 7) before running the next script.

## 4. Backup and restore verification

- [ ] `BACKUP DATABASE … WITH CHECKSUM, COPY_ONLY` immediately before the first migration; name it
      `<db>_PreRelease_<yyyyMMdd_HHmmss>.bak`.
- [ ] `RESTORE VERIFYONLY FROM DISK = … WITH CHECKSUM`.
- [ ] Restore it under a different name on another server (or the laptop) and open the app against
      it read-only: Dashboard, Ready Stock, Material Stock load.
- [ ] Keep the backup until the release has run a full business day without issue.

## 5. Configuration

- [ ] `MaintenanceStorage:PhotoRoot` and `SuggestionStorage:PhotoRoot` set (environment variables
      `MaintenanceStorage__PhotoRoot`, `SuggestionStorage__PhotoRoot`) to persistent folders outside
      the deploy directory; the app account can write there.
- [ ] `ASPNETCORE_ENVIRONMENT=Production` (the Development config switches off the seedling Area scope).
- [ ] `ConnectionStrings__DefaultConnection` unchanged from the current live value.
- [ ] No Vehicle Tracking keys are needed unless section 6 says the module ships.

## 6. Vehicle Tracking (paused 2026-10-09) — EXCLUDED from the next release (owner decision 2026-10-10)

Do not apply its four migrations; do not configure its keys; do not deploy its pages, API or menu.
The working tree keeps all of its code untouched. The release is built from the reviewed scope in
[RELEASE_SCOPE_PLAN.md](RELEASE_SCOPE_PLAN.md): HEAD + the 250 release files + the three shared files
(`Program.cs`, `FeatureAuthorizationConventions.cs`, `_Layout.cshtml`) WITHOUT their Vehicle Tracking
blocks. That candidate was built and tested on a fresh migrated scratch (2094 tests: 2091 passed,
0 failed, 3 skipped); `/VehicleTracking/*` and `/api/vehicle-tracking/v1/*` answer 404 even for an
administrator, and the menu has no Vehicle Tracking entry.

## 7. Deploy and smoke tests

- [ ] Stop the app service, deploy the `-c Release` publish output, start the service; check the
      service log for startup errors.
- [ ] Smoke test as Admin and as one user of each role:
  - Dashboard: "Ready seedlings — available for booking/dispatch" equals the Ready Stock page's
    "Available for booking/dispatch".
  - Ready Stock list, booking create, seedling dispatch (with a past date: lands on that date).
  - Direct Seed Sowing and Confirm Cutting Sowing (material + trays).
  - Material Stock: trays per cavity; Adjust Trays and Tray Transfers pages open for their roles.
  - Mother Plant transfer list and Transplanting list open.
  - Daily Operations Report for today and yesterday; WhatsApp Daily Report preview.
  - Labour, Maintenance, Store, Suggestions pages open.
- [ ] Physical tray count: each Polyhouse and cavity counted once; differences entered as Tray
      Adjustments with the reason. No opening balance is created automatically.

## 8. Rollback criteria

Roll back (stop the service, redeploy the previous build) if any of: startup failure; any page in
section 7 errors; Ready Stock totals differ between Dashboard and Ready Stock page; a stock balance
is negative; bookings or dispatch fail. Schema rollback scripts exist for every 10-06 … 10-10
script and refuse while history depends on them — prefer restoring the section 4 backup when data
has been entered since the deploy only after the owner decides what to do with that data.
