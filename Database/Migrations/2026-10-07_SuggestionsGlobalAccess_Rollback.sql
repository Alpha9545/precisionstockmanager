-- ============================================================================
-- 2026-10-07_SuggestionsGlobalAccess_Rollback.sql
--
-- Undoes 2026-10-07_SuggestionsGlobalAccess.sql: re-creates Suggestion.View /
-- Suggestion.Create / Suggestion.Comment and grants them again to the 11 Area-based
-- roles (the state after 2026-10-07_SuggestionsProblems.sql). Deploy the matching
-- (Area-scoped) application code with it -- the global-access code does not read
-- these grants.
--
-- SAFETY: same guards as the migration. Idempotent.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51858, '2026-10-07_SuggestionsGlobalAccess_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51859, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51860, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION SuggestionsGlobalRollback;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    DECLARE @Perms TABLE (Code NVARCHAR(200) PRIMARY KEY, Description NVARCHAR(400), SortOrder INT);
    INSERT INTO @Perms VALUES
        (N'Suggestion.View',    N'See Suggestions & Problems posts of own Areas', 1),
        (N'Suggestion.Create',  N'Post a Suggestion / Problem for own Areas', 2),
        (N'Suggestion.Comment', N'Comment on and reply to Suggestions & Problems posts of own Areas', 3);
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
            PRINT CONCAT('=== Permission ', @Code, ' re-created ===');
        END
        FETCH NEXT FROM perm_cursor INTO @Code, @PermDescription;
    END
    CLOSE perm_cursor;
    DEALLOCATE perm_cursor;

    DECLARE @EmployeeRoles TABLE (Name NVARCHAR(100) PRIMARY KEY);
    INSERT INTO @EmployeeRoles VALUES
        (N'Office Coordinator'), (N'Sowing Operator'), (N'Booking Executive'), (N'Dispatch Executive'),
        (N'Fertilizer Supervisor'), (N'Mother Plant Supervisor'), (N'Sowing Supervisor'),
        (N'Main Office Store Keeper'), (N'Outlet Sales'), (N'Pot Production Operator'), (N'Purchase Officer');
    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM dbo.Roles r
    JOIN @EmployeeRoles e ON e.Name = COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName)
    CROSS JOIN dbo.Permissions p
    WHERE p.Code IN (N'Suggestion.View', N'Suggestion.Create', N'Suggestion.Comment')
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    PRINT CONCAT('Grants restored: ', @@ROWCOUNT);
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION SuggestionsGlobalRollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION SuggestionsGlobalRollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION SuggestionsGlobalRollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
