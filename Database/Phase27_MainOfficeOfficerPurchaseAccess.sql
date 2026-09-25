/*
    Phase 27: Main Office Officer can record Empty Pot purchases.

    Finding (Phase 2 audit, "Empty Pot Inventory and Area-specific Pot
    Issue"): the entire purchase -> stock -> issue -> production workflow
    already existed before this phase --
        Vendors/PurchaseOrders (Phase 11, ItemCategory = 'EmptyPot', with
          PotSize + destination AreaId) --Receive--> dbo.EmptyPotInventory
          (StockIn, PurchaseOrderRepository.cs)
        dbo.EmptyPotInventory (Phase 7/8), one row per (PotSize, AreaId),
          its own ledger (dbo.EmptyPotInventoryTransactions)
        InternalTransferRepository (Phase 8), StockType = 'EmptyPot',
          Area -> Area, Area-scoped + supervisor-checked (Phase 1)
        PotProductionRepository consumes it Area-scoped (F1) into
          dbo.PottedPlantStock.
    Nothing here was duplicated; no new inventory concept, no new Area
    concept, no new fields.

    The one gap: the 'Purchase.View'/'Purchase.Enter' permissions defined
    in Phase 14 were never granted to ANY operational role -- only Admin
    (every permission) and Management (every '*.View', read-only) can
    reach them. Every page that lets someone record a purchase or add
    Empty Pot stock is gated by one of these two codes
    (FeatureAuthorizationConventions.cs):
        /Production/PurchaseOrder/{Index,Details}   -> Purchase.View
        /Production/PurchaseOrder/{Create,Receive}  -> Purchase.Enter
        /Production/EmptyPotInventory/{Index,Details} -> PotProduction.View | Purchase.View
        /Production/EmptyPotInventory/{Create,AddStock} -> PotProduction.Enter | Purchase.Enter
    So "Office Officer records purchased empty pots in Empty Pot
    Inventory" (business requirement) was not actually possible for any
    day-to-day user -- only a System Administrator could do it.

    MainOfficeOfficer ("Confirms incoming deliveries and routes stock from
    Main Office", Phase 14) is the existing role matching "Office Officer":
    it already has MainOffice.View/Confirm and InternalTransfer.View/Enter
    (so it can already do the "Issue to Area" step). This phase adds the
    missing 'Purchase.View'/'Purchase.Enter' pair so the same role can also
    do the "record the purchase" step -- no new role, no new permission
    code, same idempotent NOT EXISTS guard as every other grant in
    Phase 14.

    Safe to run multiple times. Grants ONLY; nothing is revoked, no table
    is created or altered, no existing data is touched.
*/

INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id
FROM dbo.Roles r
CROSS JOIN dbo.Permissions p
WHERE r.Name = 'MainOfficeOfficer'
  AND p.Code IN ('Purchase.View', 'Purchase.Enter')
  AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
GO
