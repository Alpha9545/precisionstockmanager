-- ============================================================================
-- 2026-10-07_SuggestionsProblems.sql
--
-- WHY: internal "Suggestions & Problems" board. An employee posts a Problem or a
-- Suggestion for an Area (and a Polyhouse of that Area), optionally with one
-- photo; colleagues who may see that Area discuss it with comments and replies.
-- No likes / reactions of any kind.
--
-- RULES (the application enforces them first; this schema repeats them):
--   * Every post belongs to an active Area; a Polyhouse must belong to that Area
--     and is REQUIRED for the MainOffice Area (the DailyLabourRules.RequiresPolyhouse
--     convention).
--   * Lifetime: ExpiresAt = CreatedAt + 48 hours, a PERSISTED computed column --
--     never a value from the browser. A post is expired when IsPermanent = 0 AND
--     ExpiresAt <= SYSUTCDATETIME(); no background job marks anything.
--   * Permanent: only an administrator (Suggestion.Manage) sets IsPermanent = 1;
--     it never goes back, and CreatedAt is never changed.
--   * Business Status (Open / InDiscussion / InProgress / Resolved / Closed) is
--     separate from lifetime and from Permanent.
--   * Delete = SOFT delete with a reason (never undone here); comments too.
--   * Replies: one level -- a reply's parent is a top-level comment of the SAME
--     post (composite foreign key + trigger).
--
-- WHAT THIS DOES:
--   dbo.Permissions          Suggestion.View / Suggestion.Create / Suggestion.Comment / Suggestion.Manage
--   dbo.RolePermissions      View + Create + Comment -> every Area-based role that exists:
--                              Office Coordinator, Sowing Operator, Booking Executive, Dispatch Executive,
--                              Fertilizer Supervisor, Mother Plant Supervisor, Sowing Supervisor,
--                              Main Office Store Keeper, Outlet Sales, Pot Production Operator, Purchase Officer
--                            (NOT the System Administrator -- full access already -- and NOT the
--                             Electrician / Welder / Plumber roles, which have no Area.)
--                            Suggestion.Manage -> no role (full-access users have it implicitly).
--   dbo.SuggestionPostTypes  Problem, Suggestion (more types = more rows)
--   dbo.SuggestionPosts      the post; PostNo = 'SP-' + 6-digit Id (computed, persisted)
--   dbo.SuggestionPostPhotos photo METADATA only (files live outside wwwroot under a GUID name)
--   dbo.SuggestionComments   comments and replies (ParentCommentId)
--   dbo.SuggestionPostEvents audit: created, status changes, made permanent, deleted,
--                            comment edited (old text) / deleted
--   TR_SuggestionPosts_Rules        location rules; immutable columns; no un-delete / un-permanent
--   TR_SuggestionComments_Rules     reply parent is a top-level comment; immutable columns
--   TR_SuggestionPostEvents_Protect events never UPDATEd, nor DELETEd while the post exists
--                                   (events / photos / comments go only WITH their post: ON DELETE
--                                   CASCADE; the application never deletes a post row)
--
-- NOT changed: any existing table, column, row, role, permission or grant other
-- than the grants listed above.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the
-- server named with -v TARGET_SERVER=<@@SERVERNAME> (the live database is ALSO
-- named PlantsIMS2_Test). One transaction. Idempotent (re-run = no-op; triggers
-- are CREATE OR ALTER).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
--       then MODE=COMMIT.   Undo: 2026-10-07_SuggestionsProblems_Rollback.sql
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51831, '2026-10-07_SuggestionsProblems.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51832, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51833, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION SuggestionsProblems2026_10_07;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    PRINT '=== Before: row counts of tables this script must NOT change (Permissions / RolePermissions only grow) ===';
    SELECT 'Area' AS TableName, COUNT(*) AS Rows FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'IMSUsers', COUNT(*) FROM dbo.IMSUsers
    UNION ALL SELECT 'Roles', COUNT(*) FROM dbo.Roles
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'MaintenanceTickets', COUNT(*) FROM dbo.MaintenanceTickets
    UNION ALL SELECT 'MaintenanceTicketPhotos', COUNT(*) FROM dbo.MaintenanceTicketPhotos;

    ------------------------------------------------------------------
    -- 1. Permissions (Id identity or not -- the live database may differ) and grants
    ------------------------------------------------------------------
    DECLARE @Perms TABLE (Code NVARCHAR(200) PRIMARY KEY, Description NVARCHAR(400), SortOrder INT);
    INSERT INTO @Perms VALUES
        (N'Suggestion.View',    N'See Suggestions & Problems posts of own Areas', 1),
        (N'Suggestion.Create',  N'Post a Suggestion / Problem for own Areas', 2),
        (N'Suggestion.Comment', N'Comment on and reply to Suggestions & Problems posts of own Areas', 3),
        (N'Suggestion.Manage',  N'Administrator: all posts (expired / deleted too), make permanent, status, delete, moderate comments', 4);
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

    DECLARE @EmployeeRoles TABLE (Name NVARCHAR(100) PRIMARY KEY);
    INSERT INTO @EmployeeRoles VALUES
        (N'Office Coordinator'), (N'Sowing Operator'), (N'Booking Executive'), (N'Dispatch Executive'),
        (N'Fertilizer Supervisor'), (N'Mother Plant Supervisor'), (N'Sowing Supervisor'),
        (N'Main Office Store Keeper'), (N'Outlet Sales'), (N'Pot Production Operator'), (N'Purchase Officer');
    DECLARE @RolesFound INT = (SELECT COUNT(*) FROM dbo.Roles r JOIN @EmployeeRoles e ON e.Name = COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName));
    PRINT CONCAT('Employee roles found: ', @RolesFound, ' of 11 (a missing role simply gets no grant)');

    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM dbo.Roles r
    JOIN @EmployeeRoles e ON e.Name = COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName)
    CROSS JOIN dbo.Permissions p
    WHERE p.Code IN (N'Suggestion.View', N'Suggestion.Create', N'Suggestion.Comment')
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    PRINT CONCAT('Employee grants inserted: ', @@ROWCOUNT, ' (expect 3 per role found on first run, 0 on re-run)');

    ------------------------------------------------------------------
    -- 2. Post types (master data)
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.SuggestionPostTypes', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.SuggestionPostTypes (
            Code       NVARCHAR(30)  NOT NULL CONSTRAINT PK_SuggestionPostTypes PRIMARY KEY,
            Name       NVARCHAR(60)  NOT NULL CONSTRAINT UQ_SuggestionPostTypes_Name UNIQUE,
            SortOrder  INT           NOT NULL CONSTRAINT DF_SuggestionPostTypes_SortOrder DEFAULT (0),
            IsActive   BIT           NOT NULL CONSTRAINT DF_SuggestionPostTypes_IsActive DEFAULT (1),
            CONSTRAINT CK_SuggestionPostTypes_Code CHECK (Code NOT LIKE N'%[^A-Za-z]%' AND LEN(Code) > 0)
        );
        PRINT '=== SuggestionPostTypes created ===';
    END
    INSERT INTO dbo.SuggestionPostTypes (Code, Name, SortOrder)
    SELECT v.Code, v.Name, v.SortOrder FROM (VALUES (N'Problem', N'Problem', 1), (N'Suggestion', N'Suggestion', 2)) v(Code, Name, SortOrder)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.SuggestionPostTypes t WHERE t.Code = v.Code);
    PRINT CONCAT('Post types inserted: ', @@ROWCOUNT);

    ------------------------------------------------------------------
    -- 3. Posts
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.SuggestionPosts', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.SuggestionPosts (
            Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SuggestionPosts PRIMARY KEY,
            PostNo              AS (CONVERT(NVARCHAR(12), N'SP-' + RIGHT(N'000000' + CONVERT(NVARCHAR(10), Id), 6))) PERSISTED,
            TypeCode            NVARCHAR(30)   NOT NULL CONSTRAINT FK_SuggestionPosts_Type REFERENCES dbo.SuggestionPostTypes (Code),
            Title               NVARCHAR(150)  NOT NULL,
            Description         NVARCHAR(2000) NOT NULL,
            AreaId              INT            NOT NULL CONSTRAINT FK_SuggestionPosts_Area REFERENCES dbo.Area (Id),
            PolyhouseId         INT            NULL     CONSTRAINT FK_SuggestionPosts_Polyhouse REFERENCES dbo.Polyhouses (Id),
            PostedById          INT            NOT NULL CONSTRAINT FK_SuggestionPosts_PostedBy REFERENCES dbo.IMSUsers (Id),
            CreatedAt           DATETIME2(3)   NOT NULL CONSTRAINT DF_SuggestionPosts_CreatedAt DEFAULT (SYSUTCDATETIME()),
            -- Lifetime is decided HERE (48 hours), never by the browser or the page.
            ExpiresAt           AS (DATEADD(HOUR, 48, CreatedAt)) PERSISTED,
            Status              NVARCHAR(20)   NOT NULL CONSTRAINT DF_SuggestionPosts_Status DEFAULT (N'Open'),
            StatusChangedById   INT            NULL     CONSTRAINT FK_SuggestionPosts_StatusChangedBy REFERENCES dbo.IMSUsers (Id),
            StatusChangedAt     DATETIME2(3)   NULL,
            IsPermanent         BIT            NOT NULL CONSTRAINT DF_SuggestionPosts_IsPermanent DEFAULT (0),
            MadePermanentById   INT            NULL     CONSTRAINT FK_SuggestionPosts_MadePermanentBy REFERENCES dbo.IMSUsers (Id),
            MadePermanentAt     DATETIME2(3)   NULL,
            IsDeleted           BIT            NOT NULL CONSTRAINT DF_SuggestionPosts_IsDeleted DEFAULT (0),
            DeletedById         INT            NULL     CONSTRAINT FK_SuggestionPosts_DeletedBy REFERENCES dbo.IMSUsers (Id),
            DeletedAt           DATETIME2(3)   NULL,
            DeleteReason        NVARCHAR(500)  NULL,
            CONSTRAINT CK_SuggestionPosts_Title CHECK (LEN(LTRIM(RTRIM(Title))) > 0),
            CONSTRAINT CK_SuggestionPosts_Description CHECK (LEN(LTRIM(RTRIM(Description))) > 0),
            CONSTRAINT CK_SuggestionPosts_Status CHECK (Status IN (N'Open', N'InDiscussion', N'InProgress', N'Resolved', N'Closed')),
            CONSTRAINT CK_SuggestionPosts_StatusChange CHECK ((StatusChangedById IS NULL AND StatusChangedAt IS NULL) OR (StatusChangedById IS NOT NULL AND StatusChangedAt IS NOT NULL)),
            CONSTRAINT CK_SuggestionPosts_Permanent CHECK (
                (IsPermanent = 0 AND MadePermanentById IS NULL AND MadePermanentAt IS NULL)
             OR (IsPermanent = 1 AND MadePermanentById IS NOT NULL AND MadePermanentAt IS NOT NULL)),
            CONSTRAINT CK_SuggestionPosts_Deleted CHECK (
                (IsDeleted = 0 AND DeletedById IS NULL AND DeletedAt IS NULL AND DeleteReason IS NULL)
             OR (IsDeleted = 1 AND DeletedById IS NOT NULL AND DeletedAt IS NOT NULL AND LEN(LTRIM(RTRIM(ISNULL(DeleteReason, N'')))) > 0))
        );
        PRINT '=== SuggestionPosts created ===';
    END

    -- Feed of a user's Areas, newest first (the WHERE also reads IsDeleted / IsPermanent / ExpiresAt).
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SuggestionPosts_Area_Created' AND object_id = OBJECT_ID('dbo.SuggestionPosts'))
        CREATE INDEX IX_SuggestionPosts_Area_Created ON dbo.SuggestionPosts (AreaId, CreatedAt DESC)
            INCLUDE (IsDeleted, IsPermanent, ExpiresAt, TypeCode, Status, PolyhouseId, PostedById);
    -- Administrator feed over every Area, newest first.
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SuggestionPosts_Created' AND object_id = OBJECT_ID('dbo.SuggestionPosts'))
        CREATE INDEX IX_SuggestionPosts_Created ON dbo.SuggestionPosts (CreatedAt DESC)
            INCLUDE (IsDeleted, IsPermanent, ExpiresAt, TypeCode, Status, AreaId, PolyhouseId, PostedById);
    -- Polyhouse filter and the Polyhouse foreign key.
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SuggestionPosts_Polyhouse' AND object_id = OBJECT_ID('dbo.SuggestionPosts'))
        CREATE INDEX IX_SuggestionPosts_Polyhouse ON dbo.SuggestionPosts (PolyhouseId) WHERE PolyhouseId IS NOT NULL;
    -- "Posted by" filter.
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SuggestionPosts_PostedBy' AND object_id = OBJECT_ID('dbo.SuggestionPosts'))
        CREATE INDEX IX_SuggestionPosts_PostedBy ON dbo.SuggestionPosts (PostedById, CreatedAt DESC);

    ------------------------------------------------------------------
    -- 4. Photo metadata (one photo per post for now: UQ on PostId)
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.SuggestionPostPhotos', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.SuggestionPostPhotos (
            Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SuggestionPostPhotos PRIMARY KEY,
            PostId            INT           NOT NULL CONSTRAINT FK_SuggestionPostPhotos_Post REFERENCES dbo.SuggestionPosts (Id) ON DELETE CASCADE,
            StoredFileName    NVARCHAR(64)  NOT NULL,
            OriginalFileName  NVARCHAR(255) NOT NULL,
            ContentType       NVARCHAR(50)  NOT NULL,
            SizeBytes         INT           NOT NULL,
            UploadedById      INT           NOT NULL CONSTRAINT FK_SuggestionPostPhotos_UploadedBy REFERENCES dbo.IMSUsers (Id),
            UploadedAt        DATETIME2(3)  NOT NULL CONSTRAINT DF_SuggestionPostPhotos_UploadedAt DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT UQ_SuggestionPostPhotos_Post UNIQUE (PostId),
            CONSTRAINT UQ_SuggestionPostPhotos_StoredFileName UNIQUE (StoredFileName),
            CONSTRAINT CK_SuggestionPostPhotos_ContentType CHECK (ContentType IN (N'image/jpeg', N'image/png', N'image/webp')),
            CONSTRAINT CK_SuggestionPostPhotos_Size CHECK (SizeBytes > 0 AND SizeBytes <= 5242880),
            CONSTRAINT CK_SuggestionPostPhotos_StoredFileName CHECK (StoredFileName NOT LIKE N'%[\/:]%' AND StoredFileName NOT LIKE N'%..%')
        );
        PRINT '=== SuggestionPostPhotos created ===';
    END

    ------------------------------------------------------------------
    -- 5. Comments and replies
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.SuggestionComments', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.SuggestionComments (
            Id               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SuggestionComments PRIMARY KEY,
            PostId           INT            NOT NULL CONSTRAINT FK_SuggestionComments_Post REFERENCES dbo.SuggestionPosts (Id) ON DELETE CASCADE,
            ParentCommentId  INT            NULL,
            AuthorId         INT            NOT NULL CONSTRAINT FK_SuggestionComments_Author REFERENCES dbo.IMSUsers (Id),
            CommentText      NVARCHAR(1000) NOT NULL,
            CreatedAt        DATETIME2(3)   NOT NULL CONSTRAINT DF_SuggestionComments_CreatedAt DEFAULT (SYSUTCDATETIME()),
            UpdatedAt        DATETIME2(3)   NULL,
            IsDeleted        BIT            NOT NULL CONSTRAINT DF_SuggestionComments_IsDeleted DEFAULT (0),
            DeletedById      INT            NULL     CONSTRAINT FK_SuggestionComments_DeletedBy REFERENCES dbo.IMSUsers (Id),
            DeletedAt        DATETIME2(3)   NULL,
            -- (PostId, Id) is the target of the reply foreign key below.
            CONSTRAINT UQ_SuggestionComments_Post_Id UNIQUE (PostId, Id),
            -- A reply's parent is a comment of the SAME post (checked only when ParentCommentId is set).
            CONSTRAINT FK_SuggestionComments_Parent FOREIGN KEY (PostId, ParentCommentId) REFERENCES dbo.SuggestionComments (PostId, Id),
            CONSTRAINT CK_SuggestionComments_Text CHECK (LEN(LTRIM(RTRIM(CommentText))) > 0),
            CONSTRAINT CK_SuggestionComments_NotOwnParent CHECK (ParentCommentId IS NULL OR ParentCommentId <> Id),
            CONSTRAINT CK_SuggestionComments_Deleted CHECK (
                (IsDeleted = 0 AND DeletedById IS NULL AND DeletedAt IS NULL)
             OR (IsDeleted = 1 AND DeletedById IS NOT NULL AND DeletedAt IS NOT NULL))
        );
        PRINT '=== SuggestionComments created ===';
    END
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SuggestionComments_Post' AND object_id = OBJECT_ID('dbo.SuggestionComments'))
        CREATE INDEX IX_SuggestionComments_Post ON dbo.SuggestionComments (PostId, CreatedAt) INCLUDE (ParentCommentId, IsDeleted, AuthorId);

    ------------------------------------------------------------------
    -- 6. Audit history (insert-only)
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.SuggestionPostEvents', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.SuggestionPostEvents (
            Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SuggestionPostEvents PRIMARY KEY,
            PostId      INT            NOT NULL CONSTRAINT FK_SuggestionPostEvents_Post REFERENCES dbo.SuggestionPosts (Id) ON DELETE CASCADE,
            EventType   NVARCHAR(30)   NOT NULL,
            FromStatus  NVARCHAR(20)   NULL,
            ToStatus    NVARCHAR(20)   NULL,
            CommentId   INT            NULL,
            Note        NVARCHAR(2000) NULL,
            ActorId     INT            NOT NULL CONSTRAINT FK_SuggestionPostEvents_Actor REFERENCES dbo.IMSUsers (Id),
            CreatedAt   DATETIME2(3)   NOT NULL CONSTRAINT DF_SuggestionPostEvents_CreatedAt DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT CK_SuggestionPostEvents_Type CHECK (EventType IN (N'Created', N'StatusChanged', N'MadePermanent', N'Deleted', N'CommentEdited', N'CommentDeleted'))
        );
        PRINT '=== SuggestionPostEvents created ===';
    END
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SuggestionPostEvents_Post' AND object_id = OBJECT_ID('dbo.SuggestionPostEvents'))
        CREATE INDEX IX_SuggestionPostEvents_Post ON dbo.SuggestionPostEvents (PostId, CreatedAt);

    ------------------------------------------------------------------
    -- 7. Triggers (CREATE OR ALTER: re-running brings them to this version)
    ------------------------------------------------------------------
    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_SuggestionPosts_Rules ON dbo.SuggestionPosts
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.Polyhouses p ON p.Id = i.PolyhouseId WHERE p.AreaId <> i.AreaId)
        THROW 51713, ''This Polyhouse does not belong to the selected Area.'', 1;
    IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id = i.Id WHERE d.Id IS NULL)
    BEGIN
        IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id = i.Id JOIN dbo.Area a ON a.Id = i.AreaId WHERE d.Id IS NULL AND a.IsActive = 0)
            THROW 51834, ''The selected Area is not active.'', 1;
        IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id = i.Id JOIN dbo.Area a ON a.Id = i.AreaId
                   WHERE d.Id IS NULL AND a.AreaType = N''MainOffice'' AND i.PolyhouseId IS NULL)
            THROW 51835, ''Please select a Polyhouse for this Area.'', 1;
        IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id = i.Id JOIN dbo.SuggestionPostTypes t ON t.Code = i.TypeCode
                   WHERE d.Id IS NULL AND t.IsActive = 0)
            THROW 51836, ''This post type is not available.'', 1;
        IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id = i.Id
                   WHERE d.Id IS NULL AND (i.IsPermanent = 1 OR i.IsDeleted = 1 OR i.Status <> N''Open'' OR i.CreatedAt > DATEADD(MINUTE, 1, SYSUTCDATETIME())))
            THROW 51837, ''A new post starts Open, not permanent, not deleted, and is not dated in the future.'', 1;
    END
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE i.TypeCode <> d.TypeCode OR i.AreaId <> d.AreaId OR ISNULL(i.PolyhouseId, -1) <> ISNULL(d.PolyhouseId, -1)
                  OR i.PostedById <> d.PostedById OR i.CreatedAt <> d.CreatedAt)
        THROW 51838, ''The type, location, author and creation time of a post cannot be changed.'', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE (d.IsPermanent = 1 AND i.IsPermanent = 0) OR (d.IsDeleted = 1 AND i.IsDeleted = 0))
        THROW 51839, ''A permanent post stays permanent and a deleted post stays deleted.'', 1;
END');
    PRINT '=== TR_SuggestionPosts_Rules created / updated ===';

    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_SuggestionComments_Rules ON dbo.SuggestionComments
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    -- One level of replies: the parent must itself be a top-level comment.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.SuggestionComments p ON p.Id = i.ParentCommentId WHERE p.ParentCommentId IS NOT NULL)
        THROW 51840, ''A reply can only be added to a top-level comment.'', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE i.PostId <> d.PostId OR ISNULL(i.ParentCommentId, -1) <> ISNULL(d.ParentCommentId, -1)
                  OR i.AuthorId <> d.AuthorId OR i.CreatedAt <> d.CreatedAt)
        THROW 51841, ''The post, parent, author and time of a comment cannot be changed.'', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id WHERE d.IsDeleted = 1 AND (i.IsDeleted = 0 OR i.CommentText <> d.CommentText))
        THROW 51842, ''A deleted comment cannot be changed.'', 1;
END');
    PRINT '=== TR_SuggestionComments_Rules created / updated ===';

    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_SuggestionPostEvents_Protect ON dbo.SuggestionPostEvents
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted)
        THROW 51843, ''Suggestion post history cannot be changed.'', 1;
    IF EXISTS (SELECT 1 FROM deleted d WHERE EXISTS (SELECT 1 FROM dbo.SuggestionPosts p WHERE p.Id = d.PostId))
        THROW 51844, ''Suggestion post history cannot be deleted while its post exists.'', 1;
END');
    PRINT '=== TR_SuggestionPostEvents_Protect created / updated ===';

    ------------------------------------------------------------------
    -- 8. Verify
    ------------------------------------------------------------------
    PRINT '=== Permissions and grants ===';
    SELECT p.Code, COUNT(rp.RoleId) AS Roles FROM dbo.Permissions p LEFT JOIN dbo.RolePermissions rp ON rp.PermissionId = p.Id
    WHERE p.Code LIKE N'Suggestion.%' GROUP BY p.Code ORDER BY p.Code;
    SELECT OBJECT_NAME(parent_object_id) AS TableName, name AS ConstraintName, type_desc FROM sys.objects
    WHERE parent_object_id IN (OBJECT_ID('dbo.SuggestionPostTypes'), OBJECT_ID('dbo.SuggestionPosts'), OBJECT_ID('dbo.SuggestionPostPhotos'),
                               OBJECT_ID('dbo.SuggestionComments'), OBJECT_ID('dbo.SuggestionPostEvents'))
      AND type IN ('C', 'F', 'PK', 'UQ', 'D', 'TR')
    ORDER BY 1, 3, 2;
    SELECT OBJECT_NAME(object_id) AS TableName, name AS IndexName FROM sys.indexes
    WHERE object_id IN (OBJECT_ID('dbo.SuggestionPosts'), OBJECT_ID('dbo.SuggestionComments'), OBJECT_ID('dbo.SuggestionPostEvents'), OBJECT_ID('dbo.SuggestionPostPhotos'))
      AND name IS NOT NULL
    ORDER BY 1, 2;

    PRINT '=== After: row counts (master tables must equal Before) ===';
    SELECT 'Area' AS TableName, COUNT(*) AS Rows FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'IMSUsers', COUNT(*) FROM dbo.IMSUsers
    UNION ALL SELECT 'Roles', COUNT(*) FROM dbo.Roles
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'MaintenanceTickets', COUNT(*) FROM dbo.MaintenanceTickets
    UNION ALL SELECT 'MaintenanceTicketPhotos', COUNT(*) FROM dbo.MaintenanceTicketPhotos
    UNION ALL SELECT 'SuggestionPosts', COUNT(*) FROM dbo.SuggestionPosts;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION SuggestionsProblems2026_10_07;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION SuggestionsProblems2026_10_07;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION SuggestionsProblems2026_10_07;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
