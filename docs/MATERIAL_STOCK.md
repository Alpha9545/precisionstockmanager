# Material Stock — Cocopeat and Pitmos

Growing material kept in the **Polyhouses of the Main Office Area**. The Sowing Supervisor adds it;
every sowing in a Main Office Polyhouse uses **exactly one** material, deducted from **that same
Polyhouse** in the sowing's own database transaction. A separate stock domain: not the Inventory
Store and not `dbo.Inventory`.

Migrations: `Database/Migrations/2026-10-08_MaterialStock.sql`, then
`2026-10-08_MaterialStockApprovals.sql` (approval overage; undo each with its `..._Rollback.sql`, in
reverse order).
Code: `Data/MaterialStockRepository.cs`, `Services/MaterialStockRules.cs`, `Models/MaterialStock.cs`,
`Pages/Production/MaterialStock/*`, `Pages/Production/SeedSowing/_GrowingMaterial.cshtml`,
`GrowingMaterialForm.cs`, `wwwroot/js/growing-material.js`; sowing integration in
`Data/SeedSowingRepository.cs`, approval overage in `Data/ReadyConfirmationRepository.cs`. Tests: `MaterialStockRulesTests`, `MaterialStockE2ETests`, `MaterialStockCuttingE2ETests` (cutting sowing, approval overage, cancellation).

## Master data (approximate business values — data, not code)

| Table | Holds | Initial values |
|---|---|---|
| `MaterialTypes` | Code, Name, Unit, **ConversionFactor** (usable kg per entered kg) | COCOPEAT 5.2 (5 kg dry → 26 kg), PITMOS 1.0 |
| `MaterialConsumptionRules` | **kg per tray** per Cavity and Material | 9 / 24 / 42 / 102 / 150 Cavity × Cocopeat / Pitmos = 1.5 kg |

Change them in **Material Stock → Settings / Adjust** (permission `MaterialStock.Manage`, held by no
role, so Administrators only). A change applies from then on; every ledger row keeps the factor or kg
per tray it was calculated with, so history never changes.

## Stock and ledger

* `MaterialStock`: one balance per **Material + Area + Polyhouse** (`UQ_MaterialStock_Location`),
  `DECIMAL(18,3)`, never negative, only at a Polyhouse of an active Main Office Area.
* `MaterialTransactions`: insert-only ledger.

| Type | Sign | Keeps |
|---|---|---|
| RECEIVE | + | InputQuantity (dry kg entered), ConversionFactor, StockQuantity = ROUND(input × factor, 3) |
| CONSUME | − | Trays, QuantityPerTray, the sowing (ReferenceType `SeedSowing`) or, for extra trays at approval, the approval (ReferenceType `ReadyConfirmation`) |
| REVERSAL | + | the cancelled sowing or approval; returns exactly what that row consumed |
| ADJUSTMENT | + / − | Remarks = the required reason |

Every row: BalanceBefore / BalanceAfter, user, UTC time. The balance changes only through the ledger,
in the same transaction, after `sp_getapplock` + `UPDLOCK` on that balance (the Tray Stock pattern),
so simultaneous operations run one after the other.

## Sowing rule

1. A sowing whose Polyhouse is in a **Main Office Area** must name **one** material (radio buttons;
   two posted values are refused). A sowing elsewhere (e.g. a Cutting Sowing at a Mother Plant Area)
   uses **none**, because no material stock exists there.
   **Direct Seed Sowing** (2026-10-08) is done ONLY in Main Area (AreaType `MainOffice`) Polyhouses:
   the form has no Area field — the user picks the Polyhouse, and the server derives the Area from
   `dbo.Polyhouses.AreaId` (`DirectSowingRules.ResolveDirectSowingArea`, page + repository), refusing a
   non-Main Polyhouse, an unknown one, a posted AreaId that disagrees, or an Area outside the user's scope.
   So every Direct Seed Sowing needs exactly one material. Cutting Sowing is unchanged.
2. Physical trays = CEILING(quantity / cavity), the same trays Tray Stock gives out.
3. Required = trays × kg per tray (`MaterialConsumptionRules`); not enough → the **whole sowing** is
   refused: *"Insufficient Cocopeat stock in Facility-3. Available: 100.00 kg. Required: 150.00 kg."*
4. Seed / cutting, trays and material are deducted, and the sowing saved, in **one transaction**.
5. Cancelling a sowing returns exactly its consumption to the same Polyhouse.
6. **Extra trays at Supervisor Approval** (a Cutting Sowing approved with more trays than sown): the
   approval also takes extra trays × the kg per tray **the sowing itself was charged** (its own
   CONSUME row) of the **same material** from the **same Area + Polyhouse**, in the approval's
   transaction together with the extra cuttings, extra trays and Ready Stock. Not enough material →
   the whole approval is refused; nothing changes. Example: 100 trays = 150 kg; approved 105 →
   +7.5 kg = 157.5 kg. Cancelling the approval returns its 7.5 kg; cancelling the sowing (only possible
   once its approvals are cancelled) returns its 150 kg — exactly the ledger quantities. A sowing with
   no material (outside the Main Office) takes none. Seed sowings never take extra trays.
7. The form's one-time `SubmissionToken` (`UX_SeedSowings_SubmissionToken`): a double submit creates
   one sowing and one deduction.

The form's live figures (available, trays, kg/tray, required, remaining, enough) are a preview; the
server recalculates everything.

## Database rules (triggers / constraints)

`TR_MaterialStock_Rules` (Main Office Polyhouse only, starts at 0, changes only to SUM(ledger), key
immutable), `TR_MaterialTransactions_Ledger` (insert-only, balance-before must match, consumption must
match its sowing's material + Area + Polyhouse, reversal = consumption), `TR_SeedSowings_GrowingMaterial`
(material required at Main Office, forbidden elsewhere, never changes), CHECK constraints for types,
signs, positive quantities, the receive formula, the consume formula and the adjustment reason.

## Permissions

| Permission | Page | Granted to |
|---|---|---|
| MaterialStock.View | Stock | Sowing Supervisor |
| MaterialStock.Add | Add Material | Sowing Supervisor |
| MaterialStock.History | History | Sowing Supervisor |
| MaterialStock.Manage | Settings / Adjust | nobody (full-access users only) |

All pages are limited to the user's own Areas (`AreaAccessService.CanAccessRequiredArea`).
Sowing entry keeps its existing rule (`Sowing.Enter`).

## Open business decisions

* **Who records sowings:** the Sowing Supervisor role does not hold `Sowing.Enter`. Sowings are recorded
  by the Main Office Store Keeper / Sowing Operator, who choose the material on the form. Unchanged.
* **Mother Plant Area sowings** (Cutting Sowing destinations outside the Main Office) use no material.
* **Overage rate:** extra trays are charged at the sowing's own recorded kg per tray, not at a value
  changed in Settings after the sowing (so one sowing is never charged at two rates).
* **Low stock:** no threshold exists, so no low-stock warning is shown.

## Tray adjustment and tray transfers (2026-10-10)

Migration `Database/Migrations/2026-10-10_TrayTransfers.sql` (not yet applied to `PlantsIMS2_Test`).

* **Adjust Trays** (`/Production/MaterialStock/TrayAdjust`, permission `TrayStock.Adjust`: Sowing Supervisor
  in its own Areas, Admin everywhere). Whole trays, add or remove, mandatory reason, date of the count (not in
  the future). One new `Adjustment` ledger row (ReferenceType `TrayAdjustment`); never below zero; a repeated
  submit changes nothing twice.
* **Tray Transfers** (`/Production/MaterialStock/TrayTransfers`). Send / cancel: `TrayStock.Transfer`
  (Sowing Supervisor, Office Coordinator) from an Area they may stock trays in. Receive / reject is
  LOCATION-AWARE (2026-10-10): into a Mother Plant Area with `TrayStock.ReceiveTransfer` (Mother Plant
  Supervisor), into a Main Office Area with `TrayStock.ReceiveTransferMainArea` (Sowing Supervisor,
  `2026-10-11_TrayAccessByLocation.sql`) -- always only in Areas the user is assigned to. Admin: all.
  * Sent: the source loses the trays at once (`TransferOut`, dated at the date sent); they are **in transit**.
  * Received: the destination gains them (`TransferIn`, dated at the date received, not before the send).
  * Rejected / cancelled (reason required): the trays return to the source (`TransferReturn`).
  * Material Stock shows trays in transit as a separate line; history shows the category "Transfer".
* **Opening balances:** none are created automatically. Count each Polyhouse and cavity once and enter the
  difference as an adjustment. See [STOCK_DEFINITIONS.md](STOCK_DEFINITIONS.md).
* **Destinations (2026-10-11):** a non-administrator may send only to an active, non-Outlet Polyhouse in an
  Area where an active user assigned to that Area holds the receive permission for that Area's type; the page lists only those
  Areas and the server re-checks. Administrators may make exceptional transfers to any active non-Outlet
  Polyhouse and can receive / reject them; senders can always cancel. Nothing is left in transit for want of a
  receiver.
* **Mother Plant Supervisor view (2026-10-11):** `TrayStock.ViewMotherPlant` shows empty-tray balances and tray
  history of the supervisor's own Mother Plant Areas only -- read-only (no add, adjust, transfer, Cocopeat or
  Pitmos).
