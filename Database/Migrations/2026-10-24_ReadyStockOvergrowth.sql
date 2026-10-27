-- ============================================================================
-- 2026-10-24_ReadyStockOvergrowth.sql            *** PREPARED FOR REVIEW -- NOT APPLIED ***
--
-- WHY (requirement 2026-10-22): staff must be able to identify OVERGROWN seedlings in Ready Stock ("⚠ OG -- Overgrowth"),
--   monitor them separately, and still book, fulfil and dispatch them. Nothing in the schema can carry a deliberate,
--   audited per-batch flag today, so this script adds one.
--
-- WHERE THE MARKER LIVES: dbo.ReadyStock -- one row per Ready Stock BATCH. Reservations (BookingBatchAllocations),
--   partial fulfilment and dispatch (SeedlingDispatchLines) all reference ReadyStock.Id and only move the batch's own
--   Reserved / Dispatched counters, so whatever remains of a marked batch stays marked. Send to Main Office / Outlet moves
--   the SAME row (AreaId), so the marker travels with it. A Mother Plant transfer RECEIVES the plants into a new child row
--   (ReadyStock.OriginType = 'Transfer'); the application copies the marker onto it in the same transaction and logs
--   'Inherited'. (A transplanted output is a new batch of new trays and starts unmarked -- shown on the Transplanting page.)
--
-- WHAT:
--   1. dbo.ReadyStock: IsOvergrowth BIT NOT NULL DEFAULT 0, OvergrowthMarkedAt DATETIME2 NULL, OvergrowthMarkedById INT NULL
--      (FK dbo.IMSUsers), OvergrowthReason NVARCHAR(200) NULL; CK_ReadyStock_Overgrowth keeps them consistent
--      (marked = a time and a reason; unmarked = all empty). Existing rows: 0 (normal stock) -- NOTHING is marked
--      automatically (no age / variety rule).
--   2. dbo.ReadyStockOvergrowthLog: append-only history (Marked / Unmarked / Inherited) -- who, when, why;
--      TR_ReadyStockOvergrowthLog_AppendOnly refuses UPDATE and DELETE.
--   3. dbo.SeedlingDispatchLines.IsOvergrowth BIT NOT NULL DEFAULT 0: whether the batch was marked AT DISPATCH -- a snapshot
--      written by the application with the line, so the dispatch record stays right if the batch is unmarked later.
--      Existing lines: 0 (no batch was marked before this script).
--   4. Permission ReadyStock.Overgrowth ("mark / unmark Ready Stock batches as Overgrowth in the user's own Area"),
--      created only if missing, granted to the two roles that approve Ready Stock (ReadyStock.Confirm):
--      "Sowing Supervisor" and "Mother Plant Supervisor". The application checks it per Area: the user must hold it
--      through a role assignment IN THE BATCH'S AREA (dbo.UserRoles.AreaId), or be a System Administrator (full access).
--      Logged in dbo.RolePermissionChangeLog.
--
-- NOT changed: quantities, reservations, dispatches, bookings, prices, the Ready Stock ledger, any other role or user.
--   Booking fulfilment and dispatch rules are unchanged -- Overgrowth stock stays eligible for both.
--
-- APPLICATION WITHOUT THIS SCRIPT: the code probes for it (Data/ReadyStockOvergrowthSchema.cs). Without it no badge is shown,
--   the Overgrowth page says the update is not applied, and nothing can be marked -- every other screen works as before.
--
-- OWNERSHIP RECORD (what the rollback may undo): a permission code CREATED here carries @OwnedDescription; each grant's
--   'Added' row in dbo.RolePermissionChangeLog ends "[code created by this script; PermissionId=<Id>]" or
--   "[code pre-existed; PermissionId=<Id>]". The schema objects above are new and owned by this script.
--
-- TARGET SAFEGUARDS (as 2026-10-23_ReadyStockDispatchToCustomer.sql): the local AND the live database can both be named
--   PlantsIMS2_Test. The first batch refuses to continue unless ALL of these hold (nothing is read or written before them):
--     1. DB_NAME() is PlantsIMS2_Test or PlantsIMS2_Scratch_*
--     2. MODE is DRYRUN or COMMIT
--     3. @@SERVERNAME = $(TARGET_SERVER)
--     4. SERVERPROPERTY('ServerName') = @@SERVERNAME
--     5. SERVERPROPERTY('MachineName') = $(TARGET_MACHINE)
--     6. DB_NAME() = $(TARGET_DB)
--     7. sys.databases.create_date of THIS database = $(TARGET_DB_CREATED)   (yyyy-mm-ddThh:mi:ss)
--
-- OPERATOR CHECKLIST
--   a) read-only identity query, keep its output:
--        SELECT @@SERVERNAME AS ServerName, CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) AS ServerProperty,
--               CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) AS MachineName, DB_NAME() AS DatabaseName,
--               CONVERT(CHAR(19), d.create_date, 126) AS DatabaseCreated
--        FROM sys.databases d WHERE d.database_id = DB_ID();
--   b) confirm independently that this machine IS the intended one; c) take a COPY_ONLY backup;
--   d) sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER="<ServerName>" TARGET_MACHINE="<MachineName>"
--             TARGET_DB="<DatabaseName>" TARGET_DB_CREATED="<DatabaseCreated>" -i 2026-10-24_ReadyStockOvergrowth.sql
--   Review the DRYRUN output (identity line, the two roles and their holders' Areas, "created" / "pre-existed", the roles
--   now holding the code). It ends "DRY RUN -- rolled back". Then the same command with MODE=COMMIT.
--
-- Requires: 2026-10-11_TrayAccessByLocation.sql (dbo.RolePermissionChangeLog). One transaction. Idempotent.
-- Undo: 2026-10-24_ReadyStockOvergrowth_Rollback.sql.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52631, '2026-10-24_ReadyStockOvergrowth.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52632, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52633, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 52634, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 52635, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 52636, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 52637, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION RsOvergrowth1024;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NULL
        THROW 52638, 'dbo.RolePermissionChangeLog is missing -- run 2026-10-11_TrayAccessByLocation.sql first.', 1;
    IF COL_LENGTH('dbo.ReadyStock', 'InTransitQuantity') IS NULL OR COL_LENGTH('dbo.ReadyStock', 'OriginType') IS NULL
        THROW 52639, 'dbo.ReadyStock is older than 2026-10-09_ReadyStockTransplanting.sql -- apply the earlier migrations first.', 1;

    -- ---- 1. dbo.ReadyStock -----------------------------------------------------------------------------------------
    IF COL_LENGTH('dbo.ReadyStock', 'IsOvergrowth') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.ReadyStock ADD
            IsOvergrowth         BIT            NOT NULL CONSTRAINT DF_ReadyStock_IsOvergrowth DEFAULT (0),
            OvergrowthMarkedAt   DATETIME2      NULL,
            OvergrowthMarkedById INT            NULL CONSTRAINT FK_ReadyStock_OvergrowthMarkedBy REFERENCES dbo.IMSUsers (Id),
            OvergrowthReason     NVARCHAR(200)  NULL;');
        EXEC (N'ALTER TABLE dbo.ReadyStock ADD CONSTRAINT CK_ReadyStock_Overgrowth CHECK (
            (IsOvergrowth = 0 AND OvergrowthMarkedAt IS NULL AND OvergrowthMarkedById IS NULL AND OvergrowthReason IS NULL)
         OR (IsOvergrowth = 1 AND OvergrowthMarkedAt IS NOT NULL AND OvergrowthReason IS NOT NULL AND LEN(OvergrowthReason) > 0));');
        EXEC (N'CREATE INDEX IX_ReadyStock_Overgrowth ON dbo.ReadyStock (IsOvergrowth) INCLUDE (AreaId, SpeciesId) WHERE IsOvergrowth = 1;');
        PRINT '=== dbo.ReadyStock: Overgrowth columns, CK_ReadyStock_Overgrowth and IX_ReadyStock_Overgrowth added (every batch unmarked) ===';
    END
    ELSE
        PRINT '=== dbo.ReadyStock.IsOvergrowth already exists -- no-op ===';

    -- ---- 2. dbo.ReadyStockOvergrowthLog (append-only) --------------------------------------------------------------
    IF OBJECT_ID('dbo.ReadyStockOvergrowthLog', 'U') IS NULL
    BEGIN
        EXEC (N'CREATE TABLE dbo.ReadyStockOvergrowthLog (
            Id                 INT            IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReadyStockOvergrowthLog PRIMARY KEY,
            ReadyStockId       INT            NOT NULL CONSTRAINT FK_RsOvergrowthLog_ReadyStock REFERENCES dbo.ReadyStock (Id),
            Action             NVARCHAR(20)   NOT NULL CONSTRAINT CK_RsOvergrowthLog_Action CHECK (Action IN (N''Marked'', N''Unmarked'', N''Inherited'')),
            Reason             NVARCHAR(200)  NULL,
            SourceReadyStockId INT            NULL CONSTRAINT FK_RsOvergrowthLog_Source REFERENCES dbo.ReadyStock (Id),
            UserId             INT            NULL CONSTRAINT FK_RsOvergrowthLog_User REFERENCES dbo.IMSUsers (Id),
            UserName           NVARCHAR(100)  NULL,
            CreatedAt          DATETIME2      NOT NULL CONSTRAINT DF_RsOvergrowthLog_CreatedAt DEFAULT (SYSUTCDATETIME())
        );');
        EXEC (N'CREATE INDEX IX_RsOvergrowthLog_ReadyStock ON dbo.ReadyStockOvergrowthLog (ReadyStockId, CreatedAt);');
        EXEC (N'CREATE TRIGGER dbo.TR_ReadyStockOvergrowthLog_AppendOnly ON dbo.ReadyStockOvergrowthLog
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 52640, ''dbo.ReadyStockOvergrowthLog is append-only: Overgrowth history cannot be changed or deleted.'', 1;
END;');
        PRINT '=== dbo.ReadyStockOvergrowthLog (append-only) created ===';
    END
    ELSE
        PRINT '=== dbo.ReadyStockOvergrowthLog already exists -- no-op ===';

    -- ---- 3. dbo.SeedlingDispatchLines.IsOvergrowth (snapshot at dispatch) ------------------------------------------
    IF COL_LENGTH('dbo.SeedlingDispatchLines', 'IsOvergrowth') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.SeedlingDispatchLines ADD IsOvergrowth BIT NOT NULL CONSTRAINT DF_SeedlingDispatchLines_IsOvergrowth DEFAULT (0);');
        PRINT '=== dbo.SeedlingDispatchLines.IsOvergrowth added (existing lines: 0) ===';
    END
    ELSE
        PRINT '=== dbo.SeedlingDispatchLines.IsOvergrowth already exists -- no-op ===';

    -- ---- 4. Permission ReadyStock.Overgrowth -> the two Ready Stock approver roles --------------------------------------
    DECLARE @Code NVARCHAR(100) = N'ReadyStock.Overgrowth';
    DECLARE @Script NVARCHAR(200) = N'2026-10-24_ReadyStockOvergrowth.sql';
    DECLARE @OwnedDescription NVARCHAR(200) =
        N'Mark / unmark Ready Stock batches as Overgrowth (own Area) [created by 2026-10-24_ReadyStockOvergrowth.sql]';
    DECLARE @Roles TABLE (RoleName NVARCHAR(100) PRIMARY KEY, RoleId INT NULL);
    INSERT INTO @Roles (RoleName) VALUES (N'Sowing Supervisor'), (N'Mother Plant Supervisor');
    UPDATE x SET RoleId = r.Id FROM @Roles x JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = x.RoleName;
    IF EXISTS (SELECT 1 FROM @Roles WHERE RoleId IS NULL)
        THROW 52641, 'Role Sowing Supervisor or Mother Plant Supervisor not found (or not unique) -- nothing changed.', 1;
    IF EXISTS (SELECT x.RoleName FROM @Roles x JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = x.RoleName GROUP BY x.RoleName HAVING COUNT(*) > 1)
        THROW 52642, 'A role name matches more than one dbo.Roles row -- nothing changed.', 1;

    PRINT '=== Holders of the two roles and the Area of each assignment (the permission applies only in that Area) ===';
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
        PRINT '=== Permission ReadyStock.Overgrowth created by this script ===';
    END
    ELSE
        PRINT '=== Permission ReadyStock.Overgrowth pre-existed -- used as it is, never deleted by the rollback ===';
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
                    N'Ready Stock approvers mark / unmark Overgrowth batches in their own Areas (requirement 2026-10-22) '
                    + CASE WHEN @CreatedHere = 1 THEN N'[code created by this script; PermissionId=' ELSE N'[code pre-existed; PermissionId=' END
                    + CAST(@Perm AS NVARCHAR(20)) + N']');
            INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@Role, @Perm);
            PRINT '=== ReadyStock.Overgrowth granted to ' + @RoleName + ' ===';
        END
        FETCH NEXT FROM grant_cursor INTO @RoleName, @Role;
    END
    CLOSE grant_cursor;
    DEALLOCATE grant_cursor;

    PRINT '=== Roles holding ReadyStock.Overgrowth (expected: Mother Plant Supervisor, Sowing Supervisor) ===';
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS Role, p.Code, p.Id AS PermissionId, p.Description
    FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code = @Code;

    EXEC (N'SELECT COUNT(*) AS ReadyStockBatches, SUM(CASE WHEN IsOvergrowth = 1 THEN 1 ELSE 0 END) AS MarkedOvergrowth FROM dbo.ReadyStock;');
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION RsOvergrowth1024;
    THROW;
END CATCH;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION RsOvergrowth1024;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION RsOvergrowth1024;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
