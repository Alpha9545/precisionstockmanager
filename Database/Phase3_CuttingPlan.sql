/* ============================================================
   Phase 3: Cutting Plan
   ------------------------------------------------------------
   ADDITIVE ONLY.
     - Does not touch, alter, or drop any existing table.
     - Every CREATE is guarded with an existence check, so this
       script is safe to run more than once.
     - FKs to the EXISTING dbo.MotherPlants (Phase 2), dbo.PlantSpecies
       and dbo.IMSUsers tables. Reuses the EXISTING
       dbo.BatchNumberSequences table (Phase 2) for the "CUT-" prefix --
       no new sequence table.

   Run this against the PlantsIMS2 database AFTER Phase2_Area_MotherPlant.sql
   has been applied (dbo.MotherPlants must already exist). Take a backup
   first per your own DB safety rules.
   ============================================================ */

-- Cutting Plan: a forecast/plan of cuttings to take from a specific
-- Mother Plant batch. SpeciesId is a denormalized copy of
-- MotherPlants.SpeciesId (kept on this table for query convenience and
-- reporting) -- the application layer always sets it FROM the selected
-- Mother Plant and never lets a user pick a different species than the
-- plan's own Mother Plant. The CHECK constraint below enforces that at
-- the database level too, so a mismatch can never be written even by a
-- bug elsewhere.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CuttingPlans' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.CuttingPlans
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CuttingPlans PRIMARY KEY,
        PlanNumber          NVARCHAR(20)   NOT NULL,
        MotherPlantId       INT            NOT NULL,
        SpeciesId           INT            NOT NULL,
        PlannedCuttingDate  DATE           NOT NULL,
        PlannedQuantity     DECIMAL(18,2)  NOT NULL,
        CuttingRate         DECIMAL(18,4)  NOT NULL,
        ResponsiblePersonId INT            NULL,
        SupervisorId        INT            NULL,
        Status              NVARCHAR(30)   NOT NULL CONSTRAINT DF_CuttingPlans_Status DEFAULT ('Planned'),
        Remarks             NVARCHAR(500)  NULL,
        CreatedDate         DATETIME2      NOT NULL CONSTRAINT DF_CuttingPlans_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy           NVARCHAR(100)  NULL,
        ModifiedDate        DATETIME2      NULL,
        ModifiedBy          NVARCHAR(100)  NULL,

        CONSTRAINT UQ_CuttingPlans_PlanNumber   UNIQUE (PlanNumber),
        CONSTRAINT FK_CuttingPlans_MotherPlant   FOREIGN KEY (MotherPlantId)       REFERENCES dbo.MotherPlants(Id),
        CONSTRAINT FK_CuttingPlans_Species       FOREIGN KEY (SpeciesId)           REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_CuttingPlans_Responsible    FOREIGN KEY (ResponsiblePersonId) REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_CuttingPlans_Supervisor     FOREIGN KEY (SupervisorId)        REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_CuttingPlans_PlannedQuantity CHECK (PlannedQuantity > 0),
        CONSTRAINT CK_CuttingPlans_CuttingRate     CHECK (CuttingRate >= 0),
        CONSTRAINT CK_CuttingPlans_Status          CHECK (Status IN ('Planned', 'InProgress', 'Completed', 'Cancelled'))
    );

    CREATE INDEX IX_CuttingPlans_MotherPlantId ON dbo.CuttingPlans(MotherPlantId);
    CREATE INDEX IX_CuttingPlans_SpeciesId     ON dbo.CuttingPlans(SpeciesId);
    CREATE INDEX IX_CuttingPlans_Status        ON dbo.CuttingPlans(Status);
END
GO

-- Defense-in-depth: SpeciesId on a Cutting Plan must always match the
-- SpeciesId of the Mother Plant it's planned against. This can't be
-- expressed as a normal CHECK constraint (CHECK can't reference another
-- table), so it's enforced with a scalar function + CHECK, which SQL
-- Server does support and which still runs on every INSERT/UPDATE
-- regardless of which application code path writes the row.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_CuttingPlan_SpeciesMatchesMotherPlant' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_CuttingPlan_SpeciesMatchesMotherPlant(@MotherPlantId INT, @SpeciesId INT)
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        IF EXISTS (SELECT 1 FROM dbo.MotherPlants WHERE Id = @MotherPlantId AND SpeciesId = @SpeciesId)
            SET @Result = 1;
        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CuttingPlans_SpeciesMatchesMotherPlant')
BEGIN
    ALTER TABLE dbo.CuttingPlans
        ADD CONSTRAINT CK_CuttingPlans_SpeciesMatchesMotherPlant
        CHECK (dbo.fn_CuttingPlan_SpeciesMatchesMotherPlant(MotherPlantId, SpeciesId) = 1);
END
GO
