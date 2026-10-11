# Historical empty-tray availability, receipt dates in transfer history, receiving check (2026-10-10)

Continues [LOCAL_VERIFICATION_REPORT_2026-10-10.md](LOCAL_VERIFICATION_REPORT_2026-10-10.md).

* No migration was run on `PlantsIMS2_Test`; it is unchanged (verified by fingerprint, and it holds no test users).
* All write tests ran on fresh scratch restores of the migrated local database.
* Nothing was committed, staged or deployed, and production was never contacted.
* Vehicle Tracking is untouched: 237 Vehicle Tracking / mobile / publish files are byte-identical.

## 1. Root cause

Transplanting checked the target pool's **current** balance (`TrayStock.PhysicalQuantity`), then wrote the tray consumption with the **transplanting business date**. Nothing checked the balance on that date.

* Smoke test example: trays received on 10-10, transplant dated 09-10.
  * The current balance (30) passed.
  * The consumption landed on 09-10, before the receipt.
  * The pool's history by date was −5 for that day.
* The same gap existed for every other **back-dated tray deduction**:
  * a tray adjustment that removes trays, dated earlier;
  * a tray transfer sent with an earlier date.

## 2. Fix

**`TrayStockRepository.AvailableFromAsync(conn, tx, pool, atUtc)`** gives the trays available to a deduction dated `atUtc`. That is the **lowest** balance the pool has from that instant onward: the balance at `atUtc`, then the running balance after every later-dated row (receipts, transfers, adjustments, other consumption), in business-date order.

* A deduction is valid only if it fits under all of them.
* Today-dated deductions keep the existing current-balance check.

Where it is applied:

| Place | What it checks |
|---|---|
| `TrayStockRepository.RecordTransactionAsync` (the one place every tray movement is written) | Refuses any back-dated deduction that does not fit — a safety net for every caller |
| Transplanting (`TransplantRepository`) | Validates trays as of the transplanting date. Message: "Not enough empty 42 Cavity trays in the target Polyhouse on 09-10-2026: 1 needed, 0 available from that date onward (counting every later movement). Record the trays' arrival first, or use a later date." |
| Tray transfer send (`TrayTransferRepository`) | Source trays as of the transfer date |
| Tray adjustment (`TrayStockRepository.AdjustAsync`) | A back-dated removal is checked against the trays available from its date onward |

**Atomicity and concurrency are unchanged.**

* The check runs inside the same transaction, after the pool's `sp_getapplock` / `UPDLOCK`.
* So validation, consumption, the ledger row and the transplant / transfer row still commit or fail together.
* Duplicate-submission tokens are untouched.

**Existing history is not changed.** The new `Services/TrayHistoryRules.NegativeStretches` lists every pool whose business-date running balance goes below zero.

* Material History shows these as a warning: pool, from / until dates, lowest balance.
* On the scratch smoke clone it flags exactly the earlier case: "Green Bless Nursery / P-1 / 42 Cavity: below zero from 09-10-2026, back to zero or more on 10-10-2026, lowest −5 trays".
* Local `PlantsIMS2_Test` has **no** such pool (checked read-only).

## 3. Transfer history (Ready Stock → Mother Plant Areas)

The Mother Plant Transfers history table now shows:

| Column | Content |
|---|---|
| **Sent on** | The transfer business date, with the recorded time underneath (India time) |
| **Status** | Unchanged |
| **Received on** | The business receipt date, with the recorded time and, for a late entry, its reason. "in transit" while pending. For receipts recorded before receipt dates existed: "— recorded dd-mm-yyyy hh:mm (receipt date not entered)". Nothing is invented |

* The list query works with or without the receipt-date columns.
* Existing receipts are not re-dated.
* Permissions and filters are unchanged.
* Mobile cards carry the new labels.
* Desktop and tablet layouts are unchanged, with no horizontal scroll at 375 / 768 / 1280.

## 4. Receiving (unchanged model, re-verified)

| Destination | Receiver |
|---|---|
| Main Office | Sowing Supervisor of that Area (`TrayStock.ReceiveTransferMainArea`) |
| Mother Plant Areas | Mother Plant Supervisor of that Area (`TrayStock.ReceiveTransfer`) |
| Anywhere | Admin (full access) |

No permission was broadened.

* **New E2E test:** using the same rule the page passes, these attempts are refused for both receive and reject, and nothing moves:
  * Mother Plant Supervisor → Main Office;
  * Sowing Supervisor → Mother Plant;
  * Booking Executive → either.
  * Each location's own receiver succeeds.
* **Browser:** a forged POST by the Mother Plant Supervisor for a Main Office transfer was refused ("You are not authorized to receive trays in the destination Area"). A Booking Executive's POST was denied by the page rule. The transfer stayed pending.

## 5. Tests

**New tests:**

* Transplant dated before the trays arrived: refused, nothing moves. After arrival: accepted.
* Insufficient historical stock: trays on the day, used up later. Refused above what is left from that date; accepted within it.
* Transfers and adjustments with different business dates:
  * a send or removal dated before arrival is refused;
  * a send dated after arrival, received today, is accepted;
  * at the destination, a transplant dated before the receipt is refused, and the same transplant dated today is accepted.
* Each case asserts no negative history, using its own scratch-only Mother Plant Area / Polyhouse fixture, deactivated afterwards.
* Location-aware receiving refusals (section 4).
* Pure tests for `TrayHistoryRules` and the dated tray message.

**Corrected fixtures:** three older tests stocked trays "now" and then used them in a back-dated transplant or transfer. That is exactly the invalid history the fix now refuses. Their setup trays are now dated before use; the assertions are unchanged.

| Run (fresh scratch restores of the migrated local database) | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Pure rules | 42 | 42 | 0 | 0 |
| Targeted tray / transplant / transfer / report classes (`R5Tgt2`) | 356 | 356 | 0 | 0 |
| **Full suite (`R5Full`)** | **2280** | **2214** | **0** | **66** |

* The 66 skips are 63 Vehicle Tracking (paused), 1 manual WhatsApp sender and 2 booking data-condition tests.
* **Historical balance validation:** after the full suite, which includes hundreds of back-dated tray operations, **0** pools have a negative business-date history. Local `PlantsIMS2_Test`: 0.
* **Browser on the smoke clone:**
  * the original case (trays received today, transplant dated yesterday) is now refused;
  * the same transplant dated today is accepted;
  * the history warning is shown.

## 6. Files changed

| Area | Files |
|---|---|
| Validation | `Data/TrayStockRepository.cs`, `Data/TransplantRepository.cs`, `Data/TrayTransferRepository.cs`, `Services/TransplantRules.cs` |
| History flag | `Services/TrayHistoryRules.cs` (new), `Pages/Production/MaterialStock/History.cshtml(.cs)` |
| Transfer history | `Models/ReadyStockTransplant.cs`, `Data/MotherPlantTransferRepository.cs`, `Pages/Production/MotherPlantTransfers/Index.cshtml` |
| Tests | `ReadyStockTransplantingE2ETests.cs`, `TrayTransfersE2ETests.cs`, `TrayTransferRulesTests.cs` |
| Docs | `docs/STOCK_DEFINITIONS.md`, this report |

## 7. Remaining risks

* **Intra-day order.** A back-dated row is placed at the business date plus the time of day it was recorded (unchanged convention). So on one business day, a deduction recorded earlier in the clock day than that day's receipt is treated as happening before it, and is refused. The user can enter the receipt first, or use the next date. This errs on the safe side.
* **Existing invalid history is flagged, not corrected.** That is by design. None exists on `PlantsIMS2_Test`; production should be checked with the same query before release (`PRODUCTION_PREFLIGHT.md`).
* **Sowing consumption.** Seed / cutting sowings consume trays at the recorded time, not the sowing date, as before. They cannot create negative history, but sowing trays still appear on the day recorded.
* **Release blockers** are unchanged:
  * the Vehicle-Tracking-free release commit (needs approval);
  * the production preflight;
  * a physical tray count.
