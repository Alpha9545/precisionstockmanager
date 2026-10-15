-- ============================================================================
-- Verify_After.sql   (2026-10-15, duplicate-variety cleanup) -- READ-ONLY, writes nothing.
--
-- Run after: 2026-10-15_PlantSpeciesIsActive.sql, Fix_Duplicate_Varieties.sql (@Commit = 1),
--            2026-10-15_PlantSpeciesUniqueActiveName.sql (and the old-sowing script, if used).
-- Does not name a database: select it first. Every check prints PASS / FAIL; the last grid is the summary.
-- ============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;

DECLARE @Ws NVARCHAR(10) = N' ' + NCHAR(9) + NCHAR(10) + NCHAR(13) + NCHAR(160);
DROP TABLE IF EXISTS #Check;
CREATE TABLE #Check (No INT IDENTITY, CheckName NVARCHAR(120), Result NVARCHAR(4), Detail NVARCHAR(400));

SELECT @@SERVERNAME AS ServerName, DB_NAME() AS DatabaseName, SYSDATETIME() AS CheckedAt;

-- 1. schema
INSERT #Check (CheckName, Result, Detail)
SELECT N'PlantSpecies.IsActive exists', CASE WHEN COL_LENGTH(N'dbo.PlantSpecies', N'IsActive') IS NULL THEN N'FAIL' ELSE N'PASS' END, NULL;
INSERT #Check (CheckName, Result, Detail)
SELECT N'Unique index UX_PlantSpecies_Name_Active exists',
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PlantSpecies') AND name = N'UX_PlantSpecies_Name_Active' AND is_unique = 1 AND filter_definition LIKE N'%IsActive%=%1%')
            THEN N'PASS' ELSE N'FAIL' END, NULL;
IF COL_LENGTH(N'dbo.PlantSpecies', N'IsActive') IS NULL
BEGIN
    SELECT * FROM #Check ORDER BY No;
    RETURN;
END

-- the rest needs IsActive (dynamic SQL so this file compiles without it)
EXEC sp_executesql N'
DECLARE @Ws NVARCHAR(10) = N'' '' + NCHAR(9) + NCHAR(10) + NCHAR(13) + NCHAR(160);

-- 2. no two ACTIVE varieties share a name (capitals / spaces ignored, as Add Variety compares)
INSERT #Check (CheckName, Result, Detail)
SELECT N''No duplicate active variety names'', CASE WHEN COUNT(*) = 0 THEN N''PASS'' ELSE N''FAIL'' END,
       NULLIF(STRING_AGG(CAST(d.K AS NVARCHAR(MAX)), N''; ''), N'''')
FROM (SELECT UPPER(REPLACE(REPLACE(REPLACE(TRIM(@Ws FROM Name), N'' '', N'' '' + NCHAR(7)), NCHAR(7) + N'' '', N''''), NCHAR(7), N'''')) AS K
      FROM dbo.PlantSpecies WHERE IsActive = 1 GROUP BY UPPER(REPLACE(REPLACE(REPLACE(TRIM(@Ws FROM Name), N'' '', N'' '' + NCHAR(7)), NCHAR(7) + N'' '', N''''), NCHAR(7), N''''))
      HAVING COUNT(*) > 1) d;

-- 3. every name trimmed
INSERT #Check (CheckName, Result, Detail)
SELECT N''All variety names trimmed'', CASE WHEN COUNT(*) = 0 THEN N''PASS'' ELSE N''FAIL'' END,
       NULLIF(STRING_AGG(CAST(Id AS NVARCHAR(12)), N'', ''), N'''')
FROM dbo.PlantSpecies WHERE DATALENGTH(Name) <> DATALENGTH(TRIM(@Ws FROM Name));

-- 4. the kept names: each exactly once and active
INSERT #Check (CheckName, Result, Detail)
SELECT N''Kept variety: '' + v.N, CASE WHEN COUNT(s.Id) = 1 THEN N''PASS'' ELSE N''FAIL'' END,
       CONCAT(N''active rows: '', COUNT(s.Id), N''; Id '', MIN(s.Id), N''; days '', MIN(s.ReadyStockDays))
FROM (VALUES (N''Lantana Beach''), (N''Lantana Coconut''), (N''Lantana IBIS''), (N''Lantana Litchi''), (N''Lantana Nandu''), (N''Lantana Pitaya''),
             (N''Lantana Red''), (N''Lantana Transizin''), (N''Aster chinensis Gala Mix''), (N''M.G Hot pack mix''), (N''Aishwarya Yellow''),
             (N''Pramila Bronze''), (N''Sharvari Purple''), (N''SH-WR''), (N''Jayashree Bronze Red'')) v (N)
LEFT JOIN dbo.PlantSpecies s ON s.IsActive = 1 AND s.Name = v.N
GROUP BY v.N;

-- 5. the old names are gone from active varieties
INSERT #Check (CheckName, Result, Detail)
SELECT N''Old names not active (Latania..., Callistephus, Mg. Hot Pak)'', CASE WHEN COUNT(*) = 0 THEN N''PASS'' ELSE N''FAIL'' END,
       NULLIF(STRING_AGG(CONCAT(Id, N'' '', Name), N'', ''), N'''')
FROM dbo.PlantSpecies WHERE IsActive = 1 AND (Name LIKE N''Latania%'' OR Name = N''Callistephus Gala Mix'' OR Name = N''Mg. Hot Pak mix'');

-- 6. Lantana growing days
INSERT #Check (CheckName, Result, Detail)
SELECT N''Every active Lantana variety has 25 growing days'', CASE WHEN COUNT(*) = 0 THEN N''PASS'' ELSE N''FAIL'' END,
       NULLIF(STRING_AGG(CONCAT(Name, N'' = '', ISNULL(CAST(ReadyStockDays AS NVARCHAR(12)), N''empty'')), N'', ''), N'''')
FROM dbo.PlantSpecies WHERE IsActive = 1 AND Name LIKE N''Lantana %'' AND ISNULL(ReadyStockDays, -1) <> 25;

-- 7. retired rows: all named DUP-..., all inactive
INSERT #Check (CheckName, Result, Detail)
SELECT N''Retired rows are inactive and named DUP-...'', CASE WHEN ISNULL(SUM(CASE WHEN x.IsDup <> 1 OR x.IsActive <> 0 THEN 1 ELSE 0 END), 0) = 0 THEN N''PASS'' ELSE N''FAIL'' END,
       CONCAT(COUNT(*), N'' retired row(s)'')
FROM (SELECT Name, IsActive, CASE WHEN Name LIKE N''DUP-%'' THEN 1 ELSE 0 END AS IsDup FROM dbo.PlantSpecies) x
WHERE x.IsDup = 1 OR x.IsActive = 0;';

-- 8. no record anywhere still points at a retired DUP-... variety
DECLARE @sql NVARCHAR(MAX) = N'', @t SYSNAME, @c SYSNAME;
DROP TABLE IF EXISTS #Left;
CREATE TABLE #Left (T SYSNAME, C SYSNAME, N INT);
DECLARE rc CURSOR LOCAL FAST_FORWARD FOR
    SELECT OBJECT_NAME(fkc.parent_object_id), COL_NAME(fkc.parent_object_id, fkc.parent_column_id)
    FROM sys.foreign_key_columns fkc JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
    WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.PlantSpecies')
    UNION
    SELECT v.T, v.C FROM (VALUES (N'BookingRevisions', N'NewSpeciesId'), (N'BookingRevisions', N'PreviousSpeciesId'), (N'CuttingProductions', N'SpeciesId'),
             (N'LabRequests', N'SpeciesId'), (N'OutletBookingItems', N'SpeciesId'), (N'OutletSaleItems', N'SpeciesId'),
             (N'OutletWastages', N'SpeciesId'), (N'PotProductionBatches', N'SpeciesId')) v (T, C)
    WHERE COL_LENGTH(N'dbo.' + v.T, v.C) IS NOT NULL;
OPEN rc; FETCH NEXT FROM rc INTO @t, @c;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'INSERT #Left SELECT @t, @c, COUNT(*) FROM dbo.' + QUOTENAME(@t) + N' x JOIN dbo.PlantSpecies s ON s.Id = x.' + QUOTENAME(@c)
             + N' WHERE s.Name LIKE N''DUP-%'' HAVING COUNT(*) > 0;';
    EXEC sp_executesql @sql, N'@t SYSNAME, @c SYSNAME', @t, @c;
    FETCH NEXT FROM rc INTO @t, @c;
END
CLOSE rc; DEALLOCATE rc;
INSERT #Check (CheckName, Result, Detail)
SELECT N'No record points at a retired DUP- variety', CASE WHEN COUNT(*) = 0 THEN N'PASS' ELSE N'FAIL' END,
       NULLIF(STRING_AGG(CONCAT(T, N'.', C, N' = ', N), N', '), N'')
FROM #Left;

-- 9. stock and ledger agree (seed stock balance = its ledger, per lot)
INSERT #Check (CheckName, Result, Detail)
SELECT N'Seed Stock balances equal their ledger', CASE WHEN COUNT(*) = 0 THEN N'PASS' ELSE N'FAIL' END,
       NULLIF(STRING_AGG(CAST(x.Id AS NVARCHAR(12)), N', '), N'')
FROM (SELECT ss.Id FROM dbo.SeedStock ss LEFT JOIN dbo.SeedStockTransactions t ON t.SeedStockId = ss.Id
      GROUP BY ss.Id, ss.PhysicalQuantity HAVING ss.PhysicalQuantity <> ISNULL(SUM(t.Quantity), 0)) x;

SELECT No, Result, CheckName, Detail FROM #Check ORDER BY No;
SELECT SUM(CASE WHEN Result = N'PASS' THEN 1 ELSE 0 END) AS Passed, SUM(CASE WHEN Result = N'FAIL' THEN 1 ELSE 0 END) AS Failed FROM #Check;

-- for the record (compare with the Fix preview grids)
SELECT (SELECT COUNT(*) FROM dbo.PlantSpecies) AS Varieties, (SELECT COUNT(*) FROM dbo.SeedStock) AS SeedLots,
       (SELECT SUM(PhysicalQuantity) FROM dbo.SeedStock) AS SeedsInStock, (SELECT COUNT(*) FROM dbo.MotherPlants) AS MotherPlants,
       (SELECT COUNT(*) FROM dbo.Bookings) AS Bookings, (SELECT COUNT(*) FROM dbo.Dispatches) AS Dispatches;
