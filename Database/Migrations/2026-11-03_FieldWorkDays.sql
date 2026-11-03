-- ============================================================================
-- 2026-11-03_FieldWorkDays.sql          *** PREPARED FOR REVIEW -- NOT APPLIED to PlantsIMS2_Test (local or live) ***
--
-- WHY (owner requirement 2026-11-03, Field Work module Phase 1): marketing / sales staff who visit customers record
--   Start Day / End Day with a live odometer photo, GPS position and km reading. Managers review each day (approve /
--   flag / reopen). An open day is closed automatically at midnight IST ("AutoClosed").
--
-- WHAT (ADDS ONLY -- no existing table, column, constraint, trigger or index is changed):
--   1. dbo.FieldPhotos        live camera photos (files on disk outside wwwroot; the row holds the GUID file name)
--   2. dbo.FieldDays          one row per employee per IST date: start + end readings, status, review
--   3. dbo.FieldDayEvents     audit trail: Started / Ended / AutoClosed / Approved / Flagged / Reopened
--   4. dbo.FieldWorkConsents  the one-time consent (location, time and photos are recorded for work purposes)
--   5. TR_FieldDays_Guard     a day's owner, date, start reading and start photo never change; a photo belongs to the
--                             day's employee and has the right purpose
--   6. Permissions FieldWork.Day (field employee: must Start Day; sees own days only) and FieldWork.Manage (all field
--      employees' days, review); roles "Field Employee" (FieldWork.Day) and "Field Manager" (FieldWork.Manage),
--      both also Dashboard.View (the page every login lands on, as every other role has it).
--      New rows in dbo.Permissions / dbo.Roles / dbo.RolePermissions, logged in dbo.RolePermissionChangeLog.
--      No user is assigned to either role by this script (Admin > User Roles does that).
--
-- APPLICATION WITHOUT THIS SCRIPT: Data/FieldWorkSchema.cs reports "not ready": the Field Work pages explain that the
--   update is missing, the Start Day gate does nothing, and the auto-close job skips. Every other page is unchanged.
--
-- TARGET SAFEGUARDS (as 2026-10-31_AllSeedlingsDamagedApproval.sql): the local AND the live database can both be named
--   PlantsIMS2_Test. The first batch refuses to continue unless ALL of these hold (nothing is read or written before them):
--     1. DB_NAME() is PlantsIMS2_Test or PlantsIMS2_Scratch_*      2. MODE is DRYRUN or COMMIT (exact upper case)
--     3. @@SERVERNAME = $(TARGET_SERVER)                            4. SERVERPROPERTY('ServerName') = @@SERVERNAME
--     5. SERVERPROPERTY('MachineName') = $(TARGET_MACHINE)          6. DB_NAME() = $(TARGET_DB)
--     7. sys.databases.create_date of THIS database = $(TARGET_DB_CREATED)   (yyyy-mm-ddThh:mi:ss)
--
-- OPERATOR CHECKLIST
--   a) read-only identity query, keep its output:
--        SELECT @@SERVERNAME AS ServerName, CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) AS ServerProperty,
--               CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) AS MachineName, DB_NAME() AS DatabaseName,
--               CONVERT(CHAR(19), d.create_date, 126) AS DatabaseCreated
--        FROM sys.databases d WHERE d.database_id = DB_ID();
--   b) confirm independently that this machine IS the intended one; c) take a COPY_ONLY backup;
--   d) sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN 'TARGET_SERVER="<ServerName>"' 'TARGET_MACHINE="<MachineName>"'
--             'TARGET_DB="<DatabaseName>"' 'TARGET_DB_CREATED="<DatabaseCreated>"' -i 2026-11-03_FieldWorkDays.sql
--      (from PowerShell, single-quote each -v argument as shown, or the quotes are lost)
--   It ends "DRY RUN -- rolled back". Then MODE=COMMIT -- only with the owner's explicit approval for that database.
--
-- Requires: 2026-10-11_TrayAccessByLocation.sql (dbo.RolePermissionChangeLog). One transaction. Idempotent: a re-run on
--   a database that has it reports "already applied" and changes nothing.
-- Undo: 2026-11-03_FieldWorkDays_Rollback.sql (refused once any Field Work row exists or a user holds either role).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 54101, '2026-11-03_FieldWorkDays.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 54102, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 54103, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 54104, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 54105, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 54106, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 54107, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION FieldWorkDays1103;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NULL
        THROW 54108, 'Run 2026-10-11_TrayAccessByLocation.sql first (dbo.RolePermissionChangeLog).', 1;
    IF OBJECT_ID('dbo.IMSUsers', 'U') IS NULL OR COL_LENGTH('dbo.IMSUsers', 'Id') IS NULL
        THROW 54109, 'dbo.IMSUsers(Id) is missing -- nothing changed.', 1;

    PRINT '=== Before: row counts of existing tables (only Roles / Permissions / RolePermissions / RolePermissionChangeLog may grow) ===';
    SELECT 'IMSUsers' AS TableName, COUNT(*) AS Rows FROM dbo.IMSUsers
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'Roles', COUNT(*) FROM dbo.Roles
    UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'RolePermissionChangeLog', COUNT(*) FROM dbo.RolePermissionChangeLog;

    DECLARE @Script NVARCHAR(200) = N'2026-11-03_FieldWorkDays.sql';
    DECLARE @Marker NVARCHAR(100) = N' [created by 2026-11-03_FieldWorkDays.sql]';

    ------------------------------------------------------------------
    -- 1. dbo.FieldPhotos
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.FieldPhotos', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldPhotos (
            Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldPhotos PRIMARY KEY,
            OwnerUserId     INT           NOT NULL CONSTRAINT FK_FieldPhotos_Owner REFERENCES dbo.IMSUsers(Id),
            Purpose         VARCHAR(20)   NOT NULL CONSTRAINT CK_FieldPhotos_Purpose CHECK (Purpose IN ('DayStart', 'DayEnd')),
            StoredFileName  VARCHAR(64)   NOT NULL CONSTRAINT UQ_FieldPhotos_StoredFileName UNIQUE,
            ContentType     VARCHAR(30)   NOT NULL CONSTRAINT CK_FieldPhotos_ContentType CHECK (ContentType = 'image/jpeg'),
            SizeBytes       INT           NOT NULL CONSTRAINT CK_FieldPhotos_Size CHECK (SizeBytes > 0),
            CapturedAtUtc   DATETIME2(0)  NOT NULL CONSTRAINT DF_FieldPhotos_CapturedAtUtc DEFAULT (SYSUTCDATETIME()),
            Latitude        DECIMAL(9,6)  NULL,
            Longitude       DECIMAL(9,6)  NULL,
            AccuracyM       DECIMAL(9,1)  NULL
        );
        CREATE INDEX IX_FieldPhotos_Owner ON dbo.FieldPhotos (OwnerUserId, CapturedAtUtc);
        PRINT '=== dbo.FieldPhotos created ===';
    END
    ELSE PRINT '=== dbo.FieldPhotos already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 2. dbo.FieldDays
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.FieldDays', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldDays (
            Id                    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldDays PRIMARY KEY,
            UserId                INT            NOT NULL CONSTRAINT FK_FieldDays_User REFERENCES dbo.IMSUsers(Id),
            WorkDate              DATE           NOT NULL,
            Status                VARCHAR(12)    NOT NULL CONSTRAINT CK_FieldDays_Status CHECK (Status IN ('Open', 'Closed', 'AutoClosed')),
            VehicleType           VARCHAR(20)    NOT NULL CONSTRAINT CK_FieldDays_VehicleType CHECK (VehicleType IN ('OwnBike', 'OwnCar', 'CompanyVehicle')),
            StartedAtUtc          DATETIME2(0)   NOT NULL CONSTRAINT DF_FieldDays_StartedAtUtc DEFAULT (SYSUTCDATETIME()),
            StartLatitude         DECIMAL(9,6)   NULL,
            StartLongitude        DECIMAL(9,6)   NULL,
            StartAccuracyM        DECIMAL(9,1)   NULL,
            StartKm               DECIMAL(9,1)   NOT NULL CONSTRAINT CK_FieldDays_StartKm CHECK (StartKm >= 0),
            StartPhotoId          INT            NULL CONSTRAINT FK_FieldDays_StartPhoto REFERENCES dbo.FieldPhotos(Id),
            StartExceptionReason  NVARCHAR(300)  NULL,
            StartFlagReasons      NVARCHAR(500)  NULL,
            EndedAtUtc            DATETIME2(0)   NULL,
            EndLatitude           DECIMAL(9,6)   NULL,
            EndLongitude          DECIMAL(9,6)   NULL,
            EndAccuracyM          DECIMAL(9,1)   NULL,
            EndKm                 DECIMAL(9,1)   NULL,
            EndPhotoId            INT            NULL CONSTRAINT FK_FieldDays_EndPhoto REFERENCES dbo.FieldPhotos(Id),
            EndExceptionReason    NVARCHAR(300)  NULL,
            EndFlagReasons        NVARCHAR(500)  NULL,
            AutoClosedAtUtc       DATETIME2(0)   NULL,
            GpsDistanceKm         DECIMAL(9,2)   NULL CONSTRAINT CK_FieldDays_GpsDistance CHECK (GpsDistanceKm IS NULL OR GpsDistanceKm >= 0),
            ReviewStatus          VARCHAR(10)    NOT NULL CONSTRAINT DF_FieldDays_ReviewStatus DEFAULT ('Pending')
                                                 CONSTRAINT CK_FieldDays_ReviewStatus CHECK (ReviewStatus IN ('Pending', 'Approved', 'Flagged')),
            ReviewedByUserId      INT            NULL CONSTRAINT FK_FieldDays_ReviewedBy REFERENCES dbo.IMSUsers(Id),
            ReviewedAtUtc         DATETIME2(0)   NULL,
            ReviewComment         NVARCHAR(300)  NULL,
            StartSubmissionToken  UNIQUEIDENTIFIER NOT NULL,
            EndSubmissionToken    UNIQUEIDENTIFIER NULL,
            ModifiedAtUtc         DATETIME2(0)   NULL,
            CONSTRAINT CK_FieldDays_EndKm CHECK (EndKm IS NULL OR EndKm >= StartKm),
            -- a position is all three values or none, inside the valid ranges
            CONSTRAINT CK_FieldDays_StartPosition CHECK (
                (StartLatitude IS NULL AND StartLongitude IS NULL AND StartAccuracyM IS NULL)
             OR (StartLatitude BETWEEN -90 AND 90 AND StartLongitude BETWEEN -180 AND 180 AND StartAccuracyM > 0)),
            CONSTRAINT CK_FieldDays_EndPosition CHECK (
                (EndLatitude IS NULL AND EndLongitude IS NULL AND EndAccuracyM IS NULL)
             OR (EndLatitude BETWEEN -90 AND 90 AND EndLongitude BETWEEN -180 AND 180 AND EndAccuracyM > 0)),
            -- a missing photo or position needs a reason
            CONSTRAINT CK_FieldDays_StartEvidence CHECK (
                (StartPhotoId IS NOT NULL AND StartLatitude IS NOT NULL) OR LEN(LTRIM(RTRIM(ISNULL(StartExceptionReason, N'')))) > 0),
            -- Open: no end reading; Closed: an end reading (photo + position, or a reason); AutoClosed: closed by the job, no end km
            CONSTRAINT CK_FieldDays_StatusShape CHECK (
                (Status = 'Open' AND EndedAtUtc IS NULL AND EndKm IS NULL AND EndPhotoId IS NULL AND EndLatitude IS NULL
                     AND EndExceptionReason IS NULL AND AutoClosedAtUtc IS NULL AND EndSubmissionToken IS NULL)
             OR (Status = 'Closed' AND EndedAtUtc IS NOT NULL AND EndKm IS NOT NULL AND AutoClosedAtUtc IS NULL
                     AND ((EndPhotoId IS NOT NULL AND EndLatitude IS NOT NULL) OR LEN(LTRIM(RTRIM(ISNULL(EndExceptionReason, N'')))) > 0))
             OR (Status = 'AutoClosed' AND AutoClosedAtUtc IS NOT NULL AND EndedAtUtc IS NULL AND EndKm IS NULL AND EndPhotoId IS NULL)),
            CONSTRAINT CK_FieldDays_Review CHECK (
                (ReviewStatus = 'Approved' AND Status <> 'Open' AND ReviewedByUserId IS NOT NULL AND ReviewedAtUtc IS NOT NULL)
             OR ReviewStatus IN ('Pending', 'Flagged'))
        );
        CREATE UNIQUE INDEX UX_FieldDays_User_Date ON dbo.FieldDays (UserId, WorkDate);
        CREATE UNIQUE INDEX UX_FieldDays_OneOpen ON dbo.FieldDays (UserId) WHERE Status = 'Open';
        CREATE INDEX IX_FieldDays_Date_Status ON dbo.FieldDays (WorkDate, Status) INCLUDE (UserId, ReviewStatus);
        CREATE UNIQUE INDEX UX_FieldDays_StartToken ON dbo.FieldDays (StartSubmissionToken);
        CREATE UNIQUE INDEX UX_FieldDays_EndToken ON dbo.FieldDays (EndSubmissionToken) WHERE EndSubmissionToken IS NOT NULL;
        CREATE UNIQUE INDEX UX_FieldDays_StartPhoto ON dbo.FieldDays (StartPhotoId) WHERE StartPhotoId IS NOT NULL;
        CREATE UNIQUE INDEX UX_FieldDays_EndPhoto ON dbo.FieldDays (EndPhotoId) WHERE EndPhotoId IS NOT NULL;
        PRINT '=== dbo.FieldDays created ===';
    END
    ELSE PRINT '=== dbo.FieldDays already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 3. dbo.FieldDayEvents (audit trail)
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.FieldDayEvents', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldDayEvents (
            Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldDayEvents PRIMARY KEY,
            FieldDayId  INT            NOT NULL CONSTRAINT FK_FieldDayEvents_Day REFERENCES dbo.FieldDays(Id),
            EventType   VARCHAR(12)    NOT NULL CONSTRAINT CK_FieldDayEvents_Type
                                       CHECK (EventType IN ('Started', 'Ended', 'AutoClosed', 'Approved', 'Flagged', 'Reopened')),
            ByUserId    INT            NULL CONSTRAINT FK_FieldDayEvents_User REFERENCES dbo.IMSUsers(Id),   -- NULL = the auto-close job
            AtUtc       DATETIME2(0)   NOT NULL CONSTRAINT DF_FieldDayEvents_AtUtc DEFAULT (SYSUTCDATETIME()),
            Details     NVARCHAR(1000) NULL
        );
        CREATE INDEX IX_FieldDayEvents_Day ON dbo.FieldDayEvents (FieldDayId, Id);
        PRINT '=== dbo.FieldDayEvents created ===';
    END
    ELSE PRINT '=== dbo.FieldDayEvents already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 4. dbo.FieldWorkConsents
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.FieldWorkConsents', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldWorkConsents (
            UserId          INT          NOT NULL CONSTRAINT PK_FieldWorkConsents PRIMARY KEY
                                         CONSTRAINT FK_FieldWorkConsents_User REFERENCES dbo.IMSUsers(Id),
            ConsentVersion  INT          NOT NULL CONSTRAINT CK_FieldWorkConsents_Version CHECK (ConsentVersion >= 1),
            AcceptedAtUtc   DATETIME2(0) NOT NULL CONSTRAINT DF_FieldWorkConsents_AcceptedAtUtc DEFAULT (SYSUTCDATETIME())
        );
        PRINT '=== dbo.FieldWorkConsents created ===';
    END
    ELSE PRINT '=== dbo.FieldWorkConsents already exists -- no-op ===';
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION FieldWorkDays1103;
    THROW;
END CATCH;
GO

------------------------------------------------------------------
-- 5. TR_FieldDays_Guard (CREATE TRIGGER must be the first statement of its batch; still inside the transaction)
------------------------------------------------------------------
IF @@TRANCOUNT = 0
    THROW 54110, 'The transaction was lost before the trigger batch -- nothing changed.', 1;
IF OBJECT_ID('dbo.TR_FieldDays_Guard', 'TR') IS NULL
    EXEC (N'
CREATE TRIGGER dbo.TR_FieldDays_Guard ON dbo.FieldDays
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;

    -- 2026-11-03_FieldWorkDays.sql: the owner, date, vehicle and start reading of a day never change.
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE i.UserId <> d.UserId OR i.WorkDate <> d.WorkDate OR i.VehicleType <> d.VehicleType
                  OR i.StartedAtUtc <> d.StartedAtUtc OR i.StartKm <> d.StartKm
                  OR ISNULL(i.StartPhotoId, -1) <> ISNULL(d.StartPhotoId, -1)
                  OR ISNULL(i.StartLatitude, 999) <> ISNULL(d.StartLatitude, 999)
                  OR ISNULL(i.StartLongitude, 999) <> ISNULL(d.StartLongitude, 999)
                  OR i.StartSubmissionToken <> d.StartSubmissionToken)
        THROW 54151, ''The start reading of a field day cannot be changed.'', 1;

    -- A photo used by a day belongs to that day''s employee and was taken for that step.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.FieldPhotos p ON p.Id = i.StartPhotoId
               WHERE p.OwnerUserId <> i.UserId OR p.Purpose <> ''DayStart'')
       OR EXISTS (SELECT 1 FROM inserted i JOIN dbo.FieldPhotos p ON p.Id = i.EndPhotoId
               WHERE p.OwnerUserId <> i.UserId OR p.Purpose <> ''DayEnd'')
        THROW 54152, ''The photo does not belong to this field day.'', 1;
END');
GO
IF @@TRANCOUNT = 0
    THROW 54111, 'The transaction was lost after the trigger batch -- nothing changed.', 1;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
    DECLARE @Script NVARCHAR(200) = N'2026-11-03_FieldWorkDays.sql';
    DECLARE @Marker NVARCHAR(100) = N' [created by 2026-11-03_FieldWorkDays.sql]';

    ------------------------------------------------------------------
    -- 6. Permissions
    ------------------------------------------------------------------
    DECLARE @Perms TABLE (Code NVARCHAR(100) PRIMARY KEY, Description NVARCHAR(400), SortOrder INT);
    INSERT INTO @Perms VALUES
        (N'FieldWork.Day',    N'Field employee: must Start Day / End Day with meter photo and GPS; sees own field days only', 1),
        (N'FieldWork.Manage', N'Field manager: sees every field employee''s days, approves / flags / reopens them', 2);
    DECLARE @Code NVARCHAR(100), @PermDescription NVARCHAR(400);
    DECLARE perm_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Code, Description FROM @Perms ORDER BY SortOrder;
    OPEN perm_cursor;
    FETCH NEXT FROM perm_cursor INTO @Code, @PermDescription;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK) WHERE Code = @Code)
        BEGIN
            IF COLUMNPROPERTY(OBJECT_ID('dbo.Permissions'), 'Id', 'IsIdentity') = 1
                INSERT INTO dbo.Permissions (Code, Description) VALUES (@Code, @PermDescription + @Marker);
            ELSE
                INSERT INTO dbo.Permissions (Id, Code, Description)
                SELECT ISNULL(MAX(Id), 0) + 1, @Code, @PermDescription + @Marker FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK);
            PRINT CONCAT('=== Permission ', @Code, ' created ===');
        END
        ELSE PRINT CONCAT('=== Permission ', @Code, ' already exists -- used as it is ===');
        FETCH NEXT FROM perm_cursor INTO @Code, @PermDescription;
    END
    CLOSE perm_cursor;
    DEALLOCATE perm_cursor;

    ------------------------------------------------------------------
    -- 7. Roles
    ------------------------------------------------------------------
    DECLARE @NewRoles TABLE (Name NVARCHAR(100) PRIMARY KEY, Description NVARCHAR(400), PermissionCode NVARCHAR(100), SortOrder INT);
    INSERT INTO @NewRoles VALUES
        (N'Field Employee', N'Marketing / sales field staff: Start Day / End Day, own field days', N'FieldWork.Day', 1),
        (N'Field Manager',  N'Reviews every field employee''s days', N'FieldWork.Manage', 2);
    DECLARE @RoleName NVARCHAR(100), @RoleDescription NVARCHAR(400), @RoleCode NVARCHAR(100);
    DECLARE role_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Name, Description, PermissionCode FROM @NewRoles ORDER BY SortOrder;
    OPEN role_cursor;
    FETCH NEXT FROM role_cursor INTO @RoleName, @RoleDescription, @RoleCode;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF (SELECT COUNT(*) FROM dbo.Roles WITH (UPDLOCK, HOLDLOCK)
            WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = @RoleName OR RoleName = @RoleName) > 1
            THROW 54112, 'A Field Work role name matches more than one dbo.Roles row -- nothing changed.', 1;
        IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = @RoleName OR RoleName = @RoleName)
        BEGIN
            IF COLUMNPROPERTY(OBJECT_ID('dbo.Roles'), 'Id', 'IsIdentity') = 1
                INSERT INTO dbo.Roles (Name, RoleName, Description, IsSystemRole, CreatedDate)
                VALUES (@RoleName, @RoleName, @RoleDescription + @Marker, 0, SYSUTCDATETIME());
            ELSE
                INSERT INTO dbo.Roles (Id, Name, RoleName, Description, IsSystemRole, CreatedDate)
                SELECT ISNULL(MAX(Id), 0) + 1, @RoleName, @RoleName, @RoleDescription + @Marker, 0, SYSUTCDATETIME() FROM dbo.Roles WITH (UPDLOCK, HOLDLOCK);
            PRINT CONCAT('=== Role ', @RoleName, ' created ===');
        END
        ELSE PRINT CONCAT('=== Role ', @RoleName, ' already exists -- used as it is ===');

        DECLARE @Role INT = (SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = @RoleName OR RoleName = @RoleName);
        DECLARE @Perm INT = (SELECT Id FROM dbo.Permissions WHERE Code = @RoleCode);
        IF EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE RoleId = @Role AND PermissionId = @Perm)
            PRINT CONCAT('=== ', @RoleName, ': ', @RoleCode, ' already granted -- no-op ===');
        ELSE
        BEGIN
            INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
            VALUES (@Role, @RoleName, @RoleCode, N'Added', @Script,
                    N'Field Work module Phase 1 (requirement 2026-11-03) [PermissionId=' + CAST(@Perm AS NVARCHAR(20)) + N']');
            INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@Role, @Perm);
            PRINT CONCAT('=== ', @RoleCode, ' granted to ', @RoleName, ' ===');
        END
        FETCH NEXT FROM role_cursor INTO @RoleName, @RoleDescription, @RoleCode;
    END
    CLOSE role_cursor;
    DEALLOCATE role_cursor;

    -- Dashboard.View for both roles, as every other role has it (login lands on /Index, which needs it; the Field Work
    -- gate then sends a field employee to Start Day). Logged like the other grants.
    DECLARE @Dash INT = (SELECT Id FROM dbo.Permissions WHERE Code = N'Dashboard.View');
    IF @Dash IS NULL
        THROW 54113, 'Permission Dashboard.View is missing -- run PhaseA_RoleBasedAccess.sql first. Nothing changed.', 1;
    DECLARE dash_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @NewRoles ORDER BY SortOrder;
    OPEN dash_cursor;
    FETCH NEXT FROM dash_cursor INTO @RoleName;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @Role = (SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = @RoleName OR RoleName = @RoleName);
        IF EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE RoleId = @Role AND PermissionId = @Dash)
            PRINT CONCAT('=== ', @RoleName, ': Dashboard.View already granted -- no-op ===');
        ELSE
        BEGIN
            INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
            VALUES (@Role, @RoleName, N'Dashboard.View', N'Added', @Script,
                    N'Field Work module Phase 1: home page after login (requirement 2026-11-03) [PermissionId=' + CAST(@Dash AS NVARCHAR(20)) + N']');
            INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@Role, @Dash);
            PRINT CONCAT('=== Dashboard.View granted to ', @RoleName, ' ===');
        END
        FETCH NEXT FROM dash_cursor INTO @RoleName;
    END
    CLOSE dash_cursor;
    DEALLOCATE dash_cursor;

    PRINT '=== After: Field Work roles and their permissions ===';
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS Role, r.Id AS RoleId, p.Code, p.Id AS PermissionId
    FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE r.Name IN (N'Field Employee', N'Field Manager') OR p.Code LIKE N'FieldWork.%' ORDER BY Role, p.Code;

    PRINT '=== After: new objects (expect 4 tables, 1 trigger) ===';
    SELECT name, type_desc FROM sys.objects
    WHERE name IN (N'FieldPhotos', N'FieldDays', N'FieldDayEvents', N'FieldWorkConsents', N'TR_FieldDays_Guard') ORDER BY name;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION FieldWorkDays1103;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION FieldWorkDays1103;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
