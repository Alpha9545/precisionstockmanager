-- ============================================================================
-- 2026-10-12_BedSowing.sql
--
-- WHY (owner decisions 2026-10-10): Bed Sowing at the Main Office "Facility 1" polyhouse.
--   1. Seed is sown into a BED (no cavity, no trays) -- deducted from a Main Office seed lot.
--   2. The bed waits for transplanting ("Pending transplant").
--   3. ONLY the Bed Sowing Transplanter (Maya Shinde) moves bed seedlings into cavity trays,
--      in part or in full. Each transplant takes empty trays (Tray Stock) and the growing
--      material (Cocopeat / Pitmos, existing kg-per-tray rule) and creates ONE growing tray batch
--      (dbo.SeedSowings, SourceType 'BedSowing') that goes through the EXISTING Sowing Approval
--      -> Ready Stock -> booking / dispatch. No Ready Stock is created before that approval.
--   4. Ready Date = ORIGINAL bed sowing date + the variety's Ready Days (dbo.PlantSpecies.
--      ReadyStockDays), snapshotted on the bed and on every tray batch made from it.
--   Conservation per bed: Quantity = Remaining + Transplanted + Lost.
--
-- WHAT:
--   dbo.BedSowingLocations   -- the Polyhouse(s) where bed sowing / bed transplanting is allowed.
--                               Seeded with ONE polyhouse, chosen by Id AND verified by name AND by
--                               its Area being an active MainOffice Area (never by name alone).
--   dbo.BedSowings           -- one row per bed sowing (code BS-YYYY-NNNNN).
--   dbo.BedSowingOperations  -- immutable transplant / loss rows (code BT-YYYY-NNNNN); a mistake
--                               is REVERSED (Status Reversed + who/when/why), never edited or deleted.
--   dbo.SeedSowings.BedSowingId + SourceType 'BedSowing' (CK_SeedSowings_Source widened).
--   Permission BedSowing.Transplant, role "Bed Sowing Transplanter", assigned to ONE user for the
--   polyhouse's Area -- the user chosen by Id AND verified by Username (no Id in C# code).
--   Logged in dbo.RolePermissionChangeLog.
--   2026-10-12 review additions:
--     * dbo.MaterialTransactions.TransactionDate (business date; NULL = recorded time, every existing row).
--     * TR_BedSowingOperations_Transplanter: a transplant or bed loss is recorded ONLY by an active user
--       holding BedSowing.Transplant for the bed's Area (Maya Shinde). No exception path: a System
--       Administrator cannot record one either. Administrators manage and reverse (audited).
--     * dbo.BedSowingAuditLog (append-only): administrator reversals, and every change of the
--       Transplanter role / permission (with the database login).
--     * TR_UserRoles_BedTransplanter: at most ONE user holds BedSowing.Transplant.
--     * TR_RolePermissions_BedTransplant: the permission only on the Bed Sowing Transplanter role.
--     * TR_ReadyConfirmations_BedReadyDate: a bed tray batch is never approved before its Ready Date.
-- No existing stock, ledger, sowing or approval row is changed.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the server named with
-- -v TARGET_SERVER=<@@SERVERNAME>. One transaction. Idempotent (re-run = no-op).
-- Undo: 2026-10-12_BedSowing_Rollback.sql (refuses once any bed sowing exists).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME>
--         BED_POLYHOUSE_ID=<Polyhouses.Id> BED_TRANSPLANTER_USER_ID=<IMSUsers.Id> -i <this file>
--       (local PlantsIMS2_Test: BED_POLYHOUSE_ID=6 BED_TRANSPLANTER_USER_ID=9). The Ids are CHECKED against
--       the expected names below (sqlcmd cannot pass values with spaces reliably, so the names are
--       constants of this script); a mismatch changes nothing.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52161, '2026-10-12_BedSowing.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52162, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52163, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION BedSowing1012;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    -- ---- Prerequisites ------------------------------------------------------
    IF OBJECT_ID('dbo.SeedSowings', 'U') IS NULL OR OBJECT_ID('dbo.ReadyConfirmations', 'U') IS NULL
       OR OBJECT_ID('dbo.TrayStock', 'U') IS NULL OR OBJECT_ID('dbo.MaterialTransactions', 'U') IS NULL
        THROW 52164, 'Sowing, approval, tray stock and material stock tables are required.', 1;
    IF OBJECT_ID('dbo.TrayTransfers', 'U') IS NULL
        THROW 52165, 'Run 2026-10-10_TrayTransfers.sql (and the 2026-10-11 migrations) first.', 1;

    -- ---- The location: chosen by Id, verified by name AND by Area type ---------
    DECLARE @PolyhouseId INT = $(BED_POLYHOUSE_ID);
    DECLARE @PolyhouseName NVARCHAR(200) = N'Facility 1 Main office';   -- expected name (owner: "Facility 1 Main Office")
    DECLARE @AreaId INT, @AreaName NVARCHAR(200);
    SELECT @AreaId = a.Id, @AreaName = a.Name
    FROM dbo.Polyhouses p
    JOIN dbo.Area a ON a.Id = p.AreaId
    WHERE p.Id = @PolyhouseId
      AND LTRIM(RTRIM(p.Name)) = LTRIM(RTRIM(@PolyhouseName))
      AND a.AreaType = N'MainOffice' AND a.IsActive = 1;
    IF @AreaId IS NULL
        THROW 52166, 'BED_POLYHOUSE_ID is not the Polyhouse "Facility 1 Main office" of an active MainOffice Area -- nothing changed.', 1;
    PRINT CONCAT('=== Bed Sowing location: Polyhouse ', @PolyhouseId, ' "', @PolyhouseName, '" in Area ', @AreaId, ' "', @AreaName, '" (MainOffice, active) ===');

    -- ---- The transplanter: chosen by Id, verified by Username, must be active ---
    DECLARE @UserId INT = $(BED_TRANSPLANTER_USER_ID);
    DECLARE @Username NVARCHAR(200) = N'Maya', @UserDisplay NVARCHAR(200) = N'Maya Shinde';   -- expected login and name
    IF NOT EXISTS (SELECT 1 FROM dbo.IMSUsers u
                   WHERE u.Id = @UserId AND u.Username = @Username AND LTRIM(RTRIM(u.Name)) = @UserDisplay AND ISNULL(u.IsActive, 0) = 1)
        THROW 52167, 'BED_TRANSPLANTER_USER_ID is not the active user Maya (Maya Shinde) -- nothing changed.', 1;
    PRINT CONCAT('=== Bed Sowing Transplanter: user ', @UserId, ' "', @Username, '" (', @UserDisplay, ') ===');

    -- ---- Guard: CK_SeedSowings_Source must be exactly the known definition ----
    DECLARE @SourceCk NVARCHAR(MAX) = (SELECT definition FROM sys.check_constraints
                                       WHERE parent_object_id = OBJECT_ID('dbo.SeedSowings') AND name = N'CK_SeedSowings_Source');
    DECLARE @SourceCkOld NVARCHAR(MAX) = N'([SourceType]=N''Seed'' AND [SourceSeedStockId] IS NOT NULL AND [SourceCuttingStockId] IS NULL OR [SourceType]=N''Cutting'' AND [SourceCuttingStockId] IS NOT NULL AND [SourceSeedStockId] IS NULL)';
    IF COL_LENGTH('dbo.SeedSowings', 'BedSowingId') IS NULL AND ISNULL(@SourceCk, N'') <> @SourceCkOld
        THROW 52168, 'CK_SeedSowings_Source is not the expected definition -- schema drift, nothing changed.', 1;

    PRINT '=== Before: rows that must not change ===';
    SELECT 'SeedSowings' AS T, COUNT(*) AS Rows FROM dbo.SeedSowings
    UNION ALL SELECT 'ReadyStock', COUNT(*) FROM dbo.ReadyStock
    UNION ALL SELECT 'SeedStockTransactions', COUNT(*) FROM dbo.SeedStockTransactions
    UNION ALL SELECT 'TrayStockTransactions', COUNT(*) FROM dbo.TrayStockTransactions
    UNION ALL SELECT 'MaterialTransactions', COUNT(*) FROM dbo.MaterialTransactions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles;

    -- ---- Tables -------------------------------------------------------------
    IF OBJECT_ID('dbo.BedSowingLocations', 'U') IS NULL
    BEGIN
        EXEC (N'CREATE TABLE dbo.BedSowingLocations (
            PolyhouseId INT NOT NULL CONSTRAINT PK_BedSowingLocations PRIMARY KEY
                        CONSTRAINT FK_BedSowingLocations_Polyhouse REFERENCES dbo.Polyhouses (Id),
            AreaId      INT NOT NULL CONSTRAINT FK_BedSowingLocations_Area REFERENCES dbo.Area (Id),
            IsActive    BIT NOT NULL CONSTRAINT DF_BedSowingLocations_IsActive DEFAULT 1,
            Notes       NVARCHAR(400) NULL,
            CreatedDate DATETIME2 NOT NULL CONSTRAINT DF_BedSowingLocations_CreatedDate DEFAULT SYSUTCDATETIME(),
            CreatedBy   NVARCHAR(200) NOT NULL);');
        PRINT '=== dbo.BedSowingLocations created ===';
    END
    IF NOT EXISTS (SELECT 1 FROM dbo.BedSowingLocations WHERE PolyhouseId = @PolyhouseId)
    BEGIN
        INSERT INTO dbo.BedSowingLocations (PolyhouseId, AreaId, IsActive, Notes, CreatedBy)
        VALUES (@PolyhouseId, @AreaId, 1, N'Owner decision 2026-10-10: bed sowing and bed transplanting at Facility 1 Main Office', N'2026-10-12_BedSowing.sql');
        PRINT '=== Bed Sowing location added ===';
    END

    IF OBJECT_ID('dbo.BedSowings', 'U') IS NULL
    BEGIN
        EXEC (N'CREATE TABLE dbo.BedSowings (
            Id                   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BedSowings PRIMARY KEY,
            BedSowingCode        NVARCHAR(30)  NOT NULL CONSTRAINT UQ_BedSowings_Code UNIQUE,
            SpeciesId            INT           NOT NULL CONSTRAINT FK_BedSowings_Species REFERENCES dbo.PlantSpecies (Id),
            SeedStockId          INT           NOT NULL CONSTRAINT FK_BedSowings_SeedStock REFERENCES dbo.SeedStock (Id),
            SeedLotNo            NVARCHAR(100) NOT NULL,
            SeedSourceId         INT           NULL,
            AreaId               INT           NOT NULL CONSTRAINT FK_BedSowings_Area REFERENCES dbo.Area (Id),
            PolyhouseId          INT           NOT NULL CONSTRAINT FK_BedSowings_Polyhouse REFERENCES dbo.Polyhouses (Id),
            SowingDate           DATE          NOT NULL,
            Quantity             DECIMAL(18,2) NOT NULL,
            ReadyStockDays       INT           NOT NULL,
            ExpectedReadyDate    DATE          NOT NULL,
            TransplantedQuantity DECIMAL(18,2) NOT NULL CONSTRAINT DF_BedSowings_Transplanted DEFAULT 0,
            LostQuantity         DECIMAL(18,2) NOT NULL CONSTRAINT DF_BedSowings_Lost DEFAULT 0,
            Status               NVARCHAR(30)  NOT NULL CONSTRAINT DF_BedSowings_Status DEFAULT N''PendingTransplant'',
            Remarks              NVARCHAR(500) NULL,
            SubmissionToken      UNIQUEIDENTIFIER NULL,
            CreatedDate          DATETIME2     NOT NULL CONSTRAINT DF_BedSowings_CreatedDate DEFAULT SYSUTCDATETIME(),
            CreatedBy            NVARCHAR(200) NULL,
            CreatedById          INT           NULL,
            ModifiedDate         DATETIME2     NULL,
            ModifiedBy           NVARCHAR(200) NULL,
            CancelledDate        DATETIME2     NULL,
            CancelledById        INT           NULL,
            CancelReason         NVARCHAR(500) NULL,
            CONSTRAINT CK_BedSowings_Quantities CHECK (
                Quantity > 0 AND Quantity = FLOOR(Quantity)
                AND TransplantedQuantity >= 0 AND TransplantedQuantity = FLOOR(TransplantedQuantity)
                AND LostQuantity >= 0 AND LostQuantity = FLOOR(LostQuantity)
                AND TransplantedQuantity + LostQuantity <= Quantity),
            CONSTRAINT CK_BedSowings_ReadyDate CHECK (ReadyStockDays > 0 AND ExpectedReadyDate = DATEADD(DAY, ReadyStockDays, SowingDate)),
            CONSTRAINT CK_BedSowings_Status CHECK (
                   (Status = N''PendingTransplant''     AND TransplantedQuantity + LostQuantity = 0 AND CancelledDate IS NULL)
                OR (Status = N''PartiallyTransplanted'' AND TransplantedQuantity + LostQuantity > 0
                                                        AND TransplantedQuantity + LostQuantity < Quantity AND CancelledDate IS NULL)
                OR (Status = N''Completed''             AND TransplantedQuantity + LostQuantity = Quantity AND CancelledDate IS NULL)
                OR (Status = N''Cancelled''             AND TransplantedQuantity + LostQuantity = 0 AND CancelledDate IS NOT NULL
                                                        AND LEN(LTRIM(RTRIM(ISNULL(CancelReason, N'''')))) > 0)));');
        EXEC (N'CREATE UNIQUE INDEX UX_BedSowings_SubmissionToken ON dbo.BedSowings (SubmissionToken) WHERE SubmissionToken IS NOT NULL;');
        EXEC (N'CREATE INDEX IX_BedSowings_Status ON dbo.BedSowings (Status) INCLUDE (SowingDate, AreaId, SpeciesId);');
        PRINT '=== dbo.BedSowings created ===';
    END

    IF OBJECT_ID('dbo.BedSowingOperations', 'U') IS NULL
    BEGIN
        EXEC (N'CREATE TABLE dbo.BedSowingOperations (
            Id                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BedSowingOperations PRIMARY KEY,
            OperationCode     NVARCHAR(30)  NOT NULL CONSTRAINT UQ_BedSowingOperations_Code UNIQUE,
            BedSowingId       INT           NOT NULL CONSTRAINT FK_BedSowingOperations_Bed REFERENCES dbo.BedSowings (Id),
            OperationType     NVARCHAR(20)  NOT NULL,
            OperationDate     DATE          NOT NULL,
            Quantity          DECIMAL(18,2) NOT NULL,
            CavityType        NVARCHAR(30)  NULL,
            OutputTrays       INT           NULL,
            TargetAreaId      INT           NULL CONSTRAINT FK_BedSowingOperations_Area REFERENCES dbo.Area (Id),
            TargetPolyhouseId INT           NULL CONSTRAINT FK_BedSowingOperations_Polyhouse REFERENCES dbo.Polyhouses (Id),
            SeedSowingId      INT           NULL CONSTRAINT FK_BedSowingOperations_SeedSowing REFERENCES dbo.SeedSowings (Id),
            Reason            NVARCHAR(500) NULL,
            Remarks           NVARCHAR(500) NULL,
            Status            NVARCHAR(20)  NOT NULL CONSTRAINT DF_BedSowingOperations_Status DEFAULT N''Active'',
            SubmissionToken   UNIQUEIDENTIFIER NULL,
            CreatedDate       DATETIME2     NOT NULL CONSTRAINT DF_BedSowingOperations_CreatedDate DEFAULT SYSUTCDATETIME(),
            CreatedBy         NVARCHAR(200) NULL,
            CreatedById       INT           NULL,
            ReversedDate      DATETIME2     NULL,
            ReversedBy        NVARCHAR(200) NULL,
            ReversedById      INT           NULL,
            ReversalReason    NVARCHAR(500) NULL,
            CONSTRAINT CK_BedSowingOperations_Type CHECK (OperationType IN (N''Transplant'', N''Loss'')),
            CONSTRAINT CK_BedSowingOperations_Quantity CHECK (Quantity > 0 AND Quantity = FLOOR(Quantity)),
            CONSTRAINT CK_BedSowingOperations_Shape CHECK (
                   (OperationType = N''Transplant''
                    AND CavityType IN (N''150 Cavity'', N''102 Cavity'', N''42 Cavity'', N''24 Cavity'', N''9 Cavity'')
                    AND OutputTrays >= 1
                    AND Quantity = OutputTrays * CASE CavityType WHEN N''9 Cavity'' THEN 9 WHEN N''24 Cavity'' THEN 24 WHEN N''42 Cavity'' THEN 42
                                                                 WHEN N''102 Cavity'' THEN 102 WHEN N''150 Cavity'' THEN 150 END
                    AND TargetAreaId IS NOT NULL AND TargetPolyhouseId IS NOT NULL AND SeedSowingId IS NOT NULL)
                OR (OperationType = N''Loss''
                    AND CavityType IS NULL AND OutputTrays IS NULL AND TargetAreaId IS NULL AND TargetPolyhouseId IS NULL AND SeedSowingId IS NULL
                    AND LEN(LTRIM(RTRIM(ISNULL(Reason, N'''')))) > 0)),
            CONSTRAINT CK_BedSowingOperations_Status CHECK (
                   (Status = N''Active''   AND ReversedDate IS NULL AND ReversedById IS NULL AND ReversalReason IS NULL)
                OR (Status = N''Reversed'' AND ReversedDate IS NOT NULL AND LEN(LTRIM(RTRIM(ISNULL(ReversalReason, N'''')))) > 0)));');
        EXEC (N'CREATE UNIQUE INDEX UX_BedSowingOperations_SubmissionToken ON dbo.BedSowingOperations (SubmissionToken) WHERE SubmissionToken IS NOT NULL;');
        EXEC (N'CREATE UNIQUE INDEX UX_BedSowingOperations_SeedSowing ON dbo.BedSowingOperations (SeedSowingId) WHERE SeedSowingId IS NOT NULL;');
        EXEC (N'CREATE INDEX IX_BedSowingOperations_Bed ON dbo.BedSowingOperations (BedSowingId, Status) INCLUDE (OperationType, Quantity);');
        PRINT '=== dbo.BedSowingOperations created ===';
    END

    -- ---- Triggers: immutable rows, totals = active operations ------------------
    IF OBJECT_ID('dbo.TR_BedSowings_Guard', 'TR') IS NULL
    BEGIN
        EXEC (N'CREATE TRIGGER dbo.TR_BedSowings_Guard ON dbo.BedSowings
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- What was sown is fixed (cancel and record again instead).
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE i.BedSowingCode <> d.BedSowingCode OR i.SpeciesId <> d.SpeciesId OR i.SeedStockId <> d.SeedStockId
                  OR i.AreaId <> d.AreaId OR i.PolyhouseId <> d.PolyhouseId OR i.SowingDate <> d.SowingDate
                  OR i.Quantity <> d.Quantity OR i.ReadyStockDays <> d.ReadyStockDays OR i.ExpectedReadyDate <> d.ExpectedReadyDate
                  OR ISNULL(i.CreatedById, -1) <> ISNULL(d.CreatedById, -1)
                  OR ISNULL(CONVERT(NCHAR(36), i.SubmissionToken), N'''') <> ISNULL(CONVERT(NCHAR(36), d.SubmissionToken), N'''')
                  OR (d.Status = N''Cancelled'' AND i.Status <> N''Cancelled''))
        THROW 52171, ''The variety, seed lot, location, sowing date, quantity and Ready Date of a bed sowing cannot be changed, and a cancelled bed sowing stays cancelled.'', 1;
    -- Conservation: Transplanted / Lost are exactly the ACTIVE operations of the bed.
    IF EXISTS (SELECT 1 FROM inserted i
               OUTER APPLY (SELECT ISNULL(SUM(CASE WHEN o.OperationType = N''Transplant'' THEN o.Quantity END), 0) AS T,
                                   ISNULL(SUM(CASE WHEN o.OperationType = N''Loss'' THEN o.Quantity END), 0) AS L
                            FROM dbo.BedSowingOperations o WHERE o.BedSowingId = i.Id AND o.Status = N''Active'') s
               WHERE i.TransplantedQuantity <> s.T OR i.LostQuantity <> s.L)
        THROW 52172, ''Bed Sowing totals must equal its active transplant and loss operations.'', 1;
END');
        PRINT '=== TR_BedSowings_Guard created ===';
    END
    IF OBJECT_ID('dbo.TR_BedSowingOperations_Immutable', 'TR') IS NULL
    BEGIN
        EXEC (N'CREATE TRIGGER dbo.TR_BedSowingOperations_Immutable ON dbo.BedSowingOperations
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        THROW 52173, ''Bed Sowing operations are never deleted -- reverse the operation instead.'', 1;
    -- Only Active -> Reversed (with who / when / why); everything else is fixed.
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE i.OperationCode <> d.OperationCode OR i.BedSowingId <> d.BedSowingId OR i.OperationType <> d.OperationType
                  OR i.OperationDate <> d.OperationDate OR i.Quantity <> d.Quantity
                  OR ISNULL(i.CavityType, N'''') <> ISNULL(d.CavityType, N'''') OR ISNULL(i.OutputTrays, -1) <> ISNULL(d.OutputTrays, -1)
                  OR ISNULL(i.TargetAreaId, -1) <> ISNULL(d.TargetAreaId, -1) OR ISNULL(i.TargetPolyhouseId, -1) <> ISNULL(d.TargetPolyhouseId, -1)
                  OR ISNULL(i.SeedSowingId, -1) <> ISNULL(d.SeedSowingId, -1)
                  OR ISNULL(i.CreatedById, -1) <> ISNULL(d.CreatedById, -1)
                  OR d.Status = N''Reversed'')
        THROW 52174, ''A bed transplant or loss cannot be changed; it can only be reversed once.'', 1;
END');
        PRINT '=== TR_BedSowingOperations_Immutable created ===';
    END
    IF OBJECT_ID('dbo.TR_BedSowingOperations_Transplanter', 'TR') IS NULL
    BEGIN
        EXEC (N'CREATE TRIGGER dbo.TR_BedSowingOperations_Transplanter ON dbo.BedSowingOperations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- Maya-only (owner decision): the recorder must be an ACTIVE user holding BedSowing.Transplant FOR
    -- the bed''s Area. Full access does not count; there is no exception.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.BedSowings b ON b.Id = i.BedSowingId
               WHERE NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur
                                 JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND ISNULL(u.IsActive, 0) = 1
                                 JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
                                 JOIN dbo.Permissions p ON p.Id = rp.PermissionId AND p.Code = N''BedSowing.Transplant''
                                 WHERE ur.UserId = i.CreatedById AND ur.AreaId = b.AreaId))
        THROW 52180, ''Only the Bed Sowing Transplanter of the bed''''s Area can record a bed transplant or bed loss.'', 1;
END');
        PRINT '=== TR_BedSowingOperations_Transplanter created (Maya-only, no exception) ===';
    END

    -- ---- dbo.SeedSowings: the tray batch made by a bed transplant ---------------
    IF COL_LENGTH('dbo.SeedSowings', 'BedSowingId') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.SeedSowings ADD BedSowingId INT NULL CONSTRAINT FK_SeedSowings_BedSowing REFERENCES dbo.BedSowings (Id);');
        EXEC (N'ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_Source;');
        EXEC (N'ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_Source CHECK (
               ([SourceType]=N''Seed'' AND [SourceSeedStockId] IS NOT NULL AND [SourceCuttingStockId] IS NULL)
            OR ([SourceType]=N''Cutting'' AND [SourceCuttingStockId] IS NOT NULL AND [SourceSeedStockId] IS NULL)
            OR ([SourceType]=N''BedSowing'' AND [SourceSeedStockId] IS NULL AND [SourceCuttingStockId] IS NULL));');
        EXEC (N'ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT CK_SeedSowings_BedLink CHECK (
               ([SourceType]=N''BedSowing'' AND [BedSowingId] IS NOT NULL) OR ([SourceType]<>N''BedSowing'' AND [BedSowingId] IS NULL));');
        EXEC (N'CREATE INDEX IX_SeedSowings_BedSowingId ON dbo.SeedSowings (BedSowingId) WHERE BedSowingId IS NOT NULL;');
        PRINT '=== dbo.SeedSowings.BedSowingId added; CK_SeedSowings_Source widened to SourceType BedSowing ===';
    END
    IF OBJECT_ID('dbo.TR_SeedSowings_ImmutableBedLink', 'TR') IS NULL
    BEGIN
        EXEC (N'CREATE TRIGGER dbo.TR_SeedSowings_ImmutableBedLink ON dbo.SeedSowings
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(BedSowingId) AND EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
                                       WHERE ISNULL(i.BedSowingId, -1) <> ISNULL(d.BedSowingId, -1))
        THROW 52175, ''The bed sowing a tray batch came from cannot be changed.'', 1;
END');
        PRINT '=== TR_SeedSowings_ImmutableBedLink created ===';
    END

    -- ---- Business dates on the material ledger (2026-10-12 review) ---------------------
    -- TransactionDate = the business instant of a movement (a back-dated bed transplant);
    -- NULL = it happened when recorded (CreatedAt, every existing row). Readers use
    -- COALESCE(TransactionDate, CreatedAt); CreatedAt stays the recorded time (audit).
    IF COL_LENGTH('dbo.MaterialTransactions', 'TransactionDate') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.MaterialTransactions ADD TransactionDate DATETIME2 NULL;');
        PRINT '=== dbo.MaterialTransactions.TransactionDate added (NULL for every existing row) ===';
    END

    -- ---- Audit log: administrator exceptions / reversals, Transplanter role changes ---
    IF OBJECT_ID('dbo.BedSowingAuditLog', 'U') IS NULL
    BEGIN
        EXEC (N'CREATE TABLE dbo.BedSowingAuditLog (
            Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BedSowingAuditLog PRIMARY KEY,
            LoggedAt     DATETIME2     NOT NULL CONSTRAINT DF_BedSowingAuditLog_LoggedAt DEFAULT SYSUTCDATETIME(),
            Action       NVARCHAR(40)  NOT NULL,
            BedSowingId  INT           NULL,
            OperationId  INT           NULL,
            UserId       INT           NULL,
            UserName     NVARCHAR(200) NULL,
            TargetUserId INT           NULL,
            RoleId       INT           NULL,
            AreaId       INT           NULL,
            Reason       NVARCHAR(500) NULL,
            Details      NVARCHAR(1000) NULL,
            DbLogin      NVARCHAR(200) NOT NULL CONSTRAINT DF_BedSowingAuditLog_DbLogin DEFAULT ORIGINAL_LOGIN(),
            HostName     NVARCHAR(200) NULL CONSTRAINT DF_BedSowingAuditLog_Host DEFAULT HOST_NAME());');
        EXEC (N'CREATE TRIGGER dbo.TR_BedSowingAuditLog_AppendOnly ON dbo.BedSowingAuditLog
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 52179, ''The Bed Sowing audit log is append-only.'', 1;
END');
        PRINT '=== dbo.BedSowingAuditLog created (append-only) ===';
    END

    -- ---- One named Transplanter: database backstop of the role-management pages -------
    IF OBJECT_ID('dbo.TR_UserRoles_BedTransplanter', 'TR') IS NULL
    BEGIN
        EXEC (N'CREATE TRIGGER dbo.TR_UserRoles_BedTransplanter ON dbo.UserRoles
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Perm INT = (SELECT Id FROM dbo.Permissions WHERE Code = N''BedSowing.Transplant'');
    IF @Perm IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.RolePermissions rp ON rp.RoleId = i.RoleId AND rp.PermissionId = @Perm)
       AND (SELECT COUNT(DISTINCT ur.UserId) FROM dbo.UserRoles ur
            JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId AND rp.PermissionId = @Perm) > 1
        THROW 52176, ''BedSowing.Transplant belongs to one named person and another user already holds it. Change the holder only with a reviewed database script.'', 1;
    INSERT INTO dbo.BedSowingAuditLog (Action, TargetUserId, RoleId, AreaId, Details)
    SELECT N''TransplanterRoleAssigned'', i.UserId, i.RoleId, i.AreaId, CONCAT(N''UserRoles row '', i.Id)
    FROM inserted i JOIN dbo.RolePermissions rp ON rp.RoleId = i.RoleId AND rp.PermissionId = @Perm
    WHERE NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id)
    UNION ALL
    SELECT N''TransplanterRoleRemoved'', d.UserId, d.RoleId, d.AreaId, CONCAT(N''UserRoles row '', d.Id)
    FROM deleted d JOIN dbo.RolePermissions rp ON rp.RoleId = d.RoleId AND rp.PermissionId = @Perm
    WHERE NOT EXISTS (SELECT 1 FROM inserted i WHERE i.Id = d.Id)
    UNION ALL
    SELECT N''TransplanterRoleChanged'', i.UserId, i.RoleId, i.AreaId,
           CONCAT(N''UserRoles row '', i.Id, N'': user '', d.UserId, N''->'', i.UserId, N'', role '', d.RoleId, N''->'', i.RoleId, N'', area '', ISNULL(CAST(d.AreaId AS NVARCHAR(20)), N''-''), N''->'', ISNULL(CAST(i.AreaId AS NVARCHAR(20)), N''-''))
    FROM inserted i JOIN deleted d ON d.Id = i.Id
    WHERE (EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = i.RoleId AND rp.PermissionId = @Perm)
        OR EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = d.RoleId AND rp.PermissionId = @Perm))
      AND (i.UserId <> d.UserId OR i.RoleId <> d.RoleId OR ISNULL(i.AreaId, -1) <> ISNULL(d.AreaId, -1));
END');
        PRINT '=== TR_UserRoles_BedTransplanter created (one holder; every change logged) ===';
    END
    IF OBJECT_ID('dbo.TR_RolePermissions_BedTransplant', 'TR') IS NULL
    BEGIN
        EXEC (N'CREATE TRIGGER dbo.TR_RolePermissions_BedTransplant ON dbo.RolePermissions
AFTER INSERT, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Perm INT = (SELECT Id FROM dbo.Permissions WHERE Code = N''BedSowing.Transplant'');
    IF @Perm IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.Roles r ON r.Id = i.RoleId
               WHERE i.PermissionId = @Perm AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''''), r.RoleName) <> N''Bed Sowing Transplanter'')
        THROW 52177, ''BedSowing.Transplant may be granted only to the Bed Sowing Transplanter role.'', 1;
    INSERT INTO dbo.BedSowingAuditLog (Action, RoleId, Details)
    SELECT N''TransplantPermissionGranted'', i.RoleId, N''RolePermissions insert'' FROM inserted i WHERE i.PermissionId = @Perm
    UNION ALL
    SELECT N''TransplantPermissionRevoked'', d.RoleId, N''RolePermissions delete'' FROM deleted d WHERE d.PermissionId = @Perm;
END');
        PRINT '=== TR_RolePermissions_BedTransplant created ===';
    END

    -- ---- Ready Date: a bed tray batch is never approved before it (backstop) ----------
    IF OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate', 'TR') IS NULL
    BEGIN
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
        -- Fires FIRST, so an early approval is refused with this message (the other approval triggers
        -- have no order set).
        EXEC sp_settriggerorder @triggername = N'dbo.TR_ReadyConfirmations_BedReadyDate', @order = N'First', @stmttype = N'INSERT';
        PRINT '=== TR_ReadyConfirmations_BedReadyDate created (fires first) ===';
    END

    -- ---- Permission, role, the one assignment -----------------------------------
    IF OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NULL
        THROW 52169, 'dbo.RolePermissionChangeLog is missing -- run 2026-10-11_TrayAccessByLocation.sql first.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'BedSowing.Transplant')
    BEGIN
        IF COLUMNPROPERTY(OBJECT_ID('dbo.Permissions'), 'Id', 'IsIdentity') = 1
            INSERT INTO dbo.Permissions (Code, Description)
            VALUES (N'BedSowing.Transplant', N'Transplant Bed Sowing seedlings into cavity trays, record and reverse bed losses (own Area)');
        ELSE
            INSERT INTO dbo.Permissions (Id, Code, Description)
            SELECT ISNULL(MAX(Id), 0) + 1, N'BedSowing.Transplant', N'Transplant Bed Sowing seedlings into cavity trays, record and reverse bed losses (own Area)' FROM dbo.Permissions;
        PRINT '=== Permission BedSowing.Transplant added ===';
    END

    DECLARE @RoleName NVARCHAR(100) = N'Bed Sowing Transplanter';
    IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = @RoleName)
    BEGIN
        IF COLUMNPROPERTY(OBJECT_ID('dbo.Roles'), 'Id', 'IsIdentity') = 1
            INSERT INTO dbo.Roles (Name, RoleName, Description, IsSystemRole, CreatedDate)
            VALUES (@RoleName, @RoleName, N'Transplants Bed Sowing seedlings into cavity trays (Facility 1 Main Office); one named person', 0, SYSUTCDATETIME());
        ELSE
            INSERT INTO dbo.Roles (Id, Name, RoleName, Description, IsSystemRole, CreatedDate)
            SELECT ISNULL(MAX(Id), 0) + 1, @RoleName, @RoleName, N'Transplants Bed Sowing seedlings into cavity trays (Facility 1 Main Office); one named person', 0, SYSUTCDATETIME() FROM dbo.Roles;
        PRINT '=== Role Bed Sowing Transplanter added ===';
    END
    DECLARE @RoleId INT = (SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = @RoleName);
    DECLARE @PermissionId INT = (SELECT Id FROM dbo.Permissions WHERE Code = N'BedSowing.Transplant');

    IF NOT EXISTS (SELECT 1 FROM dbo.RolePermissions WHERE RoleId = @RoleId AND PermissionId = @PermissionId)
    BEGIN
        INSERT INTO dbo.RolePermissionChangeLog (RoleId, RoleName, PermissionCode, Change, Script, Reason)
        VALUES (@RoleId, @RoleName, N'BedSowing.Transplant', N'Added', N'2026-10-12_BedSowing.sql',
                N'Only the named Bed Sowing Transplanter moves bed seedlings into trays (owner decision 2026-10-10)');
        INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@RoleId, @PermissionId);
        PRINT '=== BedSowing.Transplant granted to Bed Sowing Transplanter ===';
    END

    -- The role is for ONE person: refuse if someone else already holds it.
    IF EXISTS (SELECT 1 FROM dbo.UserRoles WHERE RoleId = @RoleId AND UserId <> @UserId)
        THROW 52170, 'Another user already holds the Bed Sowing Transplanter role -- nothing changed.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles WHERE RoleId = @RoleId AND UserId = @UserId AND AreaId = @AreaId)
    BEGIN
        INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId, CreatedDate) VALUES (@UserId, @RoleId, @AreaId, SYSUTCDATETIME());
        PRINT CONCAT('=== Bed Sowing Transplanter assigned to user ', @UserId, ' for Area ', @AreaId, ' ===');
    END
    ELSE PRINT '=== Assignment already exists -- no-op ===';

    PRINT '=== After ===';
    SELECT 'SeedSowings' AS T, COUNT(*) AS Rows FROM dbo.SeedSowings
    UNION ALL SELECT 'ReadyStock', COUNT(*) FROM dbo.ReadyStock
    UNION ALL SELECT 'SeedStockTransactions', COUNT(*) FROM dbo.SeedStockTransactions
    UNION ALL SELECT 'TrayStockTransactions', COUNT(*) FROM dbo.TrayStockTransactions
    UNION ALL SELECT 'MaterialTransactions', COUNT(*) FROM dbo.MaterialTransactions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles;
    SELECT l.PolyhouseId, p.Name AS Polyhouse, l.AreaId, a.Name AS Area, l.IsActive FROM dbo.BedSowingLocations l
    JOIN dbo.Polyhouses p ON p.Id = l.PolyhouseId JOIN dbo.Area a ON a.Id = l.AreaId;
    SELECT ur.UserId, u.Username, u.Name, ur.AreaId, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName
    FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId JOIN dbo.IMSUsers u ON u.Id = ur.UserId
    WHERE ur.RoleId = @RoleId;
    EXEC (N'SELECT Action, TargetUserId, RoleId, AreaId, Details, DbLogin FROM dbo.BedSowingAuditLog ORDER BY Id;');
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION BedSowing1012;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION BedSowing1012;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION BedSowing1012;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
