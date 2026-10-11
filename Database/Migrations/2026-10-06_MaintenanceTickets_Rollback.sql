-- ============================================================================
-- 2026-10-06_MaintenanceTickets_Rollback.sql
--
-- Undoes 2026-10-06_MaintenanceTickets.sql -- ONLY what it added:
--   triggers; dbo.MaintenanceTicketPhotos / Events / Tickets / Categories;
--   the grants of the Maintenance.* permissions and of the worker roles;
--   the Maintenance.* permissions; the Electrician / Welder / Plumber roles.
-- REFUSES to run while any ticket exists (tickets, their history and photo
-- records can never be lost by a rollback -- restore the pre-migration backup
-- instead if that is really intended), and while any user is still assigned a
-- worker role (remove those assignments in Admin -> User Roles first).
-- Photo FILES on disk are not touched. The application code must be rolled back first.
--
-- SAFETY: same guards as the migration. One transaction. Idempotent.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51771, 'This rollback may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51772, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51773, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION MaintenanceTicketsRollback;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    IF OBJECT_ID('dbo.MaintenanceTickets', 'U') IS NOT NULL
    BEGIN
        DECLARE @Tickets INT;
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.MaintenanceTickets', N'@n INT OUTPUT', @n = @Tickets OUTPUT;
        IF @Tickets > 0
            THROW 51774, 'dbo.MaintenanceTickets has tickets -- refusing to drop it. Restore the pre-migration backup if that is really intended.', 1;
    END

    DECLARE @WorkerRoles TABLE (Name NVARCHAR(100) PRIMARY KEY);
    INSERT INTO @WorkerRoles VALUES (N'Electrician'), (N'Welder'), (N'Plumber');
    IF EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId JOIN @WorkerRoles w ON w.Name = COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName))
        THROW 51775, 'Users are still assigned Electrician / Welder / Plumber -- remove those role assignments first.', 1;

    IF OBJECT_ID('dbo.TR_MaintenanceTicketEvents_Protect', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_MaintenanceTicketEvents_Protect;
    IF OBJECT_ID('dbo.TR_MaintenanceTickets_Location', 'TR') IS NOT NULL     DROP TRIGGER dbo.TR_MaintenanceTickets_Location;
    IF OBJECT_ID('dbo.MaintenanceTicketPhotos', 'U') IS NOT NULL             DROP TABLE dbo.MaintenanceTicketPhotos;
    IF OBJECT_ID('dbo.MaintenanceTicketEvents', 'U') IS NOT NULL             DROP TABLE dbo.MaintenanceTicketEvents;
    IF OBJECT_ID('dbo.MaintenanceTickets', 'U') IS NOT NULL                  DROP TABLE dbo.MaintenanceTickets;
    IF OBJECT_ID('dbo.MaintenanceCategories', 'U') IS NOT NULL              DROP TABLE dbo.MaintenanceCategories;

    DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code LIKE N'Maintenance.%';
    PRINT CONCAT('Maintenance grants removed: ', @@ROWCOUNT);
    DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN @WorkerRoles w ON w.Name = COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName);
    PRINT CONCAT('Worker-role grants removed: ', @@ROWCOUNT);
    DELETE FROM dbo.Permissions WHERE Code IN (N'Maintenance.Raise', N'Maintenance.View', N'Maintenance.Work', N'Maintenance.Manage');
    PRINT CONCAT('Permissions removed: ', @@ROWCOUNT);
    DELETE r FROM dbo.Roles r JOIN @WorkerRoles w ON w.Name = COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName);
    PRINT CONCAT('Worker roles removed: ', @@ROWCOUNT);

    SELECT OBJECT_ID('dbo.MaintenanceTickets') AS TicketsTable, (SELECT COUNT(*) FROM dbo.Permissions WHERE Code LIKE N'Maintenance.%') AS MaintenancePermissions;

    IF N'$(MODE)' = N'COMMIT'
    BEGIN
        COMMIT TRANSACTION MaintenanceTicketsRollback;
        PRINT '=== COMMITTED ===';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION MaintenanceTicketsRollback;
        PRINT '=== DRY RUN -- rolled back, nothing changed ===';
    END
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH
GO
