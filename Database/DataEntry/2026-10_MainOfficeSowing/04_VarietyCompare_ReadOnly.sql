-- 04_VarietyCompare_ReadOnly.sql -- Main Office Sowing import, 2026-10-14.
-- STRICTLY READ-ONLY: only SELECT statements against dbo.PlantSpecies / dbo.PlantTypes. The one INSERT fills a
-- table VARIABLE (@N, held in tempdb for this batch only); no user table, schema, setting or permission is changed.
--
-- Intended target: the LIVE server (VPS), database PlantsIMS2_Test. Run ON THE VPS:
--   sqlcmd -S localhost\MSSQLSERVER02 -d PlantsIMS2_Test -E -C -W -s "|" -i 04_VarietyCompare_ReadOnly.sql -o variety_compare_live.txt
-- It STOPS (prints STOPPED, compares nothing) when connected to the local development laptop or to any database
-- other than PlantsIMS2_Test, so a wrong -S / -d cannot produce a misleading "live" result.
--
-- Kind: E = expected existing variety, B = Petunia Bello Mix (conditional), S = source name as written,
--       A = alternative spelling under review.
-- MatchType: EXACT | CASE/SPACING VARIANT | POSSIBLE ALIAS (punctuation/spacing) - REVIEW | NONE.
--            A POSSIBLE ALIAS is only a flag for the owner, never a match.
SET NOCOUNT ON;
SELECT @@SERVERNAME AS ServerName, CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)) AS ServerProperty, DB_NAME() AS DatabaseName,
       CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) AS SqlVersion, SYSDATETIMEOFFSET() AS RunAt,
       (SELECT COUNT(*) FROM dbo.PlantSpecies) AS PlantSpeciesRows;
IF @@SERVERNAME = N'LAPTOP-R87BKQGH\MSSQLSERVER02' OR DB_NAME() <> N'PlantsIMS2_Test'
BEGIN
    PRINT 'STOPPED: this is not the live server''s PlantsIMS2_Test (see ServerName / DatabaseName above). Nothing compared.';
    SET NOEXEC ON;
END
DECLARE @N TABLE (Kind nchar(1), Name nvarchar(200));
INSERT INTO @N (Kind, Name) VALUES
(N'E',N'Antirrhinum Snapshot Mix'),
(N'E',N'Dianthus Ideal Mix'),
(N'E',N'Impatiens walleriana Imara XDR Mix'),
(N'E',N'Salvia Vista Mix'),
(N'E',N'Cineraria Silver Dust'),
(N'E',N'Smola White'),
(N'E',N'Stock Dwarf Hot Cakes Mix'),
(N'E',N'Verbena Quartz Mix'),
(N'E',N'Vido Red'),
(N'E',N'Vinca Cora XDR Mix'),
(N'E',N'Salvia F. Victoria Blue'),
(N'B',N'Petunia Bello Mix'),
(N'S',N'Antirrhinum Snapdragon Snapshot Mix'),
(N'S',N'Antirrhinum Snaptini Mix'),
(N'S',N'Aster Chinensis Gala Mix'),
(N'S',N'Aster Chinensis Standy Mix'),
(N'S',N'Begonia Super Cool Lipstick'),
(N'S',N'Dianthus Ideal Mix'),
(N'S',N'Dianthus Mix'),
(N'S',N'Impatiens Imara XDR Mix'),
(N'S',N'Lantana Beach (K.K)'),
(N'S',N'Lantana Coconut (K.K)'),
(N'S',N'Lantana Ibis (K.K)'),
(N'S',N'Lantana Litchi (K.K)'),
(N'S',N'Lantana Nandu (K.K)'),
(N'S',N'Lantana Pitaya (K.K)'),
(N'S',N'Lantana Tangerine (K.K)'),
(N'S',N'Lobularia Maritima Giga White'),
(N'S',N'Pansy Inspire Deluxe Maxi Mix'),
(N'S',N'Pansy Majus GTS Mix'),
(N'S',N'Pansy Viola Admire Maxi Mix'),
(N'S',N'Pansy Viola Cats Plus Mix'),
(N'S',N'Pansy Viola Cornuta Admire Maxi Mix'),
(N'S',N'Pansy Viola Deluxe Maxi Mix'),
(N'S',N'Pansy Viola Highflyer Mix'),
(N'S',N'Pansy Viola Highflyer Violet Face'),
(N'S',N'Pansy Viola Highflyer Yellow'),
(N'S',N'Pansy Viola Highflyer Yellow Red Wing (Trailing)'),
(N'S',N'Pansy Viola Hot Cakes Mix'),
(N'S',N'Pansy Viola Wittrockiana Inspire Deluxe Maxi Mix'),
(N'S',N'Petunia 360 Suc. Pink'),
(N'S',N'Petunia 360 Success Violet'),
(N'S',N'Petunia Boom HD Burgundy'),
(N'S',N'Petunia Boom HD Pink'),
(N'S',N'Petunia Hy. Boom Mix'),
(N'S',N'Petunia S.C. White'),
(N'S',N'Petunia Spoty Mix'),
(N'S',N'Petunia Suc. 360 Blue'),
(N'S',N'Petunia Suc. 360 Burgundy'),
(N'S',N'Petunia Suc. 360 Purple'),
(N'S',N'Petunia Suc. 360 Rose Star'),
(N'S',N'Petunia Suc. 360 White'),
(N'S',N'Petunia Suc. HD Maxi Mix'),
(N'S',N'Petunia Suc. HD Pink'),
(N'S',N'Petunia Suc. Maxi Mix'),
(N'S',N'Petunia Success 360 Rose Star'),
(N'S',N'Petunia Tritunia Plum'),
(N'S',N'Portulaca Sundial Mix'),
(N'S',N'Ptilotus Exaltatus Joey'),
(N'S',N'Rudbeckia Amarillo Gold'),
(N'S',N'Rudbeckia Pavnee Spirit'),
(N'S',N'Rudbeckia Sonata Begreen'),
(N'S',N'Salvia Blue (Cutting GP)'),
(N'S',N'Salvia Salvano'),
(N'S',N'Salvia Vista Mix'),
(N'S',N'Silver Dust'),
(N'S',N'Silver Dust Millar'),
(N'S',N'Smola White (SR)'),
(N'S',N'Stock Hot Cakes Mix'),
(N'S',N'Verbena Apex Raw'),
(N'S',N'Verbena Quartz Mix'),
(N'S',N'Verbena Quartz XP Mix'),
(N'S',N'Verbena Speciosa Imagination'),
(N'S',N'Vido Red (SR)'),
(N'S',N'Vinca Cora XDR Mix'),
(N'S',N'Vinca XDR Red (SR)'),
(N'A',N'Petunia Success 360 Pink'),
(N'A',N'Petunia Success 360 Violet'),
(N'A',N'Petunia Success 360 Purple'),
(N'A',N'Petunia Success 360 Blue'),
(N'A',N'Petunia Success 360 Burgundy'),
(N'A',N'Petunia Success 360 White'),
(N'A',N'Petunia Success 360 Rose Star'),
(N'A',N'Petunia Success HD Pink'),
(N'A',N'Petunia Success HD Maxi Mix'),
(N'A',N'Petunia Supercascade White'),
(N'A',N'Petunia Hybrid Boom Mix'),
(N'A',N'Petunia Spotty Mix'),
(N'A',N'Rudbeckia Pawnee Spirit'),
(N'A',N'Rudbeckia Sonata Be Green'),
(N'A',N'Pansy Inspire DeLuxxe Maxi Mix'),
(N'A',N'Callistephus Gala Mix'),
(N'A',N'Impatiens Imara XDR'),
(N'A',N'Vinca Cora XDR Red');
WITH M AS (
  SELECT s.Id, s.Name, t.Name AS PlantType, s.ReadyStockDays,
         -- same normalisation as PlantSpeciesNameRules (case + whitespace)
         LOWER(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(s.Name, CHAR(13), ' '), CHAR(10), ' '), CHAR(9), ' '), NCHAR(160), ' '), '  ', ' '), '  ', ' '), '  ', ' '), '  ', ' ')))) AS NormKey,
         -- loose key (punctuation and spaces removed) only to flag POSSIBLE aliases for review; never a match by itself
         LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(s.Name, ' ', ''), '.', ''), '-', ''), '(', ''), ')', ''), ',', ''), CHAR(13), ''), CHAR(10), ''), CHAR(9), ''), NCHAR(160), '')) AS LooseKey
  FROM dbo.PlantSpecies s LEFT JOIN dbo.PlantTypes t ON t.Id = s.PlantTypeId),
Q AS (
  SELECT Kind, Name,
         LOWER(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(REPLACE(Name, '  ', ' '), '  ', ' '), '  ', ' '), '  ', ' ')))) AS NormKey,
         LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(Name, ' ', ''), '.', ''), '-', ''), '(', ''), ')', ''), ',', '')) AS LooseKey
  FROM @N)
SELECT q.Kind, q.Name AS LookedUp,
       CASE WHEN m.Id IS NULL THEN 'NONE'
            WHEN m.Name = q.Name THEN 'EXACT'
            WHEN m.NormKey = q.NormKey THEN 'CASE/SPACING VARIANT'
            ELSE 'POSSIBLE ALIAS (punctuation/spacing) - REVIEW' END AS MatchType,
       m.Id, m.Name AS MasterName, m.PlantType, m.ReadyStockDays
FROM Q q
LEFT JOIN M m ON m.NormKey = q.NormKey OR m.LooseKey = q.LooseKey
ORDER BY CASE q.Kind WHEN 'E' THEN 1 WHEN 'B' THEN 2 WHEN 'S' THEN 3 ELSE 4 END, q.Name, m.Id;
-- Duplicate names already in this master (same normalised name more than once)
SELECT d.NormKey, COUNT(*) AS Copies, STRING_AGG(CAST(d.Id AS varchar(10)), ',') AS Ids
FROM (SELECT Id, LOWER(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(Name, CHAR(13), ' '), CHAR(10), ' '), CHAR(9), ' '), NCHAR(160), ' '), '  ', ' '), '  ', ' '), '  ', ' '), '  ', ' ')))) AS NormKey FROM dbo.PlantSpecies) d
GROUP BY d.NormKey HAVING COUNT(*) > 1;
SET NOEXEC OFF;
