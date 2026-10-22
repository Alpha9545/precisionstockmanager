-- ============================================================================
-- 2026-10-23_ReadyStockDispatchToCustomer.sql
--
-- WHY (requirement 2026-10-19): the Office Coordinator dispatches Ready Seedling Stock straight to a customer from
--   Seed Production > Ready Stock > Send > Customer (Pages/Production/ReadyStock/Move). Today the role is read-only on
--   Ready Stock (ReadyStock.View) and cannot open that page at all.
--
-- WHAT: the permission code ReadyStock.DispatchToCustomer (created only if missing) granted to the role "Office
--   Coordinator" only (one dbo.RolePermissions row), logged in dbo.RolePermissionChangeLog. The code means exactly
--   "dispatch whole trays of a Ready Stock batch in an active Main Office / Mother Plant Area the user is ASSIGNED to, to a
--   customer" -- the existing direct tray sale (SeedlingFulfilmentRepository.DirectTraySaleAsync: Completed booking +
--   allocation + reservation + Seedling Dispatch, one transaction, once per form token). Area scope = AreaAccessService
--   (regular Area rule), checked on the page and again inside the transaction under the batch's lock.
--
-- OWNERSHIP RECORD (what the rollback may undo) -- no table or constraint is changed for it:
--   * a code CREATED here gets the exact Description in @OwnedDescription (ends "[created by <this script>]");
--   * the grant's 'Added' row in dbo.RolePermissionChangeLog names the Permission Id and whether the code was created here:
--       Reason ends "[code created by this script; PermissionId=<Id>]"  or  "[code pre-existed; PermissionId=<Id>]".
--   A code that already existed is never marked as created here, so the rollback never deletes it.
--
-- AREA SCOPE GUARD: refuses (nothing changed) when an Office Coordinator role assignment has NO Area -- the grant must
--   stay inside the Areas each Office Coordinator is assigned to. Every holder and all of their Areas are printed for review.
--
-- NOT changed: InternalTransfer.Enter / MainOffice.Confirm (the Main Office / Outlet transfer) are NOT granted -- the
--   Office Coordinator still cannot send a batch to an Outlet unless that is decided separately. No other role, user,
--   Area, stock, booking or dispatch row; no schema. Requires 2026-10-18_SeedlingDirectSaleSubmissionToken.sql for the
--   dispatch itself (without it the page shows the dispatch as unavailable).
--
-- TARGET SAFEGUARDS: the local AND the live database can both be named PlantsIMS2_Test, so the name alone proves nothing.
--   The first batch refuses to continue unless ALL of these hold (nothing is read or written before them):
--     1. DB_NAME() is PlantsIMS2_Test or PlantsIMS2_Scratch_*                         (allow-list, unchanged)
--     2. MODE is DRYRUN or COMMIT                                                       (unchanged)
--     3. @@SERVERNAME            = $(TARGET_SERVER)                                     (unchanged)
--     4. SERVERPROPERTY('ServerName') = @@SERVERNAME   (a renamed host leaves @@SERVERNAME stale -- refused)
--     5. SERVERPROPERTY('MachineName') = $(TARGET_MACHINE)   (the Windows host, read from the OS, not from sys.servers)
--     6. DB_NAME()               = $(TARGET_DB)          (the database the operator meant, not only "an allowed name")
--     7. sys.databases.create_date of THIS database = $(TARGET_DB_CREATED)  (yyyy-mm-ddThh:mi:ss; differs between the
--        local and the live copy, and changes on every restore -- identifies the exact database instance)
--   Every variable is required: a missing -v value stops sqlcmd before anything runs. No server name is built in.
--
-- OPERATOR CHECKLIST
--   Before DRYRUN (on the intended server, connected to the intended database):
--     a) run the read-only identity query and keep its output:
--          SELECT @@SERVERNAME AS ServerName, CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) AS ServerProperty,
--                 CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) AS MachineName, DB_NAME() AS DatabaseName,
--                 CONVERT(CHAR(19), d.create_date, 126) AS DatabaseCreated
--          FROM sys.databases d WHERE d.database_id = DB_ID();
--     b) confirm independently (not from that output) that this machine IS the intended one: for live, the VPS you are
--        logged on to (e.g. hostname / the RDP or SSH session target); for local, this laptop. ServerName and MachineName
--        must name that host; DatabaseName must be PlantsIMS2_Test (or the intended scratch copy).
--     c) take a COPY_ONLY backup of that database and note its file name.
--     d) run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER="<ServerName>" TARGET_MACHINE="<MachineName>"
--                     TARGET_DB="<DatabaseName>" TARGET_DB_CREATED="<DatabaseCreated>" -i 2026-10-23_ReadyStockDispatchToCustomer.sql
--   Read the DRYRUN output: the identity line, the holders and their Areas (only intended Office Coordinators, each with
--   an Area), "created" or "pre-existed" for the code, and "Roles holding ..." = Office Coordinator only. It ends
--   "DRY RUN -- rolled back".
--   Before COMMIT: the DRYRUN above was on the SAME server and database (same four values), its output was reviewed and
--   approved, the backup exists, and nobody changed roles in between. Re-run with MODE=COMMIT and the same four values.
--
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-23_ReadyStockDispatchToCustomer_Rollback.sql.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52611, '2026-10-23_ReadyStockDispatchToCustomer.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52612, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52613, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 52617, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 52618, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 52619, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 52620, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION RsDispatchCustomer1023;

BEGIN TRY
    DECLARE @Created CHAR(19) = (SELECT CONVERT(CHAR(19), create_date, 126) FROM sys.databases WHERE database_id = DB_ID());
    PRINT '=== Server / machine / database / created: ' + @@SERVERNAME + ' / ' + CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128))
        + ' / ' + DB_NAME() + ' / ' + @Created + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NULL
        THROW 52614, 'dbo.RolePermissionChangeLog is missing -- run 2026-10-11_TrayAccessByLocation.sql first.', 1;
    IF (SELECT COUNT(*) FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Office Coordinator') <> 1
        THROW 52615, 'Exactly one role named Office Coordinator is expected -- nothing changed.', 1;

    DECLARE @RoleName NVARCHAR(100) = N'Office Coordinator';
    DECLARE @Code NVARCHAR(100) = N'ReadyStock.DispatchToCustomer';
    DECLARE @Script NVARCHAR(200) = N'2026-10-23_ReadyStockDispatchToCustomer.sql';
    DECLARE @OwnedDescription NVARCHAR(200) =
        N'Dispatch whole Ready Stock trays to a customer (own Main Office / Mother Plant Area) [created by 2026-10-23_ReadyStockDispatchToCustomer.sql]';
    DECLARE @Role INT = (SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = @RoleName);

    PRINT '=== Holders of the role and every Area they are assigned to (any role) ===';
    SELECT u.Id, u.Name, u.IsActive, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS Role, a.Id AS AreaId, a.Name AS Area, a.AreaType, a.IsActive AS AreaActive
    FROM dbo.UserRoles ur JOIN dbo.IMSUsers u ON u.Id = ur.UserId JOIN dbo.Roles r ON r.Id = ur.RoleId LEFT JOIN dbo.Area a ON a.Id = ur.AreaId
    WHERE ur.UserId IN (SELECT UserId FROM dbo.UserRoles WHERE RoleId = @Role)
    ORDER BY u.Id, Role;

    IF EXISTS (SELECT 1 FROM dbo.UserRoles WHERE RoleId = @Role AND AreaId IS NULL)
        THROW 52616, 'An Office Coordinator role assignment has no Area -- the grant would not be limited to assigned Areas. Nothing changed.', 1;

    -- The code: created here only when missing (and then marked as ours); an existing code is used as it is.
    DECLARE @CreatedHere BIT = 0;
    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK) WHERE Code = @Code)
    BEGIN
        IF COLUMNPROPERTY(OBJECT_ID('dbo.Permissions'), 'Id', 'IsIdentity') = 1
            INSERT INTO dbo.Permissions (Code, Description) VALUES (@Code, @OwnedDescription);
        ELSE
            INSERT INTO dbo.Permissions (Id, Code, Description)
            SELECT ISNULL(MAX(Id), 0) + 1, @Code, @OwnedDescription FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK);
        SET @CreatedHere = 1;
        PRINT '=== Permission ReadyStock.DispatchToCustomer created by this script ===';
    END
    ELSE
        PRINT '=== Permission ReadyStock.DispatchToCustomer pre-existed -- used as it is, never deleted by the rollback ===';
    DECLARE @Perm INT = (SELECT Id FROM dbo.Permissions WHERE Code = @Code);

    IF EXISTS (SELECT 1 FROM dbo.RolePermissions WITH (UPDLOCK, HOLDLOCK) WHERE RoleId = @Role AND PermissionId = @Perm)
        PRINT '=== Already granted -- no-op ===';
    ELSE
    BEGIN
        INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
        VALUES (@Role, @RoleName, @Code, N'Added', @Script,
                N'Office Coordinator dispatches Ready Seedling Stock directly to customers in own Areas (requirement 2026-10-19) '
                + CASE WHEN @CreatedHere = 1 THEN N'[code created by this script; PermissionId=' ELSE N'[code pre-existed; PermissionId=' END
                + CAST(@Perm AS NVARCHAR(20)) + N']');
        INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@Role, @Perm);
        PRINT '=== ReadyStock.DispatchToCustomer granted to Office Coordinator ===';
    END

    PRINT '=== Roles holding ReadyStock.DispatchToCustomer (expected: Office Coordinator only) ===';
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS Role, p.Code, p.Id AS PermissionId, p.Description
    FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code = @Code;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION RsDispatchCustomer1023;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION RsDispatchCustomer1023;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION RsDispatchCustomer1023;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
