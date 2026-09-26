-- ============================================================================
-- Phase G: fixes a gap Phase F missed. Approved Change 3 ("allow a larger
-- Ready Stock approval than the sowing's original quantity") relaxed the
-- application code (Services/DirectSowingRules.cs) and the three
-- dbo.SeedSowings CHECK constraints (Phase F), but a SEPARATE, earlier
-- trigger on dbo.ReadyConfirmations (Database/PhaseB_ReadyStockTrays.sql)
-- still independently re-enforced the OLD cap and was never touched --
-- confirmed live in Phase 2 testing (error 50124 on a real overage attempt).
-- ============================================================================
-- TARGET: PlantsIMS2_Test ONLY.
--
-- dbo.TR_ReadyConfirmations_TrayQuantity (AFTER INSERT on dbo.ReadyConfirmations):
--   - Removes the "ActualTrayQuantity > sw.NumberOfTrays" cap.
--   - Corrects the wastage check from an exact "QuantitySown - ConfirmedQuantity"
--     (which goes negative in an overage, and which never subtracted any
--     PRIOR partial approval's ConfirmedReadyQuantity/WastageQuantity in the
--     first place) to MAX(0, remaining-still-expected - this-event's-
--     ConfirmedQuantity), where remaining-still-expected accounts for
--     dbo.SeedSowings' OWN ConfirmedReadyQuantity/WastageQuantity as they
--     stand immediately before this INSERT (this trigger fires before the
--     application's own UPDATE dbo.SeedSowings in the same transaction) --
--     exactly the same arithmetic Services/DirectSowingRules.ComputeApproval
--     already uses. This also makes the trigger correct for a sowing with a
--     prior partial approval, not just a fresh one.
--   - Everything else unchanged: ActualTrayQuantity/NumberOfTrays still
--     required, ConfirmedQuantity must still equal whole trays x the
--     sowing's own cavity, still AFTER INSERT only (ReadyConfirmations rows
--     are otherwise immutable via other means).
--
-- Existing data: no existing row is changed -- this only widens what a
-- FUTURE INSERT is allowed to contain.
-- ============================================================================

IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50299, 'PhaseG_ReadyConfirmationTrayOverage.sql may only be run against PlantsIMS2_Test.', 1;
GO

CREATE OR ALTER TRIGGER dbo.TR_ReadyConfirmations_TrayQuantity
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- Seedlings come only from complete trays of the sowing's own cavity;
    -- wastage is whatever of the sowing's still-expected quantity did not
    -- become a ready seedling THIS approval (never negative -- an overage
    -- against the original sowing quantity is a legitimate outcome and
    -- produces zero wastage, matching DirectSowingRules.ComputeApproval).
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE i.ActualTrayQuantity IS NULL
                  OR sw.NumberOfTrays IS NULL
                  OR i.ConfirmedQuantity <> i.ActualTrayQuantity * CASE sw.CavityType WHEN N'9 Cavity' THEN 9 WHEN N'24 Cavity' THEN 24
                                                                                      WHEN N'42 Cavity' THEN 42 WHEN N'102 Cavity' THEN 102
                                                                                      WHEN N'150 Cavity' THEN 150 END
                  OR i.WastageQuantity <> (CASE WHEN (sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity - i.ConfirmedQuantity) > 0
                                                 THEN (sw.QuantitySown - sw.ConfirmedReadyQuantity - sw.WastageQuantity - i.ConfirmedQuantity)
                                                 ELSE 0 END))
        THROW 50124, 'Approval must be whole Actual Ready Trays; seedlings = trays x the sowing cavity; wastage = the sowing''s still-expected quantity minus seedlings this approval, never negative.', 1;
END
GO
