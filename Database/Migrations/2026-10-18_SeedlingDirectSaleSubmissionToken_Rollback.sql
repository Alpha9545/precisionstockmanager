-- ============================================================================
-- 2026-10-18_SeedlingDirectSaleSubmissionToken_Rollback.sql
--
-- Undoes 2026-10-18_SeedlingDirectSaleSubmissionToken.sql: drops UX_SeedlingDispatches_SubmissionToken and SeedlingDispatches.SubmissionToken.
-- The token is only a duplicate-submit key (no quantity, customer or audit data lives in it), so this is safe after sales
-- were recorded -- their booking, allocation, dispatch and ledger rows are untouched. The direct tray sale switches off again.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52571, '2026-10-18_SeedlingDirectSaleSubmissionToken_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52572, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52573, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION SeedlingSaleToken1018Rollback;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SeedlingDispatches_SubmissionToken' AND object_id = OBJECT_ID('dbo.SeedlingDispatches'))
        DROP INDEX UX_SeedlingDispatches_SubmissionToken ON dbo.SeedlingDispatches;
    IF COL_LENGTH('dbo.SeedlingDispatches', 'SubmissionToken') IS NOT NULL
    BEGIN
        ALTER TABLE dbo.SeedlingDispatches DROP COLUMN SubmissionToken;
        PRINT '=== SeedlingDispatches.SubmissionToken removed ===';
    END
    ELSE PRINT '=== Not applied -- no-op ===';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION SeedlingSaleToken1018Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION SeedlingSaleToken1018Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION SeedlingSaleToken1018Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
