-- ============================================================================
-- 2026-10-06_DailyLabourAttendance_Rollback.sql
--
-- Undoes 2026-10-06_DailyLabourAttendance.sql: drops its triggers, then
-- dbo.DailyLabourAttendanceHistory and dbo.DailyLabourAttendance.
-- REFUSES to run while dbo.DailyLabourAttendance has any row, so recorded
-- attendance can never be lost by a rollback (restore the pre-migration
-- backup instead if that is really intended).
-- The application code that uses these tables must be rolled back first.
--
-- NOT changed: dbo.DailyLabourCounts and every other existing object.
-- SAFETY: same guards as the migration. One transaction. Idempotent.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51731, 'This rollback may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51732, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51733, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION DailyLabourAttendanceRollback;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    IF OBJECT_ID('dbo.DailyLabourAttendance', 'U') IS NOT NULL
    BEGIN
        DECLARE @Rows INT;
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.DailyLabourAttendance', N'@n INT OUTPUT', @n = @Rows OUTPUT;
        IF @Rows > 0
            THROW 51734, 'dbo.DailyLabourAttendance has attendance rows -- refusing to drop it. Restore the pre-migration backup if that is really intended.', 1;
    END

    IF OBJECT_ID('dbo.TR_DailyLabourAttendanceHistory_Protect', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_DailyLabourAttendanceHistory_Protect;
    IF OBJECT_ID('dbo.TR_DailyLabourAttendance_Audit', 'TR') IS NOT NULL       DROP TRIGGER dbo.TR_DailyLabourAttendance_Audit;
    IF OBJECT_ID('dbo.TR_DailyLabourAttendance_Location', 'TR') IS NOT NULL    DROP TRIGGER dbo.TR_DailyLabourAttendance_Location;
    IF OBJECT_ID('dbo.DailyLabourAttendanceHistory', 'U') IS NOT NULL          DROP TABLE dbo.DailyLabourAttendanceHistory;
    IF OBJECT_ID('dbo.DailyLabourAttendance', 'U') IS NOT NULL                 DROP TABLE dbo.DailyLabourAttendance;

    SELECT OBJECT_ID('dbo.DailyLabourAttendance') AS AttendanceTable, OBJECT_ID('dbo.DailyLabourAttendanceHistory') AS HistoryTable,
           (SELECT COUNT(*) FROM dbo.DailyLabourCounts) AS DailyLabourCountsRows;

    IF N'$(MODE)' = N'COMMIT'
    BEGIN
        COMMIT TRANSACTION DailyLabourAttendanceRollback;
        PRINT '=== COMMITTED ===';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION DailyLabourAttendanceRollback;
        PRINT '=== DRY RUN -- rolled back, nothing changed ===';
    END
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH
GO
