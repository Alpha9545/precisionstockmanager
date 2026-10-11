-- ============================================================================
-- 2026-10-06_MaintenanceTickets.sql
--
-- WHY: internal Maintenance / Service tickets. A supervisor raises a ticket
-- (Electrical / Welding / Plumbing) for an Area (optionally a Polyhouse of that
-- Area); a worker of the matching trade accepts it, visits, works and resolves
-- it; a supervisor of that Area confirms the arrival and the completion.
--
-- STATUS WORKFLOW (enforced by the application, every step a conditional UPDATE
-- -- WHERE Status = <expected> -- so two people can never move the same ticket
-- twice; the CHECK constraints below make every status carry its data):
--   Open -> Accepted -> Arrived -> ArrivalConfirmed -> InProgress -> Resolved -> Closed
--   Resolved -> InProgress   (completion rejected, reason required)
--   Open / Accepted -> Cancelled   (requester or administrator, reason required)
--
-- WHAT THIS DOES:
--   dbo.Roles                      Electrician, Welder, Plumber (only if missing)
--   dbo.Permissions                Maintenance.Raise   raise tickets, confirm arrival / completion (own Areas)
--                                  Maintenance.View    see the tickets of own Areas, reports
--                                  Maintenance.Work    worker: accept / visit / work / resolve own-trade tickets
--                                  Maintenance.Manage  reassign + all-Area reports (full-access users have it
--                                                      implicitly; granted to no role here)
--   dbo.RolePermissions            Raise + View -> Sowing Supervisor, Mother Plant Supervisor,
--                                                  Fertilizer Supervisor, Office Coordinator
--                                  Work + Dashboard.View -> Electrician, Welder, Plumber
--   dbo.MaintenanceCategories      Electrical -> Electrician, Welding -> Welder, Plumbing -> Plumber
--                                  (master data: a category names the worker ROLE that may take it)
--   dbo.MaintenanceTickets         the ticket; TicketNo = 'MT-' + 6-digit Id (computed, persisted)
--   dbo.MaintenanceTicketEvents    every status change and workflow action (who, when, from/to, note)
--   dbo.MaintenanceTicketPhotos    photo METADATA only (files live outside wwwroot under a GUID name)
--   TR_MaintenanceTickets_Location           Polyhouse must belong to the ticket's Area; Area active
--   TR_MaintenanceTicketEvents_Protect       events can never be UPDATEd, nor DELETEd while the ticket exists
--                                            (events / photo records go only WITH their ticket: ON DELETE CASCADE;
--                                            the application never deletes a ticket)
--
-- NOT changed: any existing table, column, row, role, permission or grant other
-- than the grants listed above.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the
-- server named with -v TARGET_SERVER=<@@SERVERNAME> (the live database is ALSO
-- named PlantsIMS2_Test). One transaction. Idempotent (re-run = no-op).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
--       then MODE=COMMIT.   Undo: 2026-10-06_MaintenanceTickets_Rollback.sql
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51761, '2026-10-06_MaintenanceTickets.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51762, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51763, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION MaintenanceTickets2026_10_06;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    PRINT '=== Before: row counts of tables this script must NOT change (Roles / Permissions / RolePermissions only grow) ===';
    SELECT 'Area' AS TableName, COUNT(*) AS Rows FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'IMSUsers', COUNT(*) FROM dbo.IMSUsers
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'Roles', COUNT(*) FROM dbo.Roles
    UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions;

    DECLARE @SupervisorRoles TABLE (Name NVARCHAR(100) PRIMARY KEY);
    INSERT INTO @SupervisorRoles VALUES (N'Sowing Supervisor'), (N'Mother Plant Supervisor'), (N'Fertilizer Supervisor'), (N'Office Coordinator');
    IF (SELECT COUNT(*) FROM dbo.Roles r JOIN @SupervisorRoles s ON s.Name = COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName)) <> 4
        THROW 51764, 'One of the supervisor roles (Sowing / Mother Plant / Fertilizer Supervisor, Office Coordinator) is missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'Dashboard.View')
        THROW 51765, 'Permission Dashboard.View is missing -- run PhaseA_RoleBasedAccess.sql first.', 1;

    ------------------------------------------------------------------
    -- 1. Worker roles (Id identity or not -- the live database may differ)
    ------------------------------------------------------------------
    DECLARE @WorkerRoles TABLE (Name NVARCHAR(100) PRIMARY KEY, Description NVARCHAR(400), Category NVARCHAR(50), SortOrder INT);
    INSERT INTO @WorkerRoles VALUES
        (N'Electrician', N'Maintenance worker: accepts and completes Electrical tickets', N'Electrical', 1),
        (N'Welder',      N'Maintenance worker: accepts and completes Welding tickets',    N'Welding',    2),
        (N'Plumber',     N'Maintenance worker: accepts and completes Plumbing tickets',   N'Plumbing',   3);

    DECLARE @RoleName NVARCHAR(100), @RoleDescription NVARCHAR(400);
    DECLARE role_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Name, Description FROM @WorkerRoles ORDER BY SortOrder;
    OPEN role_cursor;
    FETCH NEXT FROM role_cursor INTO @RoleName, @RoleDescription;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = @RoleName OR RoleName = @RoleName)
        BEGIN
            IF COLUMNPROPERTY(OBJECT_ID('dbo.Roles'), 'Id', 'IsIdentity') = 1
                INSERT INTO dbo.Roles (Name, RoleName, Description, IsSystemRole, CreatedDate) VALUES (@RoleName, @RoleName, @RoleDescription, 0, SYSUTCDATETIME());
            ELSE
                INSERT INTO dbo.Roles (Id, Name, RoleName, Description, IsSystemRole, CreatedDate)
                SELECT ISNULL(MAX(Id), 0) + 1, @RoleName, @RoleName, @RoleDescription, 0, SYSUTCDATETIME() FROM dbo.Roles;
            PRINT CONCAT('=== Role ', @RoleName, ' added ===');
        END
        ELSE PRINT CONCAT('=== Role ', @RoleName, ' already exists -- no-op ===');
        FETCH NEXT FROM role_cursor INTO @RoleName, @RoleDescription;
    END
    CLOSE role_cursor;
    DEALLOCATE role_cursor;

    ------------------------------------------------------------------
    -- 2. Permissions
    ------------------------------------------------------------------
    DECLARE @Perms TABLE (Code NVARCHAR(200) PRIMARY KEY, Description NVARCHAR(400), SortOrder INT);
    INSERT INTO @Perms VALUES
        (N'Maintenance.Raise',  N'Raise maintenance tickets; confirm worker arrival and work completion for own Areas', 1),
        (N'Maintenance.View',   N'View maintenance tickets of own Areas and maintenance reports', 2),
        (N'Maintenance.Work',   N'Maintenance worker: accept, visit, work on and resolve tickets of own trade', 3),
        (N'Maintenance.Manage', N'Reassign maintenance tickets; all-Area maintenance reports', 4);
    DECLARE @Code NVARCHAR(200), @PermDescription NVARCHAR(400);
    DECLARE perm_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Code, Description FROM @Perms ORDER BY SortOrder;
    OPEN perm_cursor;
    FETCH NEXT FROM perm_cursor INTO @Code, @PermDescription;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = @Code)
        BEGIN
            IF COLUMNPROPERTY(OBJECT_ID('dbo.Permissions'), 'Id', 'IsIdentity') = 1
                INSERT INTO dbo.Permissions (Code, Description) VALUES (@Code, @PermDescription);
            ELSE
                INSERT INTO dbo.Permissions (Id, Code, Description) SELECT ISNULL(MAX(Id), 0) + 1, @Code, @PermDescription FROM dbo.Permissions;
            PRINT CONCAT('=== Permission ', @Code, ' added ===');
        END
        ELSE PRINT CONCAT('=== Permission ', @Code, ' already exists -- no-op ===');
        FETCH NEXT FROM perm_cursor INTO @Code, @PermDescription;
    END
    CLOSE perm_cursor;
    DEALLOCATE perm_cursor;

    ------------------------------------------------------------------
    -- 3. Grants (only missing rows)
    ------------------------------------------------------------------
    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM dbo.Roles r
    JOIN @SupervisorRoles s ON s.Name = COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName)
    CROSS JOIN dbo.Permissions p
    WHERE p.Code IN (N'Maintenance.Raise', N'Maintenance.View')
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    PRINT CONCAT('Supervisor grants inserted: ', @@ROWCOUNT, ' (expect 8 on first run, 0 on re-run)');

    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM dbo.Roles r
    JOIN @WorkerRoles w ON w.Name = COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName)
    CROSS JOIN dbo.Permissions p
    WHERE p.Code IN (N'Maintenance.Work', N'Dashboard.View')
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    PRINT CONCAT('Worker grants inserted: ', @@ROWCOUNT, ' (expect 6 on first run, 0 on re-run)');

    ------------------------------------------------------------------
    -- 4. Categories (master data)
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.MaintenanceCategories', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.MaintenanceCategories (
            Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaintenanceCategories PRIMARY KEY,
            Name          NVARCHAR(50)  NOT NULL CONSTRAINT UQ_MaintenanceCategories_Name UNIQUE,
            WorkerRoleId  INT           NOT NULL CONSTRAINT FK_MaintenanceCategories_WorkerRole REFERENCES dbo.Roles(Id),
            IsActive      BIT           NOT NULL CONSTRAINT DF_MaintenanceCategories_IsActive DEFAULT (1),
            SortOrder     INT           NOT NULL CONSTRAINT DF_MaintenanceCategories_SortOrder DEFAULT (0),
            CreatedDate   DATETIME2     NOT NULL CONSTRAINT DF_MaintenanceCategories_CreatedDate DEFAULT (SYSUTCDATETIME())
        );
        PRINT '=== dbo.MaintenanceCategories created ===';
    END
    INSERT INTO dbo.MaintenanceCategories (Name, WorkerRoleId, SortOrder)
    SELECT w.Category, r.Id, w.SortOrder
    FROM @WorkerRoles w
    JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = w.Name
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MaintenanceCategories c WHERE c.Name = w.Category);
    PRINT CONCAT('Categories inserted: ', @@ROWCOUNT, ' (expect 3 on first run, 0 on re-run)');

    ------------------------------------------------------------------
    -- 5. Tickets
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.MaintenanceTickets', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.MaintenanceTickets (
            Id                      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaintenanceTickets PRIMARY KEY,
            TicketNo                AS (CAST('MT-' + RIGHT(REPLICATE('0', 6) + CAST(Id AS VARCHAR(10)),
                                            CASE WHEN Id > 999999 THEN LEN(CAST(Id AS VARCHAR(10))) ELSE 6 END) AS VARCHAR(16))) PERSISTED,
            CategoryId              INT            NOT NULL CONSTRAINT FK_MaintenanceTickets_Category REFERENCES dbo.MaintenanceCategories(Id),
            AreaId                  INT            NOT NULL CONSTRAINT FK_MaintenanceTickets_Area REFERENCES dbo.Area(Id),
            PolyhouseId             INT            NULL     CONSTRAINT FK_MaintenanceTickets_Polyhouse REFERENCES dbo.Polyhouses(Id),
            Title                   NVARCHAR(150)  NOT NULL,
            Description             NVARCHAR(2000) NOT NULL,
            Priority                VARCHAR(10)    NOT NULL,
            Status                  VARCHAR(20)    NOT NULL CONSTRAINT DF_MaintenanceTickets_Status DEFAULT ('Open'),
            RequestedById           INT            NOT NULL CONSTRAINT FK_MaintenanceTickets_RequestedBy REFERENCES dbo.IMSUsers(Id),
            RequestedAt             DATETIME2      NOT NULL CONSTRAINT DF_MaintenanceTickets_RequestedAt DEFAULT (SYSUTCDATETIME()),
            AssignedWorkerId        INT            NULL     CONSTRAINT FK_MaintenanceTickets_AssignedWorker REFERENCES dbo.IMSUsers(Id),
            AcceptedAt              DATETIME2      NULL,
            ExpectedVisitDate       DATE           NULL,
            ExpectedVisitRemarks    NVARCHAR(500)  NULL,
            ArrivedAt               DATETIME2      NULL,
            ArrivalConfirmedById    INT            NULL     CONSTRAINT FK_MaintenanceTickets_ArrivalConfirmedBy REFERENCES dbo.IMSUsers(Id),
            ArrivalConfirmedAt      DATETIME2      NULL,
            WorkStartedAt           DATETIME2      NULL,
            ProblemFound            NVARCHAR(1000) NULL,
            WorkPerformed           NVARCHAR(1000) NULL,
            WorkEndedAt             DATETIME2      NULL,
            WorkDone                NVARCHAR(1000) NULL,
            Resolution              NVARCHAR(1000) NULL,
            MaterialsUsed           NVARCHAR(1000) NULL,
            WorkerRemarks           NVARCHAR(500)  NULL,
            CompletionConfirmedById INT            NULL     CONSTRAINT FK_MaintenanceTickets_CompletionConfirmedBy REFERENCES dbo.IMSUsers(Id),
            CompletionConfirmedAt   DATETIME2      NULL,
            ClosedAt                DATETIME2      NULL,
            CancelledById           INT            NULL     CONSTRAINT FK_MaintenanceTickets_CancelledBy REFERENCES dbo.IMSUsers(Id),
            CancelledAt             DATETIME2      NULL,
            CancelReason            NVARCHAR(500)  NULL,
            UpdatedAt               DATETIME2      NULL,
            UpdatedById             INT            NULL     CONSTRAINT FK_MaintenanceTickets_UpdatedBy REFERENCES dbo.IMSUsers(Id),
            CONSTRAINT CK_MaintenanceTickets_Priority CHECK (Priority IN ('Normal', 'Urgent', 'Critical')),
            CONSTRAINT CK_MaintenanceTickets_Status   CHECK (Status IN ('Open', 'Accepted', 'Arrived', 'ArrivalConfirmed', 'InProgress', 'Resolved', 'Closed', 'Cancelled')),
            CONSTRAINT CK_MaintenanceTickets_Text     CHECK (LEN(LTRIM(RTRIM(Title))) > 0 AND LEN(LTRIM(RTRIM(Description))) > 0),
            -- Every status carries its data (a worker can never skip a step, and a skipped step can never look done).
            CONSTRAINT CK_MaintenanceTickets_Accepted CHECK (Status IN ('Open', 'Cancelled')
                OR (AssignedWorkerId IS NOT NULL AND AcceptedAt IS NOT NULL AND ExpectedVisitDate IS NOT NULL)),
            CONSTRAINT CK_MaintenanceTickets_Open     CHECK (Status <> 'Open' OR (AssignedWorkerId IS NULL AND ArrivedAt IS NULL)),
            CONSTRAINT CK_MaintenanceTickets_Arrived  CHECK (Status NOT IN ('Arrived', 'ArrivalConfirmed', 'InProgress', 'Resolved', 'Closed')
                OR (ArrivedAt IS NOT NULL AND ArrivedAt >= AcceptedAt)),
            CONSTRAINT CK_MaintenanceTickets_ArrivalConfirmed CHECK (Status NOT IN ('ArrivalConfirmed', 'InProgress', 'Resolved', 'Closed')
                OR (ArrivalConfirmedById IS NOT NULL AND ArrivalConfirmedAt IS NOT NULL AND ArrivalConfirmedAt >= ArrivedAt AND ArrivalConfirmedById <> AssignedWorkerId)),
            CONSTRAINT CK_MaintenanceTickets_InProgress CHECK (Status NOT IN ('InProgress', 'Resolved', 'Closed')
                OR (WorkStartedAt IS NOT NULL AND WorkStartedAt >= ArrivalConfirmedAt AND ProblemFound IS NOT NULL AND WorkPerformed IS NOT NULL)),
            CONSTRAINT CK_MaintenanceTickets_WorkEnded CHECK ((Status IN ('Resolved', 'Closed') AND WorkEndedAt IS NOT NULL AND WorkEndedAt >= WorkStartedAt AND Resolution IS NOT NULL)
                OR (Status NOT IN ('Resolved', 'Closed') AND WorkEndedAt IS NULL)),
            CONSTRAINT CK_MaintenanceTickets_Closed CHECK ((Status = 'Closed' AND CompletionConfirmedById IS NOT NULL AND CompletionConfirmedAt >= WorkEndedAt
                    AND ClosedAt IS NOT NULL AND CompletionConfirmedById <> AssignedWorkerId)
                OR (Status <> 'Closed' AND ClosedAt IS NULL AND CompletionConfirmedById IS NULL)),
            CONSTRAINT CK_MaintenanceTickets_Cancelled CHECK ((Status = 'Cancelled' AND CancelledById IS NOT NULL AND CancelledAt IS NOT NULL AND LEN(LTRIM(RTRIM(CancelReason))) > 0 AND ArrivedAt IS NULL)
                OR (Status <> 'Cancelled' AND CancelledAt IS NULL))
        );
        CREATE UNIQUE INDEX UX_MaintenanceTickets_TicketNo ON dbo.MaintenanceTickets (TicketNo);
        CREATE INDEX IX_MaintenanceTickets_Status ON dbo.MaintenanceTickets (Status, CategoryId) INCLUDE (AreaId, AssignedWorkerId, Priority);
        CREATE INDEX IX_MaintenanceTickets_Area ON dbo.MaintenanceTickets (AreaId, RequestedAt);
        CREATE INDEX IX_MaintenanceTickets_Worker ON dbo.MaintenanceTickets (AssignedWorkerId, Status);
        PRINT '=== dbo.MaintenanceTickets created ===';
    END
    ELSE PRINT '=== dbo.MaintenanceTickets already exists -- no-op ===';

    IF OBJECT_ID('dbo.MaintenanceTicketEvents', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.MaintenanceTicketEvents (
            Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaintenanceTicketEvents PRIMARY KEY,
            TicketId    INT            NOT NULL CONSTRAINT FK_MaintenanceTicketEvents_Ticket REFERENCES dbo.MaintenanceTickets(Id) ON DELETE CASCADE,
            EventType   VARCHAR(30)    NOT NULL,
            FromStatus  VARCHAR(20)    NULL,
            ToStatus    VARCHAR(20)    NULL,
            ActorId     INT            NULL CONSTRAINT FK_MaintenanceTicketEvents_Actor REFERENCES dbo.IMSUsers(Id),
            EventAt     DATETIME2      NOT NULL CONSTRAINT DF_MaintenanceTicketEvents_EventAt DEFAULT (SYSUTCDATETIME()),
            Note        NVARCHAR(1000) NULL
        );
        CREATE INDEX IX_MaintenanceTicketEvents_Ticket ON dbo.MaintenanceTicketEvents (TicketId, Id);
        PRINT '=== dbo.MaintenanceTicketEvents created ===';
    END
    ELSE PRINT '=== dbo.MaintenanceTicketEvents already exists -- no-op ===';

    IF OBJECT_ID('dbo.MaintenanceTicketPhotos', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.MaintenanceTicketPhotos (
            Id               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MaintenanceTicketPhotos PRIMARY KEY,
            TicketId         INT           NOT NULL CONSTRAINT FK_MaintenanceTicketPhotos_Ticket REFERENCES dbo.MaintenanceTickets(Id) ON DELETE CASCADE,
            Stage            VARCHAR(12)   NOT NULL,
            StoredFileName   VARCHAR(40)   NOT NULL CONSTRAINT UQ_MaintenanceTicketPhotos_StoredFileName UNIQUE,
            OriginalFileName NVARCHAR(255) NOT NULL,
            ContentType      VARCHAR(20)   NOT NULL,
            SizeBytes        INT           NOT NULL,
            UploadedById     INT           NULL CONSTRAINT FK_MaintenanceTicketPhotos_UploadedBy REFERENCES dbo.IMSUsers(Id),
            UploadedAt       DATETIME2     NOT NULL CONSTRAINT DF_MaintenanceTicketPhotos_UploadedAt DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT CK_MaintenanceTicketPhotos_Stage CHECK (Stage IN ('Problem', 'Completion')),
            CONSTRAINT CK_MaintenanceTicketPhotos_ContentType CHECK (ContentType IN ('image/jpeg', 'image/png', 'image/webp')),
            CONSTRAINT CK_MaintenanceTicketPhotos_Size CHECK (SizeBytes BETWEEN 1 AND 5242880),
            -- 32 hex characters (a GUID) + .jpg / .png / .webp: no path, no user-chosen name.
            CONSTRAINT CK_MaintenanceTicketPhotos_StoredFileName CHECK (StoredFileName NOT LIKE '%[^0-9a-f.jpgnweb]%'
                AND (   (LEN(StoredFileName) = 36 AND (StoredFileName LIKE '%.jpg' OR StoredFileName LIKE '%.png'))
                     OR (LEN(StoredFileName) = 37 AND StoredFileName LIKE '%.webp'))
                AND CHARINDEX('.', StoredFileName) = LEN(StoredFileName) - CASE WHEN StoredFileName LIKE '%.webp' THEN 4 ELSE 3 END)
        );
        CREATE INDEX IX_MaintenanceTicketPhotos_Ticket ON dbo.MaintenanceTicketPhotos (TicketId, Stage);
        PRINT '=== dbo.MaintenanceTicketPhotos created ===';
    END
    ELSE PRINT '=== dbo.MaintenanceTicketPhotos already exists -- no-op ===';

    IF OBJECT_ID('dbo.TR_MaintenanceTickets_Location', 'TR') IS NULL
    BEGIN
        EXEC (N'
CREATE TRIGGER dbo.TR_MaintenanceTickets_Location ON dbo.MaintenanceTickets
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT (UPDATE(AreaId) OR UPDATE(PolyhouseId))
        RETURN;
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.Polyhouses p ON p.Id = i.PolyhouseId WHERE p.AreaId <> i.AreaId)
        THROW 51713, ''This Polyhouse does not belong to the selected Area.'', 1;
    IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id = i.Id JOIN dbo.Area a ON a.Id = i.AreaId
               WHERE d.Id IS NULL AND a.IsActive = 0)
        THROW 51766, ''The selected Area is not active.'', 1;
END');
        PRINT '=== TR_MaintenanceTickets_Location created ===';
    END

    IF OBJECT_ID('dbo.TR_MaintenanceTicketEvents_Protect', 'TR') IS NULL
    BEGIN
        EXEC (N'
CREATE TRIGGER dbo.TR_MaintenanceTicketEvents_Protect ON dbo.MaintenanceTicketEvents
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted)
        THROW 51767, ''Maintenance ticket history cannot be changed.'', 1;
    IF EXISTS (SELECT 1 FROM deleted d WHERE EXISTS (SELECT 1 FROM dbo.MaintenanceTickets t WHERE t.Id = d.TicketId))
        THROW 51768, ''Maintenance ticket history cannot be deleted while its ticket exists.'', 1;
END');
        PRINT '=== TR_MaintenanceTicketEvents_Protect created ===';
    END

    PRINT '=== After: roles / permissions / grants ===';
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, p.Code
    FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code LIKE N'Maintenance.%' OR COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) IN (N'Electrician', N'Welder', N'Plumber')
    ORDER BY RoleName, p.Code;
    SELECT c.Id, c.Name, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS WorkerRole FROM dbo.MaintenanceCategories c JOIN dbo.Roles r ON r.Id = c.WorkerRoleId ORDER BY c.SortOrder;
    SELECT name AS CheckConstraint FROM sys.check_constraints WHERE parent_object_id IN (OBJECT_ID('dbo.MaintenanceTickets'), OBJECT_ID('dbo.MaintenanceTicketPhotos')) ORDER BY name;
    SELECT OBJECT_NAME(object_id) AS TableName, name AS IndexName FROM sys.indexes WHERE object_id IN (OBJECT_ID('dbo.MaintenanceTickets'), OBJECT_ID('dbo.MaintenanceTicketEvents'), OBJECT_ID('dbo.MaintenanceTicketPhotos'), OBJECT_ID('dbo.MaintenanceCategories')) AND name IS NOT NULL ORDER BY 1, 2;
    SELECT name AS TriggerName FROM sys.triggers WHERE parent_id IN (OBJECT_ID('dbo.MaintenanceTickets'), OBJECT_ID('dbo.MaintenanceTicketEvents')) ORDER BY name;

    PRINT '=== After: row counts (Area / Polyhouses / IMSUsers / UserRoles must equal Before) ===';
    SELECT 'Area' AS TableName, COUNT(*) AS Rows FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'IMSUsers', COUNT(*) FROM dbo.IMSUsers
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'Roles', COUNT(*) FROM dbo.Roles
    UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'MaintenanceCategories', COUNT(*) FROM dbo.MaintenanceCategories
    UNION ALL SELECT 'MaintenanceTickets', COUNT(*) FROM dbo.MaintenanceTickets;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MaintenanceTickets2026_10_06;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MaintenanceTickets2026_10_06;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MaintenanceTickets2026_10_06;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
