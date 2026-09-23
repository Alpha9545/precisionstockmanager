/* ============================================================
   Phase 7: Pot Production
     - dbo.EmptyPotInventory / dbo.EmptyPotInventoryTransactions
     - dbo.PottedPlantStock  / dbo.PottedPlantStockTransactions
     - dbo.PotProduction
   ------------------------------------------------------------
   ADDITIVE ONLY.
     - Does not touch, alter, or drop any existing table.
     - Every CREATE is guarded with an existence check, so this
       script is safe to run more than once.
     - Reuses the EXISTING dbo.BatchNumberSequences table for the
       "POT-" prefix.

   ARCHITECTURE NOTE (per the approved Decision 1): this phase
   introduces the first two genuine STOCK-bearing entities in the
   system besides the pre-existing dbo.Inventory and
   dbo.SeedCuttingBank. Per the explicit decision to use separate
   dedicated ledger tables (never a single polymorphic StockLedger),
   each gets its own transaction-history table:
       dbo.EmptyPotInventory       -> dbo.EmptyPotInventoryTransactions
       dbo.PottedPlantStock        -> dbo.PottedPlantStockTransactions
   Both ledgers carry, at minimum, the fields the decision specified:
   Id, TransactionDate, TransactionType, ReferenceId, Quantity,
   BeforeQuantity, AfterQuantity, UserId, Remarks, CreatedAt.
   Quantity is a SIGNED delta (positive = stock added, negative = stock
   consumed/removed) and AfterQuantity is a PERSISTED computed column
   (BeforeQuantity + Quantity) so the ledger can never drift out of sync
   with its own arithmetic.

   Run this AFTER Phase6_PropagationBatch.sql has been applied. Take a
   backup first per your own DB safety rules.
   ============================================================ */

-- ------------------------------------------------------------
-- 1) Empty Pot Inventory (stock) + its ledger
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmptyPotInventory' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.EmptyPotInventory
    (
        Id               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmptyPotInventory PRIMARY KEY,
        PotSize          NVARCHAR(50)   NOT NULL,   -- business key, e.g. "4 inch", "6 inch", "Grow Bag Large"
        PhysicalQuantity DECIMAL(18,2)  NOT NULL CONSTRAINT DF_EmptyPotInventory_PhysicalQuantity DEFAULT (0),
        IsActive         BIT            NOT NULL CONSTRAINT DF_EmptyPotInventory_IsActive DEFAULT (1),
        CreatedDate      DATETIME2      NOT NULL CONSTRAINT DF_EmptyPotInventory_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy        NVARCHAR(100)  NULL,
        ModifiedDate     DATETIME2      NULL,
        ModifiedBy       NVARCHAR(100)  NULL,

        CONSTRAINT UQ_EmptyPotInventory_PotSize UNIQUE (PotSize),
        CONSTRAINT CK_EmptyPotInventory_PhysicalQuantity CHECK (PhysicalQuantity >= 0)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmptyPotInventoryTransactions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.EmptyPotInventoryTransactions
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmptyPotInventoryTransactions PRIMARY KEY,
        EmptyPotInventoryId INT            NOT NULL,
        TransactionDate     DATETIME2      NOT NULL CONSTRAINT DF_EmptyPotInvTx_TransactionDate DEFAULT (SYSUTCDATETIME()),
        TransactionType     NVARCHAR(30)   NOT NULL,   -- 'StockIn' | 'Consumption' | 'Adjustment' | 'ReversalReturn'
        ReferenceType       NVARCHAR(30)   NULL,        -- e.g. 'PotProduction', 'ManualAdjustment'
        ReferenceId         INT            NULL,        -- e.g. PotProduction.Id; NULL for manual stock-in/adjustment
        Quantity            DECIMAL(18,2)  NOT NULL,    -- SIGNED delta: positive = added, negative = consumed
        BeforeQuantity      DECIMAL(18,2)  NOT NULL,
        AfterQuantity AS (BeforeQuantity + Quantity) PERSISTED,
        UserId              INT            NULL,
        Remarks             NVARCHAR(500)  NULL,
        CreatedAt           DATETIME2      NOT NULL CONSTRAINT DF_EmptyPotInvTx_CreatedAt DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT FK_EmptyPotInvTx_Inventory FOREIGN KEY (EmptyPotInventoryId) REFERENCES dbo.EmptyPotInventory(Id),
        CONSTRAINT FK_EmptyPotInvTx_User      FOREIGN KEY (UserId)              REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT CK_EmptyPotInvTx_Type      CHECK (TransactionType IN ('StockIn', 'Consumption', 'Adjustment', 'ReversalReturn')),
        -- The ledger's own arithmetic must be internally consistent --
        -- this is a redundant backstop since AfterQuantity is a computed
        -- column, kept for clarity and defense-in-depth.
        CONSTRAINT CK_EmptyPotInvTx_NeverNegativeAfter CHECK (BeforeQuantity + Quantity >= 0)
    );

    CREATE INDEX IX_EmptyPotInvTx_InventoryId ON dbo.EmptyPotInventoryTransactions(EmptyPotInventoryId);
    CREATE INDEX IX_EmptyPotInvTx_Reference    ON dbo.EmptyPotInventoryTransactions(ReferenceType, ReferenceId);
END
GO

-- ------------------------------------------------------------
-- 2) Potted Plant Stock (stock) + its ledger
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PottedPlantStock' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PottedPlantStock
    (
        Id                     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PottedPlantStock PRIMARY KEY,
        SpeciesId              INT            NOT NULL,
        PotSize                NVARCHAR(50)   NOT NULL,
        PhysicalQuantity       DECIMAL(18,2)  NOT NULL CONSTRAINT DF_PottedPlantStock_PhysicalQuantity DEFAULT (0),
        ReservedQuantity       DECIMAL(18,2)  NOT NULL CONSTRAINT DF_PottedPlantStock_ReservedQuantity DEFAULT (0),
        SoldDispatchedQuantity DECIMAL(18,2)  NOT NULL CONSTRAINT DF_PottedPlantStock_SoldDispatchedQuantity DEFAULT (0), -- cumulative, informational running total
        WastedQuantity         DECIMAL(18,2)  NOT NULL CONSTRAINT DF_PottedPlantStock_WastedQuantity DEFAULT (0),        -- cumulative, informational running total
        AvailableQuantity AS (PhysicalQuantity - ReservedQuantity) PERSISTED,   -- Available = Physical - Reserved, per the stock rule
        CreatedDate            DATETIME2      NOT NULL CONSTRAINT DF_PottedPlantStock_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy              NVARCHAR(100)  NULL,
        ModifiedDate           DATETIME2      NULL,
        ModifiedBy             NVARCHAR(100)  NULL,

        CONSTRAINT UQ_PottedPlantStock_SpeciesPotSize UNIQUE (SpeciesId, PotSize),
        CONSTRAINT FK_PottedPlantStock_Species  FOREIGN KEY (SpeciesId) REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_PottedPlantStock_PotSize  FOREIGN KEY (PotSize)   REFERENCES dbo.EmptyPotInventory(PotSize),

        CONSTRAINT CK_PottedPlantStock_Physical CHECK (PhysicalQuantity >= 0),
        CONSTRAINT CK_PottedPlantStock_Reserved CHECK (ReservedQuantity >= 0),
        CONSTRAINT CK_PottedPlantStock_SoldDispatched CHECK (SoldDispatchedQuantity >= 0),
        CONSTRAINT CK_PottedPlantStock_Wasted   CHECK (WastedQuantity >= 0),
        -- Reserved can never exceed what physically exists -- otherwise
        -- Available would go negative, which the stock rules forbid.
        CONSTRAINT CK_PottedPlantStock_ReservedWithinPhysical CHECK (ReservedQuantity <= PhysicalQuantity)
    );

    CREATE INDEX IX_PottedPlantStock_SpeciesId ON dbo.PottedPlantStock(SpeciesId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PottedPlantStockTransactions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PottedPlantStockTransactions
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PottedPlantStockTransactions PRIMARY KEY,
        PottedPlantStockId  INT            NOT NULL,
        TransactionDate     DATETIME2      NOT NULL CONSTRAINT DF_PottedStockTx_TransactionDate DEFAULT (SYSUTCDATETIME()),
        -- 'Production' (Phase 7) is the only type actually written so
        -- far. 'Reservation'/'ReservationRelease'/'Dispatch'/'Wastage'/
        -- 'Transfer'/'Adjustment'/'ReversalRemoval' are reserved for
        -- Phases 8-10 to reuse this SAME ledger rather than inventing a
        -- new one, per the "dedicated ledger per stock entity" decision.
        TransactionType     NVARCHAR(30)   NOT NULL,
        ReferenceType       NVARCHAR(30)   NULL,        -- e.g. 'PotProduction'
        ReferenceId         INT            NULL,        -- e.g. PotProduction.Id
        Quantity            DECIMAL(18,2)  NOT NULL,    -- SIGNED delta applied to PhysicalQuantity
        BeforeQuantity      DECIMAL(18,2)  NOT NULL,    -- PhysicalQuantity before this transaction
        AfterQuantity AS (BeforeQuantity + Quantity) PERSISTED,
        UserId              INT            NULL,
        Remarks             NVARCHAR(500)  NULL,
        CreatedAt           DATETIME2      NOT NULL CONSTRAINT DF_PottedStockTx_CreatedAt DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT FK_PottedStockTx_Stock FOREIGN KEY (PottedPlantStockId) REFERENCES dbo.PottedPlantStock(Id),
        CONSTRAINT FK_PottedStockTx_User  FOREIGN KEY (UserId)             REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT CK_PottedStockTx_Type  CHECK (TransactionType IN
            ('Production', 'Reservation', 'ReservationRelease', 'Dispatch', 'Wastage', 'Transfer', 'Adjustment', 'ReversalRemoval')),
        CONSTRAINT CK_PottedStockTx_NeverNegativeAfter CHECK (BeforeQuantity + Quantity >= 0)
    );

    CREATE INDEX IX_PottedStockTx_StockId  ON dbo.PottedPlantStockTransactions(PottedPlantStockId);
    CREATE INDEX IX_PottedStockTx_Reference ON dbo.PottedPlantStockTransactions(ReferenceType, ReferenceId);
END
GO

-- ------------------------------------------------------------
-- 3) Pot Production (the production event linking the two stocks
--    above to the Propagation Batch that supplies the plant material)
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PotProduction' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PotProduction
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PotProduction PRIMARY KEY,
        ProductionCode      NVARCHAR(20)   NOT NULL,
        PropagationBatchId  INT            NOT NULL,

        -- Denormalized copies of PropagationBatches.MotherPlantId/SpeciesId,
        -- always set server-side FROM the referenced batch (never
        -- independently chosen) -- enforced by
        -- CK_PotProduction_MatchesPropagationBatch below.
        MotherPlantId       INT            NOT NULL,
        SpeciesId           INT            NOT NULL,

        PotSize             NVARCHAR(50)   NOT NULL,   -- which EmptyPotInventory pool this draws from
        ProductionDate      DATE           NOT NULL,
        Quantity            DECIMAL(18,2)  NOT NULL,   -- number of pots consumed = number of potted plants produced

        Status              NVARCHAR(30)   NOT NULL CONSTRAINT DF_PotProduction_Status DEFAULT ('Completed'),
        ResponsiblePersonId INT            NULL,
        SupervisorId        INT            NULL,
        Remarks             NVARCHAR(500)  NULL,
        CreatedDate         DATETIME2      NOT NULL CONSTRAINT DF_PotProduction_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy           NVARCHAR(100)  NULL,
        ModifiedDate        DATETIME2      NULL,
        ModifiedBy          NVARCHAR(100)  NULL,

        CONSTRAINT UQ_PotProduction_Code              UNIQUE (ProductionCode),
        CONSTRAINT FK_PotProduction_PropagationBatch  FOREIGN KEY (PropagationBatchId)  REFERENCES dbo.PropagationBatches(Id),
        CONSTRAINT FK_PotProduction_MotherPlant       FOREIGN KEY (MotherPlantId)        REFERENCES dbo.MotherPlants(Id),
        CONSTRAINT FK_PotProduction_Species           FOREIGN KEY (SpeciesId)            REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_PotProduction_PotSize            FOREIGN KEY (PotSize)              REFERENCES dbo.EmptyPotInventory(PotSize),
        CONSTRAINT FK_PotProduction_Responsible        FOREIGN KEY (ResponsiblePersonId)  REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_PotProduction_Supervisor         FOREIGN KEY (SupervisorId)         REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_PotProduction_Status   CHECK (Status IN ('Completed', 'Cancelled')),
        CONSTRAINT CK_PotProduction_Quantity CHECK (Quantity > 0)
    );

    CREATE INDEX IX_PotProduction_PropagationBatchId ON dbo.PotProduction(PropagationBatchId);
    CREATE INDEX IX_PotProduction_MotherPlantId       ON dbo.PotProduction(MotherPlantId);
    CREATE INDEX IX_PotProduction_SpeciesId           ON dbo.PotProduction(SpeciesId);
    CREATE INDEX IX_PotProduction_PotSize             ON dbo.PotProduction(PotSize);
    CREATE INDEX IX_PotProduction_Status              ON dbo.PotProduction(Status);
END
GO

-- Defense-in-depth #1: MotherPlantId/SpeciesId on a Pot Production
-- record must always match the MotherPlantId/SpeciesId of its own
-- Propagation Batch.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_PotProduction_MatchesPropagationBatch' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_PotProduction_MatchesPropagationBatch(@PropagationBatchId INT, @MotherPlantId INT, @SpeciesId INT)
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        IF EXISTS (
            SELECT 1 FROM dbo.PropagationBatches
            WHERE Id = @PropagationBatchId
              AND MotherPlantId = @MotherPlantId
              AND SpeciesId = @SpeciesId
        )
            SET @Result = 1;
        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PotProduction_MatchesPropagationBatch')
BEGIN
    ALTER TABLE dbo.PotProduction
        ADD CONSTRAINT CK_PotProduction_MatchesPropagationBatch
        CHECK (dbo.fn_PotProduction_MatchesPropagationBatch(PropagationBatchId, MotherPlantId, SpeciesId) = 1);
END
GO

-- Defense-in-depth #2: the STOCK RULE "consumption must never exceed
-- available input" applied to this stage -- the sum of Quantity potted
-- (across every non-Cancelled Pot Production row referencing it,
-- including this one) must never exceed that Propagation Batch's
-- SurvivedQuantity. Database-level backstop; the real concurrency-safe
-- enforcement is in Data/PotProductionRepository.cs, which takes a row
-- lock on the parent Propagation Batch (SELECT ... WITH (UPDLOCK,
-- HOLDLOCK)) before computing the running total and inserting, exactly
-- like every earlier phase in this chain.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_PotProduction_WithinSurvivedQuantity' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_PotProduction_WithinSurvivedQuantity(@Id INT, @PropagationBatchId INT, @Quantity DECIMAL(18,2))
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        DECLARE @SurvivedQuantity DECIMAL(18,2);
        DECLARE @OtherRowsTotal DECIMAL(18,2);

        SELECT @SurvivedQuantity = SurvivedQuantity FROM dbo.PropagationBatches WHERE Id = @PropagationBatchId;

        SELECT @OtherRowsTotal = ISNULL(SUM(Quantity), 0)
        FROM dbo.PotProduction
        WHERE PropagationBatchId = @PropagationBatchId AND Id <> @Id AND Status <> ''Cancelled'';

        IF @SurvivedQuantity IS NOT NULL AND (@OtherRowsTotal + @Quantity) <= @SurvivedQuantity
            SET @Result = 1;

        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PotProduction_WithinSurvivedQuantity')
BEGIN
    ALTER TABLE dbo.PotProduction
        ADD CONSTRAINT CK_PotProduction_WithinSurvivedQuantity
        CHECK (dbo.fn_PotProduction_WithinSurvivedQuantity(Id, PropagationBatchId, Quantity) = 1);
END
GO
