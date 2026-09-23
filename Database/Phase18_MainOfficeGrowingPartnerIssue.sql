-- ============================================================================
-- Phase 18: Main Office -> Growing Partner Starter Material (Phase C)
-- ============================================================================
-- Purpose: let Main Office issue potted-plant starter material to a
-- Growing Partner Area, with the same send/confirm-with-discrepancy
-- pattern already proven for Cutting (Phase 15/16), reusing the EXISTING
-- dbo.InternalTransfers table and dbo.PottedPlantStock/
-- PottedPlantStockTransactions ledger -- no new stock table, no new
-- generic transfer system.
--
-- This is additive, idempotent, safe to re-run: every ALTER/constraint is
-- guarded with an existence check, exactly like every prior phase script.
-- No DROP, no DELETE, no data loss possible from running this script.
--
-- Decisions this script implements (per the Phase C instructions):
--   1) dbo.PottedPlantStock gets a new InTransitQuantity column, mirroring
--      the exact mechanism dbo.CuttingStock already uses (Phase 16) to
--      stop the same physical stock being sent twice while a transfer is
--      still in flight -- the SAME pattern, reused, not a second
--      different in-transit mechanism. The EXISTING computed
--      AvailableQuantity column (= PhysicalQuantity - ReservedQuantity,
--      Phase 7, relied on by Phase 9/10 Booking/Reservation) is NOT
--      redefined -- InTransitQuantity is a separate, independently
--      maintained column, so Booking/Dispatch behavior is byte-for-byte
--      unchanged. "Available to send on a new Main Office Issue" is
--      computed as PhysicalQuantity - ReservedQuantity - InTransitQuantity
--      by the application (PottedPlantStockRepository.ReserveInTransitAsync),
--      the same way CuttingStock's version already does it, never as a
--      second persisted computed column.
--   2) dbo.InternalTransfers gets a new StockType value, 'MainOfficeIssue',
--      deliberately DISTINCT from the existing 'PottedPlant' value --
--      'PottedPlant' remains the immediate, no-confirmation transfer
--      Kunjir/Kiran/etc. already use for direct Area-to-Area moves
--      (Phase 8, unchanged); 'MainOfficeIssue' is its own code path in
--      InternalTransferRepository for the new send -> confirm-with-
--      discrepancy -> credit-destination workflow. This avoids
--      conflating two different business meanings under one StockType.
--   3) No Status CHECK widening needed -- 'PendingConfirmation',
--      'Rejected', 'Completed', 'Cancelled' already exist and are reused
--      exactly as-is. No new lifecycle states were invented.
--   4) dbo.CuttingStock, dbo.CuttingStockTransactions, dbo.CuttingTransplants,
--      and every Phase 16 CHECK constraint are completely untouched by
--      this script.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- PART 1: dbo.PottedPlantStock -- InTransitQuantity
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PottedPlantStock') AND name = 'InTransitQuantity')
BEGIN
    ALTER TABLE dbo.PottedPlantStock
        ADD InTransitQuantity DECIMAL(18,2) NOT NULL CONSTRAINT DF_PottedPlantStock_InTransitQuantity DEFAULT (0);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedPlantStock_InTransitNonNegative')
BEGIN
    ALTER TABLE dbo.PottedPlantStock ADD CONSTRAINT CK_PottedPlantStock_InTransitNonNegative
        CHECK (InTransitQuantity >= 0);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedPlantStock_InTransitWithinPhysical')
BEGIN
    ALTER TABLE dbo.PottedPlantStock ADD CONSTRAINT CK_PottedPlantStock_InTransitWithinPhysical
        CHECK (InTransitQuantity <= PhysicalQuantity);
END
GO


-- ----------------------------------------------------------------------------
-- PART 2: dbo.InternalTransfers -- widen StockType and SourceMatchesStockType
-- ----------------------------------------------------------------------------
-- Same drop-and-recreate pattern Phase 15 already used to widen these same
-- two constraints for 'Cutting'. Every existing row (EmptyPot/PottedPlant/
-- Cutting) already satisfies the widened CHECK, since we only ADD an
-- allowed combination, never remove one.

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_StockType')
BEGIN
    ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_StockType;
END
GO
ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_StockType
    CHECK (StockType IN ('EmptyPot', 'PottedPlant', 'Cutting', 'MainOfficeIssue'));
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_SourceMatchesStockType')
BEGIN
    ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_SourceMatchesStockType;
END
GO
ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_SourceMatchesStockType CHECK (
    (StockType = 'EmptyPot'        AND SourceEmptyPotInventoryId IS NOT NULL AND SourcePottedPlantStockId IS NULL AND SourceCuttingStockId IS NULL) OR
    (StockType = 'PottedPlant'     AND SourcePottedPlantStockId  IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL) OR
    (StockType = 'Cutting'         AND SourceCuttingStockId      IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourcePottedPlantStockId IS NULL) OR
    (StockType = 'MainOfficeIssue' AND SourcePottedPlantStockId  IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL)
);
GO

-- Note: CK_InternalTransfers_DestinationRequired (Phase 15: "StockType =
-- 'Cutting' OR DestinationAreaId IS NOT NULL") already forces
-- DestinationAreaId to be populated for 'MainOfficeIssue' rows -- exactly
-- what Phase C needs (destination is chosen up front, unlike Cutting's
-- confirm-time routing) -- so it needs NO change. Likewise
-- CK_InternalTransfers_DifferentAreas and CK_InternalTransfers_ConfirmedQuantity
-- already apply generically and need no change.

-- ============================================================================
-- End of Phase 18. Every EXISTING EmptyPot/PottedPlant/Cutting transfer
-- row, every EXISTING PottedPlantStock row (InTransitQuantity defaults to
-- 0), and the entire Phase 16 Cutting workflow (dbo.CuttingStock,
-- dbo.CuttingStockTransactions, dbo.CuttingTransplants, and their CHECK
-- constraints) are completely unaffected by this script.
-- ============================================================================
