-- ============================================================================
-- Phase 20: Growing Partner -> Outlet (Phase F)
-- ============================================================================
-- Purpose: let a Growing Partner Area send Potted Plant stock to an Outlet
-- Area, with the SAME send -> confirm-with-discrepancy -> credit-
-- destination pattern already proven twice over (Cutting, Phase 15/16;
-- MainOfficeIssue, Phase 18/C), reusing the EXISTING dbo.InternalTransfers
-- table, dbo.PottedPlantStock.InTransitQuantity (Phase 18), and the
-- existing PottedPlantStock/PottedPlantStockTransactions ledger -- no new
-- stock table, no new "OutletStock" table, no new generic transfer system,
-- no new stock column.
--
-- This is additive, idempotent, safe to re-run: every ALTER/constraint is
-- guarded with an existence check, exactly like every prior phase script.
-- No DROP, no DELETE, no data loss possible from running this script.
--
-- Decisions this script implements (per the Phase F instructions):
--   1) dbo.InternalTransfers gets a FIFTH StockType value,
--      'GrowingPartnerToOutlet', deliberately distinct from 'MainOfficeIssue'
--      even though the underlying stock mechanics (single-step confirm,
--      full InTransit release, decrement source by confirmed quantity,
--      credit destination by confirmed quantity) are identical in shape --
--      the two remain separate StockType values because their SOURCE/
--      DESTINATION AreaType validation rules are the mirror image of each
--      other (MainOfficeIssue: source must be AreaType='MainOffice',
--      destination must be Growing-Partner-linked; GrowingPartnerToOutlet:
--      source must be Growing-Partner-linked, destination must be
--      AreaType='Outlet') and conflating them under one StockType would
--      have made that validation ambiguous.
--   2) NO new column on dbo.PottedPlantStock. Its existing InTransitQuantity
--      (Phase 18) is reused exactly as-is via
--      PottedPlantStockRepository.ReserveInTransitAsync/ReleaseInTransitAsync
--      (both already fully generic -- they take a PottedPlantStock row Id
--      and a quantity, with no StockType concept at all) -- this is
--      precisely the "existing architecture can safely use a transfer-
--      level reservation without adding another stock column" case the
--      instructions asked to prefer.
--   3) NO Status CHECK widening needed -- 'PendingConfirmation', 'Rejected',
--      'Completed', 'Cancelled' already exist (Phase 15/16) and are reused
--      exactly as-is. No 'ConfirmedAwaitingTransplant'/'Transplanted'-style
--      second stage is used for this workflow, per the explicit
--      instruction that Outlet receipt is a single confirmation step.
--   4) NO change to dbo.Area or its AreaType CHECK -- 'Outlet' already
--      exists as a valid AreaType (Phase 14) and dbo.GrowingPartners/
--      Area.GrowingPartnerId already exist (Phase 17) to identify a
--      Growing Partner Area. Both sides of this workflow are represented
--      entirely by existing columns.
--   5) CK_InternalTransfers_DestinationRequired, _DifferentAreas, and
--      _ConfirmedQuantity already apply generically (see Phase 18's own
--      note below, still true) and need NO change for this StockType.
--   6) dbo.CuttingStock/CuttingStockTransactions/CuttingTransplants and the
--      dbo.PottedPlantStock InTransit columns added by Phase 18 are
--      completely untouched by this script -- only the two StockType-
--      dispatch CHECK constraints are widened, exactly the same two Phase
--      15/18/19 already widened for their own new StockType/value.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- PART 1: dbo.InternalTransfers -- widen StockType and SourceMatchesStockType
-- ----------------------------------------------------------------------------
-- Same drop-and-recreate pattern Phase 15/18 already used to widen these
-- same two constraints for 'Cutting'/'MainOfficeIssue'. Every existing row
-- (EmptyPot/PottedPlant/Cutting/MainOfficeIssue) already satisfies the
-- widened CHECK, since we only ADD an allowed combination, never remove one.

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_StockType')
BEGIN
    ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_StockType;
END
GO
ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_StockType
    CHECK (StockType IN ('EmptyPot', 'PottedPlant', 'Cutting', 'MainOfficeIssue', 'GrowingPartnerToOutlet'));
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_SourceMatchesStockType')
BEGIN
    ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_SourceMatchesStockType;
END
GO
ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_SourceMatchesStockType CHECK (
    (StockType = 'EmptyPot'                AND SourceEmptyPotInventoryId IS NOT NULL AND SourcePottedPlantStockId IS NULL AND SourceCuttingStockId IS NULL) OR
    (StockType = 'PottedPlant'              AND SourcePottedPlantStockId  IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL) OR
    (StockType = 'Cutting'                  AND SourceCuttingStockId      IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourcePottedPlantStockId IS NULL) OR
    (StockType = 'MainOfficeIssue'          AND SourcePottedPlantStockId  IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL) OR
    (StockType = 'GrowingPartnerToOutlet'   AND SourcePottedPlantStockId  IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL)
);
GO

-- Note: CK_InternalTransfers_DestinationRequired (Phase 15: "StockType =
-- 'Cutting' OR DestinationAreaId IS NOT NULL") already forces
-- DestinationAreaId to be populated for 'GrowingPartnerToOutlet' rows --
-- exactly what Phase F needs (the destination Outlet Area is chosen up
-- front by the sender, unlike Cutting's confirm-time routing) -- so it
-- needs NO change. Likewise CK_InternalTransfers_DifferentAreas and
-- CK_InternalTransfers_ConfirmedQuantity already apply generically and
-- need no change.

-- ============================================================================
-- End of Phase 20. Every EXISTING EmptyPot/PottedPlant/Cutting/
-- MainOfficeIssue transfer row, every EXISTING PottedPlantStock/
-- EmptyPotInventory row, and the entire Phase 16 Cutting workflow /
-- Phase 18 MainOfficeIssue workflow are completely unaffected by this
-- script -- only the two StockType-dispatch CHECK constraints were widened
-- to also allow 'GrowingPartnerToOutlet'.
-- ============================================================================
