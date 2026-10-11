-- ============================================================================
-- 2026-10-13_TraysToSowingApprovals_Rollback.sql -- undoes 2026-10-13_TraysToSowingApprovals.sql.
--
-- REFUSES once the new shapes are used: a tray batch with SourceType 'Transplant', a transplanting
-- whose output is a tray batch, a Bed Sowing tray batch without a supervisor, or a bed sowing
-- without the old Ready Date pair. Restores the two triggers BYTE-FOR-BYTE (verified by SHA-256),
-- CK_SeedSowings_Source / CK_RsTransplants_Status / CK_BedSowings_ReadyDate and NOT NULL.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52211, '2026-10-13_TraysToSowingApprovals_Rollback.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52212, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52213, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION TrayApprovalsRb1013;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF COL_LENGTH('dbo.ReadyStockTransplants', 'OutputSeedSowingId') IS NULL
        PRINT '=== Not applied -- nothing to undo ===';
    ELSE
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.SeedSowings WHERE SourceType = N'Transplant')
            THROW 52214, 'Tray batches made from transplanted Ready Stock exist -- rollback refused.', 1;
        DECLARE @n INT;
        EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.ReadyStockTransplants WHERE OutputSeedSowingId IS NOT NULL', N'@n INT OUTPUT', @n = @n OUTPUT;
        IF @n > 0 THROW 52215, 'Transplantings with a tray-batch output exist -- rollback refused.', 1;
        IF EXISTS (SELECT 1 FROM dbo.SeedSowings WHERE SourceType = N'BedSowing' AND SupervisorId IS NULL)
            THROW 52216, 'Bed Sowing tray batches without a supervisor exist -- rollback refused.', 1;
        IF EXISTS (SELECT 1 FROM dbo.BedSowings WHERE ReadyStockDays IS NULL OR ExpectedReadyDate IS NULL)
            THROW 52217, 'Bed sowings recorded without a bed Ready Date exist -- rollback refused.', 1;

        EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_SeedSowings_SupervisorRole
ON dbo.SeedSowings
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- SEED sowing (unchanged, INSERT only): assigned to an active Sowing Supervisor
    -- who is not the person recording it (the assigned supervisor is the only approver).
    IF NOT EXISTS (SELECT 1 FROM deleted)
       AND EXISTS (SELECT 1
               FROM inserted i
               WHERE i.SourceType <> N''Cutting''
                 AND (i.SupervisorId IS NULL
                  OR (i.CreatedById IS NOT NULL AND i.SupervisorId = i.CreatedById)
                  OR NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.SupervisorId
                                   AND ISNULL(u.IsActive, 0) = 1
                                   AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''''), r.RoleName) = N''Sowing Supervisor'')))
        THROW 50203, ''A sowing must be assigned to an active Sowing Supervisor other than the person recording it.'', 1;
    -- CUTTING TRAY sowing: recorded WITHOUT a supervisor (pending destination) is allowed.
    -- When it GETS one (insert, or NULL -> value at confirmation) the supervisor is an
    -- active user assigned to the sowing''s Area (any role) who holds the approval permission.
    IF EXISTS (SELECT 1
               FROM inserted i
               LEFT JOIN deleted d ON d.Id = i.Id
               WHERE i.SourceType = N''Cutting''
                 AND i.SupervisorId IS NOT NULL
                 AND (d.Id IS NULL OR ISNULL(d.SupervisorId, -1) <> i.SupervisorId)
                 AND (NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.SupervisorId
                                   AND ur.AreaId = i.AreaId
                                   AND ISNULL(u.IsActive, 0) = 1)
                  OR NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur2
                                 INNER JOIN dbo.RolePermissions rp ON rp.RoleId = ur2.RoleId
                                 INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                 WHERE ur2.UserId = i.SupervisorId
                                   AND p.Code = N''ReadyStock.Confirm'')))
        THROW 50203, ''A cutting tray sowing must be assigned to an active user of the sowing''''s Area who can approve sowings.'', 1;
    -- ...and its destination is a real, active Polyhouse of that Area (never Outlet).
    -- 2026-10-02: a real Polyhouse filed under a Main Office-type Area IS a
    -- valid destination (only that Area''s own Cutting Stock depot concept
    -- is excluded, never its named physical Polyhouses) -- MainOffice
    -- removed from this exclusion, Outlet unchanged.
    IF EXISTS (SELECT 1
               FROM inserted i
               LEFT JOIN deleted d ON d.Id = i.Id
               WHERE i.SourceType = N''Cutting''
                 AND i.SupervisorId IS NOT NULL
                 AND (d.Id IS NULL OR ISNULL(d.SupervisorId, -1) <> i.SupervisorId)
                 AND NOT EXISTS (SELECT 1
                                 FROM dbo.Polyhouses ph
                                 INNER JOIN dbo.Area a ON a.Id = ph.AreaId
                                 WHERE ph.Id = i.PolyhouseId
                                   AND ph.AreaId = i.AreaId
                                   AND a.IsActive = 1
                                   AND (a.AreaType IS NULL OR a.AreaType <> N''Outlet'')))
        THROW 50204, ''A cutting tray sowing must be sown at an active Polyhouse of its Area (not Outlet).'', 1;
END');
        EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- SEED: only the assigned supervisor, never the recorder.
    -- CUTTING: the assigned supervisor, or any active user who may approve sowings
    -- (ReadyStock.Confirm through any role, or the full-access System Administrator role).
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE i.ApprovedById IS NULL
                  OR sw.SupervisorId IS NULL
                  OR (sw.SourceType <> N''Cutting''
                      AND (i.ApprovedById <> sw.SupervisorId
                           OR (sw.CreatedById IS NOT NULL AND i.ApprovedById = sw.CreatedById)))
                  OR (sw.SourceType = N''Cutting''
                      AND i.ApprovedById <> sw.SupervisorId
                      AND NOT EXISTS (SELECT 1
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
                                                          AND p.Code = N''ReadyStock.Confirm'')))))
        THROW 50111, ''Only the assigned supervisor (or, for a cutting sowing, a user who may approve sowings) can approve it; a seed sowing never by the user who recorded it.'', 1;
END');
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_SeedSowings_SupervisorRole'))), 2)
               <> '7F27B6BAE19A2FC9476E4C42500F84F5CF428D42D40EC966C13B91FA118E994A'
           OR CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor'))), 2)
               <> '45BB189258FE38301D126860B6482081FE53219CC6741D1B39D0E31E934145EF'
            THROW 52218, 'The restored triggers do not match the original versions -- rollback refused.', 1;
        PRINT '=== Both triggers restored byte-for-byte ===';

        -- Ready Date gate: back to the 2026-10-12 version (BedSowing only), firing first.
        DROP TRIGGER dbo.TR_ReadyConfirmations_BedReadyDate;
        EXEC (N'CREATE TRIGGER dbo.TR_ReadyConfirmations_BedReadyDate ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- India business date (IST, +05:30, no daylight saving) -- the same day AppClock.Today gives.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE sw.SourceType = N''BedSowing''
                 AND (sw.ExpectedReadyDate IS NULL OR CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), ''+05:30'') AS date) < sw.ExpectedReadyDate))
        THROW 52178, ''A tray batch from a Bed Sowing cannot be approved before its Ready Date.'', 1;
END');
        EXEC sp_settriggerorder @triggername = N'dbo.TR_ReadyConfirmations_BedReadyDate', @order = N'First', @stmttype = N'INSERT';
        IF CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate'))), 2)
               <> '393EB10870B6B3EFBE18E56D71258793F0BED489C8FBA8F9E25C7D757BA93B65'
            THROW 52218, 'The restored TR_ReadyConfirmations_BedReadyDate does not match the original version -- rollback refused.', 1;
        PRINT '=== TR_ReadyConfirmations_BedReadyDate restored byte-for-byte (fires first) ===';

        ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT CK_RsTransplants_Status;
        EXEC (N'ALTER TABLE dbo.ReadyStockTransplants WITH CHECK ADD CONSTRAINT CK_RsTransplants_Status CHECK (
            [Status]=N''Completed'' AND [ReversedAt] IS NULL AND [ReversedById] IS NULL AND [ReversalReason] IS NULL
            OR [Status]=N''Reversed'' AND [OutputReadyStockId] IS NOT NULL AND [ReversedAt] IS NOT NULL AND [ReversedById] IS NOT NULL AND [ReversalReason] IS NOT NULL);');
        ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT CK_RsTransplants_OneOutput;
        DROP INDEX UX_RsTransplants_OutputSowing ON dbo.ReadyStockTransplants;
        ALTER TABLE dbo.ReadyStockTransplants DROP CONSTRAINT FK_RsTransplants_OutputSowing;
        EXEC (N'ALTER TABLE dbo.ReadyStockTransplants DROP COLUMN OutputSeedSowingId;');

        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_Source;
        EXEC (N'ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_Source CHECK (
               ([SourceType]=N''Seed'' AND [SourceSeedStockId] IS NOT NULL AND [SourceCuttingStockId] IS NULL)
            OR ([SourceType]=N''Cutting'' AND [SourceCuttingStockId] IS NOT NULL AND [SourceSeedStockId] IS NULL)
            OR ([SourceType]=N''BedSowing'' AND [SourceSeedStockId] IS NULL AND [SourceCuttingStockId] IS NULL));');

        ALTER TABLE dbo.BedSowings DROP CONSTRAINT CK_BedSowings_ReadyDate;
        EXEC (N'ALTER TABLE dbo.BedSowings ALTER COLUMN ReadyStockDays INT NOT NULL;');
        EXEC (N'ALTER TABLE dbo.BedSowings ALTER COLUMN ExpectedReadyDate DATE NOT NULL;');
        EXEC (N'ALTER TABLE dbo.BedSowings WITH CHECK ADD CONSTRAINT CK_BedSowings_ReadyDate CHECK (ReadyStockDays > 0 AND ExpectedReadyDate = DATEADD(DAY, ReadyStockDays, SowingDate));');
        PRINT '=== 2026-10-13 shapes removed ===';
    END
    SELECT name, definition FROM sys.check_constraints WHERE name IN (N'CK_SeedSowings_Source', N'CK_RsTransplants_Status', N'CK_BedSowings_ReadyDate');
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TrayApprovalsRb1013;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TrayApprovalsRb1013;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TrayApprovalsRb1013;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
