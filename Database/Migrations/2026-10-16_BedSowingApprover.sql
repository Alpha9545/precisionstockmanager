-- ============================================================================
-- 2026-10-16_BedSowingApprover.sql
--
-- WHY (owner requirement 2026-10-12, "Bed Sowing tray batches must be approved by Maya Shinde"):
--   Until now a tray batch transplanted from a Bed Sowing (SeedSowings.SourceType 'BedSowing') could be
--   approved in Sowing Approvals by ANY active user who may approve sowings (ReadyStock.Confirm or the
--   System Administrator role) -- but never by the user who recorded the transplant. Only Maya Shinde
--   records bed transplants (TR_BedSowingOperations_Transplanter), so she could never approve them.
--   Now ONE designated person approves them, and that person may approve a batch she transplanted herself
--   (a deliberate maker-checker exception for Bed Sowing batches ONLY -- every other batch type keeps its rule).
--
-- WHAT:
--   1. dbo.BedSowingApprover -- at most ONE row (Id = 1): the IMSUsers.Id of the Bed Sowing approver.
--      Seeded with -v BED_APPROVER_USER_ID=<IMSUsers.Id>, CHECKED here: that user must be active, have
--      Username 'Maya' and Name 'Maya Shinde', and be the ONLY active user named 'Maya Shinde'. A mismatch
--      changes nothing (the person is never chosen by a display name alone, and never by an Id alone).
--      TR_BedSowingApprover_Audit: every insert / change / delete of the row is written to dbo.BedSowingAuditLog.
--   2. TR_ReadyConfirmations_AssignedSupervisor (replaced only if it is byte-for-byte the 2026-10-13 version):
--        BedSowing  -> ONLY the active designated approver; the recorder exclusion is lifted for this type only.
--                      No approver row (or an inactive approver) -> no Bed Sowing batch can be approved.
--        Transplant, Cutting, Seed -> exactly as before.
--   3. TR_ReadyConfirmations_BedApprovalAudit (new): every approval of a Bed Sowing batch is written to
--      dbo.BedSowingAuditLog (who, which batch, and whether the approver also recorded the transplant).
--   TR_ReadyConfirmations_BedReadyDate (no approval before the Ready Date) is NOT changed and still fires first.
--
-- NOT changed: no sowing, approval, stock or ledger row; no role, permission or user assignment.
-- Requires: 2026-10-12_BedSowing.sql and 2026-10-13_TraysToSowingApprovals.sql.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-16_BedSowingApprover_Rollback.sql.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> BED_APPROVER_USER_ID=<IMSUsers.Id> -i <this file>
--       (local PlantsIMS2_Test: BED_APPROVER_USER_ID=9)
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52401, '2026-10-16_BedSowingApprover.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52402, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52403, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION BedApprover1016;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.BedSowings', 'U') IS NULL OR OBJECT_ID('dbo.BedSowingAuditLog', 'U') IS NULL
        THROW 52404, 'Run 2026-10-12_BedSowing.sql first.', 1;
    IF COL_LENGTH('dbo.ReadyStockTransplants', 'OutputSeedSowingId') IS NULL
        THROW 52405, 'Run 2026-10-13_TraysToSowingApprovals.sql first.', 1;

    DECLARE @Applied BIT = CASE WHEN OBJECT_ID('dbo.BedSowingApprover', 'U') IS NOT NULL
                                 AND OBJECT_ID('dbo.TR_ReadyConfirmations_BedApprovalAudit', 'TR') IS NOT NULL THEN 1 ELSE 0 END;
    DECLARE @UserId INT = TRY_CAST(N'$(BED_APPROVER_USER_ID)' AS INT);

    IF @Applied = 0
    BEGIN
        -- Schema-drift guard: exactly the trigger version 2026-10-13_TraysToSowingApprovals.sql created.
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor'))), 2)
           <> 'CD39F6D929BE6AE5BFBBDE1C75E70E77DF1A750826F0E19682415FDFE9EA3B39'
            THROW 52406, 'TR_ReadyConfirmations_AssignedSupervisor is not the expected 2026-10-13 version -- schema drift, nothing changed.', 1;
        IF OBJECT_ID('dbo.BedSowingApprover', 'U') IS NOT NULL OR OBJECT_ID('dbo.TR_ReadyConfirmations_BedApprovalAudit', 'TR') IS NOT NULL
            THROW 52407, 'Part of this migration already exists while it is not complete -- inspect the database, nothing changed.', 1;

        -- The approver: chosen by Id AND verified by Username + Name + uniqueness of the name.
        IF @UserId IS NULL
            THROW 52408, 'Pass -v BED_APPROVER_USER_ID=<IMSUsers.Id of Maya Shinde>.', 1;
        IF NOT EXISTS (SELECT 1 FROM dbo.IMSUsers WHERE Id = @UserId AND Username = N'Maya' AND Name = N'Maya Shinde' AND ISNULL(IsActive, 0) = 1)
            THROW 52409, 'BED_APPROVER_USER_ID is not the active user Maya / Maya Shinde -- nothing changed.', 1;
        IF (SELECT COUNT(*) FROM dbo.IMSUsers WHERE Name = N'Maya Shinde' AND ISNULL(IsActive, 0) = 1) <> 1
            THROW 52410, 'More than one active user is named Maya Shinde -- the approver is ambiguous, nothing changed.', 1;

        PRINT '=== Before ===';
        SELECT u.Id, u.Username, u.Name, u.IsActive FROM dbo.IMSUsers u WHERE u.Id = @UserId;
        SELECT COUNT(*) AS BedSowingBatchesWaitingForApproval FROM dbo.SeedSowings WHERE SourceType = N'BedSowing' AND Status = N'Sown';

        ------------------------------------------------------------------
        -- 1. dbo.BedSowingApprover (one row) + audit
        ------------------------------------------------------------------
        CREATE TABLE dbo.BedSowingApprover
        (
            Id         INT           NOT NULL CONSTRAINT PK_BedSowingApprover PRIMARY KEY
                                              CONSTRAINT CK_BedSowingApprover_OneRow CHECK (Id = 1),
            UserId     INT           NOT NULL CONSTRAINT FK_BedSowingApprover_User REFERENCES dbo.IMSUsers (Id),
            AssignedAt DATETIME2(3)  NOT NULL CONSTRAINT DF_BedSowingApprover_AssignedAt DEFAULT (SYSUTCDATETIME()),
            AssignedBy NVARCHAR(128) NOT NULL CONSTRAINT DF_BedSowingApprover_AssignedBy DEFAULT (ORIGINAL_LOGIN())
        );
        EXEC (N'
CREATE TRIGGER dbo.TR_BedSowingApprover_Audit ON dbo.BedSowingApprover
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- 2026-10-16: who approves Bed Sowing tray batches is changed only by a reviewed database script; every change is logged.
    INSERT INTO dbo.BedSowingAuditLog (Action, TargetUserId, Details)
    SELECT N''BedApproverAssigned'', i.UserId, N''BedSowingApprover set'' FROM inserted i WHERE NOT EXISTS (SELECT 1 FROM deleted)
    UNION ALL
    SELECT N''BedApproverChanged'', i.UserId, CONCAT(N''BedSowingApprover: user '', d.UserId, N'' -> '', i.UserId)
    FROM inserted i JOIN deleted d ON d.Id = i.Id WHERE i.UserId <> d.UserId
    UNION ALL
    SELECT N''BedApproverRemoved'', d.UserId, N''BedSowingApprover removed'' FROM deleted d WHERE NOT EXISTS (SELECT 1 FROM inserted);
END');
        INSERT INTO dbo.BedSowingApprover (Id, UserId) VALUES (1, @UserId);
        PRINT '=== 1. dbo.BedSowingApprover + TR_BedSowingApprover_Audit (approver set) ===';

        ------------------------------------------------------------------
        -- 2. Approval authority: Bed Sowing batches by the designated approver only
        ------------------------------------------------------------------
        EXEC (N'ALTER TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- SEED: only the assigned supervisor, never the recorder.
    -- CUTTING: the assigned supervisor, or any active user who may approve sowings
    -- (ReadyStock.Confirm through any role, or the full-access System Administrator role).
    -- 2026-10-13 TRANSPLANT (no supervisor assigned): any active user who may approve sowings.
    -- 2026-10-16 BEDSOWING: ONLY the active user named in dbo.BedSowingApprover (Maya Shinde) -- not every
    -- approver, not every administrator; she may approve a batch she transplanted herself (Bed Sowing only).
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE sw.SourceType = N''BedSowing''
                 AND (i.ApprovedById IS NULL
                      OR NOT EXISTS (SELECT 1 FROM dbo.BedSowingApprover ba
                                     INNER JOIN dbo.IMSUsers u ON u.Id = ba.UserId AND ISNULL(u.IsActive, 0) = 1
                                     WHERE ba.Id = 1 AND ba.UserId = i.ApprovedById)))
        THROW 52411, ''Only the designated Bed Sowing approver can approve a tray batch from a Bed Sowing.'', 1;
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
               WHERE sw.SourceType <> N''BedSowing''
                 AND (i.ApprovedById IS NULL
                  OR (sw.SourceType NOT IN (N''Cutting'', N''Transplant'')
                      AND (sw.SupervisorId IS NULL
                           OR i.ApprovedById <> sw.SupervisorId
                           OR (sw.CreatedById IS NOT NULL AND i.ApprovedById = sw.CreatedById)))
                  OR (sw.SourceType = N''Cutting''
                      AND (sw.SupervisorId IS NULL
                           OR (i.ApprovedById <> sw.SupervisorId AND m.MayApprove = 0)))
                  OR (sw.SourceType = N''Transplant'' AND m.MayApprove = 0)))
        THROW 50111, ''Only the assigned supervisor (or, for a cutting sowing, a user who may approve sowings) can approve it; a seed sowing never by the user who recorded it.'', 1;
END');
        PRINT '=== 2. TR_ReadyConfirmations_AssignedSupervisor: Bed Sowing batches by the designated approver only ===';

        ------------------------------------------------------------------
        -- 3. Audit of every Bed Sowing batch approval
        ------------------------------------------------------------------
        EXEC (N'
CREATE TRIGGER dbo.TR_ReadyConfirmations_BedApprovalAudit ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- 2026-10-16: who approved which Bed Sowing tray batch, and whether the approver also recorded its transplant
    -- (the Bed Sowing maker-checker exception) -- append-only (TR on dbo.BedSowingAuditLog).
    INSERT INTO dbo.BedSowingAuditLog (Action, BedSowingId, UserId, UserName, Details)
    SELECT N''BedBatchApproved'', sw.BedSowingId, i.ApprovedById, u.Name,
           CONCAT(N''ReadyConfirmation '', i.Id, N'' of tray batch '', sw.SowingCode, N'': '', i.ConfirmedQuantity, N'' ready, '', i.WastageQuantity, N'' wasted'',
                  CASE WHEN sw.CreatedById = i.ApprovedById THEN N''; the approver also recorded the transplant (Bed Sowing exception)'' ELSE N'''' END)
    FROM inserted i
    INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId AND sw.SourceType = N''BedSowing''
    LEFT JOIN dbo.IMSUsers u ON u.Id = i.ApprovedById;
END');
        PRINT '=== 3. TR_ReadyConfirmations_BedApprovalAudit ===';
    END
    ELSE PRINT '=== Already applied -- no-op ===';

    PRINT '=== After ===';
    SELECT ba.Id, ba.UserId, u.Username, u.Name, ba.AssignedAt, ba.AssignedBy
    FROM dbo.BedSowingApprover ba JOIN dbo.IMSUsers u ON u.Id = ba.UserId;
    SELECT name, is_disabled FROM sys.triggers WHERE parent_id = OBJECT_ID('dbo.ReadyConfirmations') ORDER BY name;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION BedApprover1016;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION BedApprover1016;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION BedApprover1016;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
