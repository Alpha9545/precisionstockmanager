-- ============================================================================
-- AddSeasonalVarieties_2026-09-27.sql
--
-- Adds the 120 missing "Seasonal Variety" master records (dbo.PlantSpecies
-- rows under PlantTypeId = 4, "SEASONAL VARITIES") from the uploaded
-- Crop / Series-Variety / Finish-Days list, using the app's own naming
-- convention already in use for this PlantType: Name = "<Crop> <Series/
-- Variety>" (confirmed against existing rows, e.g. Id 3106 = "Antirrhinum
-- Snappy Mix", Id 3084 = "Vinca Pacifica Mix").
--
-- 124 source rows were compared (case-insensitive, whitespace-normalized)
-- against the 79 existing rows under PlantTypeId = 4:
--   - 4 already exist and are SKIPPED: Cineraria Early Perfection Mix
--     (Id 3166), Dianthus Ideal Mix (Id 3163), Gomphrena Buddy Purple
--     (Id 3094), Vinca Pacifica Mix (Id 3084).
--   - 0 duplicate generated names within the source list itself.
--   - 120 rows are new and are inserted below.
--
-- Column values follow PlantSpeciesRepository.AddPlantSpecies's own
-- convention exactly: PlantTypeId = 4, Name = trimmed "Crop Variety",
-- ScientificName = NULL (not supplied by the source, never invented),
-- ReadyStockDays = the source's "Total Seed->3in Finish Days" (an int,
-- matches the column's existing use for the same PlantType, e.g. Id 3106
-- ReadyStockDays=90), Color = NULL (not supplied by the source).
--
-- No existing row is touched, updated or deleted. No other table is
-- touched. Only INSERTs, only into dbo.PlantSpecies.
--
-- SAFETY:
--   * Guarded to PlantsIMS2_Test only.
--   * Runs inside BEGIN TRANSACTION and ends with ROLLBACK TRANSACTION.
--   * Prints a verification report before the rollback.
--   * Change ROLLBACK TRANSACTION -> COMMIT TRANSACTION at the bottom only
--     after reviewing that report, and only with explicit approval.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50298, 'AddSeasonalVarieties_2026-09-27.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION AddSeasonalVarieties;

BEGIN TRY

    DECLARE @CountBefore INT = (SELECT COUNT(*) FROM dbo.PlantSpecies WHERE PlantTypeId = 4);

    INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ScientificName, ReadyStockDays, Color) VALUES
    (4, N'Alyssum Fields Mix', NULL, 55, NULL),
    (4, N'Antirrhinum Chimes Mix', NULL, 70, NULL),
    (4, N'Aster New Double Mix', NULL, 62, NULL),
    (4, N'Begonia Semperflorens Bada Boom/Big Mix', NULL, 86, NULL),
    (4, N'Begonia tuberous Nonstop / Mocca Mix', NULL, 115, NULL),
    (4, N'Flowering Cabbage Ornamental Kale', NULL, 69, NULL),
    (4, N'Calendula officinalis Calypso Orange/Yellow', NULL, 57, NULL),
    (4, N'Celosia Plumosa Kimono Mix', NULL, 50, NULL),
    (4, N'Celosia Cristata Amigo Mix', NULL, 50, NULL),
    (4, N'Chrysanthemum Deconova Multiflora', NULL, 64, NULL),
    (4, N'Chrysanthemum Deconova Grandiflora', NULL, 64, NULL),
    (4, N'Cineraria Silver Dust', NULL, 76, NULL),
    (4, N'Coleus Wizard/Fareway Mix', NULL, 50, NULL),
    (4, N'Coleus Kong / Jumbo', NULL, 50, NULL),
    (4, N'Cosmos Kosmik / Ladybird Mix', NULL, 45, NULL),
    (4, N'Carnation Lilliput Mix', NULL, 93, NULL),
    (4, N'Dahlia Big Flowers Mix', NULL, 57, NULL),
    (4, N'Dahlia Fresco Mix', NULL, 57, NULL),
    (4, N'Dianthus Super Parfait Series', NULL, 71, NULL),
    (4, N'Gloxinia Double Brocade Mix', NULL, 113, NULL),
    (4, N'Gazania Day Break/Kiss Mix', NULL, 71, NULL),
    (4, N'Gerbera Mega Revolution', NULL, 113, NULL),
    (4, N'Gaillardia Seedling', NULL, 71, NULL),
    (4, N'Geranium Ringo 2000/Bulls Eye Mix', NULL, 92, NULL),
    (4, N'Geranium Super', NULL, 92, NULL),
    (4, N'Hypoestes Splash Mix', NULL, 50, NULL),
    (4, N'Impatiens walleriana Imara XDR Mix', NULL, 64, NULL),
    (4, N'Impatiens New Guinea', NULL, 64, NULL),
    (4, N'Kochia Burning Blush', NULL, 50, NULL),
    (4, N'Lobelia Crystal', NULL, 71, NULL),
    (4, N'Melampodium Lemon Delight', NULL, 50, NULL),
    (4, N'Osteospermum Mix', NULL, 78, NULL),
    (4, N'Ornamental Chilli Dwarf Mix', NULL, 78, NULL),
    (4, N'Petunia Double Double Duo Series', NULL, 64, NULL),
    (4, N'Petunia Grandiflora Bravo/Supercascade Formula Mix', NULL, 64, NULL),
    (4, N'Petunia Grandiflora Ultra Star Mix', NULL, 64, NULL),
    (4, N'Petunia Grandiflora Frost/Picotee Mix', NULL, 64, NULL),
    (4, N'Petunia Grandiflora Colourwise', NULL, 64, NULL),
    (4, N'Petunia Trailing Easy Wave / Ramblin', NULL, 64, NULL),
    (4, N'Pentas Butterfly / Starla', NULL, 78, NULL),
    (4, N'Phlox Twinkle/Promise Mix', NULL, 57, NULL),
    (4, N'Platycodon Pop Star Mix', NULL, 99, NULL),
    (4, N'Portulaca Happy Hour', NULL, 44, NULL),
    (4, N'Pansy Super Majestic Giants 2', NULL, 64, NULL),
    (4, N'Rudbeckia Rustic / Toto', NULL, 71, NULL),
    (4, N'Salvia Flamex 2000/Vista Red/Sahara Red', NULL, 60, NULL),
    (4, N'Salvia Salsa/Vista Mix', NULL, 60, NULL),
    (4, N'Strawberry Sweet Charley/Winter Down', NULL, 92, NULL),
    (4, N'Stock Dwarf Mime/Hot Cakes Mix', NULL, 60, NULL),
    (4, N'Sunflower Teddy Bear', NULL, 40, NULL),
    (4, N'Marigold Inca - Orange/Yellow/Gold', NULL, 47, NULL),
    (4, N'Marigold Vanilla (White)', NULL, 47, NULL),
    (4, N'Marigold French Hot Pack Mix', NULL, 47, NULL),
    (4, N'Torenia Little Kiss Mix / Kauai Mix', NULL, 60, NULL),
    (4, N'Verbena Quartz Mix/Obsession', NULL, 71, NULL),
    (4, N'Vinca Pacifica Red', NULL, 64, NULL),
    (4, N'Vinca Cora XDR Mix / Tattoo Series', NULL, 64, NULL),
    (4, N'Vinca Valiant Mix', NULL, 64, NULL),
    (4, N'Zinnia Small Zahara/Zydeco/Profusion Double', NULL, 47, NULL),
    (4, N'Zinnia Dreamland Mix', NULL, 47, NULL),
    (4, N'Zinnia New Preciosa Mix', NULL, 47, NULL),
    (4, N'Antirrhinum Snappy / Twinny / DoubleShot', NULL, 64, NULL),
    (4, N'Vinca Solar / Solar Avalanche / Heatwave', NULL, 64, NULL),
    (4, N'Celosia Arrabona', NULL, 50, NULL),
    (4, N'Cineraria Quicksilver / Silverado', NULL, 78, NULL),
    (4, N'Cosmos Casanova', NULL, 47, NULL),
    (4, N'Dianthus Diana / Divinity / Chiba / Supra / Elegance', NULL, 71, NULL),
    (4, N'Gazania Enorma', NULL, 71, NULL),
    (4, N'Impatiens Balance', NULL, 64, NULL),
    (4, N'Geranium Nano / Apache', NULL, 92, NULL),
    (4, N'Petunia Limbo GP / Mambo GP / Shake / Tango / Lambada', NULL, 64, NULL),
    (4, N'Salvia Zenith / Reddy / Amore / Red Alert / Red Hill', NULL, 60, NULL),
    (4, N'Marigold French Chica', NULL, 47, NULL),
    (4, N'Verbena Purple Haze / Dazzling Nights', NULL, 71, NULL),
    (4, N'Viola Corina / Cello / Xtrada / Trumpet', NULL, 64, NULL),
    (4, N'Petunia Wave / Easy Wave / Shock Wave / Tidal Wave / Ez Rider / Dreams / Daddy / Double Madness', NULL, 64, NULL),
    (4, N'Vinca Pacifica XP / Tattoo / Titan / Valiant', NULL, 64, NULL),
    (4, N'Pansy Cool Wave / Matrix / Panola', NULL, 64, NULL),
    (4, N'Viola Sorbet XP', NULL, 64, NULL),
    (4, N'Pentas Lucky Star', NULL, 78, NULL),
    (4, N'Coleus Wizard / Kong / Fairway', NULL, 50, NULL),
    (4, N'Dianthus Ideal Select / Super Parfait', NULL, 71, NULL),
    (4, N'Zinnia Zahara / Double Zahara', NULL, 47, NULL),
    (4, N'Torenia Kauai', NULL, 60, NULL),
    (4, N'Marigold Inca II / Antigua / Durango', NULL, 47, NULL),
    (4, N'Salvia Vista / Salsa', NULL, 60, NULL),
    (4, N'Begonia Semperflorens Ambassador / Senator IQ / Emperor', NULL, 85, NULL),
    (4, N'Begonia Viking / Viking XL', NULL, 85, NULL),
    (4, N'Calendula Calypso', NULL, 57, NULL),
    (4, N'Celosia Kimono / Century / Dragon''s Breath / Flamma', NULL, 50, NULL),
    (4, N'Coleus Fairway / Rainbow', NULL, 50, NULL),
    (4, N'Dianthus Diamond / Lillipot', NULL, 71, NULL),
    (4, N'Gerbera Majorette', NULL, 113, NULL),
    (4, N'Gomphrena Gnome / Pinball / Ping Pong', NULL, 50, NULL),
    (4, N'Hypoestes Confetti Compact', NULL, 50, NULL),
    (4, N'Marigold Proud Mari / Coco', NULL, 47, NULL),
    (4, N'Pansy Crown / Ultima', NULL, 64, NULL),
    (4, N'Petunia Puffin', NULL, 64, NULL),
    (4, N'Vinca Victory / Virtuosa', NULL, 64, NULL),
    (4, N'Zinnia Profusion / Profusion Double', NULL, 47, NULL),
    (4, N'Antirrhinum Chantilly / Legend Double / Statement', NULL, 64, NULL),
    (4, N'Begonia Fiona', NULL, 85, NULL),
    (4, N'Calendula Zen', NULL, 57, NULL),
    (4, N'Celosia Armor / Castle', NULL, 50, NULL),
    (4, N'Coleus Giant Exhibition', NULL, 50, NULL),
    (4, N'Dianthus Telstar', NULL, 71, NULL),
    (4, N'Gomphrena Audray', NULL, 50, NULL),
    (4, N'Lobelia Aqua', NULL, 71, NULL),
    (4, N'Petunia Evening Scentsation / Opera Supreme / Trilogy', NULL, 64, NULL),
    (4, N'Salvia Hummingbird / Summer Jewel', NULL, 60, NULL),
    (4, N'Sunflower Big Smile / Smiley / Sunny Smile', NULL, 40, NULL),
    (4, N'Zinnia Dreamland / Preciosa / Belize', NULL, 47, NULL),
    (4, N'Vinca Cora XDR / Blockbuster', NULL, 64, NULL),
    (4, N'Pentas BeeBright / Beehive', NULL, 78, NULL),
    (4, N'Petunia Ramblin / Picobella / Sanguna seed lines', NULL, 64, NULL),
    (4, N'Pansy Delta / Colossus', NULL, 64, NULL),
    (4, N'Viola Penny', NULL, 64, NULL),
    (4, N'Marigold Marvel / Bonanza', NULL, 47, NULL),
    (4, N'Salvia Sahara / Mojave', NULL, 60, NULL),
    (4, N'Impatiens Imara XDR', NULL, 64, NULL);

    DECLARE @RowsInserted INT = @@ROWCOUNT;
    DECLARE @CountAfter INT = (SELECT COUNT(*) FROM dbo.PlantSpecies WHERE PlantTypeId = 4);

    ----------------------------------------------------------------------
    -- VERIFICATION -- before the ROLLBACK, so it shows what WOULD happen
    ----------------------------------------------------------------------
    PRINT '=== Insert summary ===';
    SELECT @CountBefore AS SeasonalVarietiesBefore, @RowsInserted AS RowsInserted, @CountAfter AS SeasonalVarietiesAfter;
    IF @CountAfter - @CountBefore <> 120
        THROW 50296, 'Expected exactly 120 new rows under PlantTypeId=4 -- row count mismatch, aborting.', 1;
    IF @RowsInserted <> 120
        THROW 50297, 'Expected exactly 120 rows inserted -- @@ROWCOUNT mismatch, aborting.', 1;

    PRINT '=== Duplicate check: any Name now appearing more than once under PlantTypeId=4? (should return nothing) ===';
    SELECT Name, COUNT(*) AS Occurrences
    FROM dbo.PlantSpecies
    WHERE PlantTypeId = 4
    GROUP BY Name
    HAVING COUNT(*) > 1;

    PRINT '=== The 4 pre-existing rows this script intentionally skipped -- confirm still exactly 1 row each, unmodified ===';
    SELECT Id, Name, ReadyStockDays FROM dbo.PlantSpecies
    WHERE PlantTypeId = 4
      AND Name IN (N'Cineraria Early Perfection Mix', N'Dianthus Ideal Mix', N'Gomphrena Buddy Purple', N'Vinca Pacifica Mix')
    ORDER BY Name;

    PRINT '=== Other PlantTypes (1,2,3) unchanged ===';
    SELECT PlantTypeId, COUNT(*) AS SpeciesCount FROM dbo.PlantSpecies WHERE PlantTypeId IN (1,2,3) GROUP BY PlantTypeId ORDER BY PlantTypeId;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION AddSeasonalVarieties;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-09-27: user reviewed the dry-run verification output and
-- explicitly approved committing this exact script. Verified backup in
-- place: PlantsIMS2_Test_PreSeasonalVarieties_20260927_220338.bak.
----------------------------------------------------------------------------
IF XACT_STATE() = 1 COMMIT TRANSACTION AddSeasonalVarieties;
ELSE IF XACT_STATE() <> 0 ROLLBACK TRANSACTION AddSeasonalVarieties;
GO
