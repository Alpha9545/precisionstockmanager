# Approved work, local Tray Transfers migration and release scope (2026-10-10)

Continues [AUDIT_FOLLOWUP_REPORT_2026-10-11.md](AUDIT_FOLLOWUP_REPORT_2026-10-11.md).

* **Nothing was committed, pushed, staged or deployed.**
* **Production was never connected to.**
* **Vehicle Tracking is untouched:** 237 Vehicle Tracking / mobile / publish files are byte-identical.

## 1. Files changed in this round

**New**

* `Database/Migrations/2026-10-11_TrayAccessByLocation.sql` + `_Rollback.sql`
* `docs/RELEASE_SCOPE_PLAN.md`
* `docs/release/RELEASE_MANIFEST_2026-10-10.txt`
* `docs/release/assemble_release.py`
* this report

**Changed**

| Area | Files |
|---|---|
| Location-aware receiving | `Services/TrayTransferRules.cs`, `Authorization/TrayAreaScope.cs`, `Authorization/MaterialStockAccess.cs`, `Data/TrayTransferRepository.cs`, `Pages/Production/MaterialStock/TrayTransfers.cshtml.cs` |
| Booking Executive change only | `Database/Migrations/2026-10-11_RolePermissionAlignment.sql` + `_Rollback.sql` (the tray grants moved to the new TrayAccess migration) |
| Tests | `MotherPlantSupervisorOutletSellE2ETests`, `TrayTransferAuthorizationTests`, `TrayTransferRulesTests`, `TrayTransfersE2ETests` |
| Docs | `PRODUCTION_PREFLIGHT.md`, `STOCK_DEFINITIONS.md`, `MATERIAL_STOCK.md` |
| Local only | `.claude/launch.json` (preview configs `psm-audit`, `psm-release-candidate`) |

Unchanged from the previous round: the Ready Stock receipt date (code, migration and tests) and the Mother Plant Supervisor tray view code.

## 2. Permission decisions implemented

**Booking Executive.**

* `Dispatch.Enter` is removed.
* `Dispatch.View` is removed too: the booking workflow does not need it.
  * Booking Details shows ordered / reserved / dispatched / remaining per line.
  * Booking Fulfilment, the Dispatch Register and the Potted Plant Booking pages show dispatch status.
  * All of these need only `Booking.View`.
  * `Dispatch.View` added only the potted Dispatch list and details, plus a read-only view of Seedling Dispatch, where every action needs `Dispatch.Enter`.
* Intended set: Booking.Enter, Booking.View, Dashboard.View, ReadyStock.View, TrayStock.EnterMotherPlant.
* Local `PlantsIMS2_Test` today holds both `Dispatch.*` grants. They were added by hand through the Roles page between 2026-10-01 and 2026-10-04, and no script grants them.
* `2026-10-11_RolePermissionAlignment.sql` removes them and records each removal in `dbo.RolePermissionChangeLog`; the rollback restores exactly them.
* **It was NOT applied to `PlantsIMS2_Test`. It awaits your explicit approval.**
* The exact-set test names the two grants until then.

**Mother Plant Supervisor (tray view).**

* `TrayStock.ViewMotherPlant` is read-only and covers only the user's own active Mother Plant Areas, using the existing Area model.
* It never grants adding, adjusting or transferring trays, Cocopeat / Pitmos, administration, or other Mother Plant Areas.

**Receiving tray transfers (location-aware).**

| Destination | Who receives / rejects |
|---|---|
| Mother Plant Area | `TrayStock.ReceiveTransfer`: Mother Plant Supervisor, own Areas |
| Main Office Area | new `TrayStock.ReceiveTransferMainArea`: Sowing Supervisor, own Areas — the Main Area tray role, which already adds and adjusts trays there |
| Any other type | full-access users only |

* No receiver gets cross-Area access.
* Non-administrators cannot send to a destination without an active assigned receiver of the right type.
* Administrators keep full access: exceptional transfers, and they can always receive, reject or cancel.
* Outlets are never a destination.
* Sending, receiving, cancelling and rejecting stay transactional and idempotent, with ledger rows.

**Why Sowing Supervisor receives at the Main Office.** Local `PlantsIMS2_Test` has one Main Office Area with 3 active Sowing Supervisors, who already hold TrayStock.Enter / Adjust / Transfer there. The Main Office Store Keeper (1 user) handles store and seed stock, not trays.

The two tray grants live in the additive `2026-10-11_TrayAccessByLocation.sql`, which is not applied to `PlantsIMS2_Test`. Until it is applied:

* Main Office destinations have no receiver, so non-admins can send only to Mother Plant Areas;
* the Mother Plant Supervisor has no tray view.

## 3. Ready Stock receipt business date

Already implemented (previous round) and re-verified in this round's suite:

* defaults to the IST date;
* refuses future dates and dates before the send business date;
* requires a reason for an earlier date;
* `ConfirmedDate` keeps the recorded time;
* ledger rows are dated at the receipt date, never before the hold;
* no historical date is invented.

Migration `2026-10-11_ReadyStockReceiptDate.sql` is not applied to `PlantsIMS2_Test`. Until it is, only today's date is accepted there.

## 4. Local Tray Transfers migration (approved): applied to `PlantsIMS2_Test`

1. **Instance check:** `LAPTOP-R87BKQGH\MSSQLSERVER02` / `PlantsIMS2_Test`, the laptop, not the VPS.
2. **Prerequisites present:**
   * `ReadyStockTransplants`;
   * `CK_TrayStockTx_Type` including Transplant;
   * the Sowing Supervisor, Office Coordinator and Mother Plant Supervisor roles;
   * `TrayTransfers` absent beforehand.
3. **Backup:** `PlantsIMS2_Test_PreTrayTransfers_20261010_122437.bak` (COPY_ONLY, CHECKSUM).
   * `RESTORE VERIFYONLY` passed.
   * Test-restored as `PlantsIMS2_Scratch_PreTTVerify_20261010`, with identical tray and permission fingerprints.
4. **Dry run reviewed:**
   * additive only: 3 ledger types, the table, 3 permissions, 4 grants;
   * tray data unchanged;
   * nothing kept.
5. **Commit** at 12:24.
6. **Independent verification:**
   * TrayStock: 13 rows / 11,913 trays, and TrayStockTransactions: 55 rows — identical counts, totals and checksums;
   * `TrayTransfers` exists with all its constraints and indexes;
   * the CHECK constraint accepts the 3 new types;
   * compared with the backup copy, exactly 4 grants were ADDED and 0 removed: Sowing Supervisor Adjust / Transfer, Office Coordinator Transfer, Mother Plant Supervisor ReceiveTransfer.
7. **Rollback not run** (and it must not be, once transfers exist).

## 5. Migration status

| # | Migration | Scratch | Local `PlantsIMS2_Test` | Production |
|---|---|---|---|---|
| 12 | 2026-10-10_TrayTransfers | verified | **applied** | not applied |
| 13 | 2026-10-11_TrayAccessByLocation (additive) | verified (incl. refusal before #12 and rollback guard) | awaiting approval | not applied |
| 14 | 2026-10-11_RolePermissionAlignment (removes Booking Executive Dispatch.*) | verified | **needs explicit approval** | not applied |
| 15 | 2026-10-11_ReadyStockReceiptDate | verified | awaiting approval | not applied |
| — | Vehicle Tracking (4) | scratch only | never | **excluded** |

## 6. Tests

| Run | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Baseline requested (2026-10-10 report) | 2252 | 2185 | 1 | 66 |
| Targeted (`R3Mig`) | 729 | 726 | 0 | 3 |
| **Full suite, working tree, fresh migrated scratch `R3Full_20261010`** | **2274** | **2208** | **0** | **66** |
| Full suite, release candidate without Vehicle Tracking (`R3Rel3_20261010`) | 2094 | 2091 | 0 | 3 |

**Against the baseline:**

* 22 more tests.
* The one baseline failure (Booking Executive) passes once the alignment migration is applied.
* No new failures.

**Investigated, not suppressed.** Two release-candidate runs failed for environment reasons, and both were fixed and re-run on fresh scratch:

* the Windows path to the native SQL client DLL was too long, so the copy was moved to a short path;
* `sqlcmd` misread an absolute script path, so the migrations had not applied.

**Skips:**

* Working tree: 63 Vehicle Tracking (paused, migrations not applied), 1 manual WhatsApp sender, 2 booking data-condition.
* Release candidate: the last 3 only.

**Verified:**

* transfer and receipt permissions:
  * pure rules;
  * HTTP refusals;
  * E2E with a no-receiver Area fixture;
  * the browser: the Sowing Supervisor gets Receive on the Main Office transfer only, the Mother Plant Supervisor on Mother Plant transfers only;
* in-transit trays;
* tray-view scope;
* historical receipt dates;
* the multi-day history scenario;
* existing transplanting.

## 7. Remaining deployment blockers

1. Your approval for #13, #14 and #15 on `PlantsIMS2_Test`, and later the production run of #1–#15 per `PRODUCTION_PREFLIGHT.md` (backup first).
2. Creating the release commit from the Vehicle-Tracking-free scope (`RELEASE_SCOPE_PLAN.md`). This needs your approval to commit; the Vehicle Tracking hunks in the three shared files must be left unstaged.
3. The production checks in `PRODUCTION_PREFLIGHT.md` §2–§5:
   * live instance;
   * which earlier migrations are present on live;
   * photo storage settings;
   * `ASPNETCORE_ENVIRONMENT=Production`.
4. A physical tray count per Polyhouse and cavity, entered as adjustments (no opening balances are created automatically).
