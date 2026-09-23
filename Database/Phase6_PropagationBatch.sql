/* ============================================================
   Phase 6: Propagation Batch
   ------------------------------------------------------------
   ADDITIVE ONLY.
     - Does not touch, alter, or drop any existing table.
     - Every CREATE is guarded with an existence check, so this
       script is safe to run more than once.
     - FKs to the EXISTING dbo.CuttingDeliveries (Phase 5), dbo.MotherPlants
       (Phase 2), dbo.PlantSpecies, dbo.Area (Phase 2) and dbo.IMSUsers
       tables. Reuses the EXISTING dbo.BatchNumberSequences table for the
       "PROP-" prefix.

   Business meaning: a Propagation Batch takes some of the NetQuantity
   surviving a Cutting Delivery and puts it into propagation (rooting)
   trays/beds. Unlike Phases 3-5, this stage has an explicit LIFECYCLE --
   a batch starts 'Propagating', and is later moved to 'ReadyForPotting'
   or 'Completed' once the grower records how many cuttings actually
   rooted (SurvivedQuantity) versus how many were lost during propagation
   (LossQuantity), or 'Cancelled' if the whole batch fails. Phase 7 (Pot
   Production) will consume from SurvivedQuantity, never from the
   original Quantity planted.

   NOTE ON PLACEMENT: AreaId is optional and, on purpose, is NOT required
   to belong to the same Polyhouse as the source Mother Plant -- a
   propagation nursery is typically a physically separate area from
   where mother plants are grown, so no polyhouse-matching constraint is
   applied here (contrast with MotherPlants.AreaId in Phase 2, which DOES
   have to match its own Polyhouse). If this assumption is wrong for your
   operation, flag it and the AreaId FK can be tightened later.

   Run this AFTER Phase5_CuttingDelivery.sql has been applied. Take a
   backup first per your own DB safety rules.
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PropagationBatches' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PropagationBatches
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PropagationBatches PRIMARY KEY,
        BatchCode           NVARCHAR(20)   NOT NULL,
        CuttingDeliveryId   INT            NOT NULL,

        -- Denormalized copies of CuttingDeliveries.MotherPlantId/SpeciesId,
        -- always set server-side FROM the referenced Cutting Delivery row
        -- (never independently chosen) -- kept here purely so
        -- traceability/reporting queries don't always need to join back
        -- through CuttingDeliveries->ActualCuttings->CuttingPlans.
        -- Enforced at the DB level by
        -- CK_PropagationBatches_MatchesCuttingDelivery below.
        MotherPlantId       INT            NOT NULL,
        SpeciesId           INT            NOT NULL,

        AreaId              INT            NULL,        -- where the propagation trays/beds are; not required to match the Mother Plant's Polyhouse
        PropagationDate     DATE           NOT NULL,
        Quantity            DECIMAL(18,2)  NOT NULL,    -- taken from CuttingDeliveries.NetQuantity's pool
        SurvivedQuantity    DECIMAL(18,2)  NOT NULL CONSTRAINT DF_PropagationBatches_SurvivedQuantity DEFAULT (0),
        LossQuantity        DECIMAL(18,2)  NOT NULL CONSTRAINT DF_PropagationBatches_LossQuantity     DEFAULT (0),
        Status              NVARCHAR(30)   NOT NULL CONSTRAINT DF_PropagationBatches_Status DEFAULT ('Propagating'),
        ResponsiblePersonId INT            NULL,        -- Labour / responsible person, IMSUsers.Id
        SupervisorId        INT            NULL,
        Remarks             NVARCHAR(500)  NULL,
        CreatedDate         DATETIME2      NOT NULL CONSTRAINT DF_PropagationBatches_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy           NVARCHAR(100)  NULL,
        ModifiedDate        DATETIME2      NULL,
        ModifiedBy          NVARCHAR(100)  NULL,

        CONSTRAINT UQ_PropagationBatches_Code           UNIQUE (BatchCode),
        CONSTRAINT FK_PropagationBatches_CuttingDelivery FOREIGN KEY (CuttingDeliveryId)   REFERENCES dbo.CuttingDeliveries(Id),
        CONSTRAINT FK_PropagationBatches_MotherPlant     FOREIGN KEY (MotherPlantId)       REFERENCES dbo.MotherPlants(Id),
        CONSTRAINT FK_PropagationBatches_Species         FOREIGN KEY (SpeciesId)           REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_PropagationBatches_Area            FOREIGN KEY (AreaId)              REFERENCES dbo.Area(Id),
        CONSTRAINT FK_PropagationBatches_Responsible     FOREIGN KEY (ResponsiblePersonId) REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_PropagationBatches_Supervisor      FOREIGN KEY (SupervisorId)        REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_PropagationBatches_Status          CHECK (Status IN ('Propagating', 'ReadyForPotting', 'Completed', 'Cancelled')),
        CONSTRAINT CK_PropagationBatches_Quantity        CHECK (Quantity >= 0),
        CONSTRAINT CK_PropagationBatches_SurvivedQty     CHECK (SurvivedQuantity >= 0),
        CONSTRAINT CK_PropagationBatches_LossQty         CHECK (LossQuantity >= 0),
        -- Survived + Loss can never exceed what was actually planted into
        -- propagation. While Status = 'Propagating' the two buckets may
        -- still be 0/0 (not yet assessed); the application layer requires
        -- them to add up EXACTLY to Quantity before allowing a transition
        -- to 'ReadyForPotting'/'Completed' (see
        -- Data/PropagationBatchRepository.cs), matching the same
        -- reconciliation spirit as Phase 4's Good+Damaged+Rejected rule.
        CONSTRAINT CK_PropagationBatches_SurvivedLossWithinQty
            CHECK (SurvivedQuantity + LossQuantity <= Quantity)
    );

    CREATE INDEX IX_PropagationBatches_CuttingDeliveryId ON dbo.PropagationBatches(CuttingDeliveryId);
    CREATE INDEX IX_PropagationBatches_MotherPlantId      ON dbo.PropagationBatches(MotherPlantId);
    CREATE INDEX IX_PropagationBatches_SpeciesId          ON dbo.PropagationBatches(SpeciesId);
    CREATE INDEX IX_PropagationBatches_AreaId             ON dbo.PropagationBatches(AreaId);
    CREATE INDEX IX_PropagationBatches_Status             ON dbo.PropagationBatches(Status);
END
GO

-- Defense-in-depth #1: MotherPlantId/SpeciesId on a Propagation Batch
-- must always match the MotherPlantId/SpeciesId of its own Cutting
-- Delivery.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_PropagationBatch_MatchesCuttingDelivery' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_PropagationBatch_MatchesCuttingDelivery(@CuttingDeliveryId INT, @MotherPlantId INT, @SpeciesId INT)
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        IF EXISTS (
            SELECT 1 FROM dbo.CuttingDeliveries
            WHERE Id = @CuttingDeliveryId
              AND MotherPlantId = @MotherPlantId
              AND SpeciesId = @SpeciesId
        )
            SET @Result = 1;
        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PropagationBatches_MatchesCuttingDelivery')
BEGIN
    ALTER TABLE dbo.PropagationBatches
        ADD CONSTRAINT CK_PropagationBatches_MatchesCuttingDelivery
        CHECK (dbo.fn_PropagationBatch_MatchesCuttingDelivery(CuttingDeliveryId, MotherPlantId, SpeciesId) = 1);
END
GO

-- Defense-in-depth #2: the STOCK RULE "consumption must never exceed
-- available input" applied to this stage -- the sum of Quantity planted
-- into propagation (across every non-Cancelled Propagation Batch row
-- referencing it, including this one) must never exceed that Cutting
-- Delivery's NetQuantity. This is a database-level backstop; the real
-- concurrency-safe enforcement is in
-- Data/PropagationBatchRepository.cs, which takes a row lock on the
-- parent Cutting Delivery (SELECT ... WITH (UPDLOCK, HOLDLOCK)) before
-- computing the running total and inserting, exactly like Phase 5's
-- CuttingDeliveryRepository does against ActualCuttings.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_PropagationBatch_WithinNetQuantity' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_PropagationBatch_WithinNetQuantity(@Id INT, @CuttingDeliveryId INT, @Quantity DECIMAL(18,2))
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        DECLARE @NetQuantity DECIMAL(18,2);
        DECLARE @OtherRowsTotal DECIMAL(18,2);

        SELECT @NetQuantity = NetQuantity FROM dbo.CuttingDeliveries WHERE Id = @CuttingDeliveryId;

        SELECT @OtherRowsTotal = ISNULL(SUM(Quantity), 0)
        FROM dbo.PropagationBatches
        WHERE CuttingDeliveryId = @CuttingDeliveryId AND Id <> @Id AND Status <> ''Cancelled'';

        IF @NetQuantity IS NOT NULL AND (@OtherRowsTotal + @Quantity) <= @NetQuantity
            SET @Result = 1;

        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PropagationBatches_WithinNetQuantity')
BEGIN
    ALTER TABLE dbo.PropagationBatches
        ADD CONSTRAINT CK_PropagationBatches_WithinNetQuantity
        CHECK (dbo.fn_PropagationBatch_WithinNetQuantity(Id, CuttingDeliveryId, Quantity) = 1);
END
GO
