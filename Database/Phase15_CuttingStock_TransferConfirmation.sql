-- ============================================================================
-- Phase 15: Cutting Stock + Internal Transfer confirmation workflow
-- ============================================================================
-- Purpose: database support for PHASE B of the redesign (Mother Plant
-- workflow: Take from Main Office / My Mother Plants / Enter Cutting /
-- Give to Main Office / My Transactions). See
-- claude/plantstockmanager-phase2-redesign-plan.md and the two decisions
-- confirmed with the business owner immediately before this script was
-- written:
--
--   1) Cuttings get their OWN Area-scoped stock table, dbo.CuttingStock,
--      with its own dedicated ledger dbo.CuttingStockTransactions -- per
--      Decision 1 in PROJECT_DOCUMENTATION.md ("every stock-bearing
--      entity gets its own dedicated transaction table"). This fills a
--      real gap: cuttings never had a location-scoped balance anywhere
--      (dbo.CuttingDeliveries, Phase 5, is a PRODUCTION-STAGE record --
--      Actual Cutting -> Propagation -- with no Area concept at all, and
--      is completely untouched by this script).
--
--   2) "Enter Cutting" is a fresh, independent action: it records a
--      harvest directly into CuttingStock at the Supervisor's own Area,
--      with NO CuttingPlan/ActualCutting link required. The existing
--      CuttingPlan -> ActualCutting -> CuttingDelivery -> PropagationBatch
--      chain (Phases 3-6) is left exactly as it is, untouched, for
--      whoever still reaches it directly by URL (Cutting Plan is hidden
--      from the normal menu, not deleted).
--
-- Confirmation model (dbo.InternalTransfers, extended, not replaced):
--   - A Cutting-type transfer is ALWAYS created with Status =
--     'PendingConfirmation'. Nothing is decremented or incremented on
--     EITHER side at creation time -- Quantity records what was SENT and
--     is never altered afterward ("must not silently change 500 to
--     480"). PendingConfirmationAreaId records which Main Office Area it
--     is awaiting confirmation at. DestinationAreaId is NOT yet known
--     (Main Office decides it at confirmation time), so it is nullable
--     for Cutting-type rows only -- CK_InternalTransfers_DestinationRequired
--     below still requires it for EmptyPot/PottedPlant transfers exactly
--     as before.
--   - At confirmation, Main Office enters ConfirmedQuantity (may differ
--     from Quantity) and picks the real DestinationAreaId. ONE movement
--     then happens: source CuttingStock -ConfirmedQuantity, destination
--     CuttingStock +ConfirmedQuantity -- never the original Quantity.
--     ConfirmedBy/ConfirmedDate are stamped; DiscrepancyReason is
--     required by the application whenever ConfirmedQuantity <> Quantity.
--   - This same Status/ConfirmedQuantity/ConfirmedBy/ConfirmedDate/
--     DiscrepancyReason shape is designed to be reused unchanged by
--     PHASE E (Outlet's Internal Area -> Outlet confirmation, which has
--     the identical "sent vs confirmed, reason if different" rule) --
--     built once now rather than twice, per "single source of truth per
--     stock domain."
--   - Every EXISTING EmptyPot/PottedPlant transfer behavior (Phase 8) is
--     completely unchanged: they are still created Status = 'Completed'
--     with both ledger entries written immediately, exactly as before.
--
-- Additive, idempotent, safe to re-run. No DROP TABLE, no DELETE. Run
-- this AFTER Phase14_RoleFoundation_AreaExtension.sql.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- PART 1: dbo.CuttingStock + dbo.CuttingStockTransactions
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CuttingStock' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.CuttingStock
    (
        Id               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CuttingStock PRIMARY KEY,
        SpeciesId        INT            NOT NULL,
        AreaId           INT            NOT NULL,
        PhysicalQuantity DECIMAL(18,2)  NOT NULL CONSTRAINT DF_CuttingStock_PhysicalQuantity DEFAULT (0),
        CreatedDate      DATETIME2      NOT NULL CONSTRAINT DF_CuttingStock_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy        NVARCHAR(100)  NULL,
        ModifiedDate     DATETIME2      NULL,
        ModifiedBy       NVARCHAR(100)  NULL,

        CONSTRAINT FK_CuttingStock_Species FOREIGN KEY (SpeciesId) REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_CuttingStock_Area    FOREIGN KEY (AreaId)    REFERENCES dbo.Area(Id),
        CONSTRAINT UQ_CuttingStock_Species_Area UNIQUE (SpeciesId, AreaId),
        CONSTRAINT CK_CuttingStock_PhysicalQuantity CHECK (PhysicalQuantity >= 0)
    );

    CREATE INDEX IX_CuttingStock_AreaId ON dbo.CuttingStock(AreaId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CuttingStockTransactions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.CuttingStockTransactions
    (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CuttingStockTx PRIMARY KEY,
        CuttingStockId  INT            NOT NULL,
        TransactionDate DATETIME2      NOT NULL CONSTRAINT DF_CuttingStockTx_TransactionDate DEFAULT (SYSUTCDATETIME()),
        -- 'Harvest' = Enter Cutting (fresh stock-in, no CuttingPlan/
        -- ActualCutting link). 'Transfer' = a confirmed Internal
        -- Transfer's source decrement / destination increase. 'Potted'
        -- is reserved for Phase D (Kiran consumes CuttingStock into
        -- PotProduction) rather than inventing a second ledger for that
        -- step, per the dedicated-ledger-per-entity decision.
        TransactionType NVARCHAR(30)   NOT NULL,
        ReferenceType   NVARCHAR(30)   NULL,
        ReferenceId     INT            NULL,
        Quantity        DECIMAL(18,2)  NOT NULL,
        BeforeQuantity  DECIMAL(18,2)  NOT NULL,
        AfterQuantity AS (BeforeQuantity + Quantity) PERSISTED,
        UserId          INT            NULL,
        Remarks         NVARCHAR(500)  NULL,
        CreatedAt       DATETIME2      NOT NULL CONSTRAINT DF_CuttingStockTx_CreatedAt DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT FK_CuttingStockTx_Stock FOREIGN KEY (CuttingStockId) REFERENCES dbo.CuttingStock(Id),
        CONSTRAINT FK_CuttingStockTx_User  FOREIGN KEY (UserId)         REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT CK_CuttingStockTx_Type  CHECK (TransactionType IN
            ('Harvest', 'Transfer', 'Potted', 'Adjustment', 'ReversalRemoval')),
        CONSTRAINT CK_CuttingStockTx_NeverNegativeAfter CHECK (BeforeQuantity + Quantity >= 0)
    );

    CREATE INDEX IX_CuttingStockTx_StockId   ON dbo.CuttingStockTransactions(CuttingStockId);
    CREATE INDEX IX_CuttingStockTx_Reference ON dbo.CuttingStockTransactions(ReferenceType, ReferenceId);
END
GO


-- ----------------------------------------------------------------------------
-- PART 2: dbo.InternalTransfers -- add Cutting stock type + confirmation
-- ----------------------------------------------------------------------------

-- 2a) SourceCuttingStockId: the third "which source pool" column,
--     alongside the existing SourceEmptyPotInventoryId/SourcePottedPlantStockId.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'SourceCuttingStockId')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD SourceCuttingStockId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InternalTransfers_SourceCutting')
BEGIN
    ALTER TABLE dbo.InternalTransfers
        ADD CONSTRAINT FK_InternalTransfers_SourceCutting FOREIGN KEY (SourceCuttingStockId) REFERENCES dbo.CuttingStock(Id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_InternalTransfers_SourceCutting' AND object_id = OBJECT_ID('dbo.InternalTransfers'))
BEGIN
    CREATE INDEX IX_InternalTransfers_SourceCutting ON dbo.InternalTransfers(SourceCuttingStockId);
END
GO

-- 2b) Confirmation columns.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'PendingConfirmationAreaId')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD PendingConfirmationAreaId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InternalTransfers_PendingConfirmationArea')
BEGIN
    ALTER TABLE dbo.InternalTransfers
        ADD CONSTRAINT FK_InternalTransfers_PendingConfirmationArea FOREIGN KEY (PendingConfirmationAreaId) REFERENCES dbo.Area(Id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'ConfirmedQuantity')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD ConfirmedQuantity DECIMAL(18,2) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'ConfirmedBy')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD ConfirmedBy INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InternalTransfers_ConfirmedBy')
BEGIN
    ALTER TABLE dbo.InternalTransfers
        ADD CONSTRAINT FK_InternalTransfers_ConfirmedBy FOREIGN KEY (ConfirmedBy) REFERENCES dbo.IMSUsers(Id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'ConfirmedDate')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD ConfirmedDate DATETIME2 NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'DiscrepancyReason')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD DiscrepancyReason NVARCHAR(500) NULL;
END
GO

-- 2c) DestinationAreaId: loosen to nullable -- ONLY Cutting-type transfers
--     are allowed to have it NULL (the destination isn't known until Main
--     Office confirms/routes); CK_InternalTransfers_DestinationRequired
--     below still requires it for EmptyPot/PottedPlant, exactly as the
--     original NOT NULL did.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.InternalTransfers') AND name = 'DestinationAreaId' AND is_nullable = 0
)
BEGIN
    ALTER TABLE dbo.InternalTransfers ALTER COLUMN DestinationAreaId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_DestinationRequired')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_DestinationRequired
        CHECK (StockType = 'Cutting' OR DestinationAreaId IS NOT NULL);
END
GO

-- 2d) Widen StockType to add 'Cutting' (drop + recreate, same idempotent
--     pattern Phase 8/9 used for other constraints -- safe to run every
--     time, produces the same widened definition each time).
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_StockType')
BEGIN
    ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_StockType;
END
GO
ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_StockType
    CHECK (StockType IN ('EmptyPot', 'PottedPlant', 'Cutting'));
GO

-- 2e) Widen Status to add 'PendingConfirmation' and 'Rejected'.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_Status')
BEGIN
    ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_Status;
END
GO
ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_Status
    CHECK (Status IN ('Completed', 'Cancelled', 'PendingConfirmation', 'Rejected'));
GO

-- 2f) Widen SourceMatchesStockType to add the Cutting branch.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_SourceMatchesStockType')
BEGIN
    ALTER TABLE dbo.InternalTransfers DROP CONSTRAINT CK_InternalTransfers_SourceMatchesStockType;
END
GO
ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_SourceMatchesStockType CHECK (
    (StockType = 'EmptyPot'    AND SourceEmptyPotInventoryId IS NOT NULL AND SourcePottedPlantStockId IS NULL AND SourceCuttingStockId IS NULL) OR
    (StockType = 'PottedPlant' AND SourcePottedPlantStockId  IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL) OR
    (StockType = 'Cutting'     AND SourceCuttingStockId       IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourcePottedPlantStockId IS NULL)
);
GO

-- 2g) ConfirmedQuantity, when present, must be non-negative.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_InternalTransfers_ConfirmedQuantity')
BEGIN
    ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_ConfirmedQuantity
        CHECK (ConfirmedQuantity IS NULL OR ConfirmedQuantity >= 0);
END
GO

-- ============================================================================
-- End of Phase 15. Every EXISTING EmptyPot/PottedPlant transfer row and
-- every EXISTING CuttingDeliveries/ActualCuttings/CuttingPlans row is
-- completely unaffected -- this script only adds new nullable columns,
-- widens two CHECK constraints, and adds two new tables.
-- ============================================================================
