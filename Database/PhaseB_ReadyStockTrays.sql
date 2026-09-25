-- ============================================================================
-- Phase B (add-on 2): tray-based Supervisor Approval and the permanent
-- Sowing -> Approval -> Ready Stock cavity relationship
-- ============================================================================
-- Run AFTER PhaseB_ApprovalAndTrayRules.sql.
-- TARGET: PlantsIMS2_Test ONLY (the guard below refuses any other database).
-- Additive and idempotent. No table is dropped, no existing row is inserted,
-- updated or deleted. Two NULLable columns, CHECK constraints, one unique
-- constraint, one filtered unique index, one foreign key and five triggers
-- are added. Run with QUOTED_IDENTIFIER ON / ANSI_NULLS ON (sqlcmd -I).
--
-- Rules (the application applies the same rules; these are the backstop):
--   Sowing   : SeedQuantity (as entered) is stored; for every NEW sowing
--              QuantitySown (Seeds Used) = NumberOfTrays x cavity and
--              SeedQuantity - QuantitySown < cavity (the Remaining Seeds stay
--              in the seed lot). Cavity, trays, seeds and lot never change.
--   Approval : ActualTrayQuantity (whole, >= 1, <= the sowing's trays) is
--              stored; ConfirmedQuantity = ActualTrayQuantity x the SOWING's
--              cavity; WastageQuantity = QuantitySown - ConfirmedQuantity;
--              at most one Confirmed approval per sowing.
--   ReadyStock: CavityType must equal the sowing's CavityType (FK); every
--              change of Quantity leaves a whole number of trays.
--
-- Existing rows (sowing 3 = 4,000 seeds / 166 trays / 24 Cavity, approval 5
-- = 3,700 seedlings, ReadyStock 3 = 3,700) predate the tray rule. They are
-- NOT modified: SeedQuantity and ActualTrayQuantity stay NULL for them, the
-- CHECKs accept NULL, and the triggers only test newly INSERTed rows or a
-- CHANGED Ready Stock quantity. ReadyStock 3 keeps working for reservation,
-- dispatch and release (those change Reserved/Dispatched, not Quantity).
-- ============================================================================

SET XACT_ABORT ON;
GO

IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50199, 'PhaseB_ReadyStockTrays.sql may only be run against PlantsIMS2_Test.', 1;
GO

-- ---- 1. Columns ---------------------------------------------------------
IF COL_LENGTH('dbo.SeedSowings', 'SeedQuantity') IS NULL
    ALTER TABLE dbo.SeedSowings ADD SeedQuantity DECIMAL(18,2) NULL;
GO
IF COL_LENGTH('dbo.ReadyConfirmations', 'ActualTrayQuantity') IS NULL
    ALTER TABLE dbo.ReadyConfirmations ADD ActualTrayQuantity INT NULL;
GO

-- ---- 2. Pre-validation: STOP (no change) if any existing row conflicts ----
IF EXISTS (SELECT 1 FROM dbo.ReadyStock rs INNER JOIN dbo.SeedSowings sw ON sw.Id = rs.SeedSowingId
           WHERE rs.CavityType <> sw.CavityType)
    THROW 50120, 'Stopped: an existing Ready Stock row has a cavity different from its sowing.', 1;
IF EXISTS (SELECT SeedSowingId FROM dbo.ReadyConfirmations WHERE Status = N'Confirmed'
           GROUP BY SeedSowingId HAVING COUNT(*) > 1)
    THROW 50121, 'Stopped: a sowing already has more than one Confirmed approval.', 1;
GO

-- ---- 3. Sowing: stored seed quantity and Seeds Used = trays x cavity ------
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedSowings_SeedQuantity')
    ALTER TABLE dbo.SeedSowings ADD CONSTRAINT CK_SeedSowings_SeedQuantity
        CHECK (SeedQuantity IS NULL
               OR (SeedQuantity = FLOOR(SeedQuantity)
                   AND NumberOfTrays IS NOT NULL
                   AND QuantitySown = NumberOfTrays * CASE CavityType WHEN N'9 Cavity' THEN 9 WHEN N'24 Cavity' THEN 24
                                                                      WHEN N'42 Cavity' THEN 42 WHEN N'102 Cavity' THEN 102
                                                                      WHEN N'150 Cavity' THEN 150 END
                   AND SeedQuantity >= QuantitySown
                   AND SeedQuantity - QuantitySown < CASE CavityType WHEN N'9 Cavity' THEN 9 WHEN N'24 Cavity' THEN 24
                                                                     WHEN N'42 Cavity' THEN 42 WHEN N'102 Cavity' THEN 102
                                                                     WHEN N'150 Cavity' THEN 150 END));
GO

CREATE OR ALTER TRIGGER dbo.TR_SeedSowings_RequireSeedQuantity
ON dbo.SeedSowings
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- Every new sowing records the Seed Quantity entered, so the CHECK above
    -- applies (Seeds Used = trays x cavity, remainder < one tray).
    IF EXISTS (SELECT 1 FROM inserted WHERE SeedQuantity IS NULL)
        THROW 50122, 'A new sowing must record the Seed Quantity entered (Seeds Used = complete trays x cavity).', 1;
END
GO

CREATE OR ALTER TRIGGER dbo.TR_SeedSowings_ImmutableTrayData
ON dbo.SeedSowings
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- The seed lot, variety, cavity, trays and seed quantities are fixed once
    -- sown (seed ledger and Ready Stock depend on them). Supervisor,
    -- responsible person, remarks and the approval totals may still change.
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.CavityType <> d.CavityType
                  OR i.SourceSeedStockId <> d.SourceSeedStockId
                  OR i.SpeciesId <> d.SpeciesId
                  OR i.QuantitySown <> d.QuantitySown
                  OR ISNULL(i.NumberOfTrays, -1) <> ISNULL(d.NumberOfTrays, -1)
                  OR ISNULL(i.SeedQuantity, -1) <> ISNULL(d.SeedQuantity, -1))
        THROW 50123, 'Seed lot, variety, cavity, trays and seed quantities of a sowing cannot be changed.', 1;
END
GO

-- ---- 4. Approval: Actual Ready Trays, one Confirmed approval per sowing ----
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyConfirmations_ActualTrays')
    ALTER TABLE dbo.ReadyConfirmations ADD CONSTRAINT CK_ReadyConfirmations_ActualTrays
        CHECK (ActualTrayQuantity IS NULL OR ActualTrayQuantity >= 1);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ReadyConfirmations_OneConfirmedPerSowing'
                                           AND object_id = OBJECT_ID('dbo.ReadyConfirmations'))
    CREATE UNIQUE NONCLUSTERED INDEX UX_ReadyConfirmations_OneConfirmedPerSowing
        ON dbo.ReadyConfirmations (SeedSowingId) WHERE Status = N'Confirmed';
GO

CREATE OR ALTER TRIGGER dbo.TR_ReadyConfirmations_TrayQuantity
ON dbo.ReadyConfirmations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- Seedlings come only from complete trays of the sowing's own cavity;
    -- wastage is what was sown and did not become a ready seedling.
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN dbo.SeedSowings sw ON sw.Id = i.SeedSowingId
               WHERE i.ActualTrayQuantity IS NULL
                  OR sw.NumberOfTrays IS NULL
                  OR i.ActualTrayQuantity > sw.NumberOfTrays
                  OR i.ConfirmedQuantity <> i.ActualTrayQuantity * CASE sw.CavityType WHEN N'9 Cavity' THEN 9 WHEN N'24 Cavity' THEN 24
                                                                                      WHEN N'42 Cavity' THEN 42 WHEN N'102 Cavity' THEN 102
                                                                                      WHEN N'150 Cavity' THEN 150 END
                  OR i.WastageQuantity <> sw.QuantitySown - i.ConfirmedQuantity)
        THROW 50124, 'Approval must be whole Actual Ready Trays (1 to the trays sown); seedlings = trays x the sowing cavity and wastage = seeds sown - seedlings.', 1;
END
GO

-- ---- 5. Ready Stock inherits the sowing's cavity (permanent) --------------
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_SeedSowings_IdCavity')
    ALTER TABLE dbo.SeedSowings ADD CONSTRAINT UQ_SeedSowings_IdCavity UNIQUE (Id, CavityType);
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ReadyStock_SowingCavity')
    ALTER TABLE dbo.ReadyStock WITH CHECK ADD CONSTRAINT FK_ReadyStock_SowingCavity
        FOREIGN KEY (SeedSowingId, CavityType) REFERENCES dbo.SeedSowings (Id, CavityType);
GO

CREATE OR ALTER TRIGGER dbo.TR_ReadyStock_WholeTrays
ON dbo.ReadyStock
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT UPDATE(Quantity) RETURN;
    -- Only a NEW or CHANGED Quantity is tested, so a batch approved before
    -- the tray rule keeps working for reservation and dispatch.
    IF EXISTS (SELECT 1
               FROM inserted i
               LEFT JOIN deleted d ON d.Id = i.Id
               WHERE (d.Id IS NULL OR d.Quantity <> i.Quantity)
                 AND i.Quantity % CASE i.CavityType WHEN N'9 Cavity' THEN 9 WHEN N'24 Cavity' THEN 24
                                                    WHEN N'42 Cavity' THEN 42 WHEN N'102 Cavity' THEN 102
                                                    WHEN N'150 Cavity' THEN 150 END <> 0)
        THROW 50125, 'Ready Stock quantity must be a whole number of trays of the sowing cavity.', 1;
END
GO
