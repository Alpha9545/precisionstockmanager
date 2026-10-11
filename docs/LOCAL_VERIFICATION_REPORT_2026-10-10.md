# Local verification — 2026-10-11 migrations on PlantsIMS2_Test and smoke test (2026-10-10)

**Scope:** the local laptop database only. Production / the VPS were never contacted.

* Nothing was committed, pushed, staged, stashed, reset or cleaned.
* Vehicle Tracking is untouched: 237 Vehicle Tracking / mobile / publish files are byte-identical.

## 1. Pre-checks (all passed; the run would have stopped on any mismatch)

* **Instance and database:** `@@SERVERNAME` = `LAPTOP-R87BKQGH\MSSQLSERVER02` (machine LAPTOP-R87BKQGH, instance MSSQLSERVER02); database `PlantsIMS2_Test`, ONLINE.
* **Expected schema state:**
  * `TrayTransfers` (10-10) present;
  * `TrayStock.ReceiveTransfer` present;
  * `TrayStock.ViewMotherPlant`, `TrayStock.ReceiveTransferMainArea`, `RolePermissionChangeLog` and `InternalTransfers.ReceivedDate` absent;
  * the 10-09 `InternalTransfers` columns present;
  * the Booking Executive, Mother Plant Supervisor and Sowing Supervisor roles present.
* **Dependencies:**
  * TrayAccessByLocation needs 10-10;
  * RolePermissionAlignment needs the Booking Executive role;
  * ReadyStockReceiptDate needs the 10-09 transfer columns.
  * All were met.

## 2. Backup

* **File:** `PlantsIMS2_Test_Pre1011Migrations_20261010_130350.bak` (COPY_ONLY, CHECKSUM), in the instance's Backup folder.
* **Verification:**
  * `RESTORE VERIFYONLY ... WITH CHECKSUM`: valid;
  * test-restored as `PlantsIMS2_Scratch_Pre1011Verify_20261010`;
  * 7 fingerprints identical to the live local DB (TrayStock, TrayStockTransactions, ReadyStock, ReadyStockTransactions, InternalTransfers, RolePermissions, Permissions).
* **After the migrations:** a second backup, `PlantsIMS2_Test_Post1011Migrations_*.bak`, was verified the same way and used for every smoke / regression clone.

## 3. Dry runs, then the migrations in the approved order

| Migration | Dry run (reviewed) | COMMIT |
|---|---|---|
| 2026-10-11_TrayAccessByLocation | +2 grants (Mother Plant Supervisor `TrayStock.ViewMotherPlant`, Sowing Supervisor `TrayStock.ReceiveTransferMainArea`); tray rows unchanged | committed |
| 2026-10-11_RolePermissionAlignment | Booking Executive loses `Dispatch.Enter` and `Dispatch.View`, logged; its other 5 grants kept | committed |
| 2026-10-11_ReadyStockReceiptDate | `ReceivedDate` / `ReceiptDateReason` + CHECK; 54 transfers unchanged; 0 receipt dates invented | committed |

Committed at 13:04 IST. After the dry runs, nothing had persisted (67 permissions / 115 grants / no change log / no new column).

## 4. Independent verification after the migrations

* **Schema:**
  * `InternalTransfers.ReceivedDate` (date, NULL) and `ReceiptDateReason` (nvarchar(500), NULL) exist;
  * `CK_InternalTransfers_ReceivedDate` is trusted;
  * `RolePermissionChangeLog` exists, with 4 rows, each with its reason;
  * 0 transfers carry an invented receipt date.
* **Permissions,** diffed against the pre-change backup copy:

| Change | Role | Permission |
|---|---|---|
| ADDED | Mother Plant Supervisor | TrayStock.ViewMotherPlant |
| ADDED | Sowing Supervisor | TrayStock.ReceiveTransferMainArea |
| REMOVED | Booking Executive | Dispatch.Enter |
| REMOVED | Booking Executive | Dispatch.View |

  * The other **113 grants are unchanged.**
  * Booking Executive now holds: Booking.Enter, Booking.View, Dashboard.View, ReadyStock.View, TrayStock.EnterMotherPlant.
* **Stock:** identical to the backup copy (rows, totals, checksums) for:
  * TrayStock: 13 / 11,913
  * TrayStockTransactions: 55
  * ReadyStock: 22 / 1,49,011
  * ReadyStockTransactions: 65
  * InternalTransfers (core columns): 54
  * MaterialStock: 1 / 46
  * SeedStock: 10
  * CuttingStock: 28
  * PottedPlantStock: 12
* **Ledger reconciliation:**
  * Ready Stock owned / reserved / in-transit: 0 mismatches;
  * no negative or over-committed batch;
  * tray pools: 0 mismatches, none negative;
  * Material Stock: 0 mismatches;
  * nothing in transit.

## 5. Smoke test

It ran on `PlantsIMS2_Scratch_Smoke_20261010`, a restore of the **migrated** local database, so `PlantsIMS2_Test` itself received no test data. The app (working-tree build, port 5004) was signed in as scratch-only ZZTEST users.

| Area | Result |
|---|---|
| Main Area Ready Stock | Ready Stock page lists the Main Area batches; "Available for booking/dispatch 1,35,451" |
| Empty trays to the Mother Plant Area | Sowing Supervisor sent 30 × 42-cavity and 20 × 150-cavity trays (dated yesterday) to Green Bless P-1. The Mother Plant Supervisor received them; Main polyhouses −30 / −20, P-1 +30 / +20 |
| Ready Stock transfer | Sowing Supervisor sent 20 trays (480 plants) of batch 203, dated two days ago (TR-2026-00057) |
| Receipt | A backdated receipt without a reason was **refused** ("enter why it is recorded late"). With a reason: 470 received, 10 lost in transit, `ReceivedDate` = yesterday, `ConfirmedDate` = now; the received batch is usable from yesterday |
| Transplanting into different cavities | 24 → 42-cavity: 196 plants in 5 trays (4 full plus 1 with 28), 4 lost, dated yesterday. 24 → 150-cavity: 150 plants in 1 tray, today. Taking more than is available and dating before arrival were both **refused** |
| Tray balances and history | The Mother Plant Supervisor sees only Green Bless Nursery (P-1: 42-cavity 25, 150-cavity 19), with no add / adjust links. The ledger rows are TransferOut / TransferIn / Transplant with business and recorded days |
| Lineage | Batches #222, #223 and #224 each trace back to sowing batch #203 (and on to the original sowing) |
| Conservation | Batch 203 is down by 480. 480 = 120 left in #222 + 196 (#223) + 150 (#224) + 10 transit loss + 4 transplant loss. Trays are conserved apart from the 6 used |
| Daily Operations (history) | Change from before, per day, all as expected: 07 → 0; 08 → in transit +480, available −480; 09 → owned −14, available −14; 10 → owned −14. No false "cannot be reconstructed" note |
| One Available figure | Dashboard KPI = Ready Stock page = Daily Operations (today) = WhatsApp source = SUM(AvailableQuantity) = **1,35,451** |
| Role access (33 checks, 0 failures) | Booking Executive: Dispatch list, Seedling Dispatch and Dispatch Create are **denied**; Dispatch Register, Fulfilment, Ready Stock and the tray view are allowed. Mother Plant Supervisor: tray view, history, transfers and transplanting allowed; Add Material, Adjust, Settings, sending Ready Stock and Daily Operations denied. Sowing Supervisor: adjust, transfers and send allowed; transplanting, Settings and Daily Operations denied. Office Coordinator: transfers and send allowed; adjust and transplanting denied. Admin: everything |

After the smoke test, the smoke clone reconciles fully: Ready Stock and trays have 0 unexplained and 0 negative balances.

## 6. Regression suite on the CURRENT local data

The first full run on a clone of the migrated local database showed **15 failures**. All were in Labour Attendance, Daily Labour, Overtime, Maintenance and Store, which this work did not touch.

* **Root cause:** found by diffing users and roles against the 10-09 baseline backup.
  * User **6 (Akshay)** was changed in the local data from **Sowing Supervisor to Sowing Operator** after 2026-10-09.
  * Those five test classes hard-coded "user 6 = the Main Office Sowing Supervisor".
  * This is not an application defect.
* **Fix:** the classes now look the supervisor up from the database (`tests/PlantStockManager.Tests/FixtureUsers.cs`): an active user with the Sowing Supervisor role for the Main Office Area, preferring a single-role user.
  * If none exists, the test is skipped, never silently passed.
  * No assertion was changed.
* **Result:** the 47 tests in those classes pass on both the current data and the 10-09 baseline.

| Run | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Full suite on a fresh clone of the migrated local DB (`R4Full2`) | **2274** | **2208** | **0** | **66** |

Skips: 63 Vehicle Tracking (paused), 1 manual WhatsApp sender, 2 booking data-condition tests.

## 7. Outstanding issues

1. **Tray history can go negative for a back-dated transplant.**
   * The 42-cavity transplant was dated 09, but its trays were received on 10. The P-1 tray ledger is therefore −5 at the end of 09, while the current balance (25) is correct.
   * Transplanting checks today's tray balance, not the balance on the transplant date.
   * No report rebuilds tray balances by date, so nothing shown is wrong today.
   * Decide whether a transplant must be refused when its date is earlier than the trays' arrival.
2. Only 2 Sowing Supervisors remain on the Main Office (users 9 and 13, each with a second role). They are now the only Main Office tray receivers. Confirm that this is intended.
3. The Ready Stock receipt history list does not yet show the business receipt date next to the recorded time. It is stored and used, but only visible in the ledger remarks.
4. **Release blockers:**
   * commit the Vehicle-Tracking-free scope (`RELEASE_SCOPE_PLAN.md`) — needs approval;
   * the production preflight (`PRODUCTION_PREFLIGHT.md`), including backups and migrations #1–#15 on live;
   * a physical tray count.

## 8. Ready for release review?

**Yes, for review.** The local application is ready:

* migrations #12–#15 are applied and verified locally;
* the data is intact and reconciled;
* the smoke test passed end to end;
* the full suite passes against the current local data.

What remains is the release-scope commit (needs your approval) and the production preflight. Nothing was committed or deployed.
