# Audit follow-up — owner decisions implemented (2026-10-11)

Continues [AUDIT_IMPLEMENTATION_REPORT_2026-10-10.md](AUDIT_IMPLEMENTATION_REPORT_2026-10-10.md).

* **Nothing was committed, pushed, staged or deployed.**
* **No migration was applied to `PlantsIMS2_Test` or production.** Local `PlantsIMS2_Test` was only read (role grants).
* **Vehicle Tracking is untouched.**

## 1. Files changed

**New**

* `Database/Migrations/2026-10-11_RolePermissionAlignment.sql` + `_Rollback.sql`
* `Database/Migrations/2026-10-11_ReadyStockReceiptDate.sql` + `_Rollback.sql`
* this report

**Changed**

| Area | Files |
|---|---|
| Permissions and visibility | `Authorization/MaterialStockAccess.cs`, `Authorization/TrayAreaScope.cs`, `Authorization/FeatureAuthorizationConventions.cs` (the two redirect-only Tray Stock URLs keep their original rule) |
| Tray transfer destinations | `Data/TrayTransferRepository.cs`, `Pages/Production/MaterialStock/TrayTransfers.cshtml(.cs)` |
| Receipt date | `Services/MotherPlantTransferRules.cs`, `Data/MotherPlantTransferRepository.cs`, `Data/ReadyStockRepository.cs`, `Pages/Production/MotherPlantTransfers/Index.cshtml(.cs)` |
| History flags | `Models/DailyOperationsReport.cs`, `Data/DailyOperationsReportRepository.cs` (query 18), `Services/DailyOperationsReportRules.cs` |
| Tests | `MotherPlantSupervisorOutletSellE2ETests`, `MaterialStockRulesTests`, `TrayTransferAuthorizationTests`, `TrayTransfersE2ETests`, `ReadyStockTransplantingE2ETests`, `TransplantRulesTests`, `DailyOperationsReportRulesTests` |
| Docs | `PRODUCTION_PREFLIGHT.md`, `STOCK_DEFINITIONS.md`, `MATERIAL_STOCK.md` |
| Config | `.claude/launch.json` (preview now points at scratch `R2Mig_20261011`) |

## 2. Permissions before and after

| Role | Before (local `PlantsIMS2_Test`, read only) | After `2026-10-11_RolePermissionAlignment.sql` |
|---|---|---|
| Booking Executive | Booking.Enter, Booking.View, Dashboard.View, **Dispatch.Enter**, Dispatch.View, ReadyStock.View, TrayStock.EnterMotherPlant | Booking.Enter, Booking.View, Dashboard.View, Dispatch.View, ReadyStock.View, TrayStock.EnterMotherPlant |
| Mother Plant Supervisor | Dashboard.View, InternalTransfer.Enter/View, Labour.Enter/View, Maintenance.Raise/View, MotherPlant.Enter/View, Outlet.Sell, PotProduction.View, ReadyStock.Confirm/ReceiveTransfer/View, Store.Request/View, Transplant.Enter/View (+ TrayStock.ReceiveTransfer after 10-10) | the same + **TrayStock.ViewMotherPlant** |
| Office Coordinator | unchanged (TrayStock.EnterMotherPlant kept; + TrayStock.Transfer from 10-10) | unchanged |
| All other roles | unchanged | unchanged |

**Manually granted permissions in the local database.**

* Booking Executive's `Dispatch.View` and `Dispatch.Enter` are in local `PlantsIMS2_Test`, but no script grants them.
* They appear in copies from 2026-10-04 onwards and not on 2026-10-01, so they were added through the Roles admin page.
* `RolePermissions` has no audit columns, so who added them and exactly when cannot be recovered.
* The migration controls this change:
  * it adds `Dispatch.View` only if missing;
  * it removes `Dispatch.Enter` and records the removal (with the reason) in the new `dbo.RolePermissionChangeLog`;
  * the rollback restores exactly what was logged.
* Sowing Operator also holds grants made outside scripts (cutting / main office / dispatch, present since at least 2026-10-01). No decision was asked about them, so they are unchanged; the test records them as the intended set.

**What `Dispatch.View` gives the Booking Executive** (read-only):

* Dispatch list and details
* Seedling Dispatch view
* Fulfilment
* Dispatch Register

Dispatch create, edit and seedling dispatch actions need `Dispatch.Enter`, which only the Dispatch Executive holds.

`OtherRoles_PermissionSets_AreUnaffectedByThisFix` asserts the exact set of every role. On a database without the migration it fails and names `Booking Executive: unexpected [Dispatch.Enter]`. With the migration it passes.

## 3. Tray visibility and transfer destinations

**Mother Plant Supervisor view.** `TrayStock.ViewMotherPlant`:

* opens Material Stock and Material History in tray-only mode;
* is enforced on the server by `TrayAreaScope`, not only in the menu;
* shows **only the user's own active Mother Plant Areas** (even if also assigned elsewhere);
* never shows other Mother Plant Areas or Main Office trays.

Refused (HTTP tests and the browser):

* Add Material (GET and both POSTs)
* Adjust Trays
* Material Settings
* Cocopeat and Pitmos (not shown at all)

Sending trays needs `TrayStock.Transfer`, which this role does not have. Receiving permission alone still gives no view. `TrayStock.EnterMotherPlant` (Office Coordinator, Booking Executive) is unchanged.

**Transfer destinations:**

* **Non-administrators** may send only to an active, non-Outlet Polyhouse in an Area where an **active user assigned to that Area holds `TrayStock.ReceiveTransfer`**.
  * The page lists only those Areas, and `TrayTransferRepository.CreateAsync` re-checks inside the transaction.
  * The source Area check (`TrayAreaScope`) and the destination receiver check are independent.
* **Administrators** (full access) may send exceptional transfers to any active non-Outlet Polyhouse, and can receive or reject them.
* **Outlets** are never a destination, for anyone.
* **Nothing stays stuck in transit:**
  * the sender can always cancel;
  * an administrator can always receive or reject;
  * if the last receiver of an Area leaves, new sends to that Area stop.
* Locking, duplicate-submission tokens and ledger rows are unchanged.

## 4. Ready Stock receipt business date

The receive form has two new fields:

* **Date received:** defaults to today (IST); cannot be in the future or before the send date.
* **Reason:** required when the date is earlier than today.

On the server:

* `MotherPlantTransferRules.ValidateReceiptDate` re-checks these rules.
* All receipt ledger rows are dated at that business date: the hold release and the source out / loss on the source batch, and the transfer-in on the new batch.
* They are never dated before the transfer's hold row, so the plants are counted once — never at both places or at neither.
* `InternalTransfers.ReceivedDate` / `ReceiptDateReason` store the business date and reason; `ConfirmedDate` keeps the recorded time.
* The received batch is usable (e.g. for transplanting) from the receipt date.
* No historical receipt date is invented: older receipts stay NULL and remain on the day they were recorded.
* Without the migration, the code accepts only today's date, so `PlantsIMS2_Test` keeps working unchanged.

## 5. History and report consistency

**New multi-day E2E scenario.** One Plant Type, with each Daily Operations day compared before and after the scenario:

* approval 5 days ago;
* a dispatch dated 3 days ago (reserved and released today);
* a transfer sent 3 days ago and received yesterday with a 10-plant loss;
* a transfer sent 2 days ago and cancelled today;
* a transplanting today, then reversed.

Result:

* Every day's owned / reserved / in-transit change matched the scenario exactly; days before the approval were unchanged.
* Today, Daily Operations, the Dashboard, the Ready Stock table and the WhatsApp source all gave the same Available figure.

**New flags (query 18).** Past days are flagged as "cannot be reliably reconstructed" when:

* rows are dated before their batch first held stock; or
* any figure rebuilds to a negative number.

The figures are shown as the ledger has them and never replaced by current stock.

## 6. Fixtures and accessibility

* The earlier fixture repairs are preserved, and no assertion was weakened.
* The permission test now checks exact sets and names any mismatch.
* Phone-table accessibility was re-checked in the browser:

| Width | Result |
|---|---|
| 1280 and 768 | normal tables, header row visible |
| 375 | cards with ARIA roles |
| All three | no horizontal scroll |

Print: no card-table rule exists outside the screen-only media query.

## 7. Migration dependencies

Full ordered list: [PRODUCTION_PREFLIGHT.md](PRODUCTION_PREFLIGHT.md) section 3b.

| # | Script | Requires |
|---|---|---|
| 12 | `2026-10-10_TrayTransfers` | 10-09 |
| 13 | `2026-10-11_RolePermissionAlignment` | 10-09; run after 12 |
| 14 | `2026-10-11_ReadyStockReceiptDate` | 10-09 |

Verified on fresh scratch (`PlantsIMS2_Scratch_R2Mig_20261011`):

* dry run, commit, idempotent re-run;
* rollback dry run, rollback, second rollback (a no-op), re-commit;
* the server, database and MODE guards all refused;
* the receipt-date rollback refused once dates were stored (`R2Full`).

## 8. Tests

| Run | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Targeted (`R2Mig`, all three migrations) | 453 | 452 | 0 | 1 (manual WhatsApp sender) |
| **Full suite, fresh migrated scratch `PlantsIMS2_Scratch_R2Full_20261011`** | **2271** | **2205** | **0** | **66** |
| Compatibility: database with 10-10 only (`AuditFinal`) | 44 | 40 | 1 (expected: names the leftover Dispatch.Enter) | 3 (receipt-date tests, no columns) |

* **Remaining skips (66):**
  * 63 Vehicle Tracking (paused, migrations not applied)
  * 1 manual WhatsApp sender
  * 2 booking scenarios whose data condition is absent
* **Remaining failures on the migrated database:** none.
* **The previous deliberate failure (Booking Executive) is resolved** by the owner's decision and the migration.

## 9. Confirmations

* Vehicle Tracking (pages, code, `mobile/`, migrations) is byte-identical to before.
* `PlantsIMS2_Test` was not modified.
* Production was never connected to.
* No scratch database was deleted.
* **Waiting for explicit approval before applying `2026-10-10_TrayTransfers.sql`, `2026-10-11_RolePermissionAlignment.sql` or `2026-10-11_ReadyStockReceiptDate.sql` to `PlantsIMS2_Test`, and before any deployment.**
