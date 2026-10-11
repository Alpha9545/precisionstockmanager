# Bed Sowing — implementation report (2026-10-10)

**What was not done:**
* Nothing was committed, staged or deployed.
* Production was never contacted.
* The migration was **not** applied to local `PlantsIMS2_Test`. Checked: no Bed Sowing table, column, permission or test user exists there.
* Vehicle Tracking is untouched.

## 1. How it works

1. **Bed Sowing** (`/Production/BedSowing/Create`, permission `Sowing.Enter`). The form takes:
   * sowing date (no future dates, India time);
   * variety and its Main Office seed lot;
   * Polyhouse, preselected;
   * quantity;
   * notes.

   There is no cavity and no tray input. Saving produces bed number **BS-YYYY-NNNNN** and deducts the seed from the lot in the same transaction (`'Sown'`, ReferenceType `BedSowing`). It creates **no trays, no material use and no Ready Stock**. The status becomes **Pending transplant**.
2. **Bed Transplanting** (`/Production/BedSowing/Transplant`, permission `BedSowing.Transplant` only). The page lists the waiting beds with original, transplanted, lost and remaining quantities and the status. The form takes:
   * target Polyhouse;
   * cavity, from the existing list;
   * quantity;
   * output trays (calculated);
   * transplant date;
   * the Sowing Supervisor who will approve;
   * growing material;
   * notes.

   One transaction then does all of this:
   * the bed's Transplanted figure goes up and its status is recalculated;
   * **one growing tray batch** is created (`dbo.SeedSowings`, SourceType `BedSowing`, linked through `BedSowingId`);
   * empty trays are deducted from the target Polyhouse, dated at the transplant date and checked against the tray history;
   * Cocopeat or Pitmos is deducted at the existing kg-per-tray rule.

   Partial transplants are allowed. A bed loss (with a reason) can also be recorded.
3. **Sowing Approvals.** The tray batch appears in the existing list with:
   * origin **Bed Sowing** and the bed number;
   * the transplant date;
   * cavity, quantity and trays;
   * Ready Date and status.

   The existing approval creates Ready Stock **once**. From there, booking and dispatch are unchanged.
4. **History.** `/Production/BedSowing/History` lists every transplant and loss. `Details/{id}` shows each bed's operations and their lineage: bed → transplant → tray batch → approval → Ready Stock.

## 2. Facility-1 restriction

* Allowed locations live in `dbo.BedSowingLocations`, seeded by the migration with **one** Polyhouse.
* The Polyhouse is chosen by Id (`-v BED_POLYHOUSE_ID=6` locally) and **verified**:
  * its name must be "Facility 1 Main office";
  * its Area must be an active MainOffice Area.
* The server re-checks on every bed sowing and transplant that the Polyhouse:
  * is listed and active;
  * still belongs to the recorded Area;
  * is in an active MainOffice Area.
* The Area always comes from the database, never from the request.
* A transplant target must be a listed location in the **bed's own Area**.

## 3. Maya-only enforcement

* **Permission and role.** New permission `BedSowing.Transplant` and new role **Bed Sowing Transplanter**, assigned by the migration to one user for Area 2:
  * chosen by Id (`-v BED_TRANSPLANTER_USER_ID=9`);
  * verified as active user `Maya` / "Maya Shinde";
  * the migration refuses if anyone else already holds the role.
* **No user Id in C#.** `BedSowingRepository.IsTransplanterAsync` reads `dbo.UserRoles` **inside the transaction**. The user must hold the permission **for the bed's Area**. This check applies to transplanting, recording a loss and reversing.
* **System Administrator** keeps access. Every row records the acting user.
* **Verified in the browser:**
  * the Operator was refused on both the page and a forged POST;
  * the Supervisor's forged reversal was refused.

## 4. Trays and material

* Trays come from the existing ledger (`ConsumeForSowingAsync`, given a new optional business date), under the pool lock:
  * a back-dated transplant must fit the pool's history;
  * not enough trays → **nothing** is saved.
* Material follows your decision: the existing Main Office rule applies, so Cocopeat or Pitmos is required at kg per tray × trays.
* Reversing a transplant returns exactly the trays and material it took.

## 5. Ready Date

**Ready Date = original bed sowing date + the variety's Ready Days** (`PlantSpecies.ReadyStockDays`).

* Snapshotted on the bed. A database check enforces `ExpectedReadyDate = SowingDate + ReadyStockDays`.
* Every tray batch carries the bed's sowing date, Ready Days and Ready Date.
* Missing or zero Ready Days → the bed sowing is refused. Nothing is substituted.
* Changing the variety later does not change existing beds (tested).

## 6. Approvals, conservation, reversals

* **Whole trays only.** Existing rule: quantity = trays × cavity. A remainder stays in the bed or is recorded as a loss.
* **No over-approval of a bed tray batch.** New rule: at most the seedlings in its trays can be approved. Seed and cutting batches keep their own rules.
* **Approver.** Existing rule: an active Sowing Supervisor other than the transplanter. Locally that is currently only Somnath Kadam.
* **Conservation:** Original = Remaining + Transplanted + Lost.
  * `TR_BedSowings_Guard` requires the totals to equal the active operations;
  * CHECK constraints keep every part ≥ 0.
* **Operations are immutable.** The database refuses edits and deletes (`TR_BedSowingOperations_Immutable`).
* **Reversals:**
  * a transplant can be reversed only while its tray batch has no approval: the batch is cancelled, trays and material are returned, and the seedlings go back to the bed;
  * a loss can be reversed;
  * each reversal needs a reason, keeps the original row and can happen once.
* **Cancelling a bed.** Only an untouched bed can be cancelled; its seed returns to the lot.
* **Generic sowing cancel** is refused for bed tray batches.
* **Idempotency and concurrency:**
  * one-time form tokens on beds and operations;
  * the bed row is locked (UPDLOCK/HOLDLOCK) for every operation;
  * tested: 6 parallel transplants against a 480-seedling bed → exactly 4 succeed.

## 7. Dashboards and reports (no double counting)

Sowing activity counts **a bed once, on its sowing date**; its tray batches are not counted again (`BedSowingSchema.SowingActivitySql`). This applies to:
* the Management Dashboard seed summary, trend and overview;
* the Daily Operations Report;
* the monthly sowing pivot.

The Ready pipeline counts only tray batches. Active bed losses appear in Wastage as "Bed sowing (bed loss)".

**Without the migration everything runs unchanged.** The schema check is cached, and the Wasted Stock query's text is byte-identical (its hash test passes).

## 8. Migration

`Database/Migrations/2026-10-12_BedSowing.sql` and `_Rollback.sql`:
* guarded by database name, `TARGET_SERVER` and `MODE`;
* one transaction;
* idempotent;
* `CK_SeedSowings_Source` must match the expected definition before it is widened;
* nothing existing is changed.

Run with:
`-v MODE=… TARGET_SERVER=… BED_POLYHOUSE_ID=<id> BED_TRANSPLANTER_USER_ID=<id>`. Use `-I`, and use relative paths with `-i`.

**Scratch results:**

| Check | Result |
|---|---|
| Dry run | Nothing kept |
| Commit | Done |
| Re-run | No-op |
| Rollback | Exact original CHECK restored |
| Re-commit | Done |
| Wrong polyhouse / user / server | Refused (52166 / 52167 / 52163) |
| Rollback with bed data present | Refused (52184) |

Listed as #16 in `docs/PRODUCTION_PREFLIGHT.md`. That file's stale status line for #13–#15 was corrected.

## 9. Tests

* **New tests:**
  * `BedSowingRulesTests`: 19 pure test cases;
  * `BedSowingE2ETests`: 14 scratch tests covering every item above, plus a read and list test.
* **Updated, with assertions kept:**
  * `DailyOperationsReportE2ETests.Sowing_…`: its expected query now follows the new sowing-activity rule;
  * the Wasted Stock hash test is unchanged and passes.

| Run (fresh restore of current local data) | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Baseline before this work | 2280 | 2214 | 0 | 66 |
| Migration applied (`BedFinal`) | **2313** | **2247** | **0** | 66 |
| Migration NOT applied (`BedFinalNoMig`) | 2313 | 2233 | 0 | 80 (66 + 14 Bed Sowing E2E skipped) |

## 10. Browser (scratch `PlantsIMS2_Scratch_BedBrowser_20261010`, port 5004, `ZZTEST_BedSowingUsers.sql` logins)

| Login | What was checked |
|---|---|
| Operator | Bed sowing BS-2026-00001: 1,000 seeds, Ready Date 01/11/2026 (30/09 + 32 days). Transplant page and POST: 403 |
| Test transplanter | Whole-tray warning for 250 seedlings. Transplanted 240 into 10 trays (Cocopeat 15 kg). Recorded a loss of 10 |
| Supervisor | Approval list shows origin Bed Sowing with all fields. 11 trays refused; 10 approved → one Ready Stock row of 240 |

* **Database after the flow:** trays 29 → 19, Cocopeat 46 → 31 kg, one seed deduction.
* **Loss reversed** with the row kept, marked Reversed.
* **Wastage** row shown.
* **No horizontal scroll at 375 px** on all 5 pages.
* **No server errors** after the fix in the next point.
* **Bug found and fixed:** the list and history queries failed on duplicate column names. This was caught in the browser and is now covered by an E2E test.

## 11. Risks and open points

* **Production Ids.** The production database needs its own `BED_POLYHOUSE_ID` and `BED_TRANSPLANTER_USER_ID`. The names are verified, so wrong Ids change nothing.
* **Approver.** Locally only one Sowing Supervisor besides Maya exists (Somnath Kadam). If he is unavailable, tray batches cannot be approved.
* **Seed-lot dating.** The bed's seed deduction is dated when it was recorded, as Direct Sowing does. Material use is dated at the recorded time too. Only the trays carry the transplant's business date.
* **Applying it locally.** The migration is needed on `PlantsIMS2_Test` (needs your approval) and later on production. Without it the Bed Sowing pages say "not set up yet".

---

# Finalization before enabling (2026-10-10)

The page message "Bed Sowing is not set up in this database yet" is unchanged. It disappears only when the migration is applied.

## 1. Business dates

| Event | TransactionDate (business) | CreatedAt (recorded) | Historical check |
|---|---|---|---|
| Bed sowing: seed out of the lot | Bed sowing date | Recording time | **New:** `SeedStockRepository.AvailableFromAsync` |
| Transplant: empty trays | Transplant date | Recording time | Existing `TrayStockRepository.AvailableFromAsync` |
| Transplant: Cocopeat / Pitmos | Transplant date (**new** column) | Recording time | **New:** `MaterialStockRepository.AvailableFromAsync` |
| Bed loss | `BedSowingOperations.OperationDate` (no stock ledger) | `CreatedDate` | Bed conservation |
| Reversal / cancellation returns | Recorded time (convention, section 2 of STOCK_DEFINITIONS) | Recording time | Additions, so they cannot go negative |

How the historical check works:
* Balances are computed backward from the current balance, so a lot or pool with no opening ledger row is judged correctly.
* A deduction must fit the balance on its date **and at every later point**.
* Only Bed Sowing passes back-dated deductions to the seed and material ledgers, so every other workflow is unchanged. The full regression suite confirms this.

Material History, its KPIs and its consumption summary order and filter by the business date, and show the recorded time of a back-dated row.

## 2. Maya-only transplanting

**Normal work**

* Transplant and loss are allowed **only** for the user holding `BedSowing.Transplant` for the bed's Area, read inside the transaction.
* The System Administrator bypass was removed.

**Administrator**

* **Exception:** a System Administrator can transplant or record a loss only by entering an **exception reason**. Without one, the action is refused.
* **How it is recorded:**
  * the operation is stored with `AuthorizedException = 1` and the reason;
  * `dbo.BedSowingAuditLog` gets `AdminTransplantException` or `AdminLossException`;
  * the Details page shows an "Administrator exception" badge.
* **Reversal:** a System Administrator can still reverse with a reason, logged as `AdminReversal`. The Transplanter's own reversals are ordinary work.
* **Bed cancellation:** unchanged (Sowing.Enter and the bed's Area).

**Role-management pages**

* `BedSowingRoleGuard` refuses to:
  * give or remove a role carrying the permission (User Roles; Users, both Add and Edit);
  * edit that role's permissions;
  * add the permission to any other role (Roles).
* Browser-verified on all three pages. An ordinary role save still works.

**Database backstop**

* `TR_UserRoles_BedTransplanter`: one holder at most, and every change is logged with the database login.
* `TR_RolePermissions_BedTransplant`: the permission can sit only on "Bed Sowing Transplanter", and every change is logged.
* The audit log is append-only.
* Changing the holder later needs a reviewed database script: remove the old assignment, then add the new one.

## 3. Ready Date

* `ReadyConfirmationRepository.ConfirmAsync` refuses a **Bed Sowing** tray batch before its `ExpectedReadyDate`, using the India date and checked under the row lock.
* `TR_ReadyConfirmations_BedReadyDate` refuses a direct database insert. It is set to fire first.
* Pages:
  * Sowing Approvals shows "Approve from dd-mm-yyyy";
  * the Confirm page explains the date and disables the button;
  * a forged POST is refused by the server (browser-verified).
* Direct Seed and Cutting sowings are unchanged.

## 4. Migration (re-verified)

**Local environment:**

| Item | Value |
|---|---|
| Polyhouse | 6 "Facility 1 Main office", Area 2 "Main-Office-Areas" (MainOffice, active). The only polyhouse matching "Facility 1" / "Main office" |
| User | 9 `Maya` / "Maya Shinde" (active). The only Maya |
| Seed ledger | Reconciles with its balances |
| Material ledger | Reconciles with its balances |

**Prerequisites:** `TrayTransfers`, `RolePermissionChangeLog`, the expected `CK_SeedSowings_Source` definition, and no conflicting objects — all present (read-only check on 2026-10-10).

**Scratch cycle** (two restores of that day's copy-only backup, `PlantsIMS2_Test_PreBedSowing2_20261010.bak`):
* dry run;
* commit;
* re-run (no-op);
* rollback (every object removed, CHECK restored);
* re-commit;
* rollback refused with data.

## 5. Tests

**Bed Sowing tests: 39, all passing.** New coverage:
* the Ready Date gate, both server and direct database insert;
* back-dated seed, tray and material, with no negative history and business plus recorded dates checked;
* one Transplanter, the permission only on its role, and an append-only audit;
* administrator exception (refused without a reason) and administrator reversal (audited).

| Full suite (fresh restores) | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| With the migration (`Bed3Full`) | 2319 | 2253 | 0 | 66 |
| Without it (`Bed3NoMig`) | 2319 | 2235 | 0 | 84 (66 + 18 Bed Sowing E2E) |

An earlier no-migration run stalled once: one SQL timeout after about 35 minutes, while the whole run slowed to 1 h 48 m. It did not recur on a fresh copy, so I'm treating it as environmental, not a code fault.

## 6. Needed to enable locally (awaiting approval)

Apply **one** migration to `PlantsIMS2_Test`, after a copy-only backup:

```
sqlcmd -S "LAPTOP-R87BKQGH\MSSQLSERVER02" -d PlantsIMS2_Test -E -C -I -b -v MODE=DRYRUN TARGET_SERVER="LAPTOP-R87BKQGH\MSSQLSERVER02" BED_POLYHOUSE_ID=6 BED_TRANSPLANTER_USER_ID=9 -i Database/Migrations/2026-10-12_BedSowing.sql
```

Run it with `MODE=DRYRUN`, review the output, then run it again with `MODE=COMMIT`. The other migrations it depends on (#12–#15) are already applied locally.

---

# Revision: Maya-only, no administrator exception (2026-10-10)

The owner's rule: Maya Shinde alone transplants and records bed losses. The administrator exception from the finalization above is **removed**.

## What changed

* **Code:**
  * `BedSowingRepository` allows a transplant or bed loss only for the user holding `BedSowing.Transplant` for the bed's Area.
  * Full access no longer counts, and there is no exception parameter.
  * `BedTransplantRequest.ExceptionReason` and the operation's exception fields are gone.
* **Pages:**
  * **Bed Transplanting:** an administrator sees the beds in a view-only notice, with no forms.
  * **Details:** an administrator gets no "Transplant / record loss" link but keeps the Reverse column. Reversals are audited as `AdminReversal`.
* **Database (migration):**
  * the `AuthorizedException` / `ExceptionReason` columns are removed;
  * new `TR_BedSowingOperations_Transplanter`: a transplant or loss row can be inserted only with `CreatedById` = an active user holding the permission for the bed's Area (THROW 52180).
* **Unchanged:**
  * the role-management guard and its database backstop (one holder, permission only on its role, all changes logged);
  * administrator reversal (with a reason, audited) and bed cancellation;
  * the Ready Date gate, business dates and historical checks.

## Verified

* **Migration on scratch** (`PlantsIMS2_Scratch_Bed4Mig_20261010`, from the 2026-10-10 copy-only backup):
  * dry run, commit, re-run (no-op), rollback (no Bed objects left, original CHECK restored), re-commit;
  * rollback refused once bed data exists.
* **Tests:**
  * an administrator is refused for both transplant and loss, and nothing moves;
  * direct inserts under another user, or with no user, are refused by the trigger;
  * the Transplanter's own work succeeds;
  * an administrator reversal succeeds and is audited.

| Run | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Bed Sowing and related classes (`Bed4Mig`) | 344 | 343 | 0 | 1 (needs `PSM_TEST_CONNECTION`) |
| Full suite with the migration (`Bed4Full`) | 2319 | 2253 | 0 | 66 |
| Full suite without it (`Bed4NoMig`) | 2319 | 2235 | 0 | 84 |

**Browser** (`Bed4Browser`):
* the administrator's transplant page is view-only;
* forged transplant and loss POSTs were refused, including one carrying `ExceptionReason`;
* the test transplanter transplanted (seed dated 30/09; trays and Cocopeat dated 09/10, recorded 10/10);
* the supervisor saw "Approve from 01-11-2026";
* the administrator reversed the transplant, logged as `AdminReversal`, with the returns dated when recorded.

## Database checks after the scratch runs

* bed conservation holds;
* bed totals equal their active operations;
* every operation was recorded by the Transplanter.
