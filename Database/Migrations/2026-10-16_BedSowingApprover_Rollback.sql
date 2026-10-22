-- ============================================================================
-- 2026-10-16_BedSowingApprover_Rollback.sql
--
-- Undoes 2026-10-16_BedSowingApprover.sql: TR_ReadyConfirmations_AssignedSupervisor goes back to the exact
-- 2026-10-13 version (Bed Sowing batches: any approver, never the recorder), TR_ReadyConfirmations_BedApprovalAudit
-- and dbo.BedSowingApprover (with its audit trigger) are dropped. The removal of the approver row is logged first.
-- Approvals already recorded stay valid and unchanged (no business row is touched); dbo.BedSowingAuditLog keeps its rows.
-- Refuses unless the trigger is exactly the version this migration created (schema-drift guard).
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52421, '2026-10-16_BedSowingApprover_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52422, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52423, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION BedApprover1016Rollback;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.BedSowingApprover', 'U') IS NULL AND OBJECT_ID('dbo.TR_ReadyConfirmations_BedApprovalAudit', 'TR') IS NULL
        PRINT '=== Not applied -- no-op ===';
    ELSE
    BEGIN
        IF OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor')) NOT LIKE N'%dbo.BedSowingApprover%'
            THROW 52424, 'TR_ReadyConfirmations_AssignedSupervisor is not the 2026-10-16 version -- schema drift, nothing changed.', 1;

        PRINT '=== Bed Sowing approvals recorded under the 2026-10-16 rule (they stay) ===';
        SELECT COUNT(*) AS BedBatchApprovalsLogged FROM dbo.BedSowingAuditLog WHERE Action = N'BedBatchApproved';

        EXEC (N'ALTER TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- SEED: only the assigned supervisor, never the recorder.
    -- CUTTING: the assigned supervisor, or any active user who may approve sowings
    -- (ReadyStock.Confirm through any role, or the full-access System Administrator role).
    -- 2026-10-13 BEDSOWING / TRANSPLANT (no supervisor assigned): any active user who may approve
    -- sowings; a BedSowing batch never by the user who recorded the transplant.
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               OUTER APPLY (SELECT CASE WHEN EXISTS (
                                SELECT 1
                                FROM dbo.IMSUsers u
                                INNER JOIN dbo.UserRoles ur ON ur.UserId = u.Id
                                INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                WHERE u.Id = i.ApprovedById
                                  AND ISNULL(u.IsActive, 0) = 1
                                  AND (COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''''), r.RoleName) = N''System Administrator''
                                       OR EXISTS (SELECT 1
                                                  FROM dbo.RolePermissions rp
                                                  INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                                  WHERE rp.RoleId = ur.RoleId
                                                    AND p.Code = N''ReadyStock.Confirm''))) THEN 1 ELSE 0 END AS MayApprove) m
               WHERE i.ApprovedById IS NULL
                  OR (sw.SourceType NOT IN (N''Cutting'', N''BedSowing'', N''Transplant'')
                      AND (sw.SupervisorId IS NULL
                           OR i.ApprovedById <> sw.SupervisorId
                           OR (sw.CreatedById IS NOT NULL AND i.ApprovedById = sw.CreatedById)))
                  OR (sw.SourceType = N''Cutting''
                      AND (sw.SupervisorId IS NULL
                           OR (i.ApprovedById <> sw.SupervisorId AND m.MayApprove = 0)))
                  OR (sw.SourceType IN (N''BedSowing'', N''Transplant'') AND m.MayApprove = 0)
                  OR (sw.SourceType = N''BedSowing'' AND sw.CreatedById IS NOT NULL AND i.ApprovedById = sw.CreatedById))
        THROW 50111, ''Only the assigned supervisor (or, for a cutting sowing, a user who may approve sowings) can approve it; a seed sowing never by the user who recorded it.'', 1;
END');
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor'))), 2)
           <> 'CD39F6D929BE6AE5BFBBDE1C75E70E77DF1A750826F0E19682415FDFE9EA3B39'
            THROW 52425, 'The restored TR_ReadyConfirmations_AssignedSupervisor is not byte-for-byte the 2026-10-13 version -- rolled back.', 1;

        IF OBJECT_ID('dbo.TR_ReadyConfirmations_BedApprovalAudit', 'TR') IS NOT NULL
            DROP TRIGGER dbo.TR_ReadyConfirmations_BedApprovalAudit;
        IF OBJECT_ID('dbo.BedSowingApprover', 'U') IS NOT NULL
        BEGIN
            DELETE FROM dbo.BedSowingApprover;          -- logged by TR_BedSowingApprover_Audit
            DROP TABLE dbo.BedSowingApprover;          -- drops its trigger too
        END
        PRINT '=== Rolled back to the 2026-10-13 approval rule ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION BedApprover1016Rollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION BedApprover1016Rollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION BedApprover1016Rollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
