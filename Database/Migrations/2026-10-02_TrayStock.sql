-- ============================================================================
-- 2026-10-02_TrayStock.sql
--
-- WHY: new feature -- tray inventory held at specific Main Office Polyhouses
-- (e.g. "Facility-5"), consumed automatically by Seed/Cutting Sowing when
-- the destination Polyhouse is one of those. No existing table can safely
-- represent this: dbo.EmptyPotInventory is (PotSize, AreaId)-keyed and
-- belongs to a different inventory (empty pots for Pot Production);
-- repurposing it would conflate two unrelated stock entities. Two new
-- tables, mirroring EmptyPotInventory/EmptyPotInventoryTransactions'
-- existing design exactly, with (PolyhouseId, TraySize) as the business key
-- instead of (PotSize, AreaId) -- trays belong to a Polyhouse, never an Area,
-- per the explicit business requirement.
--
-- WHAT THIS DOES:
--   dbo.TrayStock               -- one row per (PolyhouseId, TraySize)
--   dbo.TrayStockTransactions   -- append-only ledger, same shape as
--                                  dbo.EmptyPotInventoryTransactions
-- TraySize reuses the EXACT same closed string domain as
-- dbo.SeedSowings.CavityType (CK_SeedSowings_CavityType) -- no new
-- Tray Size / Cavity master table.
--
-- NOT changed: any existing table, column, constraint, trigger, row,
-- permission, or role.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy. One
-- transaction. Run with sqlcmd -v MODE=DRYRUN (rolls back) or MODE=COMMIT.
-- Idempotent (checks INFORMATION_SCHEMA.TABLES before creating).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51601, '2026-10-02_TrayStock.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51602, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION TrayStock2026_10_02;

BEGIN TRY

    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'TrayStock')
    BEGIN
        CREATE TABLE dbo.TrayStock (
            Id               INT IDENTITY PRIMARY KEY,
            PolyhouseId      INT NOT NULL,
            TraySize         NVARCHAR(20) NOT NULL,
            PhysicalQuantity DECIMAL(18,2) NOT NULL CONSTRAINT DF_TrayStock_PhysicalQuantity DEFAULT (0),
            IsActive         BIT NOT NULL CONSTRAINT DF_TrayStock_IsActive DEFAULT (1),
            CreatedDate      DATETIME2 NOT NULL CONSTRAINT DF_TrayStock_CreatedDate DEFAULT (SYSUTCDATETIME()),
            CreatedBy        NVARCHAR(100) NULL,
            ModifiedDate     DATETIME2 NULL,
            ModifiedBy       NVARCHAR(100) NULL,
            CONSTRAINT FK_TrayStock_Polyhouse FOREIGN KEY (PolyhouseId) REFERENCES dbo.Polyhouses(Id),
            CONSTRAINT UQ_TrayStock_Polyhouse_Size UNIQUE (PolyhouseId, TraySize),
            CONSTRAINT CK_TrayStock_TraySize CHECK (TraySize IN (N'9 Cavity', N'24 Cavity', N'42 Cavity', N'102 Cavity', N'150 Cavity')),
            CONSTRAINT CK_TrayStock_NonNegative CHECK (PhysicalQuantity >= 0)
        );
        PRINT '=== dbo.TrayStock created ===';
    END
    ELSE
        PRINT '=== dbo.TrayStock already exists -- no-op ===';

    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'TrayStockTransactions')
    BEGIN
        CREATE TABLE dbo.TrayStockTransactions (
            Id              INT IDENTITY PRIMARY KEY,
            TrayStockId     INT NOT NULL,
            TransactionDate DATETIME2 NOT NULL CONSTRAINT DF_TrayStockTx_TransactionDate DEFAULT (SYSUTCDATETIME()),
            TransactionType NVARCHAR(30) NOT NULL,
            ReferenceType   NVARCHAR(30) NULL,
            ReferenceId     INT NULL,
            Quantity        DECIMAL(18,2) NOT NULL,
            BeforeQuantity  DECIMAL(18,2) NOT NULL,
            UserId          INT NULL,
            Remarks         NVARCHAR(500) NULL,
            CreatedAt       DATETIME2 NOT NULL CONSTRAINT DF_TrayStockTx_CreatedAt DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT FK_TrayStockTx_TrayStock FOREIGN KEY (TrayStockId) REFERENCES dbo.TrayStock(Id),
            CONSTRAINT FK_TrayStockTx_User FOREIGN KEY (UserId) REFERENCES dbo.IMSUsers(Id),
            CONSTRAINT CK_TrayStockTx_Type CHECK (TransactionType IN (N'Allocation', N'Sowing', N'ReversalReturn', N'Adjustment'))
        );
        CREATE INDEX IX_TrayStockTransactions_TrayStockId ON dbo.TrayStockTransactions(TrayStockId);
        PRINT '=== dbo.TrayStockTransactions created ===';
    END
    ELSE
        PRINT '=== dbo.TrayStockTransactions already exists -- no-op ===';

    ----------------------------------------------------------------------
    -- VERIFICATION
    ----------------------------------------------------------------------
    PRINT '=== Tables exist (expect 2 rows) ===';
    SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME IN ('TrayStock', 'TrayStockTransactions');

    PRINT '=== TrayStock columns ===';
    SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'TrayStock' ORDER BY ORDINAL_POSITION;

    PRINT '=== TrayStockTransactions columns ===';
    SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'TrayStockTransactions' ORDER BY ORDINAL_POSITION;

    PRINT '=== Row counts (expect 0, 0 on first run) ===';
    SELECT 'TrayStock' AS TableName, COUNT(*) AS Rows FROM dbo.TrayStock
    UNION ALL SELECT 'TrayStockTransactions', COUNT(*) FROM dbo.TrayStockTransactions;

    PRINT '=== No existing table/column touched (sanity: SeedSowings/CuttingStock/InternalTransfers row counts unchanged -- compare manually) ===';
    SELECT 'SeedSowings' AS TableName, COUNT(*) AS Rows FROM dbo.SeedSowings
    UNION ALL SELECT 'CuttingStock', COUNT(*) FROM dbo.CuttingStock
    UNION ALL SELECT 'InternalTransfers', COUNT(*) FROM dbo.InternalTransfers
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TrayStock2026_10_02;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-10-02: user reviewed the dry run (2 new tables created,
-- correct columns/constraints, 0 rows, no existing table touched) and
-- explicitly approved committing.
-- Verified backup: PlantsIMS2_Test_PreTrayStock_20261002.bak
----------------------------------------------------------------------------
IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TrayStock2026_10_02;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TrayStock2026_10_02;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
