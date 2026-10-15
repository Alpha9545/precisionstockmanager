-- ============================================================================
-- 2026-10-15_PlantSpeciesUniqueActiveName.sql
--
-- WHY (owner requirement 2026-10-15): after the duplicate-variety cleanup, the database itself must refuse a second
--   ACTIVE variety with the same name (the Add / Edit Variety page already refuses it; this also stops scripts / imports).
--
-- WHAT (one transaction; nothing else changes): unique index UX_PlantSpecies_Name_Active on dbo.PlantSpecies (Name)
--   WHERE IsActive = 1. The column's collation (SQL_Latin1_General_CP1_CI_AS) ignores capitals and trailing spaces;
--   retired "DUP-..." rows (IsActive = 0) are outside the index.
--
-- ORDER: run AFTER 2026-10-15_PlantSpeciesIsActive.sql AND Fix_Duplicate_Varieties.sql (@Commit = 1).
--   Refuses -- listing the clashing names -- while any two active varieties still share a name.
-- Safe to run more than once. Does not name a database: select the target database first.
-- Rollback: 2026-10-15_PlantSpeciesUniqueActiveName_Rollback.sql
-- ============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' ===';
IF COL_LENGTH(N'dbo.PlantSpecies', N'IsActive') IS NULL
    THROW 53221, 'dbo.PlantSpecies.IsActive is missing -- run 2026-10-15_PlantSpeciesIsActive.sql first.', 1;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PlantSpecies') AND name = N'UX_PlantSpecies_Name_Active')
BEGIN
    PRINT '=== UX_PlantSpecies_Name_Active already exists -- no-op ===';
    RETURN;
END

DECLARE @Dups TABLE (Name NVARCHAR(100), Ids NVARCHAR(400));
EXEC sp_executesql N'SELECT MIN(Name), STRING_AGG(CAST(Id AS NVARCHAR(12)), N'', '') FROM dbo.PlantSpecies WHERE IsActive = 1 GROUP BY Name HAVING COUNT(*) > 1;';
INSERT @Dups EXEC sp_executesql N'SELECT MIN(Name), STRING_AGG(CAST(Id AS NVARCHAR(12)), N'', '') FROM dbo.PlantSpecies WHERE IsActive = 1 GROUP BY Name HAVING COUNT(*) > 1;';
IF EXISTS (SELECT 1 FROM @Dups)
    THROW 53222, 'Active varieties still share a name (listed above) -- run Fix_Duplicate_Varieties.sql first. Nothing created.', 1;

BEGIN TRANSACTION PlantSpeciesUniqueActiveName;
BEGIN TRY
    EXEC sp_executesql N'CREATE UNIQUE NONCLUSTERED INDEX UX_PlantSpecies_Name_Active ON dbo.PlantSpecies (Name) WHERE IsActive = 1;';
    COMMIT TRANSACTION PlantSpeciesUniqueActiveName;
    PRINT '=== Created UX_PlantSpecies_Name_Active (unique Name among active varieties) ===';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH
