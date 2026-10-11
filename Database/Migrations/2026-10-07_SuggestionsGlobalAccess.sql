-- ============================================================================
-- 2026-10-07_SuggestionsGlobalAccess.sql      (run AFTER 2026-10-07_SuggestionsProblems.sql)
--
-- WHY: Suggestions & Problems became a GLOBAL module: EVERY signed-in user (any role,
-- with or without an Area -- Electrician, Welder, Plumber included) may view, post,
-- comment and reply, for every Area. The application now grants Suggestion.View /
-- Suggestion.Create / Suggestion.Comment to every authenticated user in code
-- (SuggestionAuthorization.GlobalPermissions) and the three pages use the existing
-- "@authenticated" page rule. The per-role grants of those three codes are therefore
-- dead data -- and misleading (un-ticking one in Role Permissions would appear to
-- restrict access but would not). This script removes them.
--
-- WHAT THIS DOES:
--   dbo.RolePermissions   removes every grant of Suggestion.View / Create / Comment
--   dbo.Permissions       removes the codes Suggestion.View / Create / Comment
--   Suggestion.Manage     KEPT (administrator-only; granted to no role, full access has it)
--
-- NOT changed: any table, column, post, comment, photo, history row, role, user,
-- Area assignment, or any other permission / grant. Area authorization of every
-- other module is untouched.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the
-- server named with -v TARGET_SERVER=<@@SERVERNAME>. One transaction. Idempotent.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
--       then MODE=COMMIT.   Undo: 2026-10-07_SuggestionsGlobalAccess_Rollback.sql
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51853, '2026-10-07_SuggestionsGlobalAccess.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51854, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51855, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION SuggestionsGlobal20261007;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.SuggestionPosts', 'U') IS NULL
        THROW 51856, 'Run 2026-10-07_SuggestionsProblems.sql first.', 1;

    PRINT '=== Before ===';
    SELECT p.Code, COUNT(rp.RoleId) AS Roles FROM dbo.Permissions p LEFT JOIN dbo.RolePermissions rp ON rp.PermissionId = p.Id
    WHERE p.Code LIKE N'Suggestion.%' GROUP BY p.Code ORDER BY p.Code;
    SELECT 'Permissions' AS TableName, COUNT(*) AS Rows FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'SuggestionPosts', COUNT(*) FROM dbo.SuggestionPosts
    UNION ALL SELECT 'SuggestionComments', COUNT(*) FROM dbo.SuggestionComments;

    DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code IN (N'Suggestion.View', N'Suggestion.Create', N'Suggestion.Comment');
    PRINT CONCAT('Per-role grants removed: ', @@ROWCOUNT, ' (33 where the first migration ran, 0 on re-run)');

    DELETE FROM dbo.Permissions WHERE Code IN (N'Suggestion.View', N'Suggestion.Create', N'Suggestion.Comment');
    PRINT CONCAT('Permission codes removed: ', @@ROWCOUNT, ' (3 first time, 0 on re-run)');

    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'Suggestion.Manage')
        THROW 51857, 'Suggestion.Manage is missing -- run 2026-10-07_SuggestionsProblems.sql first.', 1;

    PRINT '=== After ===';
    SELECT p.Code, COUNT(rp.RoleId) AS Roles FROM dbo.Permissions p LEFT JOIN dbo.RolePermissions rp ON rp.PermissionId = p.Id
    WHERE p.Code LIKE N'Suggestion.%' GROUP BY p.Code ORDER BY p.Code;
    SELECT 'Permissions' AS TableName, COUNT(*) AS Rows FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'SuggestionPosts', COUNT(*) FROM dbo.SuggestionPosts
    UNION ALL SELECT 'SuggestionComments', COUNT(*) FROM dbo.SuggestionComments;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION SuggestionsGlobal20261007;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION SuggestionsGlobal20261007;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION SuggestionsGlobal20261007;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
