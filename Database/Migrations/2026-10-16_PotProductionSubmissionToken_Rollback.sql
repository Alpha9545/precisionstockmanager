-- ============================================================================
-- 2026-10-16_PotProductionSubmissionToken_Rollback.sql
--
-- Undoes 2026-10-16_PotProductionSubmissionToken.sql: drops UX_PotBatches_SubmissionToken and the
-- SubmissionToken column. The token is only a duplicate-submit key (no quantity, source or audit data lives in
-- it), so this is safe after batches were started -- their source, ledger and audit rows are untouched.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52471, '2026-10-16_PotProductionSubmissionToken_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52472, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52473, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION PotToken1016Rollback;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PotBatches_SubmissionToken' AND object_id = OBJECT_ID('dbo.PotProductionBatches'))
        DROP INDEX UX_PotBatches_SubmissionToken ON dbo.PotProductionBatches;
    IF COL_LENGTH('dbo.PotProductionBatches', 'SubmissionToken') IS NOT NULL
    BEGIN
        ALTER TABLE dbo.PotProductionBatches DROP COLUMN SubmissionToken;
        PRINT '=== SubmissionToken removed ===';
    END
    ELSE PRINT '=== Not applied -- no-op ===';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION PotToken1016Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION PotToken1016Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION PotToken1016Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
