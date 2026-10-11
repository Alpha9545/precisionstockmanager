-- ============================================================================
-- 2026-10-10_TrayTransfers.sql
--
-- WHY: empty trays could only be ADDED or used. This adds the two controlled corrections
-- the audit asked for, on the EXISTING Tray Stock pools and ledger (no second tray system):
--   * Tray adjustment  -- a compensating dbo.TrayStockTransactions row of the existing type
--                         'Adjustment' (ReferenceType 'TrayAdjustment', mandatory reason); no
--                         schema change, only the permission below.
--   * Tray transfer    -- dbo.TrayTransfers (one row per transfer):
--                         Pending   source pool -q   'TransferOut'  (the trays have left; in transit)
--                         Received  destination +q   'TransferIn'
--                         Rejected / Cancelled  source +q 'TransferReturn'
--                         A pending transfer is in neither pool's balance, so nothing is counted twice.
-- dbo.TrayStockTransactions: types TransferOut, TransferIn, TransferReturn added.
-- Permissions (owner decisions 2026-10-10):
--   TrayStock.Adjust          Sowing Supervisor (own Areas; full-access users everywhere)
--   TrayStock.Transfer        Sowing Supervisor, Office Coordinator (send / cancel from Areas they may stock)
--   TrayStock.ReceiveTransfer Mother Plant Supervisor (receive / reject into own Areas)
-- No existing row, balance or grant changes. Requires 2026-10-09_ReadyStockTransplanting.sql.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the server named with
-- -v TARGET_SERVER=<@@SERVERNAME>. One transaction. Idempotent (re-run = no-op).
-- Undo: 2026-10-10_TrayTransfers_Rollback.sql (refuses while any tray transfer exists).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52001, '2026-10-10_TrayTransfers.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52002, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52003, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION TrayTransfers2026_10_10;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    IF OBJECT_ID('dbo.ReadyStockTransplants', 'U') IS NULL
        THROW 52004, 'Run 2026-10-09_ReadyStockTransplanting.sql first.', 1;
    DECLARE @Missing NVARCHAR(100);
    DECLARE @Roles TABLE (Name NVARCHAR(100));
    INSERT INTO @Roles VALUES (N'Sowing Supervisor'), (N'Office Coordinator'), (N'Mother Plant Supervisor');
    SELECT @Missing = r.Name FROM @Roles r
    WHERE NOT EXISTS (SELECT 1 FROM dbo.Roles x WHERE COALESCE(NULLIF(LTRIM(RTRIM(x.Name)), N''), x.RoleName) = r.Name);
    IF @Missing IS NOT NULL
        THROW 52005, 'A required role (Sowing Supervisor / Office Coordinator / Mother Plant Supervisor) is missing.', 1;

    PRINT '=== Before: rows that must not change ===';
    SELECT 'TrayStock' AS TableName, COUNT(*) AS Rows, SUM(PhysicalQuantity) AS Qty FROM dbo.TrayStock
    UNION ALL SELECT 'TrayStockTransactions', COUNT(*), SUM(Quantity) FROM dbo.TrayStockTransactions;

    ------------------------------------------------------------------
    -- 1. Ledger types
    ------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_TrayStockTx_Type' AND definition LIKE '%TransferReturn%')
    BEGIN
        EXEC (N'ALTER TABLE dbo.TrayStockTransactions DROP CONSTRAINT CK_TrayStockTx_Type;');
        EXEC (N'ALTER TABLE dbo.TrayStockTransactions ADD CONSTRAINT CK_TrayStockTx_Type CHECK (TransactionType IN (
            N''Allocation'', N''Sowing'', N''ReversalReturn'', N''Adjustment'', N''Transplant'',
            N''TransferOut'', N''TransferIn'', N''TransferReturn''));');
        PRINT '=== TrayStockTransactions: types TransferOut / TransferIn / TransferReturn added ===';
    END
    ELSE PRINT '=== TrayStockTransactions types already include transfers -- no-op ===';

    ------------------------------------------------------------------
    -- 2. dbo.TrayTransfers
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.TrayTransfers', 'U') IS NULL
    BEGIN
        EXEC (N'
CREATE TABLE dbo.TrayTransfers (
    Id                     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TrayTransfers PRIMARY KEY,
    TransferCode           NVARCHAR(30)  NOT NULL CONSTRAINT UQ_TrayTransfers_Code UNIQUE,
    SourceTrayStockId      INT           NOT NULL CONSTRAINT FK_TrayTransfers_SourcePool REFERENCES dbo.TrayStock(Id),
    SourceAreaId           INT           NOT NULL CONSTRAINT FK_TrayTransfers_SourceArea REFERENCES dbo.Area(Id),
    SourcePolyhouseId      INT           NOT NULL CONSTRAINT FK_TrayTransfers_SourcePolyhouse REFERENCES dbo.Polyhouses(Id),
    DestinationAreaId      INT           NOT NULL CONSTRAINT FK_TrayTransfers_DestinationArea REFERENCES dbo.Area(Id),
    DestinationPolyhouseId INT           NOT NULL CONSTRAINT FK_TrayTransfers_DestinationPolyhouse REFERENCES dbo.Polyhouses(Id),
    DestinationTrayStockId INT           NULL     CONSTRAINT FK_TrayTransfers_DestinationPool REFERENCES dbo.TrayStock(Id),
    TraySize               NVARCHAR(20)  NOT NULL,
    Quantity               DECIMAL(18,2) NOT NULL,
    TransferDate           DATE          NOT NULL,
    Status                 NVARCHAR(20)  NOT NULL CONSTRAINT DF_TrayTransfers_Status DEFAULT N''Pending'',
    Remarks                NVARCHAR(500) NULL,
    SubmissionToken        UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_TrayTransfers_Token UNIQUE,
    CreatedById            INT           NULL     CONSTRAINT FK_TrayTransfers_CreatedBy REFERENCES dbo.IMSUsers(Id),
    CreatedBy              NVARCHAR(256) NULL,
    CreatedAt              DATETIME2     NOT NULL CONSTRAINT DF_TrayTransfers_CreatedAt DEFAULT SYSUTCDATETIME(),
    ClosedById             INT           NULL     CONSTRAINT FK_TrayTransfers_ClosedBy REFERENCES dbo.IMSUsers(Id),
    ClosedBy               NVARCHAR(256) NULL,
    ClosedAt               DATETIME2     NULL,
    CloseReason            NVARCHAR(500) NULL,
    CONSTRAINT CK_TrayTransfers_Size CHECK (TraySize IN (N''9 Cavity'', N''24 Cavity'', N''42 Cavity'', N''102 Cavity'', N''150 Cavity'')),
    CONSTRAINT CK_TrayTransfers_Quantity CHECK (Quantity > 0 AND Quantity = FLOOR(Quantity)),
    CONSTRAINT CK_TrayTransfers_DifferentPolyhouse CHECK (SourcePolyhouseId <> DestinationPolyhouseId),
    CONSTRAINT CK_TrayTransfers_Status CHECK (
        (Status = N''Pending''   AND ClosedAt IS NULL AND DestinationTrayStockId IS NULL)
     OR (Status = N''Received''  AND ClosedAt IS NOT NULL AND DestinationTrayStockId IS NOT NULL)
     OR (Status IN (N''Rejected'', N''Cancelled'') AND ClosedAt IS NOT NULL AND DestinationTrayStockId IS NULL
         AND CloseReason IS NOT NULL AND LEN(LTRIM(RTRIM(CloseReason))) > 0))
);');
        EXEC (N'CREATE INDEX IX_TrayTransfers_Status ON dbo.TrayTransfers (Status) INCLUDE (SourceAreaId, DestinationAreaId, Quantity);');
        EXEC (N'CREATE INDEX IX_TrayTransfers_SourcePool ON dbo.TrayTransfers (SourceTrayStockId);');
        PRINT '=== dbo.TrayTransfers created ===';
    END
    ELSE PRINT '=== dbo.TrayTransfers already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 3. Permissions + grants (least privilege)
    ------------------------------------------------------------------
    DECLARE @Perms TABLE (Code NVARCHAR(200) PRIMARY KEY, Description NVARCHAR(400), SortOrder INT);
    INSERT INTO @Perms VALUES
        (N'TrayStock.Adjust',          N'Correct an empty-tray balance with a compensating entry and a reason (own Areas)', 1),
        (N'TrayStock.Transfer',        N'Send empty trays to another polyhouse, or cancel a pending send (from Areas the user may stock trays in)', 2),
        (N'TrayStock.ReceiveTransfer', N'Receive or reject empty trays sent to own Areas', 3);
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
            PRINT CONCAT('=== Permission ', @Code, ' added ===');
        END
        ELSE PRINT CONCAT('=== Permission ', @Code, ' already exists -- no-op ===');
        FETCH NEXT FROM perm_cursor INTO @Code, @PermDescription;
    END
    CLOSE perm_cursor;
    DEALLOCATE perm_cursor;

    DECLARE @Grants TABLE (RoleName NVARCHAR(100), Code NVARCHAR(200));
    INSERT INTO @Grants VALUES
        (N'Sowing Supervisor', N'TrayStock.Adjust'),
        (N'Sowing Supervisor', N'TrayStock.Transfer'),
        (N'Office Coordinator', N'TrayStock.Transfer'),
        (N'Mother Plant Supervisor', N'TrayStock.ReceiveTransfer');
    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM @Grants g
    JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = g.RoleName
    JOIN dbo.Permissions p ON p.Code = g.Code
    WHERE NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    PRINT CONCAT('Grants inserted: ', @@ROWCOUNT, ' (expect 4 on first run, 0 on re-run)');

    PRINT '=== After ===';
    SELECT 'TrayStock' AS TableName, COUNT(*) AS Rows, SUM(PhysicalQuantity) AS Qty FROM dbo.TrayStock
    UNION ALL SELECT 'TrayStockTransactions', COUNT(*), SUM(Quantity) FROM dbo.TrayStockTransactions;
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, p.Code
    FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code IN (SELECT Code FROM @Perms) ORDER BY RoleName, p.Code;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TrayTransfers2026_10_10;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TrayTransfers2026_10_10;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TrayTransfers2026_10_10;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
