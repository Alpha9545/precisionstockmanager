-- ============================================================================
-- Phase 32: Growing Partner -> Main Office (Phase 7, Potted Plant
-- Distribution)
-- ============================================================================
-- Purpose: let a Growing Partner (or other production) Area send Potted
-- Plant stock to Main Office, with the SAME send -> confirm-with-
-- discrepancy -> credit-destination pattern already proven three times
-- over (Cutting, Phase 15/16; MainOfficeIssue, Phase 18/C;
-- GrowingPartnerToOutlet, Phase 20/F), reusing the EXISTING
-- dbo.InternalTransfers table, dbo.PottedPlantStock.InTransitQuantity
-- (Phase 18), and the existing PottedPlantStock/PottedPlantStockTransactions
-- ledger -- no new stock table, no new "MainOfficeStock" table, no new
-- generic transfer system, no new stock column.
--
-- Phase 7's three required destinations for Ready Potted Plant Stock are
-- Main Office, Outlet, and Direct Customer Sale. Outlet (Phase 20) and
-- Direct Customer Sale (Phase 21, widened in Phase 7's own C# change to
-- also allow a Main Office source) already existed. This script closes
-- the one remaining gap -- Main Office was previously only reachable via
-- the generic, unrestricted, single-step 'PottedPlant' Internal Transfer
-- (no destination-Area-type check, no receiving-side confirmation queue),
-- which is inconsistent with every other cross-organizational movement in
-- this app and was never audited/confirmed by the receiving side. Rather
-- than restrict that generic type (which is also still used, unmodified,
-- for ordinary Area<->Area moves outside Phase 7's scope) or build a new
-- table, this adds a SIXTH already-proven StockType value,
-- 'GrowingPartnerToMainOffice', the mirror image of 'GrowingPartnerToOutlet'
-- with Source/Destination validation swapped (source must be an active
-- Growing-Partner-linked Area, exactly like GrowingPartnerToOutlet's own
-- source check; destination must be an active AreaType='MainOffice' Area,
-- the mirror of MainOfficeIssue's own SOURCE check applied here to the
-- destination instead).
--
-- This is additive, idempotent, safe to re-run: every ALTER/constraint is
-- guarded with an existence check, exactly like every prior phase script.
-- No DROP, no DELETE, no data loss possible from running this script.
--
-- Decisions this script implements:
--   1) NO new column on dbo.PottedPlantStock or dbo.InternalTransfers.
--      Every column this StockType needs (SourcePottedPlantStockId,
--      DestinationAreaId, PendingConfirmationAreaId is unused here exactly
--      like GrowingPartnerToOutlet/MainOfficeIssue, Status, ConfirmedQuantity,
--      DiscrepancyReason) already exists.
--   2) NO Status CHECK widening needed -- 'PendingConfirmation', 'Rejected',
--      'Completed', 'Cancelled' already exist and are reused exactly as-is.
--      Single confirmation step, no second "transplant"-style stage.
--   3) NO change to dbo.Area or its AreaType CHECK -- 'MainOffice' already
--      exists as a valid AreaType (Phase 14) and dbo.GrowingPartners/
--      Area.GrowingPartnerId already exist (Phase 17) to identify a
--      Growing Partner Area. Both sides of this workflow are represented
--      entirely by existing columns.
--   4) CK_InternalTransfers_DestinationRequired, _DifferentAreas, and
--      _ConfirmedQuantity already apply generically and need NO change.
--   5) The pre-existing 'PottedPlant' and 'MainOfficeIssue' StockTypes are
--      completely untouched by this script -- only the two StockType-
--      dispatch CHECK constraints are widened, exactly the same two every
--      prior phase already widened for its own new StockType value.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- PART 1: dbo.InternalTransfers -- widen StockType and SourceMatchesStockType
-- ----------------------------------------------------------------------------
-- Same drop-and-recreate pattern Phase 15/18/20 already used to widen these
-- same two constraints for 'Cutting'/'MainOfficeIssue'/'GrowingPartnerToOutlet'.
-- Every existing row already satisfies the widened CHECK, since we only ADD
-- an allowed combination, never remove one.

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_StockType')
BEGIN
    ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_StockType;
END
GO
ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_StockType
    CHECK (StockType IN ('EmptyPot', 'PottedPlant', 'Cutting', 'MainOfficeIssue', 'GrowingPartnerToOutlet', 'GrowingPartnerToMainOffice'));
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_SourceMatchesStockType')
BEGIN
    ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_SourceMatchesStockType;
END
GO
ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_SourceMatchesStockType CHECK (
    (StockType = 'EmptyPot'                    AND SourceEmptyPotInventoryId IS NOT NULL AND SourcePottedPlantStockId IS NULL AND SourceCuttingStockId IS NULL) OR
    (StockType = 'PottedPlant'                  AND SourcePottedPlantStockId  IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL) OR
    (StockType = 'Cutting'                      AND SourceCuttingStockId      IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourcePottedPlantStockId IS NULL) OR
    (StockType = 'MainOfficeIssue'              AND SourcePottedPlantStockId  IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL) OR
    (StockType = 'GrowingPartnerToOutlet'       AND SourcePottedPlantStockId  IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL) OR
    (StockType = 'GrowingPartnerToMainOffice'   AND SourcePottedPlantStockId  IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL)
);
GO

-- Note: CK_InternalTransfers_DestinationRequired ("StockType = 'Cutting' OR
-- DestinationAreaId IS NOT NULL") already forces DestinationAreaId to be
-- populated for 'GrowingPartnerToMainOffice' rows -- exactly what this
-- phase needs (the destination Main Office Area is chosen up front by the
-- sender) -- so it needs NO change. Likewise CK_InternalTransfers_DifferentAreas
-- and CK_InternalTransfers_ConfirmedQuantity already apply generically and
-- need no change.

-- ============================================================================
-- End of Phase 32. Every EXISTING EmptyPot/PottedPlant/Cutting/
-- MainOfficeIssue/GrowingPartnerToOutlet transfer row, every EXISTING
-- PottedPlantStock/EmptyPotInventory row, and every prior workflow are
-- completely unaffected by this script -- only the two StockType-dispatch
-- CHECK constraints were widened to also allow 'GrowingPartnerToMainOffice'.
-- ============================================================================
