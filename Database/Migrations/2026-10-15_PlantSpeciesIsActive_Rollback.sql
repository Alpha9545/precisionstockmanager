-- ============================================================================
-- 2026-10-15_PlantSpeciesIsActive_Rollback.sql
--
-- Undoes 2026-10-15_PlantSpeciesIsActive.sql: drops dbo.PlantSpecies.IsActive and its default.
-- REFUSES while any variety is inactive (IsActive = 0): those are retired "DUP-..." duplicates, and dropping the flag
-- would make them selectable again. Also refuses while any index uses the column (drop the unique-name index first).
-- Safe to run more than once. Does not name a database: select the target database first.
-- ============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' ===';
IF COL_LENGTH(N'dbo.PlantSpecies', N'IsActive') IS NULL
BEGIN
    PRINT '=== PlantSpecies.IsActive not present -- no-op ===';
    RETURN;
END

IF EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
           WHERE ic.object_id = OBJECT_ID(N'dbo.PlantSpecies') AND c.name = N'IsActive')
   OR EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PlantSpecies') AND filter_definition LIKE N'%IsActive%')
    THROW 53211, 'An index on dbo.PlantSpecies uses IsActive -- drop it first (unique variety-name index rollback).', 1;

DECLARE @Inactive INT;
EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.PlantSpecies WHERE IsActive = 0', N'@n INT OUTPUT', @n = @Inactive OUTPUT;
IF @Inactive > 0
    THROW 53212, 'Some varieties are inactive (retired duplicates) -- refusing to drop IsActive.', 1;

BEGIN TRANSACTION PlantSpeciesIsActiveRollback;
BEGIN TRY
    IF OBJECT_ID(N'dbo.DF_PlantSpecies_IsActive', N'D') IS NOT NULL
        ALTER TABLE dbo.PlantSpecies DROP CONSTRAINT DF_PlantSpecies_IsActive;
    ALTER TABLE dbo.PlantSpecies DROP COLUMN IsActive;
    COMMIT TRANSACTION PlantSpeciesIsActiveRollback;
    PRINT '=== Dropped PlantSpecies.IsActive ===';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH
