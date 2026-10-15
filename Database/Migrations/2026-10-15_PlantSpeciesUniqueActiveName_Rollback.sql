-- ============================================================================
-- 2026-10-15_PlantSpeciesUniqueActiveName_Rollback.sql
--
-- Undoes 2026-10-15_PlantSpeciesUniqueActiveName.sql: drops UX_PlantSpecies_Name_Active. No data changes.
-- (The Add / Edit Variety page keeps refusing duplicate names without it.)
-- Safe to run more than once. Does not name a database: select the target database first.
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' ===';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PlantSpecies') AND name = N'UX_PlantSpecies_Name_Active')
BEGIN
    PRINT '=== UX_PlantSpecies_Name_Active not present -- no-op ===';
    RETURN;
END

BEGIN TRANSACTION PlantSpeciesUniqueActiveNameRollback;
BEGIN TRY
    DROP INDEX UX_PlantSpecies_Name_Active ON dbo.PlantSpecies;
    COMMIT TRANSACTION PlantSpeciesUniqueActiveNameRollback;
    PRINT '=== Dropped UX_PlantSpecies_Name_Active ===';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH
