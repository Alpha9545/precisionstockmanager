-- ============================================================================
-- 2026-10-07_SuggestionsProblems_Rollback.sql
--
-- Undoes 2026-10-07_SuggestionsProblems.sql: drops the Suggestion* tables and
-- triggers, the Suggestion.* grants and permissions.
--
-- REFUSES while any post, comment, photo record or event exists -- posts are
-- business records; this script never deletes data. (Photo FILES live outside the
-- database under SuggestionStorage:PhotoRoot and are not touched.)
--
-- SAFETY: same guards as the migration (database name, -v TARGET_SERVER, MODE).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51846, '2026-10-07_SuggestionsProblems_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51847, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51848, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION SuggestionsProblemsRollback;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    IF OBJECT_ID('dbo.SuggestionPosts', 'U') IS NOT NULL
        IF EXISTS (SELECT 1 FROM dbo.SuggestionPosts)
            THROW 51849, 'Suggestions & Problems posts exist -- refusing to drop business records.', 1;
    IF OBJECT_ID('dbo.SuggestionComments', 'U') IS NOT NULL
        IF EXISTS (SELECT 1 FROM dbo.SuggestionComments)
            THROW 51850, 'Suggestion comments exist -- refusing to drop business records.', 1;
    IF OBJECT_ID('dbo.SuggestionPostEvents', 'U') IS NOT NULL
        IF EXISTS (SELECT 1 FROM dbo.SuggestionPostEvents)
            THROW 51851, 'Suggestion history exists -- refusing to drop business records.', 1;
    IF OBJECT_ID('dbo.SuggestionPostPhotos', 'U') IS NOT NULL
        IF EXISTS (SELECT 1 FROM dbo.SuggestionPostPhotos)
            THROW 51852, 'Suggestion photo records exist -- refusing to drop business records.', 1;

    IF OBJECT_ID('dbo.TR_SuggestionPostEvents_Protect', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_SuggestionPostEvents_Protect;
    IF OBJECT_ID('dbo.TR_SuggestionComments_Rules', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_SuggestionComments_Rules;
    IF OBJECT_ID('dbo.TR_SuggestionPosts_Rules', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_SuggestionPosts_Rules;
    IF OBJECT_ID('dbo.SuggestionPostEvents', 'U') IS NOT NULL DROP TABLE dbo.SuggestionPostEvents;
    IF OBJECT_ID('dbo.SuggestionComments', 'U') IS NOT NULL DROP TABLE dbo.SuggestionComments;
    IF OBJECT_ID('dbo.SuggestionPostPhotos', 'U') IS NOT NULL DROP TABLE dbo.SuggestionPostPhotos;
    IF OBJECT_ID('dbo.SuggestionPosts', 'U') IS NOT NULL DROP TABLE dbo.SuggestionPosts;
    IF OBJECT_ID('dbo.SuggestionPostTypes', 'U') IS NOT NULL DROP TABLE dbo.SuggestionPostTypes;
    PRINT '=== Suggestion tables / triggers dropped ===';

    DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code LIKE N'Suggestion.%';
    PRINT CONCAT('Grants removed: ', @@ROWCOUNT);
    DELETE FROM dbo.Permissions WHERE Code IN (N'Suggestion.View', N'Suggestion.Create', N'Suggestion.Comment', N'Suggestion.Manage');
    PRINT CONCAT('Permissions removed: ', @@ROWCOUNT);
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION SuggestionsProblemsRollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION SuggestionsProblemsRollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION SuggestionsProblemsRollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
