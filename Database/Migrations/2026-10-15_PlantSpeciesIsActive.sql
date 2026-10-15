-- ============================================================================
-- 2026-10-15_PlantSpeciesIsActive.sql
--
-- WHY (owner requirement 2026-10-15, duplicate-variety cleanup): a duplicate variety must be RETIRED, never deleted --
--   its history moves to the kept variety and the row itself stays, marked inactive and renamed "DUP-...".
--   dbo.PlantSpecies has no active flag, so there is nothing to mark.
--
-- WHAT (one transaction; nothing else changes): adds dbo.PlantSpecies.IsActive BIT NOT NULL, default 1
--   (constraint DF_PlantSpecies_IsActive), so every existing and future variety starts active.
--
-- Safe to run more than once (no-op when the column already exists). Does not name a database:
-- select the target database first (SSMS: database dropdown / sqlcmd -d), then run.
-- Rollback: 2026-10-15_PlantSpeciesIsActive_Rollback.sql
-- ============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' ===';
IF OBJECT_ID(N'dbo.PlantSpecies', N'U') IS NULL
    THROW 53201, 'dbo.PlantSpecies not found -- wrong database selected?', 1;

IF COL_LENGTH(N'dbo.PlantSpecies', N'IsActive') IS NOT NULL
    PRINT '=== PlantSpecies.IsActive already exists -- no-op ===';
ELSE
BEGIN
    BEGIN TRANSACTION PlantSpeciesIsActive;
    BEGIN TRY
        ALTER TABLE dbo.PlantSpecies ADD IsActive BIT NOT NULL CONSTRAINT DF_PlantSpecies_IsActive DEFAULT (1);
        COMMIT TRANSACTION PlantSpeciesIsActive;
        PRINT '=== Added PlantSpecies.IsActive (all existing varieties = 1) ===';
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO
SELECT COUNT(*) AS Varieties, SUM(CASE WHEN IsActive = 1 THEN 1 ELSE 0 END) AS Active FROM dbo.PlantSpecies;
