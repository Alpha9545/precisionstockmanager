-- ============================================================================
-- 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql
--
-- WHY (owner requirements 2026-10-14):
--   * Ready Stock sent to a Mother Plant Area goes to the AREA -- no Polyhouse is chosen to send,
--     receive or transplant it -- and every transfer records its PURPOSE: 'PotProduction' (kept as
--     Ready Stock in the Mother Plant Area, never transplanted into trays) or 'TrayTransplant'
--     (received, then Transplant into Trays -> a tray batch in the existing Sowing Approvals).
--   * Empty trays in a Mother Plant Area are held per AREA + cavity (Add Empty Trays in the Mother
--     Plant / Cutting sections, Area only). Every user of Mother Plant Area trays -- Ready Stock
--     transplanting, Cutting Tray Sowing, a Bed Sowing transplant into a Mother Plant Area, tray
--     transfers and adjustments -- uses that one Area pool (Data/TrayStockRepository.cs resolves it).
--     Main Office (and every other Area type) keeps its Polyhouse pools, unchanged.
--
-- WHAT:
--   1. dbo.InternalTransfers.TransferPurpose NVARCHAR(30) NULL + CK_InternalTransfers_TransferPurpose
--      ('PotProduction' | 'TrayTransplant'). Existing rows keep NULL (= not recorded; history unchanged).
--      TR_InternalTransfers_MotherPlantPurpose: a NEW Mother Plant transfer must carry a purpose, and a
--      purpose is never changed afterwards.
--   2. CK_InternalTransfers_MotherPlantTransfer: the same rule WITHOUT "DestinationPolyhouseId IS NOT NULL"
--      (existing rows keep their Polyhouse; new ones may have none). Verified against the exact current
--      definition first (schema-drift guard).
--   3. dbo.ReadyStockTransplants.PolyhouseId becomes NULLable (a transplant into the Area's trays; old rows
--      keep their Polyhouse). FK_RsTransplants_Polyhouse is unchanged.
--   4. dbo.TrayStock: CK_TrayStock_ActivePoolHasPolyhouse is replaced by TR_TrayStock_PoolLocation -- an
--      ACTIVE pool without a Polyhouse only in a MotherPlant Area, and no ACTIVE Polyhouse pool in a
--      MotherPlant Area. Before that, every active Polyhouse pool of a MotherPlant Area is MERGED into its
--      Area pool (the retired Area pool of that cavity is re-activated, or a new one created): one
--      'Adjustment' ledger row out of the Polyhouse pool and one into the Area pool (ReferenceType
--      'AreaPoolMerge', ReferenceId = the other pool, dated now), then the empty Polyhouse pool is retired.
--      No ledger row is updated or deleted; trays are only moved, never created or lost.
--
-- NOT changed: no transfer, Ready Stock, sowing or approval row; no permission; Main Office pools.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to -v TARGET_SERVER=<@@SERVERNAME>.
-- One transaction. Idempotent (re-run = no-op). Undo: 2026-10-14_MotherPlantAreaTraysAndTransferPurpose_Rollback.sql
-- (refuses once the new shapes are used).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52301, '2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 52302, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 52303, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
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

BEGIN TRANSACTION MpAreaTrays1014;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.ReadyStockTransplants', 'U') IS NULL OR COL_LENGTH('dbo.InternalTransfers', 'DestinationPolyhouseId') IS NULL
        THROW 52304, 'Run 2026-10-09_ReadyStockTransplanting.sql first.', 1;
    IF COL_LENGTH('dbo.ReadyStockTransplants', 'OutputSeedSowingId') IS NULL
        THROW 52305, 'Run 2026-10-13_TraysToSowingApprovals.sql first.', 1;

    DECLARE @Applied BIT = CASE WHEN COL_LENGTH('dbo.InternalTransfers', 'TransferPurpose') IS NOT NULL
                                 AND OBJECT_ID('dbo.TR_TrayStock_PoolLocation', 'TR') IS NOT NULL THEN 1 ELSE 0 END;
    IF @Applied = 0
    BEGIN
        -- Schema-drift guard: exactly the constraints this script replaces.
        IF (SELECT definition FROM sys.check_constraints WHERE name = N'CK_InternalTransfers_MotherPlantTransfer')
           <> N'([StockType]<>N''MotherPlantTransfer'' OR [DestinationAreaId] IS NOT NULL AND [DestinationPolyhouseId] IS NOT NULL AND [TransferDate] IS NOT NULL AND ([Status]=N''Cancelled'' OR [Status]=N''Rejected'' OR [Status]=N''Completed'' OR [Status]=N''PendingConfirmation'') AND ([Status]<>N''Completed'' OR [ConfirmedQuantity] IS NOT NULL AND [ConfirmedQuantity]>(0) AND [ConfirmedQuantity]<=[Quantity] AND [ConfirmedBy] IS NOT NULL AND [ConfirmedDate] IS NOT NULL AND ([ConfirmedQuantity]=[Quantity] OR [DiscrepancyReason] IS NOT NULL)) AND ([Status]<>N''Rejected'' OR [ConfirmedBy] IS NOT NULL AND [ConfirmedDate] IS NOT NULL AND [DiscrepancyReason] IS NOT NULL) AND ([Status]<>N''Cancelled'' OR [CancelledById] IS NOT NULL AND [CancelledDate] IS NOT NULL AND [CancelReason] IS NOT NULL))'
            THROW 52306, 'CK_InternalTransfers_MotherPlantTransfer is not the expected 2026-10-09 definition -- schema drift, nothing changed.', 1;
        IF OBJECT_ID('dbo.CK_TrayStock_ActivePoolHasPolyhouse', 'C') IS NOT NULL
           AND (SELECT definition FROM sys.check_constraints WHERE name = N'CK_TrayStock_ActivePoolHasPolyhouse') <> N'([PolyhouseId] IS NOT NULL OR [IsActive]=(0))'
            THROW 52307, 'CK_TrayStock_ActivePoolHasPolyhouse is not the expected 2026-10-04 definition -- schema drift, nothing changed.', 1;
        IF OBJECT_ID('dbo.TR_TrayStock_PoolLocation', 'TR') IS NOT NULL OR OBJECT_ID('dbo.TR_InternalTransfers_MotherPlantPurpose', 'TR') IS NOT NULL
            THROW 52308, 'A trigger of this migration already exists while the migration is not complete -- inspect the database, nothing changed.', 1;
    END

    PRINT '=== Before: rows / trays that must not change in total ===';
    SELECT 'InternalTransfers' AS T, COUNT(*) AS Rows, CAST(NULL AS DECIMAL(18,2)) AS Total FROM dbo.InternalTransfers
    UNION ALL SELECT 'ReadyStockTransplants', COUNT(*), NULL FROM dbo.ReadyStockTransplants
    UNION ALL SELECT 'TrayStock (all trays)', COUNT(*), SUM(PhysicalQuantity) FROM dbo.TrayStock
    UNION ALL SELECT 'TrayStockTransactions (net)', COUNT(*), SUM(Quantity) FROM dbo.TrayStockTransactions;

    IF @Applied = 0
    BEGIN
        DECLARE @TraysBefore DECIMAL(18, 2) = (SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock);

        ------------------------------------------------------------------
        -- 1. InternalTransfers.TransferPurpose
        ------------------------------------------------------------------
        IF COL_LENGTH('dbo.InternalTransfers', 'TransferPurpose') IS NULL
            ALTER TABLE dbo.InternalTransfers ADD TransferPurpose NVARCHAR(30) NULL;
        IF OBJECT_ID('dbo.CK_InternalTransfers_TransferPurpose', 'C') IS NULL
            EXEC (N'ALTER TABLE dbo.InternalTransfers WITH CHECK ADD CONSTRAINT CK_InternalTransfers_TransferPurpose
                    CHECK (TransferPurpose IS NULL OR TransferPurpose IN (N''PotProduction'', N''TrayTransplant''));');

        EXEC (N'
CREATE TRIGGER dbo.TR_InternalTransfers_MotherPlantPurpose
ON dbo.InternalTransfers
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- 2026-10-14: a NEW Ready Stock transfer to a Mother Plant Area records its purpose.
    IF NOT EXISTS (SELECT 1 FROM deleted)
       AND EXISTS (SELECT 1 FROM inserted WHERE StockType = N''MotherPlantTransfer'' AND TransferPurpose IS NULL)
        THROW 52309, ''Choose the purpose of the transfer: Pot Production or Transplant into a different cavity tray.'', 1;
    -- A recorded purpose is never changed (traceability of the receiving and transplanting).
    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
               WHERE ISNULL(i.TransferPurpose, N'''') <> ISNULL(d.TransferPurpose, N''''))
        THROW 52310, ''The purpose of a transfer cannot be changed once it is recorded.'', 1;
END');
        PRINT '=== 1. InternalTransfers.TransferPurpose + TR_InternalTransfers_MotherPlantPurpose ===';

        ------------------------------------------------------------------
        -- 2. A Mother Plant transfer no longer needs a destination Polyhouse
        ------------------------------------------------------------------
        ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_MotherPlantTransfer;
        EXEC (N'ALTER TABLE dbo.InternalTransfers WITH CHECK ADD CONSTRAINT CK_InternalTransfers_MotherPlantTransfer CHECK (
            StockType <> N''MotherPlantTransfer''
            OR (DestinationAreaId IS NOT NULL AND TransferDate IS NOT NULL
                AND Status IN (N''PendingConfirmation'', N''Completed'', N''Rejected'', N''Cancelled'')
                AND (Status <> N''Completed'' OR (ConfirmedQuantity IS NOT NULL AND ConfirmedQuantity > 0 AND ConfirmedQuantity <= Quantity
                                                  AND ConfirmedBy IS NOT NULL AND ConfirmedDate IS NOT NULL
                                                  AND (ConfirmedQuantity = Quantity OR DiscrepancyReason IS NOT NULL)))
                AND (Status <> N''Rejected'' OR (ConfirmedBy IS NOT NULL AND ConfirmedDate IS NOT NULL AND DiscrepancyReason IS NOT NULL))
                AND (Status <> N''Cancelled'' OR (CancelledById IS NOT NULL AND CancelledDate IS NOT NULL AND CancelReason IS NOT NULL))));');
        PRINT '=== 2. CK_InternalTransfers_MotherPlantTransfer: destination Polyhouse optional ===';

        ------------------------------------------------------------------
        -- 3. ReadyStockTransplants.PolyhouseId NULLable
        ------------------------------------------------------------------
        ALTER TABLE dbo.ReadyStockTransplants ALTER COLUMN PolyhouseId INT NULL;
        PRINT '=== 3. ReadyStockTransplants.PolyhouseId NULLable ===';

        ------------------------------------------------------------------
        -- 4. Mother Plant Area trays: one pool per Area + cavity
        ------------------------------------------------------------------
        IF OBJECT_ID('dbo.CK_TrayStock_ActivePoolHasPolyhouse', 'C') IS NOT NULL
            ALTER TABLE dbo.TrayStock DROP CONSTRAINT CK_TrayStock_ActivePoolHasPolyhouse;

        PRINT '=== 4a. Mother Plant Area Polyhouse pools to merge (active) ===';
        SELECT t.Id, a.Name AS Area, p.Name AS Polyhouse, t.TraySize, t.PhysicalQuantity
        FROM dbo.TrayStock t JOIN dbo.Area a ON a.Id = t.AreaId LEFT JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId
        WHERE a.AreaType = N'MotherPlant' AND t.PolyhouseId IS NOT NULL AND t.IsActive = 1
        ORDER BY a.Name, t.TraySize, p.Name;

        DECLARE @Src INT, @AreaId INT, @Size NVARCHAR(50), @Qty DECIMAL(18, 2), @Pool INT, @PoolBefore DECIMAL(18, 2);
        DECLARE pools CURSOR LOCAL FAST_FORWARD FOR
            SELECT t.Id, t.AreaId, t.TraySize, t.PhysicalQuantity
            FROM dbo.TrayStock t JOIN dbo.Area a ON a.Id = t.AreaId
            WHERE a.AreaType = N'MotherPlant' AND t.PolyhouseId IS NOT NULL AND t.IsActive = 1
            ORDER BY t.AreaId, t.TraySize, t.Id;
        OPEN pools;
        FETCH NEXT FROM pools INTO @Src, @AreaId, @Size, @Qty;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @Pool = (SELECT Id FROM dbo.TrayStock WITH (UPDLOCK, HOLDLOCK) WHERE AreaId = @AreaId AND PolyhouseId IS NULL AND TraySize = @Size);
            IF @Pool IS NULL
            BEGIN
                INSERT INTO dbo.TrayStock (AreaId, PolyhouseId, TraySize, PhysicalQuantity, IsActive, CreatedDate, CreatedBy)
                VALUES (@AreaId, NULL, @Size, 0, 1, SYSUTCDATETIME(), N'2026-10-14 migration');
                SET @Pool = CAST(SCOPE_IDENTITY() AS INT);
            END
            ELSE
                UPDATE dbo.TrayStock SET IsActive = 1, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'2026-10-14 migration' WHERE Id = @Pool AND IsActive = 0;

            IF @Qty > 0
            BEGIN
                SET @PoolBefore = (SELECT PhysicalQuantity FROM dbo.TrayStock WHERE Id = @Pool);
                INSERT INTO dbo.TrayStockTransactions (TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
                VALUES (@Src, SYSUTCDATETIME(), N'Adjustment', N'AreaPoolMerge', @Pool, -@Qty, @Qty, NULL,
                        N'2026-10-14: Mother Plant Area trays are held per Area -- moved to the Area pool', SYSUTCDATETIME());
                INSERT INTO dbo.TrayStockTransactions (TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
                VALUES (@Pool, SYSUTCDATETIME(), N'Adjustment', N'AreaPoolMerge', @Src, @Qty, @PoolBefore, NULL,
                        N'2026-10-14: Mother Plant Area trays are held per Area -- moved from a Polyhouse pool', SYSUTCDATETIME());
                UPDATE dbo.TrayStock SET PhysicalQuantity = PhysicalQuantity + @Qty, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'2026-10-14 migration' WHERE Id = @Pool;
            END
            UPDATE dbo.TrayStock SET PhysicalQuantity = 0, IsActive = 0, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'2026-10-14 migration' WHERE Id = @Src;
            FETCH NEXT FROM pools INTO @Src, @AreaId, @Size, @Qty;
        END
        CLOSE pools;
        DEALLOCATE pools;

        IF (SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock) <> @TraysBefore
            THROW 52311, 'Tray total changed during the Area-pool merge -- rolled back.', 1;
        IF EXISTS (SELECT 1 FROM dbo.TrayStock t JOIN dbo.Area a ON a.Id = t.AreaId
                   WHERE a.AreaType = N'MotherPlant' AND t.PolyhouseId IS NOT NULL AND t.IsActive = 1)
            THROW 52314, 'An active Polyhouse pool is left in a Mother Plant Area after the merge -- rolled back.', 1;

        EXEC (N'
CREATE TRIGGER dbo.TR_TrayStock_PoolLocation
ON dbo.TrayStock
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- 2026-10-14: an ACTIVE pool without a Polyhouse exists only in a Mother Plant Area (its one Area pool per
    -- cavity); every other Area keeps Polyhouse pools. Retired pools of any kind stay as history.
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.Area a ON a.Id = i.AreaId
               WHERE i.IsActive = 1 AND i.PolyhouseId IS NULL AND ISNULL(a.AreaType, N'''') <> N''MotherPlant'')
        THROW 52312, ''Empty trays outside a Mother Plant Area are held per Polyhouse: choose the Polyhouse.'', 1;
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.Area a ON a.Id = i.AreaId
               WHERE i.IsActive = 1 AND i.PolyhouseId IS NOT NULL AND a.AreaType = N''MotherPlant'')
        THROW 52313, ''Empty trays in a Mother Plant Area are held per Area, not per Polyhouse.'', 1;
END');
        PRINT '=== 4b. TR_TrayStock_PoolLocation (replaces CK_TrayStock_ActivePoolHasPolyhouse) ===';
    END
    ELSE PRINT '=== Already applied -- no-op ===';

    PRINT '=== After ===';
    SELECT 'InternalTransfers' AS T, COUNT(*) AS Rows, CAST(NULL AS DECIMAL(18,2)) AS Total FROM dbo.InternalTransfers
    UNION ALL SELECT 'ReadyStockTransplants', COUNT(*), NULL FROM dbo.ReadyStockTransplants
    UNION ALL SELECT 'TrayStock (all trays)', COUNT(*), SUM(PhysicalQuantity) FROM dbo.TrayStock
    UNION ALL SELECT 'TrayStockTransactions (net)', COUNT(*), SUM(Quantity) FROM dbo.TrayStockTransactions;
    SELECT a.Name AS Area, t.TraySize, t.PhysicalQuantity, t.IsActive
    FROM dbo.TrayStock t JOIN dbo.Area a ON a.Id = t.AreaId
    WHERE a.AreaType = N'MotherPlant' AND t.PolyhouseId IS NULL ORDER BY a.Name, t.TraySize;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MpAreaTrays1014;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MpAreaTrays1014;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MpAreaTrays1014;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
