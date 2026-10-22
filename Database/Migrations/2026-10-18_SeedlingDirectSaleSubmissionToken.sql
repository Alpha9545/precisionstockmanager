-- ============================================================================
-- 2026-10-18_SeedlingDirectSaleSubmissionToken.sql
--
-- WHY (owner decision 2026-10-12, direct tray sales): ready trays of a Mother Plant Area are sold straight to a customer
--   (Mother Plant & Cutting > Ready Stock > Sell to customer, SeedlingFulfilmentRepository.DirectTraySaleAsync) -- a booking,
--   its batch allocation and its Seedling Dispatch recorded together. A repeated / replayed submit must never sell -- and
--   deduct -- the plants twice.
--
-- WHAT: dbo.SeedlingDispatches.SubmissionToken UNIQUEIDENTIFIER NULL (the sale form's one-time token) +
--   UX_SeedlingDispatches_SubmissionToken (unique where NOT NULL). Existing dispatches keep NULL. The application looks the
--   token up under the Ready Stock batch lock and returns the first sale; the index is the backstop for simultaneous submits.
--   The direct tray sale stays switched off on a database without this column.
--
-- NOT changed: no booking, allocation, dispatch, stock or ledger row; no trigger, constraint or permission.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-18_SeedlingDirectSaleSubmissionToken_Rollback.sql.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52561, '2026-10-18_SeedlingDirectSaleSubmissionToken.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52562, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52563, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION SeedlingSaleToken1018;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.SeedlingDispatches', 'U') IS NULL
        THROW 52564, 'dbo.SeedlingDispatches is missing -- nothing changed.', 1;
    IF COL_LENGTH('dbo.SeedlingDispatches', 'SubmissionToken') IS NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SeedlingDispatches_SubmissionToken')
            THROW 52565, 'UX_SeedlingDispatches_SubmissionToken exists without its column -- inspect the database, nothing changed.', 1;
        ALTER TABLE dbo.SeedlingDispatches ADD SubmissionToken UNIQUEIDENTIFIER NULL;
        EXEC (N'CREATE UNIQUE INDEX UX_SeedlingDispatches_SubmissionToken ON dbo.SeedlingDispatches (SubmissionToken) WHERE SubmissionToken IS NOT NULL;');
        PRINT '=== SeedlingDispatches.SubmissionToken + UX_SeedlingDispatches_SubmissionToken ===';
    END
    ELSE PRINT '=== Already applied -- no-op ===';
    EXEC (N'SELECT COUNT(*) AS Dispatches, COUNT(SubmissionToken) AS WithToken FROM dbo.SeedlingDispatches;');   -- deferred: the column is new
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION SeedlingSaleToken1018;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION SeedlingSaleToken1018;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION SeedlingSaleToken1018;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
