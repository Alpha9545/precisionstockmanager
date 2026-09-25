/*
    Phase 31: Cutting to Potted Plant Production -- batches with daily
    production entries.

    Adds a BATCH header, dbo.PotProductionBatches, wrapping the existing
    dbo.PotProduction Cutting-sourced path (Phase 19's
    InsertFromCuttingStockAsync, unchanged in its own stock-movement
    logic) so more than one daily production entry can be recorded
    against ONE plan over several days --

        Cutting Stock -> select cutting quantity -> choose Potted Plant
        Production -> select Pot Size -> Expected Ready Date -> issue
        required empty pots to the assigned Area -> daily production
        (dbo.PotProduction rows, now linked via PotProductionBatchId)
        -> Potted Plant Stock -> Ready confirmation -> available for
        Main Office/Outlet/Customer

    matching the business's own worked example (Day 1 = 600, Day 2 =
    500, Day 3 = 200 -> Potted Stock shows 1,300) -- dbo.PottedPlantStock
    already accumulates correctly across many dbo.PotProduction rows
    (nothing changes there); what was missing was the ONE header that
    identifies the batch (Area, Variety/Species, Pot Size, Cutting
    source, Expected Ready Date, planned/allocated cutting quantity) and
    ties the daily rows together, plus a Ready confirmation step.

    No "Color" column: per the previously approved decision (D-4, this
    engagement) color is never a separate field -- the Variety/Species
    name already carries it where the business needs it ("Rose Red",
    "Hibiscus Pink", etc).

    No Responsible Person column, no second approval level: Ready
    confirmation is the ONE supervisor action this phase adds, reusing
    the EXISTING assigned-supervisor rule (Services/DirectSowingRules.cs
    CanApprove/CanCancelApproval, unchanged) -- the same rule Direct
    Sowing/Cutting Sowing already use, just against
    SupervisorKind.ProductionArea's eligible pool (the same Production
    Area supervisors PotProduction/CreateFromCutting.cshtml.cs already
    offers) rather than SupervisorKind.Sowing.

    "Do not allow an Area to use empty pots issued to another Area" is
    enforced twice, structurally: the batch's own EmptyPotInventoryId is
    fixed at creation to a pool already scoped to the batch's own Area
    (the existing EmptyPotInventory Area-scoping, Phase 8/14, is not
    touched), and every daily entry against the batch reuses that SAME
    fixed pool -- the daily entry page never lets the Area or Pot Size be
    re-chosen.

    Safe to run multiple times. Run after Phase19_CuttingToPotProduction.sql;
    independent of every other phase.
*/

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PotProductionBatches' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PotProductionBatches
    (
        Id                          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PotProductionBatches PRIMARY KEY,
        BatchCode                   NVARCHAR(20)  NOT NULL,

        SourceCuttingStockId        INT           NOT NULL,

        -- Denormalized from the locked source Cutting Stock row at
        -- Insert time -- never trusted from the caller. AreaId is
        -- always the source pool's own Area (production happens in
        -- place, same rule PotProduction/CreateFromCutting.cshtml.cs
        -- already uses for its own single-event path).
        SpeciesId                   INT           NOT NULL,
        AreaId                      INT           NOT NULL,

        PotSize                     NVARCHAR(50)  NOT NULL,
        EmptyPotInventoryId         INT           NOT NULL,

        ExpectedReadyDate           DATE          NOT NULL,

        -- Planned/allocated CUTTING quantity for this batch (rule 1) --
        -- what daily entries are budgeted against (rule 4), separate
        -- from and in addition to the underlying Cutting Stock pool's
        -- own physical availability check (unchanged, still enforced by
        -- PotProductionRepository.InsertFromCuttingStockAsync).
        PlannedCuttingQuantity       DECIMAL(18,2) NOT NULL,

        -- Running totals maintained ONLY by daily production entries
        -- (PotProductionRepository.InsertFromCuttingStockAsync, under
        -- this row's own lock) -- mirrors SeedSowings.ConfirmedReadyQuantity/
        -- PottedPlantBookings.DispatchedQuantity's "running total on the
        -- header" pattern exactly.
        CuttingQuantityConsumedTotal DECIMAL(18,2) NOT NULL CONSTRAINT DF_PotProductionBatches_ConsumedTotal DEFAULT (0),
        QuantityProducedTotal        DECIMAL(18,2) NOT NULL CONSTRAINT DF_PotProductionBatches_ProducedTotal DEFAULT (0),

        -- 'InProduction' -> 'Ready' (Supervisor confirms, rule 6) or
        -- 'Cancelled' (only while nothing has been produced yet).
        Status                       NVARCHAR(30)  NOT NULL CONSTRAINT DF_PotProductionBatches_Status DEFAULT ('InProduction'),

        -- Deliberately NO ResponsiblePersonId (rule 8) -- Supervisor
        -- authorization alone is sufficient, same as every table added
        -- since Phase 1 of this engagement.
        SupervisorId                 INT           NULL,
        Remarks                      NVARCHAR(500) NULL,

        CreatedDate                  DATETIME2     NOT NULL CONSTRAINT DF_PotProductionBatches_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy                    NVARCHAR(100) NULL,
        CreatedById                  INT           NULL,
        ModifiedDate                 DATETIME2     NULL,
        ModifiedBy                   NVARCHAR(100) NULL,

        CONSTRAINT UQ_PotProductionBatches_Code UNIQUE (BatchCode),
        CONSTRAINT FK_PotProductionBatches_SourceCuttingStock FOREIGN KEY (SourceCuttingStockId) REFERENCES dbo.CuttingStock(Id),
        CONSTRAINT FK_PotProductionBatches_Species    FOREIGN KEY (SpeciesId)            REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_PotProductionBatches_Area        FOREIGN KEY (AreaId)               REFERENCES dbo.Area(Id),
        CONSTRAINT FK_PotProductionBatches_EmptyPot     FOREIGN KEY (EmptyPotInventoryId)  REFERENCES dbo.EmptyPotInventory(Id),
        CONSTRAINT FK_PotProductionBatches_Supervisor   FOREIGN KEY (SupervisorId)         REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_PotProductionBatches_CreatedBy    FOREIGN KEY (CreatedById)          REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_PotProductionBatches_Status CHECK (Status IN ('InProduction', 'Ready', 'Cancelled')),
        CONSTRAINT CK_PotProductionBatches_PlannedCuttingQuantity CHECK (PlannedCuttingQuantity > 0),
        CONSTRAINT CK_PotProductionBatches_ConsumedTotal CHECK (CuttingQuantityConsumedTotal >= 0 AND CuttingQuantityConsumedTotal <= PlannedCuttingQuantity),
        CONSTRAINT CK_PotProductionBatches_ProducedTotal CHECK (QuantityProducedTotal >= 0 AND QuantityProducedTotal <= CuttingQuantityConsumedTotal)
    );

    CREATE INDEX IX_PotProductionBatches_AreaId ON dbo.PotProductionBatches(AreaId);
    CREATE INDEX IX_PotProductionBatches_SpeciesId ON dbo.PotProductionBatches(SpeciesId);
    CREATE INDEX IX_PotProductionBatches_SourceCuttingStockId ON dbo.PotProductionBatches(SourceCuttingStockId);
    CREATE INDEX IX_PotProductionBatches_ExpectedReadyDate ON dbo.PotProductionBatches(ExpectedReadyDate);
END
GO

-- Links each daily dbo.PotProduction entry back to its batch. NULL for
-- every existing row (the pre-existing single-event Cutting-sourced
-- path, PotProduction/CreateFromCutting.cshtml.cs, and the legacy
-- Propagation-Batch-sourced path) -- both keep working completely
-- unchanged; PotProductionBatchId is only ever set for a NEW row created
-- through the batch's own daily-entry page.
IF COL_LENGTH('dbo.PotProduction', 'PotProductionBatchId') IS NULL
    ALTER TABLE dbo.PotProduction ADD PotProductionBatchId INT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PotProduction_PotProductionBatch')
    ALTER TABLE dbo.PotProduction WITH CHECK ADD CONSTRAINT FK_PotProduction_PotProductionBatch
        FOREIGN KEY (PotProductionBatchId) REFERENCES dbo.PotProductionBatches(Id);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PotProduction_PotProductionBatchId' AND object_id = OBJECT_ID('dbo.PotProduction'))
    CREATE INDEX IX_PotProduction_PotProductionBatchId ON dbo.PotProduction(PotProductionBatchId);
GO
