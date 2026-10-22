-- ============================================================================
-- 2026-10-17_PottedDirectSaleSubmissionToken.sql
--
-- WHY (owner requirement 2026-10-12, issuing an Area's Ready Stock): a repeated / replayed submit of "Sell to customer"
--   (Potted Plant Stock > Move, DispatchRepository.DirectSaleAsync) must never sell -- and deduct -- the pots twice.
--   (Transfers to an Outlet / Main Office already have dbo.InternalTransfers.SubmissionToken + its unique index.)
--
-- WHAT: dbo.Dispatches.SubmissionToken UNIQUEIDENTIFIER NULL (the sale form's one-time token) + UX_Dispatches_SubmissionToken
--   (unique where NOT NULL). Existing dispatches keep NULL. The application looks the token up under the stock row lock and
--   returns the first sale; the index is the database backstop for simultaneous submits.
--
-- NOT changed: no booking, dispatch, stock or ledger row; no trigger, constraint or permission.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-17_PottedDirectSaleSubmissionToken_Rollback.sql.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52541, '2026-10-17_PottedDirectSaleSubmissionToken.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52542, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52543, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION PottedSaleToken1017;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.Dispatches', 'U') IS NULL
        THROW 52544, 'dbo.Dispatches is missing -- nothing changed.', 1;
    IF COL_LENGTH('dbo.Dispatches', 'SubmissionToken') IS NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Dispatches_SubmissionToken')
            THROW 52545, 'UX_Dispatches_SubmissionToken exists without its column -- inspect the database, nothing changed.', 1;
        ALTER TABLE dbo.Dispatches ADD SubmissionToken UNIQUEIDENTIFIER NULL;
        EXEC (N'CREATE UNIQUE INDEX UX_Dispatches_SubmissionToken ON dbo.Dispatches (SubmissionToken) WHERE SubmissionToken IS NOT NULL;');
        PRINT '=== Dispatches.SubmissionToken + UX_Dispatches_SubmissionToken ===';
    END
    ELSE PRINT '=== Already applied -- no-op ===';
    EXEC (N'SELECT COUNT(*) AS Dispatches, COUNT(SubmissionToken) AS WithToken FROM dbo.Dispatches;');   -- deferred: the column is new
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION PottedSaleToken1017;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION PottedSaleToken1017;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION PottedSaleToken1017;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
