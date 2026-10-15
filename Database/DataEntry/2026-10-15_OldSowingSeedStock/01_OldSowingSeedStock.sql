-- ============================================================================
-- 01_OldSowingSeedStock.sql   (2026-10-15)
--
-- Adds the owner's "old sowing" seed list (64 rows, 1,026,098 seeds) to Seed Stock exactly as the
-- Add Seed Stock page would (SeedStockRepository.ReceiveAsync): one (Variety, Main Office Area, Batch No) lot per row,
-- PhysicalQuantity credited, one 'StockIn' ledger row (ReferenceType 'SeedStock').
--   Source = In Office | Batch No = Lot2026 | Unit = Seeds | Received On = time of the run | Notes = old sowing
--   Smola White, Vido Red -> CHRYSANTHEMUM; every other row -> SEASONAL VARITIES.
--
-- NAME-BASED (no hard-coded Ids), so the SAME file runs on local and on live:
--   * variety matched by name in ANY plant type (capitals / spaces ignored, same rule as Add Variety);
--     missing varieties are created with the plant type and Ready Stock Days below;
--     a variety found under a DIFFERENT plant type than expected, or matched twice, stops the run.
--   * Owner decisions 2026-10-15: 4 pasted names go into existing varieties
--       Antirrhinum Snapdragon Snapshot Mix -> Antirrhinum Snapshot Mix
--       Impatiens Imara XDR Mix             -> Impatiens walleriana Imara XDR Mix
--       Silver Dust                         -> Cineraria Silver Dust
--       Stock Hot Cakes Mix                 -> Stock Dwarf Hot Cakes Mix
--     'Salvia Blue (cutting - not seed)' becomes variety 'Salvia Blue' (remark kept in Notes);
--     new varieties get the Ready Stock Days their genus already uses (32 where the genus is new; Lantana = 25,
--     owner 2026-10-15).
--   * Source / Area / user resolved by name: Seed Source 'In Office', the ONE active MainOffice Area, user 'Prajwal'.
--   * Re-run safe: a row whose Lot2026 lot already has an 'old sowing' StockIn is skipped (ALREADY DONE).
--
-- MODE=PREVIEW  read-only: prints the plan, writes nothing.
-- MODE=COMMIT   one transaction; any error rolls everything back.
-- Run:  sqlcmd -S <server> -d PlantsIMS2_Test -E -C -I -b -v MODE=PREVIEW TARGET_SERVER=<@@SERVERNAME> -i 01_OldSowingSeedStock.sql
-- Live: take a backup first; run PREVIEW, check it, then COMMIT.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 53101, '01_OldSowingSeedStock.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'PREVIEW', N'COMMIT')
    THROW 53102, 'Run with sqlcmd -v MODE=PREVIEW or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 53103, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @BatchNo NVARCHAR(50) = N'Lot2026', @Unit NVARCHAR(20) = N'Seeds', @CreatedBy NVARCHAR(100) = N'Prajwal';

DECLARE @Rows TABLE (RowNo INT PRIMARY KEY, SourceName NVARCHAR(200), VarietyName NVARCHAR(200), PlantTypeName NVARCHAR(100),
                     Qty INT, ReadyStockDays INT, Notes NVARCHAR(200));
INSERT @Rows (RowNo, SourceName, VarietyName, PlantTypeName, Qty, ReadyStockDays, Notes) VALUES
(1, N'Antirrhinum Snapdragon Snapshot Mix', N'Antirrhinum Snapshot Mix', N'SEASONAL VARITIES', 88200, 35, N'old sowing'),
(2, N'Antirrhinum Snaptini Mix', N'Antirrhinum Snaptini Mix', N'SEASONAL VARITIES', 15900, 32, N'old sowing'),
(3, N'Aster chinensis Gala Mix', N'Aster chinensis Gala Mix', N'SEASONAL VARITIES', 900, 30, N'old sowing'),
(4, N'Aster chinensis Standy Mix', N'Aster chinensis Standy Mix', N'SEASONAL VARITIES', 1050, 30, N'old sowing'),
(5, N'Begonia Super Cool Lipstick', N'Begonia Super Cool Lipstick', N'SEASONAL VARITIES', 19200, 39, N'old sowing'),
(6, N'Dianthus Ideal Mix', N'Dianthus Ideal Mix', N'SEASONAL VARITIES', 59850, 32, N'old sowing'),
(7, N'Dianthus Mix', N'Dianthus Mix', N'SEASONAL VARITIES', 19500, 32, N'old sowing'),
(8, N'Impatiens Imara XDR Mix', N'Impatiens walleriana Imara XDR Mix', N'SEASONAL VARITIES', 14400, 32, N'old sowing'),
(9, N'Lantana Beach', N'Lantana Beach', N'SEASONAL VARITIES', 18000, 25, N'old sowing'),
(10, N'Lantana Coconut', N'Lantana Coconut', N'SEASONAL VARITIES', 9900, 25, N'old sowing'),
(11, N'Lantana IBIS', N'Lantana IBIS', N'SEASONAL VARITIES', 5100, 25, N'old sowing'),
(12, N'Lantana Litchi', N'Lantana Litchi', N'SEASONAL VARITIES', 4500, 25, N'old sowing'),
(13, N'Lantana Nandu', N'Lantana Nandu', N'SEASONAL VARITIES', 7800, 25, N'old sowing'),
(14, N'Lantana Pitaya', N'Lantana Pitaya', N'SEASONAL VARITIES', 13500, 25, N'old sowing'),
(15, N'Lantana Tangerine', N'Lantana Tangerine', N'SEASONAL VARITIES', 13800, 25, N'old sowing'),
(16, N'Lobularia Maritima Giga White', N'Lobularia Maritima Giga White', N'SEASONAL VARITIES', 1200, 32, N'old sowing'),
(17, N'Pansy Inspire Deluxe Maxi Mix', N'Pansy Inspire Deluxe Maxi Mix', N'SEASONAL VARITIES', 40350, 32, N'old sowing'),
(18, N'Pansy Majus GTS Mix', N'Pansy Majus GTS Mix', N'SEASONAL VARITIES', 31650, 32, N'old sowing'),
(19, N'Pansy Viola Admire Maxi Mix', N'Pansy Viola Admire Maxi Mix', N'SEASONAL VARITIES', 34500, 32, N'old sowing'),
(20, N'Pansy Viola Cats Plus Mix', N'Pansy Viola Cats Plus Mix', N'SEASONAL VARITIES', 450, 32, N'old sowing'),
(21, N'Pansy Viola Cornuta Admire Maxi Mix', N'Pansy Viola Cornuta Admire Maxi Mix', N'SEASONAL VARITIES', 900, 32, N'old sowing'),
(22, N'Pansy Viola Deluxe Maxi Mix', N'Pansy Viola Deluxe Maxi Mix', N'SEASONAL VARITIES', 750, 32, N'old sowing'),
(23, N'Pansy Viola Highflyer Mix', N'Pansy Viola Highflyer Mix', N'SEASONAL VARITIES', 900, 32, N'old sowing'),
(24, N'Pansy Viola Highflyer Violet Face', N'Pansy Viola Highflyer Violet Face', N'SEASONAL VARITIES', 7050, 32, N'old sowing'),
(25, N'Pansy Viola Highflyer Yellow', N'Pansy Viola Highflyer Yellow', N'SEASONAL VARITIES', 6600, 32, N'old sowing'),
(26, N'Pansy Viola Highflyer Yellow Red Wing (Trailing)', N'Pansy Viola Highflyer Yellow Red Wing (Trailing)', N'SEASONAL VARITIES', 7548, 32, N'old sowing'),
(27, N'Pansy Viola Hot Cakes Mix', N'Pansy Viola Hot Cakes Mix', N'SEASONAL VARITIES', 18300, 32, N'old sowing'),
(28, N'Pansy Viola Witrockiana Inspire Deluxe Maxi Mix', N'Pansy Viola Witrockiana Inspire Deluxe Maxi Mix', N'SEASONAL VARITIES', 24600, 32, N'old sowing'),
(29, N'Petunia 360 Success Pink', N'Petunia 360 Success Pink', N'SEASONAL VARITIES', 20700, 32, N'old sowing'),
(30, N'Petunia 360 Success Violet', N'Petunia 360 Success Violet', N'SEASONAL VARITIES', 21150, 32, N'old sowing'),
(31, N'Petunia Bello Mix', N'Petunia Bello Mix', N'SEASONAL VARITIES', 78300, 32, N'old sowing'),
(32, N'Petunia Boom HD Burgundy', N'Petunia Boom HD Burgundy', N'SEASONAL VARITIES', 18750, 32, N'old sowing'),
(33, N'Petunia Boom HD Pink', N'Petunia Boom HD Pink', N'SEASONAL VARITIES', 10800, 32, N'old sowing'),
(34, N'Petunia Hybrid Boom Mix', N'Petunia Hybrid Boom Mix', N'SEASONAL VARITIES', 1200, 32, N'old sowing'),
(35, N'Petunia S.C. White', N'Petunia S.C. White', N'SEASONAL VARITIES', 6450, 32, N'old sowing'),
(36, N'Petunia Spoty Mix', N'Petunia Spoty Mix', N'SEASONAL VARITIES', 3000, 32, N'old sowing'),
(37, N'Petunia Success 360 Blue', N'Petunia Success 360 Blue', N'SEASONAL VARITIES', 62100, 32, N'old sowing'),
(38, N'Petunia Success HD Maxi Mix', N'Petunia Success HD Maxi Mix', N'SEASONAL VARITIES', 11550, 32, N'old sowing'),
(39, N'Petunia Success 360 Burgundy', N'Petunia Success 360 Burgundy', N'SEASONAL VARITIES', 42900, 32, N'old sowing'),
(40, N'Petunia Success 360 Purple', N'Petunia Success 360 Purple', N'SEASONAL VARITIES', 34800, 32, N'old sowing'),
(41, N'Petunia Success 360 White', N'Petunia Success 360 White', N'SEASONAL VARITIES', 23100, 32, N'old sowing'),
(42, N'Petunia Success HD Pink', N'Petunia Success HD Pink', N'SEASONAL VARITIES', 19350, 32, N'old sowing'),
(43, N'Petunia Success Maxi Mix', N'Petunia Success Maxi Mix', N'SEASONAL VARITIES', 2100, 32, N'old sowing'),
(44, N'Petunia Success 360 Rose Star', N'Petunia Success 360 Rose Star', N'SEASONAL VARITIES', 38400, 32, N'old sowing'),
(45, N'Petunia Tritunia Plum', N'Petunia Tritunia Plum', N'SEASONAL VARITIES', 10800, 32, N'old sowing'),
(46, N'Portulaca Sundial Mix', N'Portulaca Sundial Mix', N'SEASONAL VARITIES', 1500, 22, N'old sowing'),
(47, N'Ptilotus exaltatus Joey', N'Ptilotus exaltatus Joey', N'SEASONAL VARITIES', 1200, 32, N'old sowing'),
(48, N'Rudbeckia Amarillo Gold', N'Rudbeckia Amarillo Gold', N'SEASONAL VARITIES', 1350, 32, N'old sowing'),
(49, N'Rudbeckia Pavnee Spirit', N'Rudbeckia Pavnee Spirit', N'SEASONAL VARITIES', 1500, 32, N'old sowing'),
(50, N'Rudbeckia Sonata Begreen', N'Rudbeckia Sonata Begreen', N'SEASONAL VARITIES', 1650, 32, N'old sowing'),
(51, N'Salvia Blue (cutting - not seed)', N'Salvia Blue', N'SEASONAL VARITIES', 9600, 28, N'old sowing (cutting - not seed)'),
(52, N'Salvia Salvano', N'Salvia Salvano', N'SEASONAL VARITIES', 40650, 28, N'old sowing'),
(53, N'Salvia Vista Mix', N'Salvia Vista Mix', N'SEASONAL VARITIES', 23550, 28, N'old sowing'),
(54, N'Silver Dust', N'Cineraria Silver Dust', N'SEASONAL VARITIES', 450, 30, N'old sowing'),
(55, N'Silver Dust Millar', N'Silver Dust Millar', N'SEASONAL VARITIES', 29100, 30, N'old sowing'),
(56, N'Smola White', N'Smola White', N'CHRYSANTHEMUM', 2310, 20, N'old sowing'),
(57, N'Stock Hot Cakes Mix', N'Stock Dwarf Hot Cakes Mix', N'SEASONAL VARITIES', 15000, 28, N'old sowing'),
(58, N'Verbena Apex Raw', N'Verbena Apex Raw', N'SEASONAL VARITIES', 1500, 32, N'old sowing'),
(59, N'Verbena Quartz Mix', N'Verbena Quartz Mix', N'SEASONAL VARITIES', 6300, 32, N'old sowing'),
(60, N'Verbena Quartz XP Mix', N'Verbena Quartz XP Mix', N'SEASONAL VARITIES', 5100, 32, N'old sowing'),
(61, N'Verbena Speciosa Imagination', N'Verbena Speciosa Imagination', N'SEASONAL VARITIES', 1350, 32, N'old sowing'),
(62, N'Vinca Cora XDR Mix', N'Vinca Cora XDR Mix', N'SEASONAL VARITIES', 4650, 32, N'old sowing'),
(63, N'Vinca XDR Red', N'Vinca XDR Red', N'SEASONAL VARITIES', 5670, 32, N'old sowing'),
(64, N'Vido Red', N'Vido Red', N'CHRYSANTHEMUM', 1820, 20, N'old sowing');

PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

-- ---- reference data (by name) ---------------------------------------------
DECLARE @SeedSourceId INT = (SELECT MIN(Id) FROM dbo.SeedSources WHERE LTRIM(RTRIM(Name)) = N'In Office' AND ISNULL(IsActive, 1) = 1);
IF @SeedSourceId IS NULL THROW 53110, 'Seed Source "In Office" not found.', 1;
IF (SELECT COUNT(*) FROM dbo.Area WHERE AreaType = N'MainOffice' AND IsActive = 1) <> 1
    THROW 53111, 'Expected exactly ONE active MainOffice Area -- stop and decide which Area receives the seed.', 1;
DECLARE @AreaId INT = (SELECT Id FROM dbo.Area WHERE AreaType = N'MainOffice' AND IsActive = 1);
DECLARE @UserId INT = (SELECT MIN(Id) FROM dbo.IMSUsers WHERE Username = @CreatedBy AND IsActive = 1);
IF EXISTS (SELECT 1 FROM @Rows r WHERE NOT EXISTS (SELECT 1 FROM dbo.PlantTypes t WHERE LTRIM(RTRIM(t.Name)) = r.PlantTypeName))
    THROW 53112, 'Plant Type CHRYSANTHEMUM or SEASONAL VARITIES not found.', 1;
IF EXISTS (SELECT VarietyName FROM @Rows GROUP BY VarietyName HAVING COUNT(*) > 1)
    THROW 53113, 'Two rows resolve to the same variety -- fix the data block.', 1;

DECLARE @AreaName NVARCHAR(200) = (SELECT Name FROM dbo.Area WHERE Id = @AreaId);
PRINT 'Seed Source: In Office (Id ' + CAST(@SeedSourceId AS VARCHAR) + ')   Area: '
    + @AreaName + ' (Id ' + CAST(@AreaId AS VARCHAR) + ')   User: '
    + @CreatedBy + ' (Id ' + ISNULL(CAST(@UserId AS VARCHAR), 'NOT FOUND - UserId left empty') + ')';

-- ---- plan ------------------------------------------------------------------
-- Name key = Add Variety's rule (PlantSpeciesNameRules): upper case, trimmed, tabs/line breaks/nbsp -> space, runs collapsed.
DECLARE @Species TABLE (Id INT, PlantTypeName NVARCHAR(100), NameKey NVARCHAR(400));
INSERT @Species
SELECT s.Id, LTRIM(RTRIM(t.Name)),
       UPPER(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
           REPLACE(REPLACE(REPLACE(REPLACE(s.Name, CHAR(13), N' '), CHAR(10), N' '), CHAR(9), N' '), NCHAR(160), N' '),
           N'      ', N' '), N'     ', N' '), N'    ', N' '), N'   ', N' '), N'  ', N' '), N'  ', N' '), N'  ', N' '))))
FROM dbo.PlantSpecies s LEFT JOIN dbo.PlantTypes t ON t.Id = s.PlantTypeId;

DECLARE @Plan TABLE (RowNo INT PRIMARY KEY, Matches INT, SpeciesId INT NULL, FoundType NVARCHAR(100) NULL,
                     SeedStockId INT NULL, AlreadyDone BIT, Action NVARCHAR(60));
INSERT @Plan (RowNo, Matches, SpeciesId, FoundType, AlreadyDone)
SELECT r.RowNo, (SELECT COUNT(*) FROM @Species s WHERE s.NameKey = UPPER(r.VarietyName)),
       (SELECT MIN(s.Id) FROM @Species s WHERE s.NameKey = UPPER(r.VarietyName)),
       (SELECT MIN(s.PlantTypeName) FROM @Species s WHERE s.NameKey = UPPER(r.VarietyName)), 0
FROM @Rows r;

UPDATE p SET SeedStockId = ss.Id FROM @Plan p
JOIN dbo.SeedStock ss ON ss.SpeciesId = p.SpeciesId AND ss.AreaId = @AreaId AND ss.BatchNo = @BatchNo;
UPDATE p SET AlreadyDone = 1 FROM @Plan p
WHERE EXISTS (SELECT 1 FROM dbo.SeedStockTransactions x WHERE x.SeedStockId = p.SeedStockId AND x.TransactionType = N'StockIn'
              AND x.ReferenceType = N'SeedStock' AND x.Remarks LIKE N'old sowing%');
UPDATE p SET Action = CASE
        WHEN p.Matches > 1 THEN N'ERROR: name matched twice'
        WHEN p.Matches = 1 AND ISNULL(p.FoundType, N'') <> r.PlantTypeName THEN N'ERROR: found under ' + ISNULL(p.FoundType, N'(no type)')
        WHEN p.AlreadyDone = 1 THEN N'ALREADY DONE - skip'
        WHEN p.Matches = 0 THEN N'NEW variety + new lot'
        WHEN p.SeedStockId IS NULL THEN N'existing variety, new lot'
        ELSE N'existing variety, add to lot' END
FROM @Plan p JOIN @Rows r ON r.RowNo = p.RowNo;

SELECT r.RowNo, r.SourceName, r.VarietyName, r.PlantTypeName, r.Qty, r.ReadyStockDays AS DaysIfNew, p.SpeciesId, p.SeedStockId, p.Action
FROM @Rows r JOIN @Plan p ON p.RowNo = r.RowNo ORDER BY r.RowNo;
SELECT p.Action, COUNT(*) AS Rows, SUM(r.Qty) AS Seeds FROM @Plan p JOIN @Rows r ON r.RowNo = p.RowNo GROUP BY p.Action;

IF EXISTS (SELECT 1 FROM @Plan WHERE Action LIKE N'ERROR%')
    THROW 53120, 'Plan has ERROR rows (see above) -- nothing written. Resolve them (owner decision) before COMMIT.', 1;

IF N'$(MODE)' = N'PREVIEW'
BEGIN
    PRINT '=== PREVIEW only -- nothing written. Re-run with MODE=COMMIT to apply. ===';
    RETURN;
END

-- ---- apply (COMMIT) --------------------------------------------------------
DECLARE @Now DATETIME2 = SYSUTCDATETIME();   -- Received On = now (stored in UTC like the page)
BEGIN TRANSACTION OldSowingSeedStock;

-- 1. missing varieties
INSERT dbo.PlantSpecies (PlantTypeId, Name, ScientificName, ReadyStockDays, Color)
SELECT t.Id, r.VarietyName, NULL, r.ReadyStockDays, NULL
FROM @Rows r JOIN @Plan p ON p.RowNo = r.RowNo
JOIN dbo.PlantTypes t ON LTRIM(RTRIM(t.Name)) = r.PlantTypeName
WHERE p.Matches = 0 AND p.AlreadyDone = 0;
UPDATE p SET SpeciesId = s.Id FROM @Plan p JOIN @Rows r ON r.RowNo = p.RowNo
JOIN dbo.PlantSpecies s ON s.Name = r.VarietyName
WHERE p.Matches = 0 AND p.AlreadyDone = 0;

-- 2. missing lots (Species, Area, Lot2026)
INSERT dbo.SeedStock (SpeciesId, AreaId, BatchNo, SeedSourceId, Unit, PhysicalQuantity, InTransitQuantity, CreatedDate, CreatedBy)
SELECT p.SpeciesId, @AreaId, @BatchNo, @SeedSourceId, @Unit, 0, 0, @Now, @CreatedBy
FROM @Plan p WHERE p.AlreadyDone = 0 AND p.SeedStockId IS NULL;
UPDATE p SET SeedStockId = ss.Id FROM @Plan p
JOIN dbo.SeedStock ss ON ss.SpeciesId = p.SpeciesId AND ss.AreaId = @AreaId AND ss.BatchNo = @BatchNo
WHERE p.AlreadyDone = 0;

-- 3. ledger row (BeforeQuantity = balance before) then the balance itself -- same pair RecordTransactionAsync writes
INSERT dbo.SeedStockTransactions (SeedStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
SELECT p.SeedStockId, @Now, N'StockIn', N'SeedStock', p.SeedStockId, r.Qty, ss.PhysicalQuantity, @UserId, r.Notes, @Now
FROM @Plan p JOIN @Rows r ON r.RowNo = p.RowNo JOIN dbo.SeedStock ss WITH (UPDLOCK, HOLDLOCK) ON ss.Id = p.SeedStockId
WHERE p.AlreadyDone = 0;
UPDATE ss SET PhysicalQuantity = ss.PhysicalQuantity + r.Qty, ModifiedDate = @Now
FROM dbo.SeedStock ss JOIN @Plan p ON p.SeedStockId = ss.Id JOIN @Rows r ON r.RowNo = p.RowNo
WHERE p.AlreadyDone = 0;

-- 4. verify inside the transaction
DECLARE @Expected INT = (SELECT ISNULL(SUM(r.Qty), 0) FROM @Rows r JOIN @Plan p ON p.RowNo = r.RowNo WHERE p.AlreadyDone = 0);
DECLARE @Written INT = (SELECT ISNULL(SUM(x.Quantity), 0) FROM dbo.SeedStockTransactions x
                        WHERE x.TransactionDate = @Now AND x.CreatedAt = @Now AND x.TransactionType = N'StockIn' AND x.Remarks LIKE N'old sowing%');
IF @Expected <> @Written OR EXISTS (SELECT 1 FROM @Plan WHERE AlreadyDone = 0 AND (SpeciesId IS NULL OR SeedStockId IS NULL))
BEGIN
    ROLLBACK TRANSACTION OldSowingSeedStock;
    THROW 53130, 'Verification failed -- rolled back, nothing written.', 1;
END

COMMIT TRANSACTION OldSowingSeedStock;
DECLARE @DoneRows INT = (SELECT COUNT(*) FROM @Plan WHERE AlreadyDone = 0);
PRINT '=== COMMITTED: ' + CAST(@DoneRows AS VARCHAR) + ' rows, '
    + CAST(@Expected AS VARCHAR) + ' seeds into Lot2026. ===';

SELECT ss.Id AS SeedStockId, ps.Name AS Variety, pt.Name AS PlantType, ss.BatchNo, ss.Unit, ss.PhysicalQuantity
FROM dbo.SeedStock ss JOIN dbo.PlantSpecies ps ON ps.Id = ss.SpeciesId JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
WHERE ss.AreaId = @AreaId AND ss.BatchNo = @BatchNo ORDER BY ps.Name;
