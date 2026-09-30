-- ============================================================================
-- CorrectCombinedSeasonalVarieties_2026-09-27.sql
--
-- Corrects 62 Seasonal Variety records (dbo.PlantSpecies, PlantTypeId = 4)
-- that were entered as one row combining multiple distinct series/variety
-- names with "/" (a mistake introduced by AddSeasonalVarieties_2026-09-27.sql,
-- which took each source spreadsheet row as one name even when the row
-- listed several distinct products). Splits each into one record per
-- individual name, per the user-approved before/after mapping in
-- claude_reports/SeasonalVarietyCorrection_Report_2026-09-27.md.
--
-- Of the resulting 154 individual names:
--   - 4 already exist as their own pre-existing record (from before
--     AddSeasonalVarieties ever ran) -- NOT re-inserted, left untouched:
--     Salvia Sahara Red (3164), Salvia Vista Mix (3111),
--     Verbena Quartz Mix (3167), Vinca Cora XDR Mix (3157).
--   - 4 are claimed by TWO different combined rows in the source data
--     (e.g. both "Coleus Wizard/Fareway Mix" and "Coleus Wizard / Kong /
--     Fairway" produce "Coleus Wizard") -- inserted ONCE, not twice.
--   - The remaining 152 are genuinely new INSERTs.
--   - Net: 152 new rows, 62 old rows removed. 199 - 62 + 152 = 289.
--
-- 2 of the 62 (Calendula officinalis Calypso Orange/Yellow, Marigold Inca -
-- Orange/Yellow/Gold) were colour listings where a literal "/" split would
-- produce a bare colour name with no series attached; corrected per explicit
-- user approval to repeat the series name with each colour (e.g. "Calendula
-- officinalis Calypso Orange" / "... Calypso Yellow").
--
-- FK dependency check (run again defensively below, inside the transaction,
-- immediately before the deletes): all 62 combined records were confirmed to
-- have ZERO dependent rows across all 24 FK columns that reference
-- PlantSpecies, in a prior investigation pass. If that has changed since
-- (e.g. someone started using one of these records), the check below THROWs
-- and the whole transaction rolls back -- no partial changes.
--
-- ReadyStockDays is carried over from the original combined record to every
-- new segment (matches the source's Finish-Days value, which applied to the
-- whole original row). Color/ScientificName stay NULL -- not supplied by the
-- source, nothing invented. No other table, and no other PlantType, is
-- touched.
--
-- SAFETY:
--   * Guarded to PlantsIMS2_Test only.
--   * Runs inside BEGIN TRANSACTION and ends with ROLLBACK TRANSACTION.
--   * Prints a full verification report before the rollback.
--   * Change ROLLBACK TRANSACTION -> COMMIT TRANSACTION at the bottom only
--     after reviewing that report, and only with explicit approval.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50295, 'CorrectCombinedSeasonalVarieties_2026-09-27.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION CorrectSeasonalVarieties;

BEGIN TRY

    DECLARE @DeleteIds TABLE (Id INT PRIMARY KEY);
    INSERT INTO @DeleteIds (Id) VALUES
    (3386),(3387),(3389),(3395),(3396),(3397),(3403),(3406),(3417),(3419),
    (3421),(3422),(3423),(3427),(3428),(3429),(3430),(3431),(3433),(3436),
    (3437),(3439),(3441),(3444),(3445),(3447),(3449),(3452),(3453),(3454),
    (3456),(3457),(3458),(3459),(3460),(3463),(3464),(3465),(3467),(3468),
    (3469),(3470),(3472),(3473),(3474),(3476),(3478),(3479),(3481),(3482),
    (3483),(3486),(3491),(3492),(3493),(3494),(3495),(3496),(3497),(3498),
    (3500),(3501);

    DECLARE @CountBefore INT = (SELECT COUNT(*) FROM dbo.PlantSpecies WHERE PlantTypeId = 4);
    DECLARE @DeleteIdsFound INT = (SELECT COUNT(*) FROM dbo.PlantSpecies p JOIN @DeleteIds d ON d.Id = p.Id WHERE p.PlantTypeId = 4);
    IF @DeleteIdsFound <> 62
        THROW 50290, 'Expected exactly 62 of the target Ids to exist under PlantTypeId=4 before deletion -- mismatch, aborting.', 1;

    ----------------------------------------------------------------------
    -- Defensive re-check: zero dependents on any of the 62 records, across
    -- every FK column in the database that references PlantSpecies. If
    -- anything now depends on one of these (e.g. concurrent app usage since
    -- the investigation), abort without deleting anything.
    ----------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM dbo.ActualCuttings WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'ActualCuttings now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.BookingBatchAllocations WHERE BookedSpeciesId IN (SELECT Id FROM @DeleteIds) OR ActualSpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'BookingBatchAllocations now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.Bookings WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'Bookings now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.CuttingDeliveries WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'CuttingDeliveries now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.CuttingPlans WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'CuttingPlans now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.CuttingStock WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'CuttingStock now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.Dispatches WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'Dispatches now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.Inventory WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'Inventory now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.MotherPlants WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'MotherPlants now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.OutletPurchases WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'OutletPurchases now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.PotProduction WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'PotProduction now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.PottedPlantBookings WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'PottedPlantBookings now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.PottedPlantStock WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'PottedPlantStock now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.PropagationBatches WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'PropagationBatches now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.ReadyStock WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'ReadyStock now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SeedCuttingBank WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'SeedCuttingBank now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SeedEntries WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'SeedEntries now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SeedIssues WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'SeedIssues now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SeedlingDispatchLines WHERE BookedSpeciesId IN (SELECT Id FROM @DeleteIds) OR ActualSpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'SeedlingDispatchLines now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SeedSowings WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'SeedSowings now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.SeedStock WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'SeedStock now references a record being corrected -- aborting.', 1;
    IF EXISTS (SELECT 1 FROM dbo.VendorPurchases WHERE SpeciesId IN (SELECT Id FROM @DeleteIds))
        THROW 50291, 'VendorPurchases now references a record being corrected -- aborting.', 1;

    ----------------------------------------------------------------------
    -- DELETE the 62 incorrectly-combined records
    ----------------------------------------------------------------------
    DELETE p FROM dbo.PlantSpecies p JOIN @DeleteIds d ON d.Id = p.Id;
    DECLARE @RowsDeleted INT = @@ROWCOUNT;
    IF @RowsDeleted <> 62
        THROW 50292, 'Expected to delete exactly 62 rows -- mismatch, aborting.', 1;

    ----------------------------------------------------------------------
    -- INSERT the 152 individual records (4 segments reuse an existing
    -- record and are correctly NOT listed here; 4 segments shared between
    -- two combined rows are listed only once)
    ----------------------------------------------------------------------
    INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ScientificName, ReadyStockDays, Color) VALUES
    (4, N'Begonia Semperflorens Bada Boom', NULL, 86, NULL),
    (4, N'Begonia Semperflorens Big Mix', NULL, 86, NULL),
    (4, N'Begonia tuberous Nonstop', NULL, 115, NULL),
    (4, N'Begonia tuberous Mocca Mix', NULL, 115, NULL),
    (4, N'Coleus Wizard', NULL, 50, NULL),
    (4, N'Coleus Fareway Mix', NULL, 50, NULL),
    (4, N'Coleus Kong', NULL, 50, NULL),
    (4, N'Coleus Jumbo', NULL, 50, NULL),
    (4, N'Cosmos Kosmik', NULL, 45, NULL),
    (4, N'Cosmos Ladybird Mix', NULL, 45, NULL),
    (4, N'Gazania Day Break', NULL, 71, NULL),
    (4, N'Gazania Kiss Mix', NULL, 71, NULL),
    (4, N'Geranium Ringo 2000', NULL, 92, NULL),
    (4, N'Geranium Bulls Eye Mix', NULL, 92, NULL),
    (4, N'Petunia Grandiflora Bravo', NULL, 64, NULL),
    (4, N'Petunia Grandiflora Supercascade Formula Mix', NULL, 64, NULL),
    (4, N'Petunia Grandiflora Frost', NULL, 64, NULL),
    (4, N'Petunia Grandiflora Picotee Mix', NULL, 64, NULL),
    (4, N'Petunia Trailing Easy Wave', NULL, 64, NULL),
    (4, N'Petunia Trailing Ramblin', NULL, 64, NULL),
    (4, N'Pentas Butterfly', NULL, 78, NULL),
    (4, N'Pentas Starla', NULL, 78, NULL),
    (4, N'Phlox Twinkle', NULL, 57, NULL),
    (4, N'Phlox Promise Mix', NULL, 57, NULL),
    (4, N'Rudbeckia Rustic', NULL, 71, NULL),
    (4, N'Rudbeckia Toto', NULL, 71, NULL),
    (4, N'Salvia Flamex 2000', NULL, 60, NULL),
    (4, N'Salvia Vista Red', NULL, 60, NULL),
    (4, N'Salvia Salsa', NULL, 60, NULL),
    (4, N'Strawberry Sweet Charley', NULL, 92, NULL),
    (4, N'Strawberry Winter Down', NULL, 92, NULL),
    (4, N'Stock Dwarf Mime', NULL, 60, NULL),
    (4, N'Stock Dwarf Hot Cakes Mix', NULL, 60, NULL),
    (4, N'Torenia Little Kiss Mix', NULL, 60, NULL),
    (4, N'Torenia Kauai Mix', NULL, 60, NULL),
    (4, N'Verbena Obsession', NULL, 71, NULL),
    (4, N'Vinca Tattoo Series', NULL, 64, NULL),
    (4, N'Zinnia Small Zahara', NULL, 47, NULL),
    (4, N'Zinnia Small Zydeco', NULL, 47, NULL),
    (4, N'Zinnia Small Profusion Double', NULL, 47, NULL),
    (4, N'Antirrhinum Snappy', NULL, 64, NULL),
    (4, N'Antirrhinum Twinny', NULL, 64, NULL),
    (4, N'Antirrhinum DoubleShot', NULL, 64, NULL),
    (4, N'Vinca Solar', NULL, 64, NULL),
    (4, N'Vinca Solar Avalanche', NULL, 64, NULL),
    (4, N'Vinca Heatwave', NULL, 64, NULL),
    (4, N'Cineraria Quicksilver', NULL, 78, NULL),
    (4, N'Cineraria Silverado', NULL, 78, NULL),
    (4, N'Dianthus Diana', NULL, 71, NULL),
    (4, N'Dianthus Divinity', NULL, 71, NULL),
    (4, N'Dianthus Chiba', NULL, 71, NULL),
    (4, N'Dianthus Supra', NULL, 71, NULL),
    (4, N'Dianthus Elegance', NULL, 71, NULL),
    (4, N'Geranium Nano', NULL, 92, NULL),
    (4, N'Geranium Apache', NULL, 92, NULL),
    (4, N'Petunia Limbo GP', NULL, 64, NULL),
    (4, N'Petunia Mambo GP', NULL, 64, NULL),
    (4, N'Petunia Shake', NULL, 64, NULL),
    (4, N'Petunia Tango', NULL, 64, NULL),
    (4, N'Petunia Lambada', NULL, 64, NULL),
    (4, N'Salvia Zenith', NULL, 60, NULL),
    (4, N'Salvia Reddy', NULL, 60, NULL),
    (4, N'Salvia Amore', NULL, 60, NULL),
    (4, N'Salvia Red Alert', NULL, 60, NULL),
    (4, N'Salvia Red Hill', NULL, 60, NULL),
    (4, N'Verbena Purple Haze', NULL, 71, NULL),
    (4, N'Verbena Dazzling Nights', NULL, 71, NULL),
    (4, N'Viola Corina', NULL, 64, NULL),
    (4, N'Viola Cello', NULL, 64, NULL),
    (4, N'Viola Xtrada', NULL, 64, NULL),
    (4, N'Viola Trumpet', NULL, 64, NULL),
    (4, N'Petunia Wave', NULL, 64, NULL),
    (4, N'Petunia Easy Wave', NULL, 64, NULL),
    (4, N'Petunia Shock Wave', NULL, 64, NULL),
    (4, N'Petunia Tidal Wave', NULL, 64, NULL),
    (4, N'Petunia Ez Rider', NULL, 64, NULL),
    (4, N'Petunia Dreams', NULL, 64, NULL),
    (4, N'Petunia Daddy', NULL, 64, NULL),
    (4, N'Petunia Double Madness', NULL, 64, NULL),
    (4, N'Vinca Pacifica XP', NULL, 64, NULL),
    (4, N'Vinca Tattoo', NULL, 64, NULL),
    (4, N'Vinca Titan', NULL, 64, NULL),
    (4, N'Vinca Valiant', NULL, 64, NULL),
    (4, N'Pansy Cool Wave', NULL, 64, NULL),
    (4, N'Pansy Matrix', NULL, 64, NULL),
    (4, N'Pansy Panola', NULL, 64, NULL),
    (4, N'Coleus Fairway', NULL, 50, NULL),
    (4, N'Dianthus Ideal Select', NULL, 71, NULL),
    (4, N'Dianthus Super Parfait', NULL, 71, NULL),
    (4, N'Zinnia Zahara', NULL, 47, NULL),
    (4, N'Zinnia Double Zahara', NULL, 47, NULL),
    (4, N'Marigold Inca II', NULL, 47, NULL),
    (4, N'Marigold Antigua', NULL, 47, NULL),
    (4, N'Marigold Durango', NULL, 47, NULL),
    (4, N'Salvia Vista', NULL, 60, NULL),
    (4, N'Begonia Semperflorens Ambassador', NULL, 85, NULL),
    (4, N'Begonia Semperflorens Senator IQ', NULL, 85, NULL),
    (4, N'Begonia Semperflorens Emperor', NULL, 85, NULL),
    (4, N'Begonia Viking', NULL, 85, NULL),
    (4, N'Begonia Viking XL', NULL, 85, NULL),
    (4, N'Celosia Kimono', NULL, 50, NULL),
    (4, N'Celosia Century', NULL, 50, NULL),
    (4, N'Celosia Dragon''s Breath', NULL, 50, NULL),
    (4, N'Celosia Flamma', NULL, 50, NULL),
    (4, N'Coleus Rainbow', NULL, 50, NULL),
    (4, N'Dianthus Diamond', NULL, 71, NULL),
    (4, N'Dianthus Lillipot', NULL, 71, NULL),
    (4, N'Gomphrena Gnome', NULL, 50, NULL),
    (4, N'Gomphrena Pinball', NULL, 50, NULL),
    (4, N'Gomphrena Ping Pong', NULL, 50, NULL),
    (4, N'Marigold Proud Mari', NULL, 47, NULL),
    (4, N'Marigold Coco', NULL, 47, NULL),
    (4, N'Pansy Crown', NULL, 64, NULL),
    (4, N'Pansy Ultima', NULL, 64, NULL),
    (4, N'Vinca Victory', NULL, 64, NULL),
    (4, N'Vinca Virtuosa', NULL, 64, NULL),
    (4, N'Zinnia Profusion', NULL, 47, NULL),
    (4, N'Zinnia Profusion Double', NULL, 47, NULL),
    (4, N'Antirrhinum Chantilly', NULL, 64, NULL),
    (4, N'Antirrhinum Legend Double', NULL, 64, NULL),
    (4, N'Antirrhinum Statement', NULL, 64, NULL),
    (4, N'Celosia Armor', NULL, 50, NULL),
    (4, N'Celosia Castle', NULL, 50, NULL),
    (4, N'Petunia Evening Scentsation', NULL, 64, NULL),
    (4, N'Petunia Opera Supreme', NULL, 64, NULL),
    (4, N'Petunia Trilogy', NULL, 64, NULL),
    (4, N'Salvia Hummingbird', NULL, 60, NULL),
    (4, N'Salvia Summer Jewel', NULL, 60, NULL),
    (4, N'Sunflower Big Smile', NULL, 40, NULL),
    (4, N'Sunflower Smiley', NULL, 40, NULL),
    (4, N'Sunflower Sunny Smile', NULL, 40, NULL),
    (4, N'Zinnia Dreamland', NULL, 47, NULL),
    (4, N'Zinnia Preciosa', NULL, 47, NULL),
    (4, N'Zinnia Belize', NULL, 47, NULL),
    (4, N'Vinca Cora XDR', NULL, 64, NULL),
    (4, N'Vinca Blockbuster', NULL, 64, NULL),
    (4, N'Pentas BeeBright', NULL, 78, NULL),
    (4, N'Pentas Beehive', NULL, 78, NULL),
    (4, N'Petunia Ramblin', NULL, 64, NULL),
    (4, N'Petunia Picobella', NULL, 64, NULL),
    (4, N'Petunia Sanguna seed lines', NULL, 64, NULL),
    (4, N'Pansy Delta', NULL, 64, NULL),
    (4, N'Pansy Colossus', NULL, 64, NULL),
    (4, N'Marigold Marvel', NULL, 47, NULL),
    (4, N'Marigold Bonanza', NULL, 47, NULL),
    (4, N'Salvia Sahara', NULL, 60, NULL),
    (4, N'Salvia Mojave', NULL, 60, NULL),
    (4, N'Calendula officinalis Calypso Orange', NULL, 57, NULL),
    (4, N'Calendula officinalis Calypso Yellow', NULL, 57, NULL),
    (4, N'Marigold Inca Orange', NULL, 47, NULL),
    (4, N'Marigold Inca Yellow', NULL, 47, NULL),
    (4, N'Marigold Inca Gold', NULL, 47, NULL);

    DECLARE @RowsInserted INT = @@ROWCOUNT;
    DECLARE @CountAfter INT = (SELECT COUNT(*) FROM dbo.PlantSpecies WHERE PlantTypeId = 4);

    ----------------------------------------------------------------------
    -- VERIFICATION -- before the ROLLBACK, so it shows what WOULD happen
    ----------------------------------------------------------------------
    PRINT '=== Correction summary ===';
    SELECT @CountBefore AS Before, @RowsDeleted AS Deleted, @RowsInserted AS Inserted, @CountAfter AS After,
           (@CountBefore - @RowsDeleted + @RowsInserted) AS Expected;
    IF @RowsInserted <> 152
        THROW 50293, 'Expected exactly 152 rows inserted -- mismatch, aborting.', 1;
    IF @CountAfter <> @CountBefore - 62 + 152
        THROW 50294, 'Final count does not match Before - 62 + 152 -- aborting.', 1;

    PRINT '=== All 62 old combined names confirmed GONE ===';
    SELECT COUNT(*) AS StillPresent_ShouldBe0 FROM dbo.PlantSpecies p JOIN @DeleteIds d ON d.Id = p.Id;

    PRINT '=== Duplicate check: any Name appearing more than once under PlantTypeId=4? (should return nothing) ===';
    SELECT Name, COUNT(*) AS Occurrences FROM dbo.PlantSpecies WHERE PlantTypeId = 4 GROUP BY Name HAVING COUNT(*) > 1;
    PRINT '(no rows above = clean)';

    PRINT '=== The 4 reuse cases (pre-existing records) confirmed untouched ===';
    SELECT Id, Name, ReadyStockDays FROM dbo.PlantSpecies
    WHERE Id IN (3164, 3111, 3167, 3157) ORDER BY Id;

    PRINT '=== The 4 records from the FIRST task (skipped as already-existing) still untouched ===';
    SELECT Id, Name, ReadyStockDays FROM dbo.PlantSpecies
    WHERE Id IN (3166, 3163, 3094, 3084) ORDER BY Id;

    PRINT '=== Other PlantTypes (1,2,3) unchanged ===';
    SELECT PlantTypeId, COUNT(*) AS SpeciesCount FROM dbo.PlantSpecies WHERE PlantTypeId IN (1,2,3) GROUP BY PlantTypeId ORDER BY PlantTypeId;

    PRINT '=== Sample of newly-created individual records ===';
    SELECT TOP 10 Id, Name, ReadyStockDays FROM dbo.PlantSpecies
    WHERE Name IN (N'Vinca Pacifica XP', N'Vinca Tattoo', N'Vinca Titan', N'Vinca Valiant',
                   N'Calendula officinalis Calypso Orange', N'Marigold Inca Gold')
    ORDER BY Name;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CorrectSeasonalVarieties;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-09-27: user reviewed the dry-run verification output and
-- explicitly approved committing this exact script. Verified backup in
-- place: PlantsIMS2_Test_PreSeasonalVarietiesCorrection_20260927_223452.bak.
----------------------------------------------------------------------------
IF XACT_STATE() = 1 COMMIT TRANSACTION CorrectSeasonalVarieties;
ELSE IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CorrectSeasonalVarieties;
GO
