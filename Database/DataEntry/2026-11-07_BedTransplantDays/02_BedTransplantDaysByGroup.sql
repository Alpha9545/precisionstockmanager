-- ============================================================================
-- 02_BedTransplantDaysByGroup.sql   (2026-11-09)
--
-- Fills dbo.PlantSpecies.BedTransplantDays from the owner's GROUP list ("Variety" -> "Bed Sowing Ready Date", the same
-- values as sheet "Sheet1" of BedSowing_ReadyDate.xlsx, sent again by the owner on 2026-11-09).
-- Owner rule (2026-11-09): a variety belongs to the group that its name STARTS WITH, e.g.
--   "Begonia Big Deluxe Rose Bronze Leaf" -> Begonia -> 45 days.
-- Matching: the FIRST WORD of the variety name (capitals ignored; tabs / line breaks / non-breaking spaces count as
-- spaces) must equal the group name exactly. "Pansy Viola ..." is Pansy (first word); "Galardia" and "Gaillardia" are
-- both in the list as the owner wrote them. Nothing else is guessed.
--
-- Per variety:
--   SET              first word is a group and the variety has NO days yet       -> gets the group's days
--   SAME             already has exactly the group's days                         -> untouched
--   DIFFERENT - kept already has OTHER days (e.g. typed in Plant Master)          -> untouched, listed for review
--   NO GROUP         first word is not in the list (Zinnia, Marigold, DUP-..., ...) -> untouched, summarised by first word
-- A value is never overwritten or cleared. Run 01_BedTransplantDays(_Live).sql first or not at all -- the order does not
-- matter, both only fill / agree. No stock, bed or ledger row is touched.
--
-- MODE=PREVIEW  prints the plan and counts, performs the update inside a transaction, verifies it and ROLLS BACK.
-- MODE=COMMIT   the same, then COMMITs. One transaction; any error rolls everything back. Owner approval per database.
--
-- TARGET SAFEGUARDS -- the first batch refuses to continue unless ALL of these hold:
--     1. DB_NAME() is PlantsIMS2_Test or PlantsIMS2_Scratch_*      2. MODE is PREVIEW or COMMIT (exact upper case)
--     3. @@SERVERNAME = $(TARGET_SERVER)                            4. SERVERPROPERTY('ServerName') = @@SERVERNAME
--     5. SERVERPROPERTY('MachineName') = $(TARGET_MACHINE)          6. DB_NAME() = $(TARGET_DB)
--     7. sys.databases.create_date of THIS database = $(TARGET_DB_CREATED)   (yyyy-mm-ddThh:mi:ss)
--     8. dbo.PlantSpecies.BedTransplantDays and CK_PlantSpecies_BedTransplantDays exist (2026-11-07_BedTransplantDays.sql)
--
-- LIVE (Linux, bash), after the identity query and a COPY_ONLY backup:
--   sqlcmd -S <server> -d PlantsIMS2_Test -U <login> -C -I -b -W -s"|" -v MODE=PREVIEW TARGET_SERVER=srv1202245 \
--          TARGET_MACHINE=srv1202245 TARGET_DB=PlantsIMS2_Test TARGET_DB_CREATED=2026-10-14T23:41:07 -i 02_BedTransplantDaysByGroup.sql
-- LOCAL (PowerShell):
--   sqlcmd -S <server> -d PlantsIMS2_Test -E -C -I -b -W -s"|" -v MODE=PREVIEW 'TARGET_SERVER="<ServerName>"' 'TARGET_MACHINE="<MachineName>"'
--          'TARGET_DB="PlantsIMS2_Test"' 'TARGET_DB_CREATED="<Created>"' -i 02_BedTransplantDaysByGroup.sql
-- then the same with MODE=COMMIT (only with approval).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 54701, '02_BedTransplantDaysByGroup.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'PREVIEW', N'COMMIT')
    THROW 54702, 'Run with sqlcmd -v MODE=PREVIEW or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 54703, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 54706, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 54707, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 54708, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 54709, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
IF COL_LENGTH('dbo.PlantSpecies', 'BedTransplantDays') IS NULL OR OBJECT_ID('dbo.CK_PlantSpecies_BedTransplantDays', 'C') IS NULL
    THROW 54704, 'dbo.PlantSpecies.BedTransplantDays (or its CHECK) is missing -- run Database/Migrations/2026-11-07_BedTransplantDays.sql first.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

-- The owner's list, exactly as sent (2026-11-09). GroupKey = upper case.
DECLARE @Groups TABLE (GroupName NVARCHAR(100) NOT NULL, GroupKey NVARCHAR(100) NOT NULL PRIMARY KEY, Days INT NOT NULL CHECK (Days BETWEEN 1 AND 365));
INSERT @Groups (GroupName, GroupKey, Days) VALUES
 (N'Alyssum',      N'ALYSSUM',      25)
,(N'Antirrhinum',  N'ANTIRRHINUM',  20)
,(N'Aster',        N'ASTER',        22)
,(N'Begonia',      N'BEGONIA',      45)
,(N'Callistephus', N'CALLISTEPHUS', 22)
,(N'Celosia',      N'CELOSIA',      25)
,(N'Cineraria',    N'CINERARIA',    35)
,(N'Coleus',       N'COLEUS',       20)
,(N'Dianthus',     N'DIANTHUS',     15)
,(N'Festuca',      N'FESTUCA',      50)
,(N'Gaillardia',   N'GAILLARDIA',   25)
,(N'Galardia',     N'GALARDIA',     25)
,(N'Gazania',      N'GAZANIA',      20)
,(N'Gloxinia',     N'GLOXINIA',     40)
,(N'Hypoestes',    N'HYPOESTES',    15)
,(N'Impatiens',    N'IMPATIENS',    20)
,(N'Kochia',       N'KOCHIA',       25)
,(N'Lobelia',      N'LOBELIA',      25)
,(N'Lobularia',    N'LOBULARIA',    25)
,(N'Pansy',        N'PANSY',        30)
,(N'Pentas',       N'PENTAS',       35)
,(N'Petunia',      N'PETUNIA',      25)
,(N'Phlox',        N'PHLOX',        25)
,(N'Platycodon',   N'PLATYCODON',   35)
,(N'Portulaca',    N'PORTULACA',    25)
,(N'Rudbeckia',    N'RUDBECKIA',    30)
,(N'Salvia',       N'SALVIA',       20)
,(N'Silver',       N'SILVER',       30)
,(N'Torenia',      N'TORENIA',      35)
,(N'Verbena',      N'VERBENA',      25)
,(N'Vinca',        N'VINCA',        25)
,(N'Viola',        N'VIOLA',        30);
IF (SELECT COUNT(*) FROM @Groups) <> 32
    THROW 54705, 'List check failed: expected 32 groups.', 1;

-- Every variety with its normalised name and first word.
IF OBJECT_ID('tempdb..#V') IS NOT NULL DROP TABLE #V;
CREATE TABLE #V (Id INT PRIMARY KEY, Name NVARCHAR(200), PlantType NVARCHAR(200), ExistingDays INT NULL,
                 NameKey NVARCHAR(400), FirstWord NVARCHAR(400), GroupName NVARCHAR(100) NULL, GroupDays INT NULL,
                 Action NVARCHAR(40) NULL);
INSERT #V (Id, Name, PlantType, ExistingDays, NameKey)
SELECT s.Id, s.Name, LTRIM(RTRIM(t.Name)), s.BedTransplantDays,
       UPPER(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(REPLACE(s.Name, CHAR(13), N' '), CHAR(10), N' '), CHAR(9), N' '), NCHAR(160), N' '))))
FROM dbo.PlantSpecies s LEFT JOIN dbo.PlantTypes t ON t.Id = s.PlantTypeId;
UPDATE #V SET FirstWord = LEFT(NameKey, CHARINDEX(N' ', NameKey + N' ') - 1);
UPDATE v SET GroupName = g.GroupName, GroupDays = g.Days FROM #V v JOIN @Groups g ON g.GroupKey = v.FirstWord;
UPDATE #V SET Action = CASE
        WHEN GroupName IS NULL THEN N'NO GROUP'
        WHEN ExistingDays IS NULL THEN N'SET'
        WHEN ExistingDays = GroupDays THEN N'SAME'
        ELSE N'DIFFERENT - kept' END;

PRINT '=== Counts (varieties) ===';
SELECT k.Action, (SELECT COUNT(*) FROM #V v WHERE v.Action = k.Action) AS Varieties
FROM (VALUES (1, N'SET'), (2, N'SAME'), (3, N'DIFFERENT - kept'), (4, N'NO GROUP')) AS k (Ord, Action)
ORDER BY k.Ord;

PRINT '=== Per group ===';
SELECT g.GroupName AS [Group], g.Days,
       (SELECT COUNT(*) FROM #V v WHERE v.GroupName = g.GroupName) AS Varieties,
       (SELECT COUNT(*) FROM #V v WHERE v.GroupName = g.GroupName AND v.Action = N'SET') AS ToSet,
       (SELECT COUNT(*) FROM #V v WHERE v.GroupName = g.GroupName AND v.Action = N'SAME') AS Same,
       (SELECT COUNT(*) FROM #V v WHERE v.GroupName = g.GroupName AND v.Action = N'DIFFERENT - kept') AS DifferentKept
FROM @Groups g ORDER BY g.GroupName;

PRINT '=== Varieties that get days (SET) ===';
SELECT v.Id, v.Name, v.PlantType, v.GroupName AS [Group], v.GroupDays AS Days
FROM #V v WHERE v.Action = N'SET' ORDER BY v.GroupName, v.Name;

PRINT '=== Varieties that already have OTHER days (kept, review in Plant Master) ===';
SELECT v.Id, v.Name, v.GroupName AS [Group], v.GroupDays AS GroupDays, v.ExistingDays
FROM #V v WHERE v.Action = N'DIFFERENT - kept' ORDER BY v.GroupName, v.Name;

PRINT '=== Groups in the list that match no variety in THIS database ===';
SELECT g.GroupName AS [Group], g.Days FROM @Groups g
WHERE NOT EXISTS (SELECT 1 FROM #V v WHERE v.GroupName = g.GroupName) ORDER BY g.GroupName;

PRINT '=== Varieties with no group (first word not in the list) -- left without days ===';
SELECT v.FirstWord, COUNT(*) AS Varieties, MIN(v.Name) AS Example
FROM #V v WHERE v.Action = N'NO GROUP' GROUP BY v.FirstWord ORDER BY COUNT(*) DESC, v.FirstWord;

-- ---- apply (both modes; PREVIEW rolls back) --------------------------------------------------------------------------
DECLARE @Expected INT = (SELECT COUNT(*) FROM #V WHERE Action = N'SET');
DECLARE @Changed INT;
BEGIN TRANSACTION BedTransplantDaysByGroup;

-- Lock the varieties; fill only those that are STILL empty (a Plant Master edit meanwhile = stop).
UPDATE s SET BedTransplantDays = v.GroupDays
FROM dbo.PlantSpecies s WITH (UPDLOCK, HOLDLOCK)
JOIN #V v ON v.Id = s.Id
WHERE v.Action = N'SET' AND s.BedTransplantDays IS NULL;
SET @Changed = @@ROWCOUNT;

IF @Changed <> @Expected
BEGIN
    ROLLBACK TRANSACTION BedTransplantDaysByGroup;
    THROW 54711, 'A variety changed while the script ran -- rolled back, nothing written. Run PREVIEW again.', 1;
END
IF EXISTS (SELECT 1 FROM dbo.PlantSpecies s JOIN #V v ON v.Id = s.Id
           WHERE (v.Action IN (N'SET', N'SAME') AND ISNULL(s.BedTransplantDays, -1) <> v.GroupDays)
              OR (v.Action IN (N'DIFFERENT - kept', N'NO GROUP') AND ISNULL(s.BedTransplantDays, -1) <> ISNULL(v.ExistingDays, -1)))
BEGIN
    ROLLBACK TRANSACTION BedTransplantDaysByGroup;
    THROW 54712, 'Verification failed -- rolled back, nothing written.', 1;
END

PRINT '=== After the update (inside the transaction) ===';
SELECT (SELECT COUNT(*) FROM dbo.PlantSpecies WHERE BedTransplantDays IS NOT NULL) AS VarietiesWithDays,
       (SELECT MIN(BedTransplantDays) FROM dbo.PlantSpecies) AS MinDays,
       (SELECT MAX(BedTransplantDays) FROM dbo.PlantSpecies) AS MaxDays,
       (SELECT COUNT(*) FROM dbo.PlantSpecies) AS Varieties,
       @Changed AS RowsUpdated,
       (SELECT COUNT(*) FROM dbo.BedSowings b JOIN dbo.PlantSpecies s ON s.Id = b.SpeciesId
        WHERE b.Status IN (N'PendingTransplant', N'PartiallyTransplanted')) AS OpenBeds,
       (SELECT COUNT(*) FROM dbo.BedSowings b JOIN dbo.PlantSpecies s ON s.Id = b.SpeciesId
        WHERE b.Status IN (N'PendingTransplant', N'PartiallyTransplanted') AND s.BedTransplantDays IS NULL) AS OpenBedsWithoutSchedule;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION BedTransplantDaysByGroup;
    PRINT '=== COMMITTED: ' + CAST(@Changed AS VARCHAR(10)) + ' varieties got Bed Transplant Days. ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION BedTransplantDaysByGroup;
    PRINT '=== PREVIEW -- ' + CAST(@Expected AS VARCHAR(10)) + ' varieties would get Bed Transplant Days; rolled back, nothing written. ===';
END
GO
