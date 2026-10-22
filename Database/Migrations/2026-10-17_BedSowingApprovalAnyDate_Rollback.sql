-- ============================================================================
-- 2026-10-17_BedSowingApprovalAnyDate_Rollback.sql
--
-- Undoes 2026-10-17_BedSowingApprovalAnyDate.sql: TR_ReadyConfirmations_BedReadyDate is re-created byte-for-byte as the
-- 2026-10-13 version (a Bed Sowing batch is again never approved before its Ready Date; verified by SHA-256) and fires
-- first again. Approvals already recorded stay valid and unchanged (the trigger only checks NEW approvals).
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52511, '2026-10-17_BedSowingApprovalAnyDate_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52512, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52513, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION BedAnyDate1017Rollback;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate')) LIKE N'%52178%'
        PRINT '=== Not applied -- no-op ===';
    ELSE
    BEGIN
        IF OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate')) NOT LIKE N'%2026-10-17: a tray batch from a Bed Sowing may be approved on ANY date%'
            THROW 52514, 'TR_ReadyConfirmations_BedReadyDate is not the 2026-10-17 version -- schema drift, nothing changed.', 1;
        DROP TRIGGER dbo.TR_ReadyConfirmations_BedReadyDate;
        EXEC (N'CREATE TRIGGER dbo.TR_ReadyConfirmations_BedReadyDate ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- India business date (IST, +05:30, no daylight saving) -- the same day AppClock.Today gives.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE sw.SourceType = N''BedSowing''
                 AND (sw.ExpectedReadyDate IS NULL OR CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), ''+05:30'') AS date) < sw.ExpectedReadyDate))
        THROW 52178, ''A tray batch from a Bed Sowing cannot be approved before its Ready Date.'', 1;
    -- 2026-10-13: likewise a tray batch transplanted from received Ready Stock (Ready Date = transplant
    -- date + Ready Days). Direct Seed Sowing and Cutting Sowing are not affected.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE sw.SourceType = N''Transplant''
                 AND (sw.ExpectedReadyDate IS NULL OR CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), ''+05:30'') AS date) < sw.ExpectedReadyDate))
        THROW 52208, ''A tray batch transplanted from Ready Stock cannot be approved before its Ready Date.'', 1;
END');
        EXEC sp_settriggerorder @triggername = N'dbo.TR_ReadyConfirmations_BedReadyDate', @order = N'First', @stmttype = N'INSERT';
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate'))), 2)
           <> 'E867F5775B1BEB1F050E2569E92C5E4413A06C131C650F3F5A282B65426C2045'
            THROW 52515, 'The restored trigger is not byte-for-byte the 2026-10-13 version -- rolled back.', 1;
        PRINT '=== Rolled back: Bed Sowing batches again approved only from their Ready Date ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION BedAnyDate1017Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION BedAnyDate1017Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION BedAnyDate1017Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
