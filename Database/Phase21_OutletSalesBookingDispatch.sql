-- ============================================================================
-- Phase 21: Outlet -> Customer Sales (Booking, Reservation & Dispatch) -- Phase G
-- ============================================================================
-- Purpose: complete the chain
--   Growing Partner -> [Phase F] -> Outlet Potted Plant Stock
--       -> Customer Booking (Reserved) -> Dispatch -> Physical stock reduced
-- by extending the EXISTING dbo.PottedPlantBookings (Phase 9) and
-- dbo.Dispatches (Phase 10) tables -- no new stock table, no new
-- "OutletStock" table, no new booking/dispatch table, no new generic
-- reservation ledger, no new Customer master, no new Outlet master. Outlet
-- stock continues to be identified purely by dbo.PottedPlantStock.AreaId
-- where dbo.Area.AreaType = 'Outlet' (already true since Phase 14) -- zero
-- change to dbo.Area or dbo.PottedPlantStock's own schema.
--
-- Inspection findings that drove every decision below:
--   1) dbo.PottedPlantStock already has exactly the PhysicalQuantity /
--      ReservedQuantity / AvailableQuantity (computed = Physical - Reserved)
--      triple the spec asks for (Phase 7), and
--      PottedPlantStockRepository.RecordReservationAsync/
--      RecordTransactionAsync (Phase 9/7) already enforce, under
--      UPDLOCK/HOLDLOCK, every stock-accounting rule Phase G needs
--      (reservation cannot exceed Physical, Physical cannot go negative or
--      below what is Reserved). NO change needed to either method or to
--      dbo.PottedPlantStock's schema.
--   2) dbo.PottedPlantBookings/dbo.Dispatches (Phase 9/10) already
--      implement exactly "Booking reserves, Dispatch physically consumes,"
--      using the SAME dedicated ledger (dbo.PottedPlantStockTransactions)
--      via the already-reserved 'Reservation'/'ReservationRelease'/
--      'Dispatch' TransactionTypes. NO new ledger table, NO new
--      TransactionType needed.
--   3) The ONE genuine gap: Phase 10 was built full-dispatch-only
--      (UQ_Dispatches_Booking forced exactly one Dispatch row per Booking,
--      and fn_Dispatches_MatchesBooking/CK_Dispatches_MatchesBooking forced
--      a Dispatch's Quantity to EQUAL the Booking's full Quantity). Phase G
--      explicitly requires partial dispatch (spec section 12, with a worked
--      example), so this phase widens exactly these two things -- nothing
--      else on either table's schema changes:
--        a) Drops UQ_Dispatches_Booking so more than one Dispatch row can
--           reference the same Booking.
--        b) Relaxes fn_Dispatches_MatchesBooking's Quantity check from
--           strict equality to "> 0 and <= the Booking's full Quantity" --
--           a coarse database-level bound, mirroring the existing
--           CK_InternalTransfers_ConfirmedQuantity (Phase 15) pattern of a
--           loose CHECK backstop while the PRECISE "does this dispatch fit
--           within what's actually still remaining on the Booking right
--           now" validation happens in DispatchRepository.InsertAsync
--           under the Booking row's own UPDLOCK/HOLDLOCK (spec section 22
--           explicitly asks for the repository, not a CHECK constraint, to
--           perform that exact validation).
--   4) To track "how much of this Booking has been dispatched so far"
--      across possibly-multiple partial dispatches, dbo.PottedPlantBookings
--      gets ONE new maintained column, DispatchedQuantity (mirroring the
--      established "maintained running-total column" pattern already used
--      for CuttingStock.InTransitQuantity (Phase 16) and
--      PottedPlantStock.InTransitQuantity (Phase 18), rather than
--      recomputing a SUM(Dispatches.Quantity) on every read) -- this is the
--      ONLY new column this entire phase adds. RemainingQuantity
--      (Quantity - DispatchedQuantity) is a derived C# property on the
--      model, never a second stored column, per the established "no
--      redundant quantity columns" convention (Decision 15).
--   5) dbo.PottedPlantBookings.Status CHECK widened to add
--      'PartiallyDispatched' -- a Booking that has had SOME but not all of
--      its quantity dispatched. 'Pending' -> 'PartiallyDispatched' ->
--      'Dispatched' (or 'Pending'/'PartiallyDispatched' -> 'Cancelled').
--      No other Status value needed; Dispatches.Status ('Completed' /
--      'Cancelled', Phase 10) is unchanged -- each individual partial
--      dispatch is still simply Completed or Cancelled on its own row.
--   6) Permissions: NO new permission code. Outlet.View/.Confirm/.Sell and
--      Booking.View/.Enter/Dispatch.View/.Enter (all Phase 14) already
--      exist and are already granted to OutletSupervisor -- confirmed by
--      grep of Database/Phase14_RoleFoundation_AreaExtension.sql.
--   7) Customer master: NO new table. dbo.PottedPlantBookings already has
--      CustomerName/Address/Contact/StateId/DistrictId (Phase 9) and no
--      separate Customer master exists anywhere in this codebase (confirmed
--      by grep) -- reused exactly as-is.
--   8) Area authorization for Booking/Dispatch: enforced entirely in the
--      application layer (AreaAccessService, exactly like every other
--      phase since Phase B) -- NOT a database concern, so no schema change
--      is needed for it. See Pages/Production/PottedPlantBooking/*.cshtml.cs
--      and Pages/Production/Dispatch/*.cshtml.cs.
--
-- ADDITIVE, IDEMPOTENT, SAFE TO RE-RUN: every ALTER/constraint/column is
-- guarded with an existence check. No DROP TABLE, no DELETE, no data loss.
-- Does NOT modify Database/Phase16_CuttingWorkflow_ModelB.sql,
-- Phase18_MainOfficeGrowingPartnerIssue.sql,
-- Phase19_CuttingToPotProduction.sql, or Phase20_GrowingPartnerToOutlet.sql
-- in any way, and touches no Cutting/MainOfficeIssue/GrowingPartnerToOutlet
-- object.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- PART 1: dbo.PottedPlantBookings -- DispatchedQuantity + widened Status
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PottedPlantBookings') AND name = 'DispatchedQuantity')
BEGIN
    ALTER TABLE dbo.PottedPlantBookings
        ADD DispatchedQuantity DECIMAL(18,2) NOT NULL CONSTRAINT DF_PottedPlantBookings_DispatchedQuantity DEFAULT (0);
END
GO

-- Backfill: every pre-existing 'Dispatched' Booking (created under the old
-- full-dispatch-only Phase 10 flow) was, by definition, dispatched in full
-- -- its DispatchedQuantity must equal its Quantity so the new
-- CK_PottedPlantBookings_DispatchedQuantity constraint below is satisfied
-- and RemainingQuantity correctly reads 0 for it. Idempotent: once this has
-- run, DispatchedQuantity = Quantity, so the WHERE filter (which is
-- specifically about rows still at the column's just-added default of 0)
-- no longer matches and a second run is a no-op.
UPDATE dbo.PottedPlantBookings
SET DispatchedQuantity = Quantity
WHERE Status = 'Dispatched' AND DispatchedQuantity = 0;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedPlantBookings_DispatchedQuantity')
BEGIN
    ALTER TABLE dbo.PottedPlantBookings
        ADD CONSTRAINT CK_PottedPlantBookings_DispatchedQuantity
        CHECK (DispatchedQuantity >= 0 AND DispatchedQuantity <= Quantity);
END
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedPlantBookings_Status')
BEGIN
    ALTER TABLE dbo.PottedPlantBookings DROP CONSTRAINT CK_PottedPlantBookings_Status;
END
GO
ALTER TABLE dbo.PottedPlantBookings
    ADD CONSTRAINT CK_PottedPlantBookings_Status CHECK (Status IN ('Pending', 'PartiallyDispatched', 'Dispatched', 'Cancelled'));
GO


-- ----------------------------------------------------------------------------
-- PART 2: dbo.Dispatches -- allow more than one Dispatch row per Booking
-- ----------------------------------------------------------------------------
-- Drop the Phase 10 "exactly one Dispatch per Booking" constraint. The
-- plain (non-unique) IX_Dispatches_Booking index Phase 10 already created
-- separately is untouched, so lookups by PottedPlantBookingId remain fast.

IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_Dispatches_Booking')
BEGIN
    ALTER TABLE dbo.Dispatches DROP CONSTRAINT UQ_Dispatches_Booking;
END
GO

-- Relax the Quantity-must-equal-Booking.Quantity rule inside
-- fn_Dispatches_MatchesBooking to a coarse "> 0 and <= Booking.Quantity"
-- bound -- the PRECISE "fits within what's still remaining right now"
-- check happens in DispatchRepository.InsertAsync under the Booking row's
-- own UPDLOCK/HOLDLOCK (see header note 3b). The Species/PotSize/AreaId/
-- PottedPlantStockId identity match is preserved exactly as before.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Dispatches_MatchesBooking')
BEGIN
    ALTER TABLE dbo.Dispatches DROP CONSTRAINT CK_Dispatches_MatchesBooking;
END
GO
IF EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_Dispatches_MatchesBooking' AND type = 'FN')
BEGIN
    DROP FUNCTION dbo.fn_Dispatches_MatchesBooking;
END
GO
EXEC('
CREATE FUNCTION dbo.fn_Dispatches_MatchesBooking(@PottedPlantBookingId INT, @PottedPlantStockId INT, @SpeciesId INT, @PotSize NVARCHAR(50), @AreaId INT, @Quantity DECIMAL(18,2))
RETURNS BIT
AS
BEGIN
    DECLARE @Result BIT = 0;
    IF EXISTS (
        SELECT 1 FROM dbo.PottedPlantBookings
        WHERE Id = @PottedPlantBookingId
          AND PottedPlantStockId = @PottedPlantStockId
          AND SpeciesId = @SpeciesId
          AND PotSize = @PotSize
          AND (AreaId = @AreaId OR (AreaId IS NULL AND @AreaId IS NULL))
          AND @Quantity > 0
          AND @Quantity <= Quantity
    )
        SET @Result = 1;
    RETURN @Result;
END');
GO
ALTER TABLE dbo.Dispatches
    ADD CONSTRAINT CK_Dispatches_MatchesBooking
    CHECK (dbo.fn_Dispatches_MatchesBooking(PottedPlantBookingId, PottedPlantStockId, SpeciesId, PotSize, AreaId, Quantity) = 1);
GO

-- ============================================================================
-- End of Phase 21. Every EXISTING PottedPlantBooking/Dispatch row (of any
-- prior Status) is unaffected beyond the one intentional backfill above;
-- every EXISTING PottedPlantStock row, every EmptyPot/PottedPlant/Cutting/
-- MainOfficeIssue/GrowingPartnerToOutlet InternalTransfer row, and the
-- entire Phase 16 Cutting workflow / Phase 18 MainOfficeIssue workflow /
-- Phase 20 GrowingPartnerToOutlet workflow are completely untouched by this
-- script.
-- ============================================================================
