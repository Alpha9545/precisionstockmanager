/*
    Migration: Add an optional PolyhouseId to dbo.PotProductionBatches
    Database : PlantsIMS2 (and PlantsIMS2_Test)
    Notes    : Safe to run multiple times (checks for column/FK existence first).
               Does NOT drop or modify any existing column/data.
               Nullable, so every existing batch row remains valid as-is.

    Why: Pot Production never tracked Polyhouse at all (unlike SeedSowings,
    which already has SeedSowings.PolyhouseId from Phase B). This adds the
    same optional Area -> Polyhouse relationship to Pot Production batches,
    enforced the same way (Data/PotBatchRepository.CreateAsync reuses
    Services/DirectSowingRules.ResolveGrowingLocation to reject a Polyhouse
    that does not belong to the batch's own Area). Not applied to
    PottedPlantStock: that table is pooled by (SpeciesId, PotSize, AreaId)
    only, and splitting it further by Polyhouse would change reservation/
    dispatch behavior that Booking/Outlet Sales already depend on -- out of
    scope for this fix.

    NOT executed against any database by this script's author. Run manually,
    against PlantsIMS2_Test first, only after explicit approval.
*/

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.PotProductionBatches') AND name = 'PolyhouseId'
)
BEGIN
    ALTER TABLE dbo.PotProductionBatches
        ADD PolyhouseId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PotProductionBatches_Polyhouse')
BEGIN
    ALTER TABLE dbo.PotProductionBatches WITH CHECK
        ADD CONSTRAINT FK_PotProductionBatches_Polyhouse FOREIGN KEY (PolyhouseId) REFERENCES dbo.Polyhouses (Id);
END
GO
