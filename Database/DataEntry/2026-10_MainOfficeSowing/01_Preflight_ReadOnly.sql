-- ============================================================================
-- 01_Preflight_ReadOnly.sql  --  Main Office tray sowing data, 01-10-2026 .. 12-10-2026
--
-- READ-ONLY. Makes NO change to the target database: SELECTs only, plus a #temp table in tempdb holding
-- the 79 source rows (generated from entry_rows.csv by gen_preflight.py -- edit the CSV, not this list).
-- Safe on PlantsIMS2_Test, a PlantsIMS2_Scratch_* copy, or (later, run by the owner) live PlantsIMS2.
-- Blocks that need a newer schema object check for it first and report it as missing instead of failing.
--
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -W -s "|" -i 01_Preflight_ReadOnly.sql -o preflight_<db>.txt
-- ============================================================================
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

PRINT '=== 1. Target identity ===';
SELECT @@SERVERNAME AS ServerName, DB_NAME() AS DatabaseName, DB_ID() AS DatabaseId,
       CONVERT(VARCHAR(19), DATEADD(MINUTE, 330, SYSUTCDATETIME()), 120) AS NowIST;

IF OBJECT_ID('tempdb..#Src') IS NOT NULL DROP TABLE #Src;
CREATE TABLE #Src (RowNo INT PRIMARY KEY, SowingDate DATE, SourceVariety NVARCHAR(200), CavityType NVARCHAR(30), Trays INT, Seedlings INT);
INSERT INTO #Src (RowNo, SowingDate, SourceVariety, CavityType, Trays, Seedlings) VALUES
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

PRINT '=== 2. Schema readiness (1 = present) ===';
SELECT
  CASE WHEN OBJECT_ID('dbo.MaterialConsumptionRules','U') IS NOT NULL THEN 1 ELSE 0 END AS [10-08 MaterialStock],
  CASE WHEN OBJECT_ID('dbo.BedSowings','U') IS NOT NULL THEN 1 ELSE 0 END AS [10-12 BedSowing],
  CASE WHEN OBJECT_ID('dbo.BedSowingApprover','U') IS NOT NULL THEN 1 ELSE 0 END AS [10-16 BedSowingApprover],
  CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_CavityType')) LIKE N'%70 Cavity%' THEN 1 ELSE 0 END AS [10-19 Cavity70],
  CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.TR_BedSowings_Guard')) LIKE N'%BedSowingEdit%' THEN 1 ELSE 0 END AS [10-22 BedSowingEdit];

PRINT '=== 3. Area Main-Office-Areas and Polyhouse Facility 1 (expect exactly one of each) ===';
SELECT a.Id AS AreaId, a.Name, a.AreaCode, a.AreaType, a.IsActive FROM dbo.Area a
WHERE a.AreaType = N'MainOffice' OR a.Name LIKE N'Main%Office%';
SELECT p.Id AS PolyhouseId, p.Name, p.AreaId FROM dbo.Polyhouses p
JOIN dbo.Area a ON a.Id = p.AreaId AND a.AreaType = N'MainOffice'
WHERE p.Name LIKE N'Facility 1%' OR p.Name LIKE N'Facility-1%';
IF OBJECT_ID('dbo.BedSowingLocations','U') IS NOT NULL
    EXEC (N'SELECT l.PolyhouseId, l.AreaId, l.IsActive AS BedLocationActive FROM dbo.BedSowingLocations l');
ELSE
    SELECT 'MISSING dbo.BedSowingLocations (needs 2026-10-12_BedSowing.sql)' AS BedSowingLocations;

PRINT '=== 4. Maya Shinde (expect ONE active user; never create another) ===';
SELECT u.Id AS UserId, u.Username, u.Name, u.IsActive FROM dbo.IMSUsers u
WHERE u.Username = N'Maya' OR u.Name LIKE N'%Maya%Shinde%';
SELECT ur.UserId, r.Name AS RoleName, ur.AreaId, a.Name AS AreaName FROM dbo.UserRoles ur
JOIN dbo.Roles r ON r.Id = ur.RoleId LEFT JOIN dbo.Area a ON a.Id = ur.AreaId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId
WHERE u.Username = N'Maya' OR u.Name LIKE N'%Maya%Shinde%'
ORDER BY r.Name;
SELECT DISTINCT p.Code AS PermissionCode, ur.AreaId FROM dbo.UserRoles ur
JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId
WHERE (u.Username = N'Maya' OR u.Name LIKE N'%Maya%Shinde%')
  AND (p.Code LIKE N'Sowing.%' OR p.Code LIKE N'BedSowing.%' OR p.Code LIKE N'SeedStock.%' OR p.Code LIKE N'TrayStock.%')
ORDER BY p.Code;
IF OBJECT_ID('dbo.BedSowingApprover','U') IS NOT NULL
    EXEC (N'SELECT b.UserId AS BedSowingApproverUserId, u.Name FROM dbo.BedSowingApprover b JOIN dbo.IMSUsers u ON u.Id = b.UserId');

PRINT '=== 5. Source totals by date (79 rows, 6,939 trays, 1,026,098 seedlings: row 35 entered as 150 Cavity / 900 by owner decision) ===';
SELECT CONVERT(VARCHAR(10), SowingDate, 105) AS SowingDate, COUNT(*) AS SourceRows, SUM(Trays) AS Trays, SUM(Seedlings) AS Seedlings
FROM #Src GROUP BY SowingDate
UNION ALL SELECT 'TOTAL', COUNT(*), SUM(Trays), SUM(Seedlings) FROM #Src;

PRINT '=== 6. Row checks: exact variety, cavity allowed by THIS database, trays x cavity = seedlings, source type ===';
SELECT s.RowNo, CONVERT(VARCHAR(10), s.SowingDate, 105) AS SowingDate, s.SourceVariety, s.CavityType, s.Trays, s.Seedlings,
       sp.Id AS SpeciesId, pt.Name AS PlantType, sp.ReadyStockDays,
       CASE WHEN sp.Id IS NULL THEN 'NO EXACT VARIETY' ELSE 'ok' END AS VarietyCheck,
       CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.CK_SeedSowings_CavityType')) LIKE N'%''' + s.CavityType + N'''%' THEN 'ok' ELSE 'CAVITY NOT ALLOWED' END AS CavityCheck,
       CASE WHEN TRY_CAST(LEFT(s.CavityType, CHARINDEX(N' ', s.CavityType) - 1) AS INT) * s.Trays = s.Seedlings THEN 'ok' ELSE 'TRAYS x CAVITY <> SEEDLINGS' END AS ArithmeticCheck,
       CASE WHEN s.SourceVariety LIKE N'%(SR)%' OR s.SourceVariety LIKE N'%(K.K)%' OR s.SourceVariety LIKE N'%Cutting%' OR pt.Name = N'CHRYSANTHEMUM'
            THEN 'POSSIBLE CUTTING SOURCE' ELSE '' END AS SourceFlag
FROM #Src s
LEFT JOIN dbo.PlantSpecies sp ON LTRIM(RTRIM(REPLACE(REPLACE(sp.Name, CHAR(13), N''), CHAR(10), N''))) = LTRIM(RTRIM(REPLACE(REPLACE(s.SourceVariety, N' (SR)', N''), N' (K.K)', N'')))
LEFT JOIN dbo.PlantTypes pt ON pt.Id = sp.PlantTypeId
ORDER BY s.RowNo;

PRINT '=== 7. No exact match: same-genus candidates (for the owner''s mapping worksheet; nothing is chosen here) ===';
SELECT DISTINCT s.SourceVariety, c.Id AS CandidateSpeciesId, c.Name AS CandidateName, c.ReadyStockDays
FROM #Src s
LEFT JOIN dbo.PlantSpecies sp ON LTRIM(RTRIM(REPLACE(REPLACE(sp.Name, CHAR(13), N''), CHAR(10), N''))) = LTRIM(RTRIM(REPLACE(REPLACE(s.SourceVariety, N' (SR)', N''), N' (K.K)', N'')))
JOIN dbo.PlantSpecies c ON LTRIM(c.Name) LIKE LEFT(s.SourceVariety, CHARINDEX(N' ', s.SourceVariety + N' ') - 1) + N'%'
WHERE sp.Id IS NULL
ORDER BY s.SourceVariety, c.Name;

PRINT '=== 8. Main Office seed lots of the exactly matched varieties (balances) ===';
SELECT DISTINCT sp.Id AS SpeciesId, sp.Name AS Variety, ss.Id AS SeedStockId, ss.BatchNo, src.Name AS Supplier,
       ss.PhysicalQuantity, ss.InTransitQuantity, ss.AvailableQuantity
FROM #Src s
JOIN dbo.PlantSpecies sp ON LTRIM(RTRIM(REPLACE(REPLACE(sp.Name, CHAR(13), N''), CHAR(10), N''))) = LTRIM(RTRIM(REPLACE(REPLACE(s.SourceVariety, N' (SR)', N''), N' (K.K)', N'')))
JOIN dbo.SeedStock ss ON ss.SpeciesId = sp.Id
JOIN dbo.Area a ON a.Id = ss.AreaId AND a.AreaType = N'MainOffice'
LEFT JOIN dbo.SeedSources src ON src.Id = ss.SeedSourceId
ORDER BY sp.Name, ss.Id;

PRINT '=== 9. Existing sowings 01-10-2026 .. 12-10-2026 (anything here may duplicate this batch) ===';
IF OBJECT_ID('dbo.BedSowings','U') IS NOT NULL
    EXEC (N'SELECT CONVERT(VARCHAR(10), b.SowingDate, 105) AS SowingDate, COUNT(*) AS BedSowings, SUM(b.Quantity) AS Seeds,
                   SUM(CASE WHEN b.Remarks LIKE N''%MAIN-OFFICE-BED-SOWING-2026-10%'' THEN 1 ELSE 0 END) AS TaggedWithThisBatch
            FROM dbo.BedSowings b WHERE b.SowingDate BETWEEN ''2026-10-01'' AND ''2026-10-12''
            GROUP BY b.SowingDate ORDER BY b.SowingDate');
SELECT CONVERT(VARCHAR(10), ss.SowingDate, 105) AS SowingDate, ss.CavityType, COUNT(*) AS TrayBatches, SUM(ss.NumberOfTrays) AS Trays, SUM(ss.QuantitySown) AS Plants
FROM dbo.SeedSowings ss WHERE ss.SowingDate BETWEEN '2026-10-01' AND '2026-10-12'
GROUP BY ss.SowingDate, ss.CavityType ORDER BY ss.SowingDate, ss.CavityType;
PRINT '--- source rows whose variety already has a tray batch on the same date ---';
SELECT s.RowNo, CONVERT(VARCHAR(10), s.SowingDate, 105) AS SowingDate, s.SourceVariety, ss.SowingCode, ss.CavityType, ss.NumberOfTrays
FROM #Src s
JOIN dbo.PlantSpecies sp ON LTRIM(RTRIM(REPLACE(REPLACE(sp.Name, CHAR(13), N''), CHAR(10), N''))) = LTRIM(RTRIM(REPLACE(REPLACE(s.SourceVariety, N' (SR)', N''), N' (K.K)', N'')))
JOIN dbo.SeedSowings ss ON ss.SpeciesId = sp.Id AND ss.SowingDate = s.SowingDate
ORDER BY s.RowNo;

PRINT '=== 10. Empty trays and growing material at Facility 1 vs what these rows would consume ===';
SELECT s.CavityType, SUM(s.Trays) AS TraysNeeded,
       ISNULL((SELECT SUM(t.PhysicalQuantity) FROM dbo.TrayStock t JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId
                JOIN dbo.Area a ON a.Id = t.AreaId AND a.AreaType = N'MainOffice'
                WHERE t.TraySize = s.CavityType AND t.IsActive = 1 AND p.Name LIKE N'Facility 1%'), 0) AS EmptyTraysAtFacility1
FROM #Src s GROUP BY s.CavityType ORDER BY s.CavityType;
IF OBJECT_ID('dbo.MaterialConsumptionRules','U') IS NOT NULL
    EXEC (N'SELECT mt.Name AS Material, r.CavityType, r.QuantityPerTray, mt.Unit,
                   ISNULL((SELECT SUM(ms.CurrentQuantity) FROM dbo.MaterialStock ms JOIN dbo.Polyhouses p ON p.Id = ms.PolyhouseId
                           WHERE ms.MaterialTypeId = r.MaterialTypeId AND p.Name LIKE N''Facility 1%''), 0) AS InStockAtFacility1
            FROM dbo.MaterialConsumptionRules r JOIN dbo.MaterialTypes mt ON mt.Id = r.MaterialTypeId
            WHERE r.IsActive = 1 ORDER BY mt.Name, r.CavityType');

PRINT '=== 11. Unresolved / excluded items (kept OUT of every write script until the owner decides) ===';
SELECT * FROM (VALUES
  (N'Row 35: 08-10 Pansy Viola Cornuta Admire Maxi Mix, 6 trays', N'RESOLVED 14-10-2026: supplied as 6 x 160 (=960; message said 900); entered as 150 Cavity = 900.'),
  (N'20i. Celosia glorious mix - 18', N'Date and cavity unclear: not assigned, not in any total.'),
  (N'10-10 SR cuttings: Salvia Blue 56, Rockin Deep Purple 277, Rockin Flamingo Red 233, Dahlia Magenta 407, Grand White 270 (= 1,243)', N'Cuttings, not tray sowings: not in the 6,939 / 1,026,158 totals.')
) v(Item, Status);

DROP TABLE #Src;
PRINT '=== Preflight finished: nothing in this database was changed. ===';
