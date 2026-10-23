-- ============================================================================
-- 2026-10-26_LabourAttendanceOutlet.sql           *** PREPARED FOR REVIEW -- NOT APPLIED ***
--
-- WHY (owner request 2026-10-23): "add outlet areas to attendance of labour - give permission to add attendance for that
--   areas employees". Daily Labour Attendance (9:00 AM / 2:00 PM headcounts, 2026-10-06_DailyLabourAttendance.sql) refuses
--   Outlet Areas in the database (TR_DailyLabourAttendance_Location, 51714), and the Outlet staff hold no labour permission.
--
-- WHAT:
--   1. TR_DailyLabourAttendance_Location (new version): the Outlet refusal (51714) is removed. An Outlet Area is recorded
--      AREA-WISE like a Mother Plant Area (Polyhouse must be empty, 51712 -- unchanged). Every other rule is unchanged.
--      ONLY attendance changes: TR_DailyLabourCounts_Location and TR_LabourOvertime_Location still refuse Outlet.
--   2. Permission Labour.Attendance ("record daily labour attendance in own Areas -- attendance only"), created if missing and
--      granted to the role "Outlet Sales". It opens only Labour > Daily Attendance (FeatureAuthorizationConventions); it does
--      NOT open overtime, labour counts or any history page. The Area rule is unchanged: a user records attendance only for
--      Areas they are assigned to (dbo.UserRoles.AreaId) -- for Outlet Sales, their Outlet. Logged in RolePermissionChangeLog.
--
-- NOT changed: existing attendance / count / overtime rows, the audit history, other roles or permissions.
--
-- APPLICATION WITHOUT THIS SCRIPT: the code probes the trigger (Data/LabourAttendanceOutletSchema.cs); without it Outlet stays
--   excluded from Daily Attendance exactly as before.
--
-- PREDECESSOR: TR_DailyLabourAttendance_Location must be the 2026-10-06 version (SHA-256 checked, 53458) or this script's own.
-- TARGET SAFEGUARDS and OPERATOR CHECKLIST: exactly as 2026-10-25_MaterialStockTransfers.sql (7 checks; DRYRUN first):
--   sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER="<ServerName>" TARGET_MACHINE="<MachineName>"
--             TARGET_DB="<DatabaseName>" TARGET_DB_CREATED="<DatabaseCreated>" -i 2026-10-26_LabourAttendanceOutlet.sql
-- Requires: 2026-10-06_DailyLabourAttendance.sql, 2026-10-11_TrayAccessByLocation.sql (dbo.RolePermissionChangeLog).
-- One transaction. Idempotent. Undo: 2026-10-26_LabourAttendanceOutlet_Rollback.sql (refused once Outlet attendance exists).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 53451, '2026-10-26_LabourAttendanceOutlet.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 53452, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 53453, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 53454, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 53455, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 53456, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 53457, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION LabourOutlet1026;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.DailyLabourAttendance', 'U') IS NULL OR OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NULL
        THROW 53459, 'Run 2026-10-06_DailyLabourAttendance.sql and 2026-10-11_TrayAccessByLocation.sql first.', 1;

    DECLARE @Hash VARCHAR(64) = CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_DailyLabourAttendance_Location'))), 2);
    DECLARE @IsOurs BIT = CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.TR_DailyLabourAttendance_Location')) LIKE N'%[[]2026-10-26 OutletAttendance]%' THEN 1 ELSE 0 END;
    PRINT '=== TR_DailyLabourAttendance_Location SHA-256 ' + ISNULL(@Hash, '(missing)') + CASE WHEN @IsOurs = 1 THEN ' (this script''s version)' ELSE '' END + ' ===';
    IF @IsOurs = 0 AND ISNULL(@Hash, '') <> 'BB53B26D747EE0FC3D32BE6DB16FD566DEA1CA617C5AE97514A8C785CB41B382'
        THROW 53458, 'TR_DailyLabourAttendance_Location is not the expected 2026-10-06 version -- schema drift, nothing changed.', 1;

    PRINT '=== Before: attendance rows per Area type (must be identical After) ===';
    SELECT a.AreaType, COUNT(*) AS Rows FROM dbo.DailyLabourAttendance d JOIN dbo.Area a ON a.Id = d.AreaId GROUP BY a.AreaType ORDER BY 1;

    -- ---- 1. the location trigger ----------------------------------------------------------------------------------
    EXEC (N'DROP TRIGGER dbo.TR_DailyLabourAttendance_Location;');
    EXEC (N'
CREATE TRIGGER dbo.TR_DailyLabourAttendance_Location ON dbo.DailyLabourAttendance
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- [2026-10-26 OutletAttendance] as 2026-10-06_DailyLabourAttendance.sql, except that Outlet Areas take attendance too
    -- (Area-wise, like a Mother Plant Area). Labour counts and overtime still exclude Outlet.
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
    PRINT '=== TR_DailyLabourAttendance_Location: Outlet Areas allowed (Area-wise) ===';

    -- ---- 2. Permission Labour.Attendance -> Outlet Sales ---------------------------------------------------------------
    DECLARE @Code NVARCHAR(100) = N'Labour.Attendance';
    DECLARE @Script NVARCHAR(200) = N'2026-10-26_LabourAttendanceOutlet.sql';
    DECLARE @OwnedDescription NVARCHAR(200) =
        N'Record daily labour attendance in own Areas -- attendance only [created by 2026-10-26_LabourAttendanceOutlet.sql]';
    DECLARE @Roles TABLE (RoleName NVARCHAR(100) PRIMARY KEY, RoleId INT NULL);
    INSERT INTO @Roles (RoleName) VALUES (N'Outlet Sales');
    UPDATE x SET RoleId = r.Id FROM @Roles x JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = x.RoleName;
    IF EXISTS (SELECT 1 FROM @Roles WHERE RoleId IS NULL)
        THROW 53460, 'Role Outlet Sales not found -- nothing changed.', 1;
    IF EXISTS (SELECT x.RoleName FROM @Roles x JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = x.RoleName GROUP BY x.RoleName HAVING COUNT(*) > 1)
        THROW 53461, 'A role name matches more than one dbo.Roles row -- nothing changed.', 1;

    PRINT '=== Holders of the role and the Area of each assignment (they record attendance only for these Areas) ===';
    SELECT x.RoleName AS Role, u.Id AS UserId, u.Name, u.IsActive, a.Id AS AreaId, a.Name AS Area, a.AreaType
    FROM @Roles x JOIN dbo.UserRoles ur ON ur.RoleId = x.RoleId JOIN dbo.IMSUsers u ON u.Id = ur.UserId LEFT JOIN dbo.Area a ON a.Id = ur.AreaId
    ORDER BY x.RoleName, u.Id;

    DECLARE @CreatedHere BIT = 0;
    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK) WHERE Code = @Code)
    BEGIN
        IF COLUMNPROPERTY(OBJECT_ID('dbo.Permissions'), 'Id', 'IsIdentity') = 1
            INSERT INTO dbo.Permissions (Code, Description) VALUES (@Code, @OwnedDescription);
        ELSE
            INSERT INTO dbo.Permissions (Id, Code, Description)
            SELECT ISNULL(MAX(Id), 0) + 1, @Code, @OwnedDescription FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK);
        SET @CreatedHere = 1;
        PRINT '=== Permission Labour.Attendance created by this script ===';
    END
    ELSE
        PRINT '=== Permission Labour.Attendance already exists -- used as it is (the rollback deletes it only if this script created it) ===';
    DECLARE @Perm INT = (SELECT Id FROM dbo.Permissions WHERE Code = @Code);

    DECLARE @RoleName NVARCHAR(100), @Role INT;
    DECLARE grant_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT RoleName, RoleId FROM @Roles ORDER BY RoleName;
    OPEN grant_cursor;
    FETCH NEXT FROM grant_cursor INTO @RoleName, @Role;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE RoleId = @Role AND PermissionId = @Perm)
            PRINT '=== ' + @RoleName + ': already granted -- no-op ===';
        ELSE
        BEGIN
            INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
            VALUES (@Role, @RoleName, @Code, N'Added', @Script,
                    N'Outlet staff record daily labour attendance for their own Outlet (owner request 2026-10-23) '
                    + CASE WHEN @CreatedHere = 1 THEN N'[code created by this script; PermissionId=' ELSE N'[code pre-existed; PermissionId=' END
                    + CAST(@Perm AS NVARCHAR(20)) + N']');
            INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@Role, @Perm);
            PRINT '=== Labour.Attendance granted to ' + @RoleName + ' ===';
        END
        FETCH NEXT FROM grant_cursor INTO @RoleName, @Role;
    END
    CLOSE grant_cursor;
    DEALLOCATE grant_cursor;

    PRINT '=== After ===';
    SELECT name, CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(object_id)), 2) AS Sha256 FROM sys.triggers WHERE name = 'TR_DailyLabourAttendance_Location';
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS Role, p.Code, p.Id AS PermissionId
    FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code = @Code;
    SELECT a.AreaType, COUNT(*) AS Rows FROM dbo.DailyLabourAttendance d JOIN dbo.Area a ON a.Id = d.AreaId GROUP BY a.AreaType ORDER BY 1;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION LabourOutlet1026;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION LabourOutlet1026;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION LabourOutlet1026;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
