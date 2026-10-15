-- ============================================================================
-- Fix_Duplicate_Varieties.sql   (2026-10-15)
--
-- Retires duplicate varieties in dbo.PlantSpecies and moves ALL their history to the kept variety.
-- No variety row and no transaction row is ever deleted.
--
--   @Commit = 0  PREVIEW (default): read-only. Shows the plan, the records that would move, the name / growing-day
--                changes and any BLOCKERS. Writes nothing.
--   @Commit = 1  SAVE: the same plan in ONE transaction; any error -> everything rolled back. Refused while any
--                blocker exists or while dbo.PlantSpecies.IsActive is missing (run 2026-10-15_PlantSpeciesIsActive.sql).
--
-- Does not name a database: select it first (SSMS database dropdown / sqlcmd -d). Matches by NAME, never by Id,
-- so the same file runs unchanged on local and live. Safe to run more than once (a second run finds nothing to do).
--
-- Owner decisions 2026-10-15:
--   * Each group below = the names that are one variety. KEEP = the OLDEST Id (lowest) among the active varieties whose
--     name matches any name of the group; it gets the group's final name. Every other match is a duplicate: its records
--     move to the kept Id, then it is set IsActive = 0 and renamed "DUP-<Id>-<old name>".
--     If only one match exists (e.g. live without this morning's rows), it is simply renamed.
--   * Names compare like Add Variety does: capitals ignored, spaces / tabs / line breaks at the ends ignored,
--     runs of spaces inside count as one space.
--   * All variety names are trimmed (spaces, tabs, line breaks at start / end).
--   * Every active "Lantana ..." variety gets Ready Stock Days = 25.
--   * "Marigold French Hot Pack Mix" is NOT part of this cleanup.
--
-- BLOCKERS (the script stops instead of guessing -- nothing is disabled, no trigger is bypassed):
--   - a group whose matches belong to different Plant Types
--   - a duplicate used in a table whose triggers forbid changing the variety (SeedSowings, PotProductionBatches,
--     OutletBookingItems) or by a child row of a composite (row + variety) foreign key
--   - a stock / balance row of a duplicate whose unique key (e.g. Area + Batch) already exists for the kept variety:
--     those quantities must be combined by hand first
--   - a bed sowing of a duplicate that its trigger would not let change variety (not untouched, or ledger mismatch)
--   - two different active varieties ending up with the same name after the cleanup
-- ============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;          -- 0 = PREVIEW (read-only), 1 = SAVE

DECLARE @Ws NVARCHAR(10) = N' ' + NCHAR(9) + NCHAR(10) + NCHAR(13) + NCHAR(160);
DECLARE @HasIsActive BIT = CASE WHEN COL_LENGTH(N'dbo.PlantSpecies', N'IsActive') IS NULL THEN 0 ELSE 1 END;
DECLARE @sql NVARCHAR(MAX), @t SYSNAME, @c SYSNAME, @n INT;

IF OBJECT_ID(N'dbo.PlantSpecies', N'U') IS NULL
    THROW 53301, 'dbo.PlantSpecies not found -- wrong database selected?', 1;

SELECT @@SERVERNAME AS ServerName, DB_NAME() AS DatabaseName,
       CASE WHEN @Commit = 1 THEN 'SAVE (@Commit = 1)' ELSE 'PREVIEW (@Commit = 0) - read-only' END AS Mode,
       CASE WHEN @HasIsActive = 1 THEN 'present' ELSE 'MISSING - run 2026-10-15_PlantSpeciesIsActive.sql before saving' END AS PlantSpecies_IsActive;

DROP TABLE IF EXISTS #Spec, #Species, #Member, #Refs, #RefCount, #RefCountAfter, #TableTotal, #Blocker, #NameChange, #DaysChange;

-- ---- 1. the groups (final name + every name that is the same variety) -------------------------------------------
CREATE TABLE #Spec (GroupNo INT, FinalName NVARCHAR(100), AliasName NVARCHAR(100));
INSERT #Spec (GroupNo, FinalName, AliasName) VALUES
 ( 1, N'Lantana Beach',            N'Latania Beach'),            ( 1, N'Lantana Beach',            N'Lantana Beach'),
 ( 2, N'Lantana Coconut',          N'Latania Coconut'),          ( 2, N'Lantana Coconut',          N'Lantana Coconut'),
 ( 3, N'Lantana IBIS',             N'Latania IBIS'),             ( 3, N'Lantana IBIS',             N'Lantana IBIS'),
 ( 4, N'Lantana Litchi',           N'Latania Litchi'),           ( 4, N'Lantana Litchi',           N'Lantana Litchi'),
 ( 5, N'Lantana Nandu',            N'Latania Nadu'),             ( 5, N'Lantana Nandu',            N'Lantana Nandu'),
 ( 6, N'Lantana Pitaya',           N'Latania Pattaya'),          ( 6, N'Lantana Pitaya',           N'Lantana Pitaya'),
 ( 7, N'Aster chinensis Gala Mix', N'Callistephus Gala Mix'),    ( 7, N'Aster chinensis Gala Mix', N'Aster chinensis Gala Mix'),
 ( 8, N'M.G Hot pack mix',         N'Mg. Hot Pak mix'),          ( 8, N'M.G Hot pack mix',         N'M.G Hot pack mix'),
 ( 9, N'Aishwarya Yellow',         N'Aishwarya Yellow'),
 (10, N'Pramila Bronze',           N'Pramila Bronze'),
 (11, N'Sharvari Purple',          N'Sharvari Purple'),
 (12, N'SH-WR',                    N'SH-WR'),
 (13, N'Jayashree Bronze Red',     N'Jayashree Bronze Red'),
 (14, N'Lantana Red',              N'Latania Red'),              (14, N'Lantana Red',              N'Lantana Red'),
 (15, N'Lantana Transizin',        N'Latania Transizin'),        (15, N'Lantana Transizin',        N'Lantana Transizin');

-- ---- 2. every variety with its trimmed name and comparison key ---------------------------------------------------
CREATE TABLE #Species (Id INT PRIMARY KEY, PlantTypeId INT NULL, PlantTypeName NVARCHAR(100) NULL, Name NVARCHAR(100),
                       TrimmedName NVARCHAR(100), NameKey NVARCHAR(100), ReadyStockDays INT NULL, Retired BIT);
SET @sql = N'
INSERT #Species (Id, PlantTypeId, PlantTypeName, Name, TrimmedName, NameKey, ReadyStockDays, Retired)
SELECT s.Id, s.PlantTypeId, LTRIM(RTRIM(pt.Name)), s.Name, x.T,
       UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(x.T, NCHAR(9), N'' ''), NCHAR(10), N'' ''), NCHAR(13), N'' ''), NCHAR(160), N'' ''),
             N'' '', N'' '' + NCHAR(7)), NCHAR(7) + N'' '', N''''), NCHAR(7), N'''')),
       s.ReadyStockDays,
       CASE WHEN s.Name LIKE N''DUP-%'' ' + CASE WHEN @HasIsActive = 1 THEN N'OR s.IsActive = 0 ' ELSE N'' END + N'THEN 1 ELSE 0 END
FROM dbo.PlantSpecies s
LEFT JOIN dbo.PlantTypes pt ON pt.Id = s.PlantTypeId
CROSS APPLY (SELECT TRIM(@Ws FROM s.Name) AS T) x;';
EXEC sp_executesql @sql, N'@Ws NVARCHAR(10)', @Ws;

-- ---- 3. group members: keep = lowest Id -------------------------------------------------------------------------
CREATE TABLE #Member (GroupNo INT, SpeciesId INT PRIMARY KEY, IsKeep BIT, KeepId INT);
INSERT #Member (GroupNo, SpeciesId, IsKeep, KeepId)
SELECT DISTINCT g.GroupNo, s.Id, 0, 0
FROM #Spec g
JOIN #Species s ON s.Retired = 0 AND s.NameKey = UPPER(TRIM(@Ws FROM g.AliasName));
UPDATE m SET KeepId = k.KeepId, IsKeep = CASE WHEN m.SpeciesId = k.KeepId THEN 1 ELSE 0 END
FROM #Member m JOIN (SELECT GroupNo, MIN(SpeciesId) AS KeepId FROM #Member GROUP BY GroupNo) k ON k.GroupNo = m.GroupNo;

-- ---- 4. every column that holds a variety Id (real FKs + known columns without an FK) -----------------------------
CREATE TABLE #Refs (Ord INT, T SYSNAME, C SYSNAME, HasFK BIT);
INSERT #Refs (Ord, T, C, HasFK)
SELECT 0, OBJECT_NAME(fkc.parent_object_id), COL_NAME(fkc.parent_object_id, fkc.parent_column_id), 1
FROM sys.foreign_key_columns fkc JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.PlantSpecies') AND OBJECT_SCHEMA_NAME(fkc.parent_object_id) = N'dbo';
INSERT #Refs (Ord, T, C, HasFK)
SELECT 0, v.T, v.C, 0
FROM (VALUES (N'BookingRevisions', N'NewSpeciesId'), (N'BookingRevisions', N'PreviousSpeciesId'), (N'CuttingProductions', N'SpeciesId'),
             (N'LabRequests', N'SpeciesId'), (N'OutletBookingItems', N'SpeciesId'), (N'OutletSaleItems', N'SpeciesId'),
             (N'OutletWastages', N'SpeciesId'), (N'PotProductionBatches', N'SpeciesId')) v (T, C)
WHERE COL_LENGTH(N'dbo.' + v.T, v.C) IS NOT NULL AND NOT EXISTS (SELECT 1 FROM #Refs r WHERE r.T = v.T AND r.C = v.C);
-- parents before children, so each check constraint / trigger sees its parent already moved
UPDATE #Refs SET Ord = CASE T
    WHEN N'MotherPlants' THEN 1
    WHEN N'CuttingStock' THEN 2 WHEN N'PottedPlantStock' THEN 2 WHEN N'ReadyStock' THEN 2 WHEN N'SeedStock' THEN 2
    WHEN N'CuttingPlans' THEN 3 WHEN N'ActualCuttings' THEN 4 WHEN N'CuttingDeliveries' THEN 5
    WHEN N'PropagationBatches' THEN 6 WHEN N'PotProduction' THEN 7
    WHEN N'PottedPlantBookings' THEN 8 WHEN N'LabRequests' THEN 8 WHEN N'Dispatches' THEN 9
    WHEN N'BedSowings' THEN 10 ELSE 50 END;

-- records per member, and table totals (for the before / after check)
CREATE TABLE #RefCount (T SYSNAME, C SYSNAME, SpeciesId INT, N INT);
CREATE TABLE #RefCountAfter (T SYSNAME, C SYSNAME, SpeciesId INT, N INT);
CREATE TABLE #TableTotal (T SYSNAME PRIMARY KEY, Before INT, After INT NULL);
DECLARE rc CURSOR LOCAL FAST_FORWARD FOR SELECT T, C FROM #Refs ORDER BY Ord, T, C;
OPEN rc; FETCH NEXT FROM rc INTO @t, @c;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'INSERT #RefCount (T, C, SpeciesId, N) SELECT @t, @c, x.' + QUOTENAME(@c) + N', COUNT(*) FROM dbo.' + QUOTENAME(@t)
             + N' x JOIN #Member m ON m.SpeciesId = x.' + QUOTENAME(@c) + N' GROUP BY x.' + QUOTENAME(@c) + N';';
    EXEC sp_executesql @sql, N'@t SYSNAME, @c SYSNAME', @t, @c;
    IF NOT EXISTS (SELECT 1 FROM #TableTotal WHERE T = @t)
    BEGIN
        SET @sql = N'SELECT @n = COUNT(*) FROM dbo.' + QUOTENAME(@t) + N';';
        EXEC sp_executesql @sql, N'@n INT OUTPUT', @n = @n OUTPUT;
        INSERT #TableTotal (T, Before) VALUES (@t, @n);
    END
    FETCH NEXT FROM rc INTO @t, @c;
END
CLOSE rc; DEALLOCATE rc;

-- ---- 5. blockers ------------------------------------------------------------------------------------------------
CREATE TABLE #Blocker (Kind NVARCHAR(60), Detail NVARCHAR(400));

INSERT #Blocker (Kind, Detail)
SELECT N'Group spans plant types', CONCAT(N'Group ', m.GroupNo, N': ', STRING_AGG(CONCAT(s.Id, N' ', s.TrimmedName, N' (', s.PlantTypeName, N')'), N', '))
FROM #Member m JOIN #Species s ON s.Id = m.SpeciesId GROUP BY m.GroupNo HAVING COUNT(DISTINCT ISNULL(s.PlantTypeId, -1)) > 1;

INSERT #Blocker (Kind, Detail)
SELECT N'Variety cannot change (trigger)', CONCAT(rcnt.T, N'.', rcnt.C, N': ', rcnt.N, N' row(s) of duplicate ', rcnt.SpeciesId)
FROM #RefCount rcnt JOIN #Member m ON m.SpeciesId = rcnt.SpeciesId AND m.IsKeep = 0
WHERE rcnt.T IN (N'SeedSowings', N'PotProductionBatches', N'OutletBookingItems');

-- children of composite (row + variety) foreign keys: neither side can move first without breaking the key
DECLARE @fk SYSNAME, @child SYSNAME, @col SYSNAME;
DECLARE fc CURSOR LOCAL FAST_FORWARD FOR
    SELECT fk.name, OBJECT_NAME(fk.parent_object_id), COL_NAME(fkc.parent_object_id, fkc.parent_column_id)
    FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
    WHERE fk.referenced_object_id <> OBJECT_ID(N'dbo.PlantSpecies')
      AND COL_NAME(fkc.parent_object_id, fkc.parent_column_id) LIKE N'%SpeciesId'
      AND (SELECT COUNT(*) FROM sys.foreign_key_columns z WHERE z.constraint_object_id = fk.object_id) > 1;
OPEN fc; FETCH NEXT FROM fc INTO @fk, @child, @col;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'INSERT #Blocker (Kind, Detail) SELECT N''Composite FK child'', CONCAT(@child, N''.'', @col, N'' via '', @fk, N'': '', COUNT(*), N'' row(s) of duplicate '', x.'
             + QUOTENAME(@col) + N') FROM dbo.' + QUOTENAME(@child) + N' x JOIN #Member m ON m.SpeciesId = x.' + QUOTENAME(@col)
             + N' AND m.IsKeep = 0 GROUP BY x.' + QUOTENAME(@col) + N';';
    EXEC sp_executesql @sql, N'@fk SYSNAME, @child SYSNAME, @col SYSNAME', @fk, @child, @col;
    FETCH NEXT FROM fc INTO @fk, @child, @col;
END
CLOSE fc; DEALLOCATE fc;

-- stock / balance rows: a duplicate's row whose unique key already exists for another member of its group
DECLARE @ix SYSNAME, @others NVARCHAR(MAX);
DECLARE ux CURSOR LOCAL FAST_FORWARD FOR
    SELECT r.T, r.C, i.name,
           ISNULL((SELECT STRING_AGG(N'(a.' + QUOTENAME(c2.name) + N' = b.' + QUOTENAME(c2.name) + N' OR (a.' + QUOTENAME(c2.name)
                                     + N' IS NULL AND b.' + QUOTENAME(c2.name) + N' IS NULL))', N' AND ')
                   FROM sys.index_columns ic2 JOIN sys.columns c2 ON c2.object_id = ic2.object_id AND c2.column_id = ic2.column_id
                   WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 0 AND c2.name <> r.C), N'1 = 1')
    FROM #Refs r JOIN sys.indexes i ON i.object_id = OBJECT_ID(N'dbo.' + r.T) AND i.is_unique = 1
    WHERE EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                  WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 AND c.name = r.C)
      AND EXISTS (SELECT 1 FROM #RefCount x JOIN #Member m ON m.SpeciesId = x.SpeciesId AND m.IsKeep = 0 WHERE x.T = r.T AND x.C = r.C);
OPEN ux; FETCH NEXT FROM ux INTO @t, @c, @ix, @others;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'INSERT #Blocker (Kind, Detail) SELECT N''Stock key collision'', CONCAT(@t, N'' ('', @ix, N''): duplicate '', a.' + QUOTENAME(@c)
             + N', N'' and '', b.' + QUOTENAME(@c) + N', N'' share a key -- combine quantities by hand first'')
FROM dbo.' + QUOTENAME(@t) + N' a JOIN #Member ma ON ma.SpeciesId = a.' + QUOTENAME(@c) + N' AND ma.IsKeep = 0
JOIN #Member mb ON mb.GroupNo = ma.GroupNo AND mb.SpeciesId <> ma.SpeciesId
JOIN dbo.' + QUOTENAME(@t) + N' b ON b.' + QUOTENAME(@c) + N' = mb.SpeciesId AND ' + @others + N';';
    EXEC sp_executesql @sql, N'@t SYSNAME, @ix SYSNAME', @t, @ix;
    FETCH NEXT FROM ux INTO @t, @c, @ix, @others;
END
CLOSE ux; DEALLOCATE ux;

-- bed sowings: TR_BedSowings_Guard lets the variety change only on an untouched bed whose seed ledger balances,
-- and only together with its seed lot (which must belong to the same group, so it moves too)
IF OBJECT_ID(N'dbo.BedSowings', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM #RefCount x JOIN #Member m ON m.SpeciesId = x.SpeciesId AND m.IsKeep = 0 WHERE x.T = N'BedSowings')
    EXEC sp_executesql N'
INSERT #Blocker (Kind, Detail)
SELECT N''Bed sowing cannot change variety'', CONCAT(b.BedSowingCode, N'' (status '', b.Status, N'', seed lot '', b.SeedStockId, N'')'')
FROM dbo.BedSowings b
JOIN #Member m ON m.SpeciesId = b.SpeciesId AND m.IsKeep = 0
LEFT JOIN dbo.SeedStock ss ON ss.Id = b.SeedStockId
LEFT JOIN #Member ms ON ms.SpeciesId = ss.SpeciesId AND ms.GroupNo = m.GroupNo
LEFT JOIN dbo.Area a ON a.Id = ss.AreaId
WHERE b.Status <> N''PendingTransplant'' OR b.TransplantedQuantity + b.LostQuantity <> 0 OR b.ReadyStockDays IS NOT NULL
   OR EXISTS (SELECT 1 FROM dbo.BedSowingOperations o WHERE o.BedSowingId = b.Id)
   OR ms.SpeciesId IS NULL OR ISNULL(a.AreaType, N'''') <> N''MainOffice'' OR ISNULL(a.IsActive, 0) = 0
   OR b.SeedLotNo <> ss.BatchNo OR ISNULL(b.SeedSourceId, -1) <> ISNULL(ss.SeedSourceId, -1)
   OR ISNULL((SELECT SUM(t.Quantity) FROM dbo.SeedStockTransactions t
              WHERE t.ReferenceType = N''BedSowing'' AND t.ReferenceId = b.Id AND t.SeedStockId = b.SeedStockId), 0) <> -b.Quantity
   OR EXISTS (SELECT 1 FROM dbo.SeedStockTransactions t WHERE t.ReferenceType = N''BedSowing'' AND t.ReferenceId = b.Id AND t.SeedStockId <> b.SeedStockId
              GROUP BY t.SeedStockId HAVING SUM(t.Quantity) <> 0);';

-- ---- 6. name and growing-day changes (DATALENGTH too: '=' ignores trailing spaces even in BIN2) ----------------------------------------------------------------------------
CREATE TABLE #NameChange (Id INT PRIMARY KEY, OldName NVARCHAR(100), NewName NVARCHAR(100), Change NVARCHAR(40), Retire BIT);
INSERT #NameChange (Id, OldName, NewName, Change, Retire)
SELECT s.Id, s.Name, LEFT(N'DUP-' + CAST(s.Id AS NVARCHAR(12)) + N'-' + s.TrimmedName, 100), N'retire duplicate', 1
FROM #Member m JOIN #Species s ON s.Id = m.SpeciesId WHERE m.IsKeep = 0;
INSERT #NameChange (Id, OldName, NewName, Change, Retire)
SELECT s.Id, s.Name, f.FinalName, CASE WHEN UPPER(s.TrimmedName) = UPPER(f.FinalName) THEN N'trim / capitals' ELSE N'rename (kept)' END, 0
FROM #Member m JOIN #Species s ON s.Id = m.SpeciesId
JOIN (SELECT DISTINCT GroupNo, FinalName FROM #Spec) f ON f.GroupNo = m.GroupNo
WHERE m.IsKeep = 1 AND (s.Name COLLATE Latin1_General_BIN2 <> f.FinalName COLLATE Latin1_General_BIN2 OR DATALENGTH(s.Name) <> DATALENGTH(f.FinalName));
INSERT #NameChange (Id, OldName, NewName, Change, Retire)
SELECT s.Id, s.Name, s.TrimmedName, N'trim', 0
FROM #Species s
WHERE s.Retired = 0 AND NOT EXISTS (SELECT 1 FROM #Member m WHERE m.SpeciesId = s.Id)
  AND (s.Name COLLATE Latin1_General_BIN2 <> s.TrimmedName COLLATE Latin1_General_BIN2 OR DATALENGTH(s.Name) <> DATALENGTH(s.TrimmedName));

-- names after the cleanup (active varieties only): the same name twice is a blocker
;WITH Final AS (
    SELECT s.Id, ISNULL(nc.NewName, s.Name) AS FinalName, s.PlantTypeName
    FROM #Species s LEFT JOIN #NameChange nc ON nc.Id = s.Id
    WHERE s.Retired = 0 AND ISNULL(nc.Retire, 0) = 0)
INSERT #Blocker (Kind, Detail)
SELECT N'Same name twice after cleanup', CONCAT(MIN(f.FinalName), N': Ids ', STRING_AGG(CONCAT(f.Id, N' (', f.PlantTypeName, N')'), N', ') WITHIN GROUP (ORDER BY f.Id))
FROM Final f
GROUP BY UPPER(REPLACE(REPLACE(REPLACE(TRIM(@Ws FROM f.FinalName), N' ', N' ' + NCHAR(7)), NCHAR(7) + N' ', N''), NCHAR(7), N''))
HAVING COUNT(*) > 1;

CREATE TABLE #DaysChange (Id INT PRIMARY KEY, Name NVARCHAR(100), OldDays INT NULL, NewDays INT);
INSERT #DaysChange (Id, Name, OldDays, NewDays)
SELECT s.Id, ISNULL(nc.NewName, s.TrimmedName), s.ReadyStockDays, 25
FROM #Species s LEFT JOIN #NameChange nc ON nc.Id = s.Id
WHERE s.Retired = 0 AND ISNULL(nc.Retire, 0) = 0 AND ISNULL(nc.NewName, s.TrimmedName) LIKE N'Lantana %'
  AND ISNULL(s.ReadyStockDays, -1) <> 25;

-- ---- 7. PREVIEW grids -------------------------------------------------------------------------------------------
SELECT f.GroupNo, f.FinalName,
       k.SpeciesId AS KeepId, ks.TrimmedName AS KeepCurrentName, ks.PlantTypeName, ks.ReadyStockDays AS KeepDays,
       ISNULL((SELECT STRING_AGG(CONCAT(d.SpeciesId, N' ', ds.TrimmedName, N' [', ds.ReadyStockDays, N'd]'), N', ')
               FROM #Member d JOIN #Species ds ON ds.Id = d.SpeciesId WHERE d.GroupNo = f.GroupNo AND d.IsKeep = 0), N'-') AS Duplicates,
       CASE WHEN k.SpeciesId IS NULL THEN N'NOT FOUND - skipped'
            WHEN EXISTS (SELECT 1 FROM #Member d WHERE d.GroupNo = f.GroupNo AND d.IsKeep = 0) THEN N'merge duplicates into keep'
            WHEN EXISTS (SELECT 1 FROM #NameChange nc WHERE nc.Id = k.SpeciesId) THEN N'rename only'
            ELSE N'already done' END AS Action
FROM (SELECT DISTINCT GroupNo, FinalName FROM #Spec) f
LEFT JOIN #Member k ON k.GroupNo = f.GroupNo AND k.IsKeep = 1
LEFT JOIN #Species ks ON ks.Id = k.SpeciesId
ORDER BY f.GroupNo;

SELECT m.GroupNo, x.SpeciesId AS DuplicateId, m.KeepId, x.T AS TableName, x.C AS ColumnName, x.N AS RecordsToMove
FROM #RefCount x JOIN #Member m ON m.SpeciesId = x.SpeciesId AND m.IsKeep = 0
ORDER BY m.GroupNo, x.SpeciesId, x.T;

SELECT Id, Change, REPLACE(REPLACE(REPLACE(OldName, NCHAR(13), N'\r'), NCHAR(10), N'\n'), NCHAR(9), N'\t') + N'|' AS OldName_EndMarked,
       NewName + N'|' AS NewName_EndMarked
FROM #NameChange ORDER BY CASE Change WHEN N'retire duplicate' THEN 1 WHEN N'rename (kept)' THEN 2 WHEN N'trim / capitals' THEN 3 ELSE 4 END, Id;

SELECT Id, Name, OldDays, NewDays FROM #DaysChange ORDER BY Name;

SELECT ISNULL(b.Kind, N'(none)') AS Blocker, ISNULL(b.Detail, N'No blockers - safe to save') AS Detail
FROM (SELECT 1 AS One) o LEFT JOIN #Blocker b ON 1 = 1 ORDER BY b.Kind, b.Detail;

SELECT (SELECT COUNT(*) FROM #Member WHERE IsKeep = 0) AS DuplicatesToRetire,
       (SELECT ISNULL(SUM(x.N), 0) FROM #RefCount x JOIN #Member m ON m.SpeciesId = x.SpeciesId AND m.IsKeep = 0) AS RecordsToMove,
       (SELECT COUNT(*) FROM #NameChange WHERE Change = N'rename (kept)') AS KeptRenamed,
       (SELECT COUNT(*) FROM #NameChange WHERE Change IN (N'trim', N'trim / capitals')) AS NamesTrimmed,
       (SELECT COUNT(*) FROM #DaysChange) AS LantanaDaysChanged,
       (SELECT COUNT(*) FROM #Refs) AS VarietyColumnsChecked,
       (SELECT COUNT(*) FROM #Blocker) AS Blockers;

IF @Commit = 0
BEGIN
    PRINT '=== PREVIEW only -- nothing written. Set @Commit = 1 to save. ===';
    RETURN;
END

-- ---- 8. SAVE ----------------------------------------------------------------------------------------------------
IF @HasIsActive = 0
    THROW 53310, 'dbo.PlantSpecies.IsActive is missing -- run 2026-10-15_PlantSpeciesIsActive.sql first. Nothing written.', 1;
IF EXISTS (SELECT 1 FROM #Blocker)
    THROW 53311, 'Blockers found (see the Blocker grid) -- nothing written.', 1;
IF NOT EXISTS (SELECT 1 FROM #Member WHERE IsKeep = 0) AND NOT EXISTS (SELECT 1 FROM #NameChange) AND NOT EXISTS (SELECT 1 FROM #DaysChange)
BEGIN
    PRINT '=== Nothing to do -- already clean. Nothing written. ===';
    RETURN;
END

BEGIN TRY
    BEGIN TRANSACTION FixDuplicateVarieties;

    -- 8a. move every record of each duplicate to its kept variety (parents first)
    DECLARE mv CURSOR LOCAL FAST_FORWARD FOR
        SELECT DISTINCT r.Ord, r.T, r.C FROM #Refs r
        JOIN #RefCount x ON x.T = r.T AND x.C = r.C JOIN #Member m ON m.SpeciesId = x.SpeciesId AND m.IsKeep = 0
        ORDER BY r.Ord, r.T, r.C;
    DECLARE @ord INT;
    OPEN mv; FETCH NEXT FROM mv INTO @ord, @t, @c;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'UPDATE x SET ' + QUOTENAME(@c) + N' = m.KeepId FROM dbo.' + QUOTENAME(@t) + N' x JOIN #Member m ON m.SpeciesId = x.'
                 + QUOTENAME(@c) + N' AND m.IsKeep = 0;';
        EXEC sp_executesql @sql;
        PRINT CONCAT('moved ', @@ROWCOUNT, ' row(s): ', @t, '.', @c);
        FETCH NEXT FROM mv INTO @ord, @t, @c;
    END
    CLOSE mv; DEALLOCATE mv;

    -- 8b. names, retirement, growing days
    UPDATE s SET Name = nc.NewName FROM dbo.PlantSpecies s JOIN #NameChange nc ON nc.Id = s.Id;
    EXEC sp_executesql N'UPDATE s SET IsActive = 0 FROM dbo.PlantSpecies s JOIN #NameChange nc ON nc.Id = s.Id AND nc.Retire = 1;';
    UPDATE s SET ReadyStockDays = d.NewDays FROM dbo.PlantSpecies s JOIN #DaysChange d ON d.Id = s.Id;

    -- 8c. verify inside the transaction: nothing left on a duplicate, each kept variety holds its group's total,
    --     every table has exactly as many rows as before
    DECLARE vc CURSOR LOCAL FAST_FORWARD FOR SELECT T, C FROM #Refs;
    OPEN vc; FETCH NEXT FROM vc INTO @t, @c;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'INSERT #RefCountAfter (T, C, SpeciesId, N) SELECT @t, @c, x.' + QUOTENAME(@c) + N', COUNT(*) FROM dbo.' + QUOTENAME(@t)
                 + N' x JOIN #Member m ON m.SpeciesId = x.' + QUOTENAME(@c) + N' GROUP BY x.' + QUOTENAME(@c) + N';';
        EXEC sp_executesql @sql, N'@t SYSNAME, @c SYSNAME', @t, @c;
        FETCH NEXT FROM vc INTO @t, @c;
    END
    CLOSE vc; DEALLOCATE vc;
    DECLARE tt CURSOR LOCAL FAST_FORWARD FOR SELECT T FROM #TableTotal;
    OPEN tt; FETCH NEXT FROM tt INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'SELECT @n = COUNT(*) FROM dbo.' + QUOTENAME(@t) + N';';
        EXEC sp_executesql @sql, N'@n INT OUTPUT', @n = @n OUTPUT;
        UPDATE #TableTotal SET After = @n WHERE T = @t;
        FETCH NEXT FROM tt INTO @t;
    END
    CLOSE tt; DEALLOCATE tt;

    IF EXISTS (SELECT 1 FROM #RefCountAfter a JOIN #Member m ON m.SpeciesId = a.SpeciesId AND m.IsKeep = 0)
       OR EXISTS (SELECT 1 FROM (SELECT x.T, x.C, m.KeepId, SUM(x.N) AS N FROM #RefCount x JOIN #Member m ON m.SpeciesId = x.SpeciesId GROUP BY x.T, x.C, m.KeepId) b
                  LEFT JOIN #RefCountAfter a ON a.T = b.T AND a.C = b.C AND a.SpeciesId = b.KeepId
                  WHERE ISNULL(a.N, 0) <> b.N)
       OR EXISTS (SELECT 1 FROM #TableTotal WHERE After <> Before)
       OR EXISTS (SELECT 1 FROM dbo.PlantSpecies s JOIN #NameChange nc ON nc.Id = s.Id WHERE s.Name COLLATE Latin1_General_BIN2 <> nc.NewName COLLATE Latin1_General_BIN2 OR DATALENGTH(s.Name) <> DATALENGTH(nc.NewName))
        THROW 53320, 'Verification failed -- rolling back, nothing written.', 1;

    COMMIT TRANSACTION FixDuplicateVarieties;
    PRINT '=== SAVED: duplicates retired, history moved, names trimmed, Lantana days set. ===';

    SELECT x.T AS TableName, x.C AS ColumnName, m.KeepId, SUM(x.N) AS RecordsBefore_AllMembers, ISNULL(MAX(a.N), 0) AS RecordsAfter_Keep
    FROM #RefCount x JOIN #Member m ON m.SpeciesId = x.SpeciesId
    LEFT JOIN #RefCountAfter a ON a.T = x.T AND a.C = x.C AND a.SpeciesId = m.KeepId
    GROUP BY x.T, x.C, m.KeepId ORDER BY m.KeepId, x.T;
    SELECT T AS TableName, Before AS RowsBefore, After AS RowsAfter FROM #TableTotal WHERE T IN (SELECT T FROM #RefCount) ORDER BY T;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT '=== ROLLED BACK -- nothing written. Error ' + CAST(ERROR_NUMBER() AS VARCHAR(12)) + ': ' + ERROR_MESSAGE() + ' ===';
    THROW;
END CATCH
