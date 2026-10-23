-- ============================================================================
-- 2026-10-26_LabourAttendanceOutlet_Rollback.sql   *** PREPARED FOR REVIEW -- NOT APPLIED ***
--
-- Undoes 2026-10-26_LabourAttendanceOutlet.sql:
--   * TR_DailyLabourAttendance_Location -> the 2026-10-06 version (text copied byte-for-byte from
--     2026-10-06_DailyLabourAttendance.sql; SHA-256 verified, 53484);
--   * removes the Labour.Attendance grant of Outlet Sales if (and only if) the migration added it last, and deletes the
--     permission code only if the migration created it and no role holds it any more.
-- REFUSED (53483, nothing changed) once any attendance was recorded for an Outlet Area: the restored trigger would leave those
--   rows uncorrectable. Keep the schema instead.
-- TARGET SAFEGUARDS and checklist: exactly as the migration (7 checks; DRYRUN first). One transaction. Idempotent.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 53471, '2026-10-26_LabourAttendanceOutlet_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 53472, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 53473, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 53474, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 53475, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 53476, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 53477, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION LabourOutlet1026Rollback;

BEGIN TRY
    PRINT '=== Server / machine / database: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    DECLARE @OutletRows INT = (SELECT COUNT(*) FROM dbo.DailyLabourAttendance d WITH (UPDLOCK, HOLDLOCK) JOIN dbo.Area a ON a.Id = d.AreaId WHERE a.AreaType = N'Outlet');
    PRINT '=== Outlet attendance rows: ' + CAST(@OutletRows AS NVARCHAR(20)) + ' ===';
    IF @OutletRows > 0
        THROW 53483, 'Attendance has been recorded for an Outlet Area -- the rollback would leave it uncorrectable. Nothing changed.', 1;

    -- ---- 1. permission -------------------------------------------------------------------------------------------------
    DECLARE @Code NVARCHAR(100) = N'Labour.Attendance';
    DECLARE @Script NVARCHAR(200) = N'2026-10-26_LabourAttendanceOutlet.sql';
    DECLARE @RollbackScript NVARCHAR(200) = N'2026-10-26_LabourAttendanceOutlet_Rollback.sql';
    DECLARE @OwnedDescription NVARCHAR(200) =
        N'Record daily labour attendance in own Areas -- attendance only [created by 2026-10-26_LabourAttendanceOutlet.sql]';
    DECLARE @Perm INT = (SELECT Id FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK) WHERE Code = @Code);

    IF @Perm IS NULL
        PRINT '=== Permission Labour.Attendance does not exist -- no grant or code to remove ===';
    ELSE
    BEGIN
        DECLARE @PermText NVARCHAR(20) = CAST(@Perm AS NVARCHAR(20));
        DECLARE @Roles TABLE (RoleName NVARCHAR(100) PRIMARY KEY, RoleId INT NULL);
        INSERT INTO @Roles (RoleName) VALUES (N'Outlet Sales');
        UPDATE x SET RoleId = r.Id FROM @Roles x JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = x.RoleName;

        DECLARE @RoleName NVARCHAR(100), @Role INT, @LastChange NVARCHAR(10), @LastScript NVARCHAR(200);
        DECLARE revoke_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT RoleName, RoleId FROM @Roles WHERE RoleId IS NOT NULL ORDER BY RoleName;
        OPEN revoke_cursor;
        FETCH NEXT FROM revoke_cursor INTO @RoleName, @Role;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            IF EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE RoleId = @Role AND PermissionId = @Perm)
            BEGIN
                SET @LastChange = NULL; SET @LastScript = NULL;
                SELECT TOP 1 @LastChange = Change, @LastScript = Script
                FROM dbo.RolePermissionChangeLog WHERE RoleId = @Role AND PermissionCode = @Code ORDER BY Id DESC;
                IF ISNULL(@LastChange, N'') <> N'Added' OR ISNULL(@LastScript, N'') <> @Script
                    THROW 53478, 'The Labour.Attendance grant was not (last) added by 2026-10-26_LabourAttendanceOutlet.sql -- nothing changed.', 1;
                INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
                VALUES (@Role, @RoleName, @Code, N'Removed', @RollbackScript, N'Rollback of 2026-10-26_LabourAttendanceOutlet.sql [PermissionId=' + @PermText + N']');
                DELETE FROM dbo.RolePermissions WHERE RoleId = @Role AND PermissionId = @Perm;
                PRINT '=== Labour.Attendance removed from ' + @RoleName + ' ===';
            END
            ELSE
                PRINT '=== ' + @RoleName + ': not granted -- nothing to remove ===';
            FETCH NEXT FROM revoke_cursor INTO @RoleName, @Role;
        END
        CLOSE revoke_cursor;
        DEALLOCATE revoke_cursor;

        DECLARE @CreatedByMigration BIT = CASE WHEN
                EXISTS (SELECT 1 FROM dbo.Permissions WHERE Id = @Perm AND Code = @Code AND Description = @OwnedDescription)
            AND EXISTS (SELECT 1 FROM dbo.RolePermissionChangeLog
                        WHERE PermissionCode = @Code AND Change = N'Added' AND Script = @Script
                          AND Reason LIKE N'%[[]code created by this script; PermissionId=' + @PermText + N']')
            THEN 1 ELSE 0 END;
        IF @CreatedByMigration = 0
            PRINT '=== Permission code KEPT: it was not created by the migration ===';
        ELSE IF EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE PermissionId = @Perm)
            PRINT '=== Permission code KEPT: a role still holds Labour.Attendance ===';
        ELSE
        BEGIN
            DELETE FROM dbo.Permissions WHERE Id = @Perm AND Code = @Code AND Description = @OwnedDescription;
            IF @@ROWCOUNT <> 1
                THROW 53479, 'The migration-owned permission code could not be deleted exactly once -- nothing changed.', 1;
            PRINT '=== Permission Labour.Attendance (created by the migration) deleted ===';
        END
    END

    -- ---- 2. the 2026-10-06 trigger, byte-for-byte ------------------------------------------------------------------------
    IF OBJECT_ID('dbo.TR_DailyLabourAttendance_Location', 'TR') IS NOT NULL
        EXEC (N'DROP TRIGGER dbo.TR_DailyLabourAttendance_Location;');
        EXEC (N'
CREATE TRIGGER dbo.TR_DailyLabourAttendance_Location ON dbo.DailyLabourAttendance
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- Only new rows, or rows whose date/location changed, are checked (a past
    -- record stays correctable if its Polyhouse is later moved to another Area).
    ;WITH changed AS (
        SELECT i.Id, i.LabourDate, i.AreaId, i.PolyhouseId
        FROM inserted i
        LEFT JOIN deleted d ON d.Id = i.Id
        WHERE d.Id IS NULL
           OR d.LabourDate <> i.LabourDate
           OR d.AreaId <> i.AreaId
           OR ISNULL(d.PolyhouseId, -1) <> ISNULL(i.PolyhouseId, -1)
    )
    SELECT c.Id, c.LabourDate, c.AreaId, c.PolyhouseId, a.AreaType, p.AreaId AS PolyhouseAreaId
    INTO #chk
    FROM changed c
    INNER JOIN dbo.Area a ON a.Id = c.AreaId
    LEFT JOIN dbo.Polyhouses p ON p.Id = c.PolyhouseId;

    IF EXISTS (SELECT 1 FROM #chk WHERE AreaType = N''Outlet'')
        THROW 51714, ''Outlet is not part of Daily Labour.'', 1;
    IF EXISTS (SELECT 1 FROM #chk WHERE AreaType = N''MainOffice'' AND PolyhouseId IS NULL)
        THROW 51711, ''No Polyhouse selected. Please select a Polyhouse for this Main Office Area.'', 1;
    IF EXISTS (SELECT 1 FROM #chk WHERE ISNULL(AreaType, N'''') <> N''MainOffice'' AND PolyhouseId IS NOT NULL)
        THROW 51712, ''Labour for this Area is recorded Area-wise -- Polyhouse must be empty.'', 1;
    IF EXISTS (SELECT 1 FROM #chk WHERE PolyhouseId IS NOT NULL AND ISNULL(PolyhouseAreaId, -1) <> AreaId)
        THROW 51713, ''This Polyhouse does not belong to the selected Area.'', 1;
    IF EXISTS (SELECT 1 FROM #chk c
               WHERE EXISTS (SELECT 1 FROM dbo.DailyLabourCounts l
                             WHERE l.LabourDate = c.LabourDate AND l.AreaId = c.AreaId
                               AND ISNULL(l.PolyhouseId, -1) = ISNULL(c.PolyhouseId, -1)))
        THROW 51725, ''This date and location already has a manual labour record. Attendance cannot be added for it.'', 1;
END');
    IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_DailyLabourAttendance_Location'))), 2) <> 'BB53B26D747EE0FC3D32BE6DB16FD566DEA1CA617C5AE97514A8C785CB41B382'
        THROW 53484, 'The restored trigger is not byte-identical to its 2026-10-06 version -- nothing changed.', 1;
    PRINT '=== TR_DailyLabourAttendance_Location restored (SHA-256 verified; Outlet excluded again) ===';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION LabourOutlet1026Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION LabourOutlet1026Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION LabourOutlet1026Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
