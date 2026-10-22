-- ============================================================================
-- 2026-10-20_TransplantApprovalAnyDate_Rollback.sql
--
-- Undoes 2026-10-20_TransplantApprovalAnyDate.sql: TR_ReadyConfirmations_BedReadyDate goes back to the exact 2026-10-17
-- version (a Transplant batch is approved only from its Ready Date; SHA-256 checked after the restore).
-- Approvals already recorded stay valid and unchanged (the trigger only checks NEW approvals).
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52621, '2026-10-20_TransplantApprovalAnyDate_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52622, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52623, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION TransplantAnyDate1020Rollback;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF NOT (OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate')) LIKE N'%TransplantApprovalAnyDate%')
        PRINT '=== Not applied -- no-op ===';
    ELSE
    BEGIN
        EXEC (N'ALTER TRIGGER dbo.TR_ReadyConfirmations_BedReadyDate ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- India business date (IST, +05:30, no daylight saving) -- the same day AppClock.Today gives.
    -- 2026-10-17: a tray batch from a Bed Sowing may be approved on ANY date (its Ready Date stays recorded) -- only by
    -- the designated Bed Sowing approver (TR_ReadyConfirmations_AssignedSupervisor).
    -- 2026-10-13: a tray batch transplanted from received Ready Stock (Ready Date = transplant date + Ready Days) is
    -- never approved before its Ready Date. Direct Seed Sowing and Cutting Sowing are not affected.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE sw.SourceType = N''Transplant''
                 AND (sw.ExpectedReadyDate IS NULL OR CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), ''+05:30'') AS date) < sw.ExpectedReadyDate))
        THROW 52208, ''A tray batch transplanted from Ready Stock cannot be approved before its Ready Date.'', 1;
END');
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate'))), 2)
           <> '0DBAF1E4FFF23864C5C31E851D47BEA53F30D97DC5080795E867620E233501F4'
            THROW 52624, 'TR_ReadyConfirmations_BedReadyDate was not restored byte-for-byte -- rolling back.', 1;
        PRINT '=== TR_ReadyConfirmations_BedReadyDate restored: a Transplant batch only from its Ready Date ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TransplantAnyDate1020Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TransplantAnyDate1020Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TransplantAnyDate1020Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
