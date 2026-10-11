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
and `..._PostBedSowingApply_20261010_211733.bak`). #17 NOT applied -- awaits explicit approval.

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
