-- ============================================================================
-- 03_Verify_ReadOnly.sql  --  verification / reconciliation of batch MAIN-OFFICE-SOWING-2026-10
--
-- READ-ONLY: SELECTs only (#temp tables live in tempdb). Run after tools/MainOfficeSowingImport "run".
-- The batch is identified by its tag at the start of Remarks ("MAIN-OFFICE-SOWING-2026-10 | ...") on the sowings,
-- the seed receipts, the tray receipts and the Cocopeat receipt, and by the sowings' own consumption rows.
-- Expected values are generated from entry_rows.csv by gen_verify.py.
--
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -W -s "|" -i 03_Verify_ReadOnly.sql -o verify_<db>.txt
-- Every check prints PASS or FAIL; the last block counts the FAILs.
-- ============================================================================
SET NOCOUNT ON;
DECLARE @Tag NVARCHAR(60) = N'MAIN-OFFICE-SOWING-2026-10 |%';

PRINT '=== 1. Target ===';
SELECT @@SERVERNAME AS ServerName, DB_NAME() AS DatabaseName, DB_ID() AS DatabaseId, CONVERT(VARCHAR(19), DATEADD(MINUTE, 330, SYSUTCDATETIME()), 120) AS NowIST;

IF OBJECT_ID('tempdb..#Exp') IS NOT NULL DROP TABLE #Exp;
IF OBJECT_ID('tempdb..#ExpDate') IS NOT NULL DROP TABLE #ExpDate;
IF OBJECT_ID('tempdb..#ExpCavity') IS NOT NULL DROP TABLE #ExpCavity;
IF OBJECT_ID('tempdb..#S') IS NOT NULL DROP TABLE #S;
IF OBJECT_ID('tempdb..#R') IS NOT NULL DROP TABLE #R;
CREATE TABLE #Exp (RowNo INT PRIMARY KEY, SowingDate DATE, SourceVariety NVARCHAR(200), CavityType NVARCHAR(30), Trays INT, Seedlings INT);
INSERT INTO #Exp VALUES
(1, '2026-10-01', N'Petunia 360 Suc. Pink', N'150 Cavity', 138, 20700),
(2, '2026-10-01', N'Petunia Bello Mix', N'150 Cavity', 522, 78300),
(3, '2026-10-01', N'Dianthus Ideal Mix', N'150 Cavity', 259, 38850),
(4, '2026-10-01', N'Salvia Blue (Cutting GP)', N'150 Cavity', 64, 9600),
(5, '2026-10-02', N'Lantana Coconut (K.K)', N'150 Cavity', 30, 4500),
(6, '2026-10-02', N'Lantana Ibis (K.K)', N'150 Cavity', 34, 5100),
(7, '2026-10-02', N'Petunia Success 360 Rose Star', N'150 Cavity', 120, 18000),
(8, '2026-10-02', N'Petunia Boom HD Burgundy', N'150 Cavity', 125, 18750),
(9, '2026-10-02', N'Petunia 360 Success Violet', N'150 Cavity', 141, 21150),
(10, '2026-10-03', N'Petunia Suc. 360 Rose Star', N'150 Cavity', 136, 20400),
(11, '2026-10-03', N'Petunia Suc. HD Pink', N'150 Cavity', 129, 19350),
(12, '2026-10-03', N'Petunia Suc. 360 Purple', N'150 Cavity', 133, 19950),
(13, '2026-10-03', N'Petunia Suc. 360 Burgundy', N'150 Cavity', 56, 8400),
(14, '2026-10-03', N'Antirrhinum Snapdragon Snapshot Mix', N'150 Cavity', 336, 50400),
(15, '2026-10-04', N'Lantana Pitaya (K.K)', N'150 Cavity', 90, 13500),
(16, '2026-10-04', N'Lantana Nandu (K.K)', N'150 Cavity', 52, 7800),
(17, '2026-10-04', N'Verbena Quartz XP Mix', N'150 Cavity', 34, 5100),
(18, '2026-10-04', N'Petunia Suc. 360 Burgundy', N'150 Cavity', 93, 13950),
(19, '2026-10-04', N'Antirrhinum Snapdragon Snapshot Mix', N'150 Cavity', 252, 37800),
(20, '2026-10-04', N'Salvia Vista Mix', N'150 Cavity', 71, 10650),
(21, '2026-10-04', N'Petunia Boom HD Pink', N'150 Cavity', 72, 10800),
(22, '2026-10-05', N'Stock Hot Cakes Mix', N'150 Cavity', 100, 15000),
(23, '2026-10-05', N'Pansy Viola Hot Cakes Mix', N'150 Cavity', 122, 18300),
(24, '2026-10-05', N'Petunia Suc. HD Maxi Mix', N'150 Cavity', 77, 11550),
(25, '2026-10-05', N'Dianthus Mix', N'150 Cavity', 130, 19500),
(26, '2026-10-05', N'Antirrhinum Snaptini Mix', N'150 Cavity', 106, 15900),
(27, '2026-10-05', N'Petunia Spoty Mix', N'150 Cavity', 20, 3000),
(28, '2026-10-06', N'Verbena Quartz Mix', N'150 Cavity', 42, 6300),
(29, '2026-10-06', N'Silver Dust Millar', N'150 Cavity', 194, 29100),
(30, '2026-10-06', N'Salvia Salvano', N'150 Cavity', 80, 12000),
(31, '2026-10-06', N'Dianthus Ideal Mix', N'150 Cavity', 140, 21000),
(32, '2026-10-06', N'Lantana Tangerine (K.K)', N'150 Cavity', 92, 13800),
(33, '2026-10-07', N'Lantana Beach (K.K)', N'150 Cavity', 120, 18000),
(34, '2026-10-08', N'Petunia Suc. Maxi Mix', N'150 Cavity', 14, 2100),
(35, '2026-10-08', N'Pansy Viola Cornuta Admire Maxi Mix', N'150 Cavity', 6, 900),
(36, '2026-10-08', N'Verbena Apex Raw', N'150 Cavity', 10, 1500),
(37, '2026-10-08', N'Lobularia Maritima Giga White', N'150 Cavity', 8, 1200),
(38, '2026-10-08', N'Petunia Hy. Boom Mix', N'150 Cavity', 8, 1200),
(39, '2026-10-08', N'Aster Chinensis Gala Mix', N'150 Cavity', 6, 900),
(40, '2026-10-08', N'Ptilotus Exaltatus Joey', N'150 Cavity', 8, 1200),
(41, '2026-10-08', N'Pansy Viola Highflyer Mix', N'150 Cavity', 6, 900),
(42, '2026-10-08', N'Verbena Speciosa Imagination', N'150 Cavity', 9, 1350),
(43, '2026-10-08', N'Portulaca Sundial Mix', N'150 Cavity', 10, 1500),
(44, '2026-10-08', N'Aster Chinensis Standy Mix', N'150 Cavity', 7, 1050),
(45, '2026-10-08', N'Rudbeckia Sonata Begreen', N'150 Cavity', 11, 1650),
(46, '2026-10-08', N'Rudbeckia Amarillo Gold', N'150 Cavity', 9, 1350),
(47, '2026-10-08', N'Silver Dust', N'150 Cavity', 3, 450),
(48, '2026-10-08', N'Pansy Viola Deluxe Maxi Mix', N'150 Cavity', 5, 750),
(49, '2026-10-08', N'Pansy Viola Cats Plus Mix', N'150 Cavity', 3, 450),
(50, '2026-10-08', N'Rudbeckia Pavnee Spirit', N'150 Cavity', 10, 1500),
(51, '2026-10-08', N'Vinca Cora XDR Mix', N'150 Cavity', 31, 4650),
(52, '2026-10-08', N'Salvia Salvano', N'150 Cavity', 191, 28650),
(53, '2026-10-08', N'Salvia Vista Mix', N'150 Cavity', 86, 12900),
(54, '2026-10-08', N'Petunia Suc. 360 Blue', N'150 Cavity', 150, 22500),
(55, '2026-10-08', N'Petunia Suc. 360 Burgundy', N'150 Cavity', 109, 16350),
(56, '2026-10-08', N'Petunia Suc. 360 Purple', N'150 Cavity', 99, 14850),
(57, '2026-10-09', N'Lantana Litchi (K.K)', N'150 Cavity', 30, 4500),
(58, '2026-10-09', N'Lantana Coconut (K.K)', N'150 Cavity', 36, 5400),
(59, '2026-10-09', N'Pansy Viola Admire Maxi Mix', N'150 Cavity', 64, 9600),
(60, '2026-10-09', N'Impatiens Imara XDR Mix', N'150 Cavity', 96, 14400),
(61, '2026-10-09', N'Petunia Tritunia Plum', N'150 Cavity', 72, 10800),
(62, '2026-10-09', N'Petunia Suc. 360 Blue', N'150 Cavity', 112, 16800),
(63, '2026-10-09', N'Petunia S.C. White', N'150 Cavity', 43, 6450),
(64, '2026-10-10', N'Pansy Majus GTS Mix', N'150 Cavity', 211, 31650),
(65, '2026-10-10', N'Pansy Viola Admire Maxi Mix', N'150 Cavity', 166, 24900),
(66, '2026-10-10', N'Pansy Inspire Deluxe Maxi Mix', N'150 Cavity', 60, 9000),
(67, '2026-10-10', N'Petunia Suc. 360 White', N'150 Cavity', 50, 7500),
(68, '2026-10-10', N'Petunia Suc. 360 Blue', N'150 Cavity', 152, 22800),
(69, '2026-10-11', N'Petunia Suc. 360 Burgundy', N'150 Cavity', 28, 4200),
(70, '2026-10-11', N'Petunia Suc. 360 White', N'150 Cavity', 104, 15600),
(71, '2026-10-11', N'Pansy Inspire Deluxe Maxi Mix', N'150 Cavity', 209, 31350),
(72, '2026-10-11', N'Begonia Super Cool Lipstick', N'150 Cavity', 128, 19200),
(73, '2026-10-12', N'Pansy Viola Highflyer Yellow Red Wing (Trailing)', N'102 Cavity', 74, 7548),
(74, '2026-10-12', N'Pansy Viola Highflyer Yellow', N'150 Cavity', 44, 6600),
(75, '2026-10-12', N'Pansy Viola Highflyer Violet Face', N'150 Cavity', 47, 7050),
(76, '2026-10-12', N'Pansy Viola Wittrockiana Inspire Deluxe Maxi Mix', N'150 Cavity', 164, 24600),
(77, '2026-10-12', N'Vido Red (SR)', N'70 Cavity', 26, 1820),
(78, '2026-10-12', N'Smola White (SR)', N'70 Cavity', 33, 2310),
(79, '2026-10-12', N'Vinca XDR Red (SR)', N'70 Cavity', 81, 5670);
CREATE TABLE #ExpDate (SowingDate DATE PRIMARY KEY, Rows_ INT, Trays INT, Seedlings INT);
INSERT INTO #ExpDate VALUES
('2026-10-01', 4, 983, 147450),
('2026-10-02', 5, 450, 67500),
('2026-10-03', 5, 790, 118500),
('2026-10-04', 7, 664, 99600),
('2026-10-05', 6, 555, 83250),
('2026-10-06', 5, 548, 82200),
('2026-10-07', 1, 120, 18000),
('2026-10-08', 23, 799, 119850),
('2026-10-09', 7, 453, 67950),
('2026-10-10', 5, 639, 95850),
('2026-10-11', 4, 469, 70350),
('2026-10-12', 7, 469, 55598);
CREATE TABLE #ExpCavity (CavityType NVARCHAR(30) PRIMARY KEY, Trays INT);
INSERT INTO #ExpCavity VALUES
(N'102 Cavity', 74),
(N'150 Cavity', 6725),
(N'70 Cavity', 140);
CREATE TABLE #R (Chk NVARCHAR(200), Result VARCHAR(4), Detail NVARCHAR(400));

-- The batch's sowings (row number parsed from "... | row N: ...").
SELECT ss.Id, ss.SowingCode, ss.SowingDate, ss.SpeciesId, ss.SourceSeedStockId, ss.SourceType, ss.AreaId, ss.PolyhouseId, ss.CavityType,
       ss.NumberOfTrays, ss.QuantitySown, ss.SeedQuantity, ss.SupervisorId, ss.CreatedById, ss.GrowingMaterialTypeId, ss.Status,
       ss.SubmissionToken, TRY_CAST(SUBSTRING(ss.Remarks, CHARINDEX(N'| row ', ss.Remarks) + 6, CHARINDEX(N':', ss.Remarks + N':', CHARINDEX(N'| row ', ss.Remarks)) - CHARINDEX(N'| row ', ss.Remarks) - 6) AS INT) AS RowNo
INTO #S FROM dbo.SeedSowings ss WHERE ss.Remarks LIKE @Tag;

PRINT '=== 2. Sowings by date (expected vs recorded) ===';
SELECT CONVERT(VARCHAR(10), e.SowingDate, 105) AS SowingDate, e.Rows_ AS ExpRows, COUNT(s.Id) AS Rows_, e.Trays AS ExpTrays, ISNULL(SUM(s.NumberOfTrays), 0) AS Trays,
       e.Seedlings AS ExpSeedlings, ISNULL(SUM(s.QuantitySown), 0) AS Seedlings,
       CASE WHEN COUNT(s.Id) = e.Rows_ AND ISNULL(SUM(s.NumberOfTrays), 0) = e.Trays AND ISNULL(SUM(s.QuantitySown), 0) = e.Seedlings THEN 'PASS' ELSE 'FAIL' END AS Result
FROM #ExpDate e LEFT JOIN #S s ON s.SowingDate = e.SowingDate
GROUP BY e.SowingDate, e.Rows_, e.Trays, e.Seedlings ORDER BY e.SowingDate;
INSERT INTO #R SELECT N'Totals 79 rows / 6,939 trays / 1,026,098 seedlings',
    CASE WHEN (SELECT COUNT(*) FROM #S) = 79 AND (SELECT SUM(NumberOfTrays) FROM #S) = 6939 AND (SELECT SUM(QuantitySown) FROM #S) = 1026098 THEN 'PASS' ELSE 'FAIL' END,
    CONCAT((SELECT COUNT(*) FROM #S), N' rows / ', (SELECT SUM(NumberOfTrays) FROM #S), N' trays / ', (SELECT CAST(SUM(QuantitySown) AS BIGINT) FROM #S), N' seedlings');

PRINT '=== 3. Row by row against entry_rows.csv (only differences are listed) ===';
SELECT e.RowNo, e.SowingDate, e.SourceVariety, e.CavityType, e.Trays, e.Seedlings, s.SowingCode, s.SowingDate AS RecSowingDate, s.CavityType AS RecCavity, s.NumberOfTrays AS RecTrays, s.QuantitySown AS RecSeedlings
FROM #Exp e LEFT JOIN #S s ON s.RowNo = e.RowNo
WHERE s.Id IS NULL OR s.SowingDate <> e.SowingDate OR s.CavityType <> e.CavityType OR s.NumberOfTrays <> e.Trays OR s.QuantitySown <> e.Seedlings OR s.SeedQuantity <> e.Seedlings;
INSERT INTO #R SELECT N'Every source row recorded exactly once, as supplied',
    CASE WHEN NOT EXISTS (SELECT 1 FROM #Exp e LEFT JOIN #S s ON s.RowNo = e.RowNo
                          WHERE s.Id IS NULL OR s.SowingDate <> e.SowingDate OR s.CavityType <> e.CavityType OR s.NumberOfTrays <> e.Trays OR s.QuantitySown <> e.Seedlings OR s.SeedQuantity <> e.Seedlings)
          AND NOT EXISTS (SELECT RowNo FROM #S GROUP BY RowNo HAVING COUNT(*) > 1) THEN 'PASS' ELSE 'FAIL' END, NULL;

PRINT '=== 4. Totals by variety and cavity ===';
SELECT sp.Name AS Variety, s.CavityType, COUNT(*) AS Sowings, SUM(s.NumberOfTrays) AS Trays, SUM(s.QuantitySown) AS Seedlings,
       STRING_AGG(CONVERT(VARCHAR(5), s.SowingDate, 105), ', ') WITHIN GROUP (ORDER BY s.SowingDate) AS Dates
FROM #S s JOIN dbo.PlantSpecies sp ON sp.Id = s.SpeciesId GROUP BY sp.Name, s.CavityType ORDER BY sp.Name, s.CavityType;

PRINT '=== 5. Location, people, material, source on every sowing ===';
SELECT COUNT(*) AS Sowings,
       SUM(CASE WHEN a.Name = N'Main-Office-Areas' AND a.AreaType = N'MainOffice' THEN 1 ELSE 0 END) AS InMainOfficeArea,
       SUM(CASE WHEN p.Name = N'Facility 1 Main office' THEN 1 ELSE 0 END) AS InFacility1,
       SUM(CASE WHEN sup.Username = N'Maya' AND sup.Name = N'Maya Shinde' THEN 1 ELSE 0 END) AS SupervisorMaya,
       SUM(CASE WHEN rec.Username = N'Prajwal' THEN 1 ELSE 0 END) AS RecordedByPrajwal,
       SUM(CASE WHEN mt.Code = N'COCOPEAT' THEN 1 ELSE 0 END) AS Cocopeat,
       SUM(CASE WHEN s.SourceType = N'Seed' THEN 1 ELSE 0 END) AS SourceSeed,
       SUM(CASE WHEN la.AreaType = N'MainOffice' AND lot.SpeciesId = s.SpeciesId THEN 1 ELSE 0 END) AS OwnVarietyMainOfficeLot
FROM #S s
LEFT JOIN dbo.Area a ON a.Id = s.AreaId LEFT JOIN dbo.Polyhouses p ON p.Id = s.PolyhouseId
LEFT JOIN dbo.IMSUsers sup ON sup.Id = s.SupervisorId LEFT JOIN dbo.IMSUsers rec ON rec.Id = s.CreatedById
LEFT JOIN dbo.MaterialTypes mt ON mt.Id = s.GrowingMaterialTypeId
LEFT JOIN dbo.SeedStock lot ON lot.Id = s.SourceSeedStockId LEFT JOIN dbo.Area la ON la.Id = lot.AreaId;
INSERT INTO #R SELECT N'Area, Facility 1, Maya, Prajwal, Cocopeat, seed source and own-variety Main Office lot on every sowing',
    CASE WHEN NOT EXISTS (SELECT 1 FROM #S s
        LEFT JOIN dbo.Area a ON a.Id = s.AreaId LEFT JOIN dbo.Polyhouses p ON p.Id = s.PolyhouseId
        LEFT JOIN dbo.IMSUsers sup ON sup.Id = s.SupervisorId LEFT JOIN dbo.IMSUsers rec ON rec.Id = s.CreatedById
        LEFT JOIN dbo.MaterialTypes mt ON mt.Id = s.GrowingMaterialTypeId
        LEFT JOIN dbo.SeedStock lot ON lot.Id = s.SourceSeedStockId LEFT JOIN dbo.Area la ON la.Id = lot.AreaId
        WHERE NOT (a.Name = N'Main-Office-Areas' AND a.AreaType = N'MainOffice' AND p.Name = N'Facility 1 Main office' AND sup.Username = N'Maya'
                   AND rec.Username = N'Prajwal' AND mt.Code = N'COCOPEAT' AND s.SourceType = N'Seed' AND la.AreaType = N'MainOffice' AND lot.SpeciesId = s.SpeciesId))
         AND (SELECT COUNT(*) FROM #S) = 79 THEN 'PASS' ELSE 'FAIL' END, NULL;

PRINT '=== 6. Seed lots used by the batch: receipts, deductions, balance ===';
SELECT lot.Id AS SeedStockId, sp.Name AS Variety, lot.BatchNo,
       SUM(CASE WHEN t.TransactionType = N'StockIn' AND t.Remarks LIKE @Tag THEN t.Quantity ELSE 0 END) AS BatchReceipt,
       -SUM(CASE WHEN bs.Id IS NOT NULL THEN t.Quantity ELSE 0 END) AS BatchSown,
       SUM(CASE WHEN NOT ((t.TransactionType = N'StockIn' AND t.Remarks LIKE @Tag) OR bs.Id IS NOT NULL) THEN t.Quantity ELSE 0 END) AS OtherMovements,
       lot.PhysicalQuantity, lot.InTransitQuantity
FROM dbo.SeedStock lot JOIN dbo.PlantSpecies sp ON sp.Id = lot.SpeciesId
JOIN dbo.SeedStockTransactions t ON t.SeedStockId = lot.Id
LEFT JOIN #S bs ON t.ReferenceType = N'SeedSowing' AND bs.Id = t.ReferenceId
WHERE lot.Id IN (SELECT SourceSeedStockId FROM #S)
GROUP BY lot.Id, sp.Name, lot.BatchNo, lot.PhysicalQuantity, lot.InTransitQuantity ORDER BY sp.Name;
INSERT INTO #R SELECT N'Seed: each lot''s batch receipt = its batch sowings (1 seed = 1 seedling); totals 1,026,098 in / out',
    CASE WHEN NOT EXISTS (
        SELECT 1 FROM dbo.SeedStock lot JOIN dbo.SeedStockTransactions t ON t.SeedStockId = lot.Id
        LEFT JOIN #S bs ON t.ReferenceType = N'SeedSowing' AND bs.Id = t.ReferenceId
        WHERE lot.Id IN (SELECT SourceSeedStockId FROM #S)
        GROUP BY lot.Id HAVING SUM(CASE WHEN t.TransactionType = N'StockIn' AND t.Remarks LIKE @Tag THEN t.Quantity ELSE 0 END)
                            <> -SUM(CASE WHEN bs.Id IS NOT NULL THEN t.Quantity ELSE 0 END))
     AND (SELECT SUM(Quantity) FROM dbo.SeedStockTransactions WHERE TransactionType = N'StockIn' AND Remarks LIKE @Tag) = 1026098
     AND (SELECT -SUM(Quantity) FROM dbo.SeedStockTransactions WHERE ReferenceType = N'SeedSowing' AND ReferenceId IN (SELECT Id FROM #S)) = 1026098
    THEN 'PASS' ELSE 'FAIL' END, NULL;
INSERT INTO #R SELECT N'Seed: one Sown row per sowing = -QuantitySown, no wastage (whole trays)',
    CASE WHEN NOT EXISTS (SELECT 1 FROM #S s WHERE (SELECT COUNT(*) FROM dbo.SeedStockTransactions t WHERE t.ReferenceType = N'SeedSowing' AND t.ReferenceId = s.Id AND t.TransactionType = N'Sown' AND t.Quantity = -s.QuantitySown) <> 1
                                            OR EXISTS (SELECT 1 FROM dbo.SeedStockTransactions t WHERE t.ReferenceType = N'SeedSowing' AND t.ReferenceId = s.Id AND t.TransactionType <> N'Sown'))
    THEN 'PASS' ELSE 'FAIL' END, NULL;

PRINT '=== 7. Empty trays at Facility 1: batch receipts vs batch consumption ===';
SELECT ts.TraySize, ec.Trays AS ExpTrays,
       SUM(CASE WHEN t.ReferenceType = N'TrayStockAdd' AND t.Remarks LIKE @Tag THEN t.Quantity ELSE 0 END) AS BatchReceived,
       -SUM(CASE WHEN bs.Id IS NOT NULL THEN t.Quantity ELSE 0 END) AS BatchUsed,
       ts.PhysicalQuantity AS PoolNow
FROM dbo.TrayStock ts JOIN dbo.Polyhouses p ON p.Id = ts.PolyhouseId AND p.Name = N'Facility 1 Main office'
JOIN dbo.TrayStockTransactions t ON t.TrayStockId = ts.Id
LEFT JOIN #S bs ON t.ReferenceType = N'SeedSowing' AND bs.Id = t.ReferenceId
LEFT JOIN #ExpCavity ec ON ec.CavityType = ts.TraySize
GROUP BY ts.TraySize, ec.Trays, ts.PhysicalQuantity ORDER BY ts.TraySize;
INSERT INTO #R SELECT N'Trays: received = used = expected per cavity (6,939)',
    CASE WHEN NOT EXISTS (
        SELECT 1 FROM #ExpCavity ec
        WHERE ec.Trays <> ISNULL((SELECT SUM(t.Quantity) FROM dbo.TrayStockTransactions t JOIN dbo.TrayStock ts ON ts.Id = t.TrayStockId
                                  WHERE ts.TraySize = ec.CavityType AND t.ReferenceType = N'TrayStockAdd' AND t.Remarks LIKE @Tag), 0)
           OR ec.Trays <> ISNULL((SELECT -SUM(t.Quantity) FROM dbo.TrayStockTransactions t JOIN dbo.TrayStock ts ON ts.Id = t.TrayStockId
                                  WHERE ts.TraySize = ec.CavityType AND t.ReferenceType = N'SeedSowing' AND t.ReferenceId IN (SELECT Id FROM #S)), 0))
    THEN 'PASS' ELSE 'FAIL' END, NULL;

PRINT '=== 8. Cocopeat at Facility 1: batch receipt vs batch consumption ===';
SELECT mt.Name AS Material,
       SUM(CASE WHEN x.TransactionType = N'RECEIVE' AND x.Remarks LIKE @Tag THEN x.StockQuantity ELSE 0 END) AS BatchReceivedKg,
       SUM(CASE WHEN x.TransactionType = N'CONSUME' AND bs.Id IS NOT NULL THEN x.StockQuantity ELSE 0 END) AS BatchUsedKg,
       SUM(CASE WHEN x.TransactionType = N'CONSUME' AND bs.Id IS NOT NULL THEN 1 ELSE 0 END) AS ConsumeRows,
       ms.CurrentQuantity AS StockNowKg
FROM dbo.MaterialStock ms JOIN dbo.MaterialTypes mt ON mt.Id = ms.MaterialTypeId AND mt.Code = N'COCOPEAT'
JOIN dbo.Polyhouses p ON p.Id = ms.PolyhouseId AND p.Name = N'Facility 1 Main office'
JOIN dbo.MaterialTransactions x ON x.MaterialStockId = ms.Id
LEFT JOIN #S bs ON x.ReferenceType = N'SeedSowing' AND bs.Id = x.ReferenceId
GROUP BY mt.Name, ms.CurrentQuantity;
INSERT INTO #R SELECT N'Cocopeat: one consumption per sowing; batch receipt covers batch use',
    CASE WHEN (SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE TransactionType = N'CONSUME' AND ReferenceType = N'SeedSowing' AND ReferenceId IN (SELECT Id FROM #S)) = 79
          AND (SELECT COUNT(DISTINCT ReferenceId) FROM dbo.MaterialTransactions WHERE TransactionType = N'CONSUME' AND ReferenceType = N'SeedSowing' AND ReferenceId IN (SELECT Id FROM #S)) = 79
          AND (SELECT SUM(StockQuantity) FROM dbo.MaterialTransactions WHERE TransactionType = N'RECEIVE' AND Remarks LIKE @Tag)
           >= (SELECT SUM(StockQuantity) FROM dbo.MaterialTransactions WHERE TransactionType = N'CONSUME' AND ReferenceType = N'SeedSowing' AND ReferenceId IN (SELECT Id FROM #S))
    THEN 'PASS' ELSE 'FAIL' END, NULL;

PRINT '=== 9. Duplicates and orphans ===';
INSERT INTO #R SELECT N'No duplicate sowings (token, code, or same variety + date + cavity twice)',
    CASE WHEN NOT EXISTS (SELECT SubmissionToken FROM #S GROUP BY SubmissionToken HAVING COUNT(*) > 1)
          AND NOT EXISTS (SELECT SowingCode FROM dbo.SeedSowings GROUP BY SowingCode HAVING COUNT(*) > 1)
          AND NOT EXISTS (SELECT 1 FROM #S GROUP BY SpeciesId, SowingDate, CavityType HAVING COUNT(*) > 1)
          AND (SELECT COUNT(*) FROM dbo.SeedStockTransactions WHERE TransactionType = N'StockIn' AND Remarks LIKE @Tag)
            = (SELECT COUNT(DISTINCT SeedStockId) FROM dbo.SeedStockTransactions WHERE TransactionType = N'StockIn' AND Remarks LIKE @Tag)
    THEN 'PASS' ELSE 'FAIL' END, NULL;
SELECT s.SpeciesId, sp.Name, CONVERT(VARCHAR(10), s.SowingDate, 105) AS SowingDate, s.CavityType, COUNT(*) AS Times
FROM #S s JOIN dbo.PlantSpecies sp ON sp.Id = s.SpeciesId GROUP BY s.SpeciesId, sp.Name, s.SowingDate, s.CavityType HAVING COUNT(*) > 1;
INSERT INTO #R SELECT N'No orphans (variety, lot, area, polyhouse, supervisor, material all exist)',
    CASE WHEN NOT EXISTS (SELECT 1 FROM #S s
        WHERE NOT EXISTS (SELECT 1 FROM dbo.PlantSpecies x WHERE x.Id = s.SpeciesId) OR NOT EXISTS (SELECT 1 FROM dbo.SeedStock x WHERE x.Id = s.SourceSeedStockId)
           OR NOT EXISTS (SELECT 1 FROM dbo.Area x WHERE x.Id = s.AreaId) OR NOT EXISTS (SELECT 1 FROM dbo.Polyhouses x WHERE x.Id = s.PolyhouseId)
           OR NOT EXISTS (SELECT 1 FROM dbo.IMSUsers x WHERE x.Id = s.SupervisorId) OR NOT EXISTS (SELECT 1 FROM dbo.MaterialTypes x WHERE x.Id = s.GrowingMaterialTypeId))
    THEN 'PASS' ELSE 'FAIL' END, NULL;
INSERT INTO #R SELECT N'No negative stock (seed lots, tray pools, material)',
    CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.SeedStock WHERE PhysicalQuantity < 0 OR PhysicalQuantity - InTransitQuantity < 0)
          AND NOT EXISTS (SELECT 1 FROM dbo.TrayStock WHERE PhysicalQuantity < 0)
          AND NOT EXISTS (SELECT 1 FROM dbo.MaterialStock WHERE CurrentQuantity < 0)
    THEN 'PASS' ELSE 'FAIL' END, NULL;
INSERT INTO #R SELECT N'Sowings await approval (Status Sown), none approved or cancelled yet', CASE WHEN NOT EXISTS (SELECT 1 FROM #S WHERE Status <> N'Sown') THEN 'PASS' ELSE 'INFO' END,
    (SELECT STRING_AGG(CONCAT(Status, N' ', n), N', ') FROM (SELECT Status, COUNT(*) n FROM #S GROUP BY Status) z);

PRINT '=== 10. Result ===';
SELECT Chk AS [Check], Result, Detail FROM #R;
SELECT SUM(CASE WHEN Result = 'FAIL' THEN 1 ELSE 0 END) AS Failures, COUNT(*) AS Checks FROM #R;
PRINT 'Unrelated data: compare a fingerprint or row counts taken before the import with one taken after (see VALIDATION_REPORT.md section 9).';
DROP TABLE #Exp; DROP TABLE #ExpDate; DROP TABLE #ExpCavity; DROP TABLE #S; DROP TABLE #R;
