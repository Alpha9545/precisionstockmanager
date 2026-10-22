-- ============================================================================
-- 01_DeleteChrysanthemumSeedStock_UnusedOnly.sql   (2026-10-17, live variant)
--
-- Deletes ONLY the unused CHRYSANTHEMUM Seed Stock lots, and their 'StockIn' ledger rows.
-- A lot is deleted only if ALL are true:
--   * no Seed Sowing, Bed Sowing or Seed Issue points at it
--   * nothing in transit
--   * its only ledger rows are 'StockIn'
-- Every other lot is left exactly as it is (listed as SKIP). Varieties (PlantSpecies) are NOT touched.
-- Re-run safe: once the lots are gone it finds nothing and writes nothing.
--
-- MODE=PREVIEW  read-only.   MODE=COMMIT  one transaction, any error rolls back.
-- Run: sqlcmd ... -v MODE=PREVIEW TARGET_SERVER=srv1202245 -i 01_DeleteChrysanthemumSeedStock_UnusedOnly.sql
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 53201, 'This script may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'PREVIEW', N'COMMIT')
    THROW 53202, 'Run with sqlcmd -v MODE=PREVIEW or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 53203, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

DECLARE @PlantTypeId INT, @TypeMatches INT;
SELECT @TypeMatches = COUNT(*), @PlantTypeId = MAX(Id)
FROM PlantTypes WHERE UPPER(REPLACE(Name, ' ', '')) = N'CHRYSANTHEMUM';
IF @TypeMatches <> 1
    THROW 53210, 'Expected exactly one plant type named CHRYSANTHEMUM -- nothing written.', 1;
PRINT 'Plant type: CHRYSANTHEMUM (Id ' + CAST(@PlantTypeId AS VARCHAR) + ')';

DECLARE @Lots TABLE (SeedStockId INT PRIMARY KEY, Variety NVARCHAR(200), BatchNo NVARCHAR(50), Unit NVARCHAR(20),
                     Physical DECIMAL(18,2), InTransit DECIMAL(18,2), Available DECIMAL(18,2),
                     LedgerRows INT, OtherLedgerRows INT, Sowings INT, BedSowings INT, Issues INT, Status NVARCHAR(200));
INSERT @Lots (SeedStockId, Variety, BatchNo, Unit, Physical, InTransit, Available, LedgerRows, OtherLedgerRows, Sowings, BedSowings, Issues)
SELECT ss.Id, sp.Name, ss.BatchNo, ss.Unit, ss.PhysicalQuantity, ss.InTransitQuantity, ss.AvailableQuantity,
       (SELECT COUNT(*) FROM SeedStockTransactions t WHERE t.SeedStockId = ss.Id),
       (SELECT COUNT(*) FROM SeedStockTransactions t WHERE t.SeedStockId = ss.Id AND t.TransactionType <> N'StockIn'),
       (SELECT COUNT(*) FROM SeedSowings x WHERE x.SourceSeedStockId = ss.Id),
       (SELECT COUNT(*) FROM BedSowings x WHERE x.SeedStockId = ss.Id),
       (SELECT COUNT(*) FROM SeedIssues x WHERE x.SourceSeedStockId = ss.Id)
FROM SeedStock ss
JOIN PlantSpecies sp ON sp.Id = ss.SpeciesId
WHERE sp.PlantTypeId = @PlantTypeId;

UPDATE @Lots SET Status = CASE
    WHEN Sowings + BedSowings + Issues > 0 THEN N'SKIP: used by a sowing / issue'
    WHEN InTransit <> 0 THEN N'SKIP: seeds in transit'
    WHEN OtherLedgerRows > 0 THEN N'SKIP: has movements other than StockIn'
    ELSE N'DELETE' END;

SELECT SeedStockId, Variety, BatchNo, Unit, Physical, InTransit, Available, LedgerRows, Sowings, BedSowings, Issues, Status
FROM @Lots WHERE Status = N'DELETE' ORDER BY Variety, SeedStockId;

DECLARE @Skipped INT = (SELECT COUNT(*) FROM @Lots WHERE Status <> N'DELETE');
PRINT 'Lots left untouched (SKIP): ' + CAST(@Skipped AS VARCHAR);

DELETE FROM @Lots WHERE Status <> N'DELETE';

DECLARE @LotCount INT = (SELECT COUNT(*) FROM @Lots),
        @Seeds DECIMAL(18,2) = (SELECT ISNULL(SUM(Physical), 0) FROM @Lots),
        @LedgerCount INT = (SELECT ISNULL(SUM(LedgerRows), 0) FROM @Lots);
PRINT 'Unused CHRYSANTHEMUM seed lots to delete: ' + CAST(@LotCount AS VARCHAR) + ' (' + CAST(@Seeds AS VARCHAR) + ' in stock, '
    + CAST(@LedgerCount AS VARCHAR) + ' ledger rows)';

IF @LotCount = 0
BEGIN
    PRINT '=== Nothing to delete. ===';
    RETURN;
END

IF N'$(MODE)' = N'PREVIEW'
BEGIN
    PRINT '=== PREVIEW only -- nothing written. Re-run with MODE=COMMIT to apply. ===';
    RETURN;
END

BEGIN TRANSACTION DeleteChrysanthemumSeedStock;

DELETE t FROM SeedStockTransactions t JOIN @Lots l ON l.SeedStockId = t.SeedStockId;
DECLARE @DeletedLedger INT = @@ROWCOUNT;
DELETE ss FROM SeedStock ss JOIN @Lots l ON l.SeedStockId = ss.Id;
DECLARE @DeletedLots INT = @@ROWCOUNT;

IF @DeletedLots <> @LotCount OR @DeletedLedger <> @LedgerCount
   OR EXISTS (SELECT 1 FROM SeedStock ss JOIN @Lots l ON l.SeedStockId = ss.Id)
   OR (SELECT COUNT(*) FROM SeedStock ss JOIN PlantSpecies sp ON sp.Id = ss.SpeciesId WHERE sp.PlantTypeId = @PlantTypeId) <> @Skipped
BEGIN
    ROLLBACK TRANSACTION DeleteChrysanthemumSeedStock;
    THROW 53230, 'Post-delete check failed -- rolled back, nothing written.', 1;
END

COMMIT TRANSACTION DeleteChrysanthemumSeedStock;
PRINT '=== COMMITTED: ' + CAST(@DeletedLots AS VARCHAR) + ' unused seed lots and ' + CAST(@DeletedLedger AS VARCHAR)
    + ' ledger rows deleted (' + CAST(@Seeds AS VARCHAR) + ' seeds). ' + CAST(@Skipped AS VARCHAR) + ' used lots kept. ===';
