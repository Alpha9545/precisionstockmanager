/* ============================================================
   Phase 5: Cutting Delivery
   ------------------------------------------------------------
   ADDITIVE ONLY.
     - Does not touch, alter, or drop any existing table.
     - Every CREATE is guarded with an existence check, so this
       script is safe to run more than once.
     - FKs to the EXISTING dbo.ActualCuttings (Phase 4), dbo.MotherPlants
       (Phase 2), dbo.PlantSpecies and dbo.IMSUsers tables. Reuses the
       EXISTING dbo.BatchNumberSequences table for the "CD-" prefix.

   Business meaning: a Cutting Delivery moves some of the GOOD cuttings
   recorded on an Actual Cutting record out to propagation. Delivery
   itself is not lossless -- handling/transit can cause further Loss,
   Removal (pulled for QC), Rejection or Damage. What actually survives
   to reach propagation is NetQuantity = DeliveredQuantity minus those
   four outcome buckets, and Phase 6 (Propagation Batch) will consume
   from THIS NetQuantity, never from DeliveredQuantity directly.

   A single Actual Cutting record can be delivered across MANY Cutting
   Delivery rows over time (partial deliveries) -- this table is a
   transaction history, never a single row that gets overwritten. The
   cumulative DeliveredQuantity across all delivery rows for one Actual
   Cutting can never exceed that Actual Cutting's GoodQuantity (the
   stock rule: production consumption must never exceed what is
   available), enforced the same two-layer way as Phase 3->4: a
   database CHECK-constraint backstop plus a concurrency-safe
   UPDLOCK/HOLDLOCK row lock in Data/CuttingDeliveryRepository.cs.

   Run this AFTER Phase4_ActualCutting.sql has been applied. Take a
   backup first per your own DB safety rules.
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CuttingDeliveries' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.CuttingDeliveries
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CuttingDeliveries PRIMARY KEY,
        DeliveryCode        NVARCHAR(20)   NOT NULL,
        ActualCuttingId     INT            NOT NULL,

        -- Denormalized copies of ActualCuttings.MotherPlantId/SpeciesId,
        -- always set server-side FROM the referenced Actual Cutting row
        -- (never independently chosen) -- kept here purely so
        -- traceability/reporting queries don't always need to join back
        -- through ActualCuttings->CuttingPlans. Enforced at the DB level
        -- by CK_CuttingDeliveries_MatchesActualCutting below.
        MotherPlantId       INT            NOT NULL,
        SpeciesId           INT            NOT NULL,

        DeliveryDate        DATE           NOT NULL,
        DeliveredQuantity   DECIMAL(18,2)  NOT NULL,   -- taken from ActualCuttings.GoodQuantity's pool
        LossQuantity        DECIMAL(18,2)  NOT NULL CONSTRAINT DF_CuttingDeliveries_LossQuantity    DEFAULT (0),
        RemovedQuantity     DECIMAL(18,2)  NOT NULL CONSTRAINT DF_CuttingDeliveries_RemovedQuantity DEFAULT (0),
        RejectedQuantity    DECIMAL(18,2)  NOT NULL CONSTRAINT DF_CuttingDeliveries_RejectedQuantity DEFAULT (0),
        DamagedQuantity     DECIMAL(18,2)  NOT NULL CONSTRAINT DF_CuttingDeliveries_DamagedQuantity  DEFAULT (0),

        -- What actually survives to reach propagation. Persisted computed
        -- column so it can never drift out of sync with its inputs --
        -- there is no code path (application or ad-hoc SQL) that can set
        -- this to an inconsistent value.
        NetQuantity AS (DeliveredQuantity - LossQuantity - RemovedQuantity - RejectedQuantity - DamagedQuantity) PERSISTED,

        Status              NVARCHAR(30)   NOT NULL CONSTRAINT DF_CuttingDeliveries_Status DEFAULT ('Completed'),
        ResponsiblePersonId INT            NULL,        -- Labour / responsible person, IMSUsers.Id
        SupervisorId        INT            NULL,
        Remarks             NVARCHAR(500)  NULL,
        CreatedDate         DATETIME2      NOT NULL CONSTRAINT DF_CuttingDeliveries_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy           NVARCHAR(100)  NULL,
        ModifiedDate        DATETIME2      NULL,
        ModifiedBy          NVARCHAR(100)  NULL,

        CONSTRAINT UQ_CuttingDeliveries_Code       UNIQUE (DeliveryCode),
        CONSTRAINT FK_CuttingDeliveries_ActualCutting FOREIGN KEY (ActualCuttingId)     REFERENCES dbo.ActualCuttings(Id),
        CONSTRAINT FK_CuttingDeliveries_MotherPlant   FOREIGN KEY (MotherPlantId)       REFERENCES dbo.MotherPlants(Id),
        CONSTRAINT FK_CuttingDeliveries_Species       FOREIGN KEY (SpeciesId)           REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_CuttingDeliveries_Responsible    FOREIGN KEY (ResponsiblePersonId) REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_CuttingDeliveries_Supervisor     FOREIGN KEY (SupervisorId)        REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_CuttingDeliveries_Status         CHECK (Status IN ('Completed', 'Cancelled')),
        CONSTRAINT CK_CuttingDeliveries_DeliveredQty   CHECK (DeliveredQuantity >= 0),
        CONSTRAINT CK_CuttingDeliveries_LossQty        CHECK (LossQuantity >= 0),
        CONSTRAINT CK_CuttingDeliveries_RemovedQty     CHECK (RemovedQuantity >= 0),
        CONSTRAINT CK_CuttingDeliveries_RejectedQty    CHECK (RejectedQuantity >= 0),
        CONSTRAINT CK_CuttingDeliveries_DamagedQty     CHECK (DamagedQuantity >= 0),
        -- The four loss-type buckets can never add up to more than what
        -- was actually delivered -- NetQuantity (computed above) must
        -- never go negative.
        CONSTRAINT CK_CuttingDeliveries_LossWithinDelivered
            CHECK (LossQuantity + RemovedQuantity + RejectedQuantity + DamagedQuantity <= DeliveredQuantity)
    );

    CREATE INDEX IX_CuttingDeliveries_ActualCuttingId ON dbo.CuttingDeliveries(ActualCuttingId);
    CREATE INDEX IX_CuttingDeliveries_MotherPlantId    ON dbo.CuttingDeliveries(MotherPlantId);
    CREATE INDEX IX_CuttingDeliveries_SpeciesId        ON dbo.CuttingDeliveries(SpeciesId);
    CREATE INDEX IX_CuttingDeliveries_Status           ON dbo.CuttingDeliveries(Status);
END
GO

-- Defense-in-depth #1: MotherPlantId/SpeciesId on a Cutting Delivery must
-- always match the MotherPlantId/SpeciesId of its own Actual Cutting.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_CuttingDelivery_MatchesActualCutting' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_CuttingDelivery_MatchesActualCutting(@ActualCuttingId INT, @MotherPlantId INT, @SpeciesId INT)
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        IF EXISTS (
            SELECT 1 FROM dbo.ActualCuttings
            WHERE Id = @ActualCuttingId
              AND MotherPlantId = @MotherPlantId
              AND SpeciesId = @SpeciesId
        )
            SET @Result = 1;
        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CuttingDeliveries_MatchesActualCutting')
BEGIN
    ALTER TABLE dbo.CuttingDeliveries
        ADD CONSTRAINT CK_CuttingDeliveries_MatchesActualCutting
        CHECK (dbo.fn_CuttingDelivery_MatchesActualCutting(ActualCuttingId, MotherPlantId, SpeciesId) = 1);
END
GO

-- Defense-in-depth #2: the STOCK RULE "consumption must never exceed
-- available input" applied to this stage -- the sum of DeliveredQuantity
-- recorded against an Actual Cutting (across every Cutting Delivery row
-- referencing it, including this one) must never exceed that Actual
-- Cutting's GoodQuantity. This is a database-level backstop; the real
-- concurrency-safe enforcement is in Data/CuttingDeliveryRepository.cs,
-- which takes a row lock on the parent Actual Cutting (SELECT ... WITH
-- (UPDLOCK, HOLDLOCK)) before computing the running total and inserting,
-- exactly like Phase 4's ActualCuttingRepository does against CuttingPlans.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_CuttingDelivery_WithinGoodQuantity' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_CuttingDelivery_WithinGoodQuantity(@Id INT, @ActualCuttingId INT, @DeliveredQuantity DECIMAL(18,2))
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        DECLARE @GoodQuantity DECIMAL(18,2);
        DECLARE @OtherRowsTotal DECIMAL(18,2);

        SELECT @GoodQuantity = GoodQuantity FROM dbo.ActualCuttings WHERE Id = @ActualCuttingId;

        SELECT @OtherRowsTotal = ISNULL(SUM(DeliveredQuantity), 0)
        FROM dbo.CuttingDeliveries
        WHERE ActualCuttingId = @ActualCuttingId AND Id <> @Id AND Status <> ''Cancelled'';

        IF @GoodQuantity IS NOT NULL AND (@OtherRowsTotal + @DeliveredQuantity) <= @GoodQuantity
            SET @Result = 1;

        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CuttingDeliveries_WithinGoodQuantity')
BEGIN
    ALTER TABLE dbo.CuttingDeliveries
        ADD CONSTRAINT CK_CuttingDeliveries_WithinGoodQuantity
        CHECK (dbo.fn_CuttingDelivery_WithinGoodQuantity(Id, ActualCuttingId, DeliveredQuantity) = 1);
END
GO
