-- ============================================================================
-- 2026-10-16_PotProductionSubmissionToken.sql
--
-- WHY (owner requirement 2026-10-12, Pot Production from Cutting or Main Office seedlings): a double click,
-- refresh or retry of "Start pot batch" must never start a second batch and take the source plants twice.
--
-- WHAT: dbo.PotProductionBatches.SubmissionToken UNIQUEIDENTIFIER NULL (the form's one-time token) +
--       UX_PotBatches_SubmissionToken (unique where NOT NULL). Existing batches keep NULL.
--       The application looks the token up under the source lock and returns the first batch; the unique
--       index is the database backstop for two simultaneous submits.
--
-- NOT changed: no batch, ledger or stock row; no trigger, constraint or permission.
-- Requires: 2026-10-15_PotProductionFromReadyStock.sql.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-16_PotProductionSubmissionToken_Rollback.sql.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52461, '2026-10-16_PotProductionSubmissionToken.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52462, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52463, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION PotToken1016;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF COL_LENGTH('dbo.PotProductionBatches', 'SourceReadyStockId') IS NULL OR OBJECT_ID('dbo.TR_PotBatches_ReadyStockSource', 'TR') IS NULL
        THROW 52464, 'Run 2026-10-15_PotProductionFromReadyStock.sql first.', 1;

    IF COL_LENGTH('dbo.PotProductionBatches', 'SubmissionToken') IS NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PotBatches_SubmissionToken')
            THROW 52465, 'UX_PotBatches_SubmissionToken exists without its column -- inspect the database, nothing changed.', 1;
        ALTER TABLE dbo.PotProductionBatches ADD SubmissionToken UNIQUEIDENTIFIER NULL;
        EXEC (N'CREATE UNIQUE INDEX UX_PotBatches_SubmissionToken ON dbo.PotProductionBatches (SubmissionToken) WHERE SubmissionToken IS NOT NULL;');
        PRINT '=== PotProductionBatches.SubmissionToken + UX_PotBatches_SubmissionToken ===';
    END
    ELSE PRINT '=== Already applied -- no-op ===';

    EXEC (N'SELECT COUNT(*) AS PotBatches, COUNT(SubmissionToken) AS WithToken FROM dbo.PotProductionBatches;');   -- deferred: the column is new
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION PotToken1016;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION PotToken1016;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION PotToken1016;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
