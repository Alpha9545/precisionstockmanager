/* ============================================================
   Phase 4: Actual Cutting
   ------------------------------------------------------------
   ADDITIVE ONLY.
     - Does not touch, alter, or drop any existing table.
     - Every CREATE is guarded with an existence check, so this
       script is safe to run more than once.
     - FKs to the EXISTING dbo.CuttingPlans (Phase 3), dbo.MotherPlants
       (Phase 2), dbo.PlantSpecies and dbo.IMSUsers tables. Reuses the
       EXISTING dbo.BatchNumberSequences table for the "AC-" prefix.

   Run this AFTER Phase3_CuttingPlan.sql has been applied. Take a
   backup first per your own DB safety rules.
   ============================================================ */

-- Actual Cutting: what actually happened against a Cutting Plan.
-- MotherPlantId and SpeciesId are denormalized copies of
-- CuttingPlans.MotherPlantId/SpeciesId (kept here so reports and
-- traceability queries don't always need to join back through
-- CuttingPlans) -- the application layer always copies them FROM the
-- selected Cutting Plan, and the CHECK constraint below guarantees it
-- at the database level too.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ActualCuttings' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ActualCuttings
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ActualCuttings PRIMARY KEY,
        ActualCuttingCode   NVARCHAR(20)   NOT NULL,
        CuttingPlanId       INT            NOT NULL,
        MotherPlantId       INT            NOT NULL,
        SpeciesId           INT            NOT NULL,
        CuttingDate         DATE           NOT NULL,
        PlannedQuantity     DECIMAL(18,2)  NOT NULL,   -- snapshot of the plan's PlannedQuantity at the time this was recorded
        ActualQuantity      DECIMAL(18,2)  NOT NULL,
        GoodQuantity        DECIMAL(18,2)  NOT NULL,
        DamagedQuantity     DECIMAL(18,2)  NOT NULL CONSTRAINT DF_ActualCuttings_DamagedQuantity DEFAULT (0),
        RejectedQuantity    DECIMAL(18,2)  NOT NULL CONSTRAINT DF_ActualCuttings_RejectedQuantity DEFAULT (0),
        ResponsiblePersonId INT            NULL,        -- Labour / responsible person, IMSUsers.Id
        SupervisorId        INT            NULL,
        Remarks             NVARCHAR(500)  NULL,
        CreatedDate         DATETIME2      NOT NULL CONSTRAINT DF_ActualCuttings_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy           NVARCHAR(100)  NULL,
        ModifiedDate        DATETIME2      NULL,
        ModifiedBy          NVARCHAR(100)  NULL,

        CONSTRAINT UQ_ActualCuttings_Code          UNIQUE (ActualCuttingCode),
        CONSTRAINT FK_ActualCuttings_CuttingPlan     FOREIGN KEY (CuttingPlanId)       REFERENCES dbo.CuttingPlans(Id),
        CONSTRAINT FK_ActualCuttings_MotherPlant     FOREIGN KEY (MotherPlantId)       REFERENCES dbo.MotherPlants(Id),
        CONSTRAINT FK_ActualCuttings_Species         FOREIGN KEY (SpeciesId)           REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_ActualCuttings_Responsible      FOREIGN KEY (ResponsiblePersonId) REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_ActualCuttings_Supervisor       FOREIGN KEY (SupervisorId)        REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_ActualCuttings_ActualQuantity   CHECK (ActualQuantity >= 0),
        CONSTRAINT CK_ActualCuttings_GoodQuantity     CHECK (GoodQuantity >= 0),
        CONSTRAINT CK_ActualCuttings_DamagedQuantity  CHECK (DamagedQuantity >= 0),
        CONSTRAINT CK_ActualCuttings_RejectedQuantity CHECK (RejectedQuantity >= 0),
        -- The three outcome buckets must reconcile exactly to the actual
        -- quantity recorded -- nothing is allowed to silently vanish or
        -- be double-counted between Good/Damaged/Rejected.
        CONSTRAINT CK_ActualCuttings_Reconciliation   CHECK (GoodQuantity + DamagedQuantity + RejectedQuantity = ActualQuantity)
    );

    CREATE INDEX IX_ActualCuttings_CuttingPlanId ON dbo.ActualCuttings(CuttingPlanId);
    CREATE INDEX IX_ActualCuttings_MotherPlantId ON dbo.ActualCuttings(MotherPlantId);
    CREATE INDEX IX_ActualCuttings_SpeciesId     ON dbo.ActualCuttings(SpeciesId);
END
GO

-- Defense-in-depth #1: MotherPlantId/SpeciesId on an Actual Cutting must
-- always match the MotherPlantId/SpeciesId of its own Cutting Plan.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_ActualCutting_MatchesCuttingPlan' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_ActualCutting_MatchesCuttingPlan(@CuttingPlanId INT, @MotherPlantId INT, @SpeciesId INT)
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        IF EXISTS (
            SELECT 1 FROM dbo.CuttingPlans
            WHERE Id = @CuttingPlanId
              AND MotherPlantId = @MotherPlantId
              AND SpeciesId = @SpeciesId
        )
            SET @Result = 1;
        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ActualCuttings_MatchesCuttingPlan')
BEGIN
    ALTER TABLE dbo.ActualCuttings
        ADD CONSTRAINT CK_ActualCuttings_MatchesCuttingPlan
        CHECK (dbo.fn_ActualCutting_MatchesCuttingPlan(CuttingPlanId, MotherPlantId, SpeciesId) = 1);
END
GO

-- Defense-in-depth #2: the STOCK RULE "production consumption must never
-- exceed available input" applied to this stage -- the sum of
-- ActualQuantity recorded against a Cutting Plan (across every Actual
-- Cutting row referencing it, including this one) must never exceed that
-- plan's PlannedQuantity. This is a database-level backstop; the real
-- concurrency-safe enforcement is in Data/ActualCuttingRepository.cs,
-- which takes a row lock on the parent Cutting Plan (SELECT ... WITH
-- (UPDLOCK, HOLDLOCK)) before computing the running total and inserting,
-- exactly like the batch-number generator's HOLDLOCK MERGE. A CHECK
-- constraint alone cannot fully close the race between two concurrent
-- transactions that both read "before" the other commits; the repository
-- lock is what actually closes that gap. This constraint still catches
-- any write that bypasses the repository (a manual UPDATE, a future bug).
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_ActualCutting_WithinPlannedQuantity' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_ActualCutting_WithinPlannedQuantity(@Id INT, @CuttingPlanId INT, @ActualQuantity DECIMAL(18,2))
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        DECLARE @PlannedQuantity DECIMAL(18,2);
        DECLARE @OtherRowsTotal DECIMAL(18,2);

        SELECT @PlannedQuantity = PlannedQuantity FROM dbo.CuttingPlans WHERE Id = @CuttingPlanId;

        SELECT @OtherRowsTotal = ISNULL(SUM(ActualQuantity), 0)
        FROM dbo.ActualCuttings
        WHERE CuttingPlanId = @CuttingPlanId AND Id <> @Id;

        IF @PlannedQuantity IS NOT NULL AND (@OtherRowsTotal + @ActualQuantity) <= @PlannedQuantity
            SET @Result = 1;

        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ActualCuttings_WithinPlannedQuantity')
BEGIN
    ALTER TABLE dbo.ActualCuttings
        ADD CONSTRAINT CK_ActualCuttings_WithinPlannedQuantity
        CHECK (dbo.fn_ActualCutting_WithinPlannedQuantity(Id, CuttingPlanId, ActualQuantity) = 1);
END
GO
