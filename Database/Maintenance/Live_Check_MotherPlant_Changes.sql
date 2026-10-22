/* ============================================================================
   READ-ONLY CHECK for replicating the 2026-10-14 local Mother Plant work on LIVE.
   SELECT statements only: no INSERT/UPDATE/DELETE, no transaction, no codes.
   Run on the VPS and send back the whole output file.
   Everything is looked up by NAME, because Ids on live can differ from local.
   ============================================================================ */
SET NOCOUNT ON;

PRINT '=== 0. Where am I ===';
SELECT @@SERVERNAME AS ServerName, SERVERPROPERTY('MachineName') AS Machine, DB_NAME() AS DatabaseName,
       HOST_NAME() AS ClientHost, CONNECTIONPROPERTY('net_transport') AS Transport, SYSUTCDATETIME() AS CheckedAtUtc;

PRINT '=== 1. Schema needed by the scripts ===';
SELECT OBJECT_ID('dbo.MotherPlants') AS MotherPlantsTable, OBJECT_ID('dbo.PlantSpecies') AS PlantSpeciesTable,
       OBJECT_ID('dbo.BatchNumberSequences') AS SequenceTable,
       COL_LENGTH('dbo.PlantSpecies', 'ReadyStockDays') AS SpeciesReadyDaysCol, COL_LENGTH('dbo.PlantSpecies', 'Color') AS SpeciesColorCol,
       COL_LENGTH('dbo.MotherPlants', 'AreaId') AS MpAreaCol, COL_LENGTH('dbo.MotherPlants', 'SupervisorId') AS MpSupervisorCol,
       (SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_MotherPlants_AreaAndSupervisor' AND is_disabled = 0) AS MpTriggerEnabled,
       (SELECT LastNumber FROM dbo.BatchNumberSequences WHERE Prefix = N'MP' AND [Year] = 2026) AS MpCounter2026,
       (SELECT COUNT(*) FROM dbo.MotherPlants) AS MotherPlantRows, (SELECT COUNT(*) FROM dbo.PlantSpecies) AS VarietyRows;
SELECT Id, Name FROM dbo.PlantTypes ORDER BY Id;

PRINT '=== 2. Areas, Polyhouses, supervisors, creator ===';
SELECT Id, Name, AreaType, IsActive FROM dbo.Area
WHERE Name LIKE N'%Ropvatika%' OR Name LIKE N'%Ashi%' OR Name LIKE N'%Green Bl%' OR Name LIKE N'%Swami%' ORDER BY Id;
SELECT ph.Id, ph.Name, ph.AreaId, a.Name AS AreaName FROM dbo.Polyhouses ph INNER JOIN dbo.Area a ON a.Id = ph.AreaId
WHERE a.Name LIKE N'%Ropvatika%' OR a.Name LIKE N'%Ashi%' OR a.Name LIKE N'%Green Bl%' OR a.Name LIKE N'%Swami%' ORDER BY a.Name, ph.Name;
SELECT u.Id, u.Username, u.Name, u.IsActive, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS Role, ur.AreaId, a.Name AS AreaName
FROM dbo.IMSUsers u LEFT JOIN dbo.UserRoles ur ON ur.UserId = u.Id LEFT JOIN dbo.Roles r ON r.Id = ur.RoleId LEFT JOIN dbo.Area a ON a.Id = ur.AreaId
WHERE u.Name IN (N'Abhijeet Gaikwad', N'Achyut Kunjir', N'Vasant Shinde', N'Kiran Kohle') OR u.Username = N'Prajwal'
ORDER BY u.Id;

PRINT '=== 3. Varieties (NEW = created locally today; EXISTING = used by today''s batches) ===';
DECLARE @V TABLE (Seq int IDENTITY(1,1), Role varchar(10), Name nvarchar(200));
INSERT @V (Role, Name) VALUES
('NEW',N'Branangel White'),('NEW',N'Branchili'),('NEW',N'Brandevil'),('NEW',N'Brandune'),('NEW',N'Branfountain Apricot'),
('NEW',N'Branfountain Pink'),('NEW',N'Branfountain Salmon'),('NEW',N'Branfountain White'),('NEW',N'Branfountain Yellow'),
('NEW',N'Branfountain Purple'),('NEW',N'Branglobe Pink'),('NEW',N'Branglobe Red'),('NEW',N'Branglobe White'),('NEW',N'Branlena'),
('NEW',N'Branperfect Ice (White)'),('NEW',N'Branperfect Pink'),('NEW',N'Branperfect Purple'),('NEW',N'Branperfect Yellow'),
('NEW',N'Branroyal Purple'),('NEW',N'Branroyal Scarlet (Red)'),('NEW',N'Bransound Dark Orange'),('NEW',N'Bransound Magenta'),
('NEW',N'Bransound Purple'),('NEW',N'Bransound Red'),
('NEW',N'Kibo'),('NEW',N'Madhura'),('NEW',N'Trinidad'),('NEW',N'Trinidad Fire'),('NEW',N'Wendy Red'),
('NEW',N'Catalina'),('NEW',N'Cayman Orange'),('NEW',N'Cayman Yellow'),('NEW',N'Dahlia Grande White'),
('NEW',N'Dahlia Labelia Grande Coral'),('NEW',N'Dahlia Magenta IMPR'),('NEW',N'Dahlia Red Grande'),
('NEW',N'Impatiens Orange Boost'),('NEW',N'Impatiens Pink Jellew'),('NEW',N'Impatiens Spark Pink Jellew'),
('NEW',N'Impatiens Magenta Bliss'),('NEW',N'Impatiens Salmon Punch'),('NEW',N'Impatiens White'),
('NEW',N'Latania Red'),('NEW',N'Latania Beach'),('NEW',N'Latania Pattaya'),('NEW',N'Latania Transizin'),
('NEW',N'Latania Nadu'),('NEW',N'Latania Coconut'),('NEW',N'Latania Litchi'),('NEW',N'Latania IBIS'),
('EXISTING',N'Branangel Pink'),('EXISTING',N'Ping Pong Green'),('EXISTING',N'Ping Pong Pink'),('EXISTING',N'Ping Pong Purple'),
('EXISTING',N'Ping Pong Red'),('EXISTING',N'Ping Pong White'),('EXISTING',N'Ping Pong Yellow'),('EXISTING',N'Samui (Pink)'),
('EXISTING',N'Smola White'),('EXISTING',N'Vido Purple'),('EXISTING',N'Vido Red'),('EXISTING',N'Bhagyshree White'),
('EXISTING',N'Suvidha White'),('EXISTING',N'Pramila Yellow'),('EXISTING',N'Meghana Orange'),('EXISTING',N'Sharvari Purple'),
('EXISTING',N'Aishwarya Yellow'),('EXISTING',N'Pramila Bronze'),('EXISTING',N'Aditi Pink'),('EXISTING',N'Wendy Bronze'),
('EXISTING',N'Salvia F. Evalution Violet'),('EXISTING',N'Salvia F. Victoria Blue'),('EXISTING',N'Salvia F. White');

-- Name key = the app's rule (trim, collapse whitespace incl. CR/LF/TAB, case-insensitive).
;WITH S AS (SELECT ps.Id, ps.Name, ps.PlantTypeId, ps.ReadyStockDays, ps.Color,
            UPPER(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ps.Name, CHAR(13), N' '), CHAR(10), N' '), CHAR(9), N' '), N'  ', N' '), N'  ', N' '), N'  ', N' ')))) AS K
            FROM dbo.PlantSpecies ps)
SELECT v.Seq, v.Role, v.Name AS Wanted, COUNT(s.Id) AS Matches,
       STRING_AGG(CONCAT(s.Id, ':', pt.Name, ':', s.ReadyStockDays, ':', ISNULL(s.Color, N'-'),
                         CASE WHEN s.Name LIKE N'%' + CHAR(10) + N'%' THEN N':CRLF' ELSE N'' END), N' | ') AS Found
FROM @V v LEFT JOIN S s ON s.K = UPPER(v.Name) LEFT JOIN dbo.PlantTypes pt ON pt.Id = s.PlantTypeId
GROUP BY v.Seq, v.Role, v.Name ORDER BY v.Seq;

PRINT '=== 4. Existing Mother Plant batches in the four Areas ===';
SELECT mp.Id, mp.MotherPlantCode, a.Name AS Area, ph.Name AS Polyhouse,
       REPLACE(REPLACE(ps.Name, CHAR(13), N''), CHAR(10), N'') AS Variety, mp.SupervisorId, mp.PlantingDate,
       mp.MotherPlantQuantity, mp.CuttingPeriodDays, mp.CuttingRate, mp.Status, mp.CreatedBy
FROM dbo.MotherPlants mp
INNER JOIN dbo.Area a ON a.Id = mp.AreaId INNER JOIN dbo.Polyhouses ph ON ph.Id = mp.PolyhouseId INNER JOIN dbo.PlantSpecies ps ON ps.Id = mp.SpeciesId
WHERE a.Name LIKE N'%Ropvatika%' OR a.Name LIKE N'%Ashi%' OR a.Name LIKE N'%Green Bl%' OR a.Name LIKE N'%Swami%'
ORDER BY a.Name, mp.Id;
PRINT '=== END ===';
