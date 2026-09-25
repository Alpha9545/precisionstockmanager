/*
    Phase 30: Cutting Sowing / Tray Production.

    Adds a SECOND, entirely separate tray-production source alongside
    Direct Seed Sowing (dbo.SeedSowings, Phase 23/PhaseB) -- Cutting
    Sowing, consuming dbo.CuttingStock instead of dbo.SeedStock. Per the
    explicit "do not mix seed stock and cutting stock" instruction, this
    is a brand-new table, never a widened dbo.SeedSowings:

        Cutting Stock -> Cutting Sowing -> Tray/Cavity -> Complete Trays
        -> Ready Seedlings -> Wastage -> Ready Stock

    exactly mirroring the existing

        Seed Stock -> Direct Sowing -> Tray/Cavity -> Complete Trays
        -> Ready Seedlings -> Wastage -> Ready Stock

    Both pipelines share the SAME downstream dbo.ReadyStock /
    dbo.ReadyConfirmations tables (a Ready Stock seedling is fungible for
    Booking/Dispatch regardless of production origin) via a parallel
    nullable CuttingSowingId FK on each -- the identical dual-source
    pattern already used by dbo.PotProduction (PropagationBatchId /
    SourceCuttingStockId, Phase 19). Application code (Data/
    ReadyStockRepository.cs, Data/ReadyConfirmationRepository.cs,
    Data/SeedlingFulfilmentRepository.cs) reads whichever source produced
    a row via COALESCE, so Booking/Dispatch/allocation see cutting-sourced
    Ready Stock exactly like seed-sourced Ready Stock, with no separate
    pool. (Not touched by this phase, and therefore NOT source-aware yet:
    Data/ManagementDashboardRepository.cs's KPIs and Pages/Production/
    ReadyAlerts/Index.cshtml.cs's "ready soon" list -- both are read-only
    reporting features outside the scope this phase was asked to cover;
    they will simply not show Cutting Sowing batches until a later phase
    widens them the same way.)

    IMPORTANT -- the existing database-level tray/authority backstop
    (Database/PhaseB_ApprovalAndTrayRules.sql's
    TR_ReadyConfirmations_AssignedSupervisor trigger, Database/
    PhaseB_ReadyStockTrays.sql's TR_ReadyConfirmations_TrayQuantity
    trigger and FK_ReadyStock_SowingCavity foreign key) is deliberately
    LEFT UNTOUCHED. Every one of those objects is guarded to run only
    against PlantsIMS2_Test and is explicitly "not yet approved for
    production" per its own header comment; extending it to also cover
    CuttingSowings would mean editing several delicate, already-fragile
    triggers with special QUOTED_IDENTIFIER/ANSI_NULLS session
    requirements that cannot be verified from this environment (no
    reachable database). Instead, every rule this phase requires --
    whole trays, valid cavity, no negative/over-consumption quantities,
    server-only calculation, assigned-supervisor-only approval -- is
    enforced in Data/CuttingSowingRepository.cs and Data/
    ReadyConfirmationRepository.cs (ConfirmCuttingSowingAsync), under the
    same row locks, exactly like every other business rule added in
    Phases 1-4 of this engagement (the dominant, non-trigger pattern used
    everywhere except Direct Sowing/Ready Stock's own extra hardening).
    This mirrors the identical, already-reported decision made for Phase
    4's Cutting Delivery tray arithmetic. Direct Sowing itself is
    completely unaffected: its own trigger backstop keeps working exactly
    as before, on exactly the rows it always covered.

    Adds ONE new TransactionType to the existing dbo.CuttingStockTransactions
    ledger, 'Sown' (mirrors 'Sown' already added to dbo.SeedStockTransactions
    in Phase23_SeedSowing.sql for the identical purpose). 'ReversalReturn'
    already exists there (Phase19) and is reused as-is for cancelling a
    Cutting Sowing.

    Safe to run multiple times. No existing table, column, row, trigger
    or constraint is dropped, altered destructively, or has its behavior
    changed for any pre-existing SeedSowings-sourced row. Run after
    Phase15_CuttingStock_TransferConfirmation.sql and
    Phase25_ReadyConfirmation.sql / PhaseB_ReadyStockTrays.sql;
    independent of every other phase.
*/

SET XACT_ABORT ON;
GO

-- ---- 1. dbo.CuttingSowings (brand new; the full, final shape is baked in
--         from day one -- there is no legacy data to grandfather, unlike
--         dbo.SeedSowings' incremental history). --------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CuttingSowings' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.CuttingSowings
    (
        Id                   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CuttingSowings PRIMARY KEY,
        SowingCode           NVARCHAR(20)   NOT NULL,

        SourceCuttingStockId INT            NOT NULL,

        -- Denormalized from the locked source CuttingStock row at Insert
        -- time -- never trusted from the caller. AreaId is always the
        -- source pool's own Area (no separate "growing Area" choice --
        -- a Cutting Sowing is sown in place).
        SpeciesId            INT            NOT NULL,
        AreaId               INT            NOT NULL,

        CavityType           NVARCHAR(30)   NOT NULL,
        NumberOfTrays        INT            NOT NULL,

        -- Quantity of CUTTING consumed (complete trays x cavity size) --
        -- this is what decrements CuttingStock.PhysicalQuantity.
        QuantitySown         DECIMAL(18,2)  NOT NULL,
        -- The raw cutting quantity entered on the form (mirrors
        -- SeedSowings.SeedQuantity); the remainder (CuttingQuantityEntered
        -- - QuantitySown, always < one tray) simply stays in the source
        -- Cutting Stock pool -- no separate column/concept for it.
        CuttingQuantityEntered DECIMAL(18,2) NOT NULL,

        SowingDate           DATETIME2      NOT NULL CONSTRAINT DF_CuttingSowings_SowingDate DEFAULT (SYSUTCDATETIME()),

        ReadyStockDays       INT            NULL,
        ExpectedReadyDate    DATE           NULL,

        WastageQuantity      DECIMAL(18,2)  NOT NULL CONSTRAINT DF_CuttingSowings_WastageQuantity DEFAULT (0),
        ConfirmedReadyQuantity DECIMAL(18,2) NOT NULL CONSTRAINT DF_CuttingSowings_ConfirmedReadyQuantity DEFAULT (0),

        -- 'Sown' -> 'Completed' (Ready + Wastage = Sown) or 'Cancelled'.
        Status               NVARCHAR(30)   NOT NULL CONSTRAINT DF_CuttingSowings_Status DEFAULT ('Sown'),

        -- Deliberately NO ResponsiblePersonId -- Responsible Person was
        -- retired from data entry across this whole application (Phase 1);
        -- a brand-new table never gets it back. Supervisor authorization
        -- alone is sufficient, per this phase's own instruction.
        SupervisorId         INT            NULL,
        Remarks              NVARCHAR(500)  NULL,

        CreatedDate          DATETIME2      NOT NULL CONSTRAINT DF_CuttingSowings_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy            NVARCHAR(100)  NULL,
        CreatedById          INT            NULL,
        ModifiedDate         DATETIME2      NULL,
        ModifiedBy           NVARCHAR(100)  NULL,

        CONSTRAINT UQ_CuttingSowings_Code UNIQUE (SowingCode),
        -- Mirrors UQ_SeedSowings_IdCavity -- the Ready Stock cavity FK below.
        CONSTRAINT UQ_CuttingSowings_IdCavity UNIQUE (Id, CavityType),
        CONSTRAINT FK_CuttingSowings_SourceCuttingStock FOREIGN KEY (SourceCuttingStockId) REFERENCES dbo.CuttingStock(Id),
        CONSTRAINT FK_CuttingSowings_Species    FOREIGN KEY (SpeciesId) REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_CuttingSowings_Area        FOREIGN KEY (AreaId)   REFERENCES dbo.Area(Id),
        CONSTRAINT FK_CuttingSowings_Supervisor   FOREIGN KEY (SupervisorId) REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_CuttingSowings_CreatedBy     FOREIGN KEY (CreatedById)  REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_CuttingSowings_Status CHECK (Status IN ('Sown', 'Completed', 'Cancelled')),
        CONSTRAINT CK_CuttingSowings_CavityType CHECK (CavityType IN ('9 Cavity', '24 Cavity', '42 Cavity', '102 Cavity', '150 Cavity')),
        CONSTRAINT CK_CuttingSowings_NumberOfTrays CHECK (NumberOfTrays >= 1),
        CONSTRAINT CK_CuttingSowings_QuantitySown CHECK (QuantitySown > 0),
        -- Seeds Used = trays x cavity (no fractional trays / invalid cavity
        -- ever reaches this table -- the application computes it, this is
        -- the backstop CHECK, same discipline as CK_SeedSowings_TrayCount).
        CONSTRAINT CK_CuttingSowings_TrayMath CHECK (
            QuantitySown = NumberOfTrays * CASE CavityType WHEN N'9 Cavity' THEN 9 WHEN N'24 Cavity' THEN 24
                                                            WHEN N'42 Cavity' THEN 42 WHEN N'102 Cavity' THEN 102
                                                            WHEN N'150 Cavity' THEN 150 END),
        -- Entered quantity is whole, at least what was sown, and the
        -- remainder never reaches a full extra tray (mirrors
        -- CK_SeedSowings_SeedQuantity).
        CONSTRAINT CK_CuttingSowings_CuttingQuantityEntered CHECK (
            CuttingQuantityEntered = FLOOR(CuttingQuantityEntered)
            AND CuttingQuantityEntered >= QuantitySown
            AND CuttingQuantityEntered - QuantitySown < CASE CavityType WHEN N'9 Cavity' THEN 9 WHEN N'24 Cavity' THEN 24
                                                                        WHEN N'42 Cavity' THEN 42 WHEN N'102 Cavity' THEN 102
                                                                        WHEN N'150 Cavity' THEN 150 END),
        CONSTRAINT CK_CuttingSowings_WastageQuantity CHECK (WastageQuantity >= 0 AND WastageQuantity <= QuantitySown),
        CONSTRAINT CK_CuttingSowings_ConfirmedReadyQuantity CHECK (ConfirmedReadyQuantity >= 0 AND ConfirmedReadyQuantity <= QuantitySown),
        CONSTRAINT CK_CuttingSowings_ReadyStockDays CHECK (ReadyStockDays IS NULL OR ReadyStockDays > 0),
        -- A Completed batch's Ready + Wastage must exactly equal what was
        -- sown (mirrors CK_SeedSowings_CompletedAccounted).
        CONSTRAINT CK_CuttingSowings_CompletedAccounted CHECK (
            Status <> 'Completed' OR ConfirmedReadyQuantity + WastageQuantity = QuantitySown)
    );

    CREATE INDEX IX_CuttingSowings_AreaId ON dbo.CuttingSowings(AreaId);
    CREATE INDEX IX_CuttingSowings_SpeciesId ON dbo.CuttingSowings(SpeciesId);
    CREATE INDEX IX_CuttingSowings_SourceCuttingStockId ON dbo.CuttingSowings(SourceCuttingStockId);
END
GO

-- ---- 2. dbo.CuttingStockTransactions: one new TransactionType ----------
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CuttingStockTx_Type')
BEGIN
    ALTER TABLE dbo.CuttingStockTransactions DROP CONSTRAINT CK_CuttingStockTx_Type;
END
GO

ALTER TABLE dbo.CuttingStockTransactions ADD CONSTRAINT CK_CuttingStockTx_Type
    CHECK (TransactionType IN ('Harvest', 'Transfer', 'Potted', 'Adjustment', 'ReversalRemoval', 'Transplanted', 'ReversalReturn', 'Sown'));
GO

-- ---- 3. dbo.ReadyStock: dual source (SeedSowingId / CuttingSowingId) ---
IF COL_LENGTH('dbo.ReadyStock', 'CuttingSowingId') IS NULL
    ALTER TABLE dbo.ReadyStock ADD CuttingSowingId INT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ReadyStock_CuttingSowing')
    ALTER TABLE dbo.ReadyStock WITH CHECK ADD CONSTRAINT FK_ReadyStock_CuttingSowing
        FOREIGN KEY (CuttingSowingId) REFERENCES dbo.CuttingSowings(Id);
GO

-- Relax SeedSowingId to nullable (every existing row keeps its real,
-- non-null value -- ALTER COLUMN does not touch data) and replace its
-- UNIQUE constraint with a filtered unique index that still forbids two
-- rows sharing the same SeedSowingId, while allowing any number of NULLs
-- (Cutting-sourced rows).
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ReadyStock') AND name = 'SeedSowingId' AND is_nullable = 0)
BEGIN
    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_ReadyStock_SeedSowing')
        ALTER TABLE dbo.ReadyStock DROP CONSTRAINT UQ_ReadyStock_SeedSowing;
    ALTER TABLE dbo.ReadyStock ALTER COLUMN SeedSowingId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ReadyStock_SeedSowingId' AND object_id = OBJECT_ID('dbo.ReadyStock'))
    CREATE UNIQUE NONCLUSTERED INDEX UX_ReadyStock_SeedSowingId ON dbo.ReadyStock(SeedSowingId) WHERE SeedSowingId IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ReadyStock_CuttingSowingId' AND object_id = OBJECT_ID('dbo.ReadyStock'))
    CREATE UNIQUE NONCLUSTERED INDEX UX_ReadyStock_CuttingSowingId ON dbo.ReadyStock(CuttingSowingId) WHERE CuttingSowingId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStock_SourceType')
    ALTER TABLE dbo.ReadyStock ADD CONSTRAINT CK_ReadyStock_SourceType
        CHECK ((CASE WHEN SeedSowingId IS NOT NULL THEN 1 ELSE 0 END) + (CASE WHEN CuttingSowingId IS NOT NULL THEN 1 ELSE 0 END) = 1);
GO

-- Ready Stock inherits the CUTTING sowing's cavity too (mirrors
-- FK_ReadyStock_SowingCavity, which already protects the seed path and is
-- left untouched).
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ReadyStock_CuttingSowingCavity')
    ALTER TABLE dbo.ReadyStock WITH CHECK ADD CONSTRAINT FK_ReadyStock_CuttingSowingCavity
        FOREIGN KEY (CuttingSowingId, CavityType) REFERENCES dbo.CuttingSowings (Id, CavityType);
GO

-- ---- 4. dbo.ReadyConfirmations: dual source -----------------------------
IF COL_LENGTH('dbo.ReadyConfirmations', 'CuttingSowingId') IS NULL
    ALTER TABLE dbo.ReadyConfirmations ADD CuttingSowingId INT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ReadyConfirmations_CuttingSowing')
    ALTER TABLE dbo.ReadyConfirmations WITH CHECK ADD CONSTRAINT FK_ReadyConfirmations_CuttingSowing
        FOREIGN KEY (CuttingSowingId) REFERENCES dbo.CuttingSowings(Id);
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ReadyConfirmations') AND name = 'SeedSowingId' AND is_nullable = 0)
BEGIN
    ALTER TABLE dbo.ReadyConfirmations ALTER COLUMN SeedSowingId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyConfirmations_SourceType')
    ALTER TABLE dbo.ReadyConfirmations ADD CONSTRAINT CK_ReadyConfirmations_SourceType
        CHECK ((CASE WHEN SeedSowingId IS NOT NULL THEN 1 ELSE 0 END) + (CASE WHEN CuttingSowingId IS NOT NULL THEN 1 ELSE 0 END) = 1);
GO

-- At most one Confirmed approval per Cutting Sowing -- mirrors
-- UX_ReadyConfirmations_OneConfirmedPerSowing (left untouched, still
-- protecting the seed path).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ReadyConfirmations_OneConfirmedPerCuttingSowing'
                                           AND object_id = OBJECT_ID('dbo.ReadyConfirmations'))
    CREATE UNIQUE NONCLUSTERED INDEX UX_ReadyConfirmations_OneConfirmedPerCuttingSowing
        ON dbo.ReadyConfirmations (CuttingSowingId) WHERE Status = N'Confirmed';
GO
