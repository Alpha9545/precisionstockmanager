-- ============================================================================
-- Phase D: one consistent production + stock flow
-- ============================================================================
-- TARGET: PlantsIMS2_Test ONLY (the guard below refuses any other database).
-- Run AFTER PhaseB_ReadyStockTrays.sql, with QUOTED_IDENTIFIER ON / ANSI_NULLS
-- ON (sqlcmd -I, SSMS default). Idempotent: every object is guarded.
--
-- What it adds (nothing is dropped; no existing business row is changed --
-- the only UPDATE copies the old Area.PolyhouseId links into the new single
-- relationship Polyhouses.AreaId, see section 9):
--   Masters
--     dbo.PotSizes                 controlled pot-size list; every PotSize
--                                  column gets a FK to it (seeded with the
--                                  values already in use, verbatim)
--     PlantSpecies.Color           optional colour of a variety
--   Seed / cutting tray sowing
--     SeedSowings.SourceType       'Seed' (existing rows) | 'Cutting'
--     SeedSowings.SourceCuttingStockId, SourceSeedStockId becomes NULLable
--                                  (its FK + index are dropped and recreated
--                                  identically -- required by ALTER COLUMN)
--     TR_SeedSowings_ImmutableTrayData  now also freezes the source, the
--                                  assigned supervisor and the recorder
--     TR_SeedSowings_SupervisorRole     a new sowing's supervisor must hold
--                                  the 'Sowing Supervisor' role and is never
--                                  the recorder
--   Cutting
--     dbo.CuttingProductions       Mother Plant -> cutting production record
--                                  (credits CuttingStock, 'Harvest')
--     CuttingStockTransactions     + 'Sown', 'TransitLoss'
--     InternalTransfers            completed Cutting delivery must carry the
--                                  received quantity and destination
--   Pot production
--     dbo.EmptyPotPurchases        Office purchase -> Empty Pot stock
--     dbo.PotProductionBatches     batch: cuttings allocated, Area, pot size,
--                                  expected ready date, assigned supervisor,
--                                  READY confirmation
--     dbo.PotProductionEntries     daily production (consumes the batch
--                                  Area's own empty pots)
--   Integrity
--     whole-number CHECKs on the cutting / empty-pot / potted-plant stocks
--     and ledgers (all existing rows are whole numbers -- verified first)
--   Area / Polyhouse
--     Polyhouses.AreaId is the only Area <-> Polyhouse link (old
--     Area.PolyhouseId values copied once; the column is retired, not dropped)
--     a Mother Plant's Polyhouse must belong to its Area
--   Roles (created with no users): Main Office Store Keeper, Purchase
--     Officer, Pot Production Operator, Outlet Sales
--   Pot batches can be closed as a complete loss (status Lost, reason required)
--
-- Existing data: the historical sowing (4,000 seeds / 166 trays / 24 Cavity)
-- and every other existing row keep their values. New CHECKs are only added
-- when every existing row already satisfies them; triggers only test new
-- rows or changed identity columns.
-- ============================================================================

SET XACT_ABORT ON;
GO

IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50299, 'PhaseD_ProductionRestructure.sql may only be run against PlantsIMS2_Test.', 1;
GO

-- ============================================================================
-- 1. Pot Size master
-- ============================================================================
IF OBJECT_ID('dbo.PotSizes', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PotSizes
    (
        Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PotSizes PRIMARY KEY,
        Name        NVARCHAR(50)  NOT NULL CONSTRAINT UQ_PotSizes_Name UNIQUE,
        SortOrder   INT           NOT NULL CONSTRAINT DF_PotSizes_SortOrder DEFAULT (0),
        IsActive    BIT           NOT NULL CONSTRAINT DF_PotSizes_IsActive DEFAULT (1),
        CreatedDate DATETIME2     NOT NULL CONSTRAINT DF_PotSizes_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy   NVARCHAR(100) NULL,
        CONSTRAINT CK_PotSizes_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0)
    );
END
GO

-- Master rows for the values ALREADY stored (verbatim), so every FK below
-- validates without touching a business row.
INSERT INTO dbo.PotSizes (Name, CreatedBy)
SELECT v.PotSize, N'PhaseD migration'
FROM (SELECT PotSize FROM dbo.EmptyPotInventory
      UNION SELECT PotSize FROM dbo.PottedPlantStock
      UNION SELECT PotSize FROM dbo.PotProduction
      UNION SELECT PotSize FROM dbo.PottedPlantBookings
      UNION SELECT PotSize FROM dbo.Dispatches
      UNION SELECT PotSize FROM dbo.LabRequests
      UNION SELECT PotSize FROM dbo.PurchaseOrderItems WHERE PotSize IS NOT NULL) v
WHERE NOT EXISTS (SELECT 1 FROM dbo.PotSizes p WHERE p.Name = v.PotSize);
GO

IF OBJECT_ID('dbo.FK_EmptyPotInventory_PotSize', 'F') IS NULL
    ALTER TABLE dbo.EmptyPotInventory WITH CHECK ADD CONSTRAINT FK_EmptyPotInventory_PotSize FOREIGN KEY (PotSize) REFERENCES dbo.PotSizes (Name);
IF OBJECT_ID('dbo.FK_PottedPlantStock_PotSize', 'F') IS NULL
    ALTER TABLE dbo.PottedPlantStock WITH CHECK ADD CONSTRAINT FK_PottedPlantStock_PotSize FOREIGN KEY (PotSize) REFERENCES dbo.PotSizes (Name);
IF OBJECT_ID('dbo.FK_PotProduction_PotSize', 'F') IS NULL
    ALTER TABLE dbo.PotProduction WITH CHECK ADD CONSTRAINT FK_PotProduction_PotSize FOREIGN KEY (PotSize) REFERENCES dbo.PotSizes (Name);
IF OBJECT_ID('dbo.FK_PottedPlantBookings_PotSize', 'F') IS NULL
    ALTER TABLE dbo.PottedPlantBookings WITH CHECK ADD CONSTRAINT FK_PottedPlantBookings_PotSize FOREIGN KEY (PotSize) REFERENCES dbo.PotSizes (Name);
IF OBJECT_ID('dbo.FK_Dispatches_PotSize', 'F') IS NULL
    ALTER TABLE dbo.Dispatches WITH CHECK ADD CONSTRAINT FK_Dispatches_PotSize FOREIGN KEY (PotSize) REFERENCES dbo.PotSizes (Name);
IF OBJECT_ID('dbo.FK_LabRequests_PotSize', 'F') IS NULL
    ALTER TABLE dbo.LabRequests WITH CHECK ADD CONSTRAINT FK_LabRequests_PotSize FOREIGN KEY (PotSize) REFERENCES dbo.PotSizes (Name);
IF OBJECT_ID('dbo.FK_PurchaseOrderItems_PotSize', 'F') IS NULL
    ALTER TABLE dbo.PurchaseOrderItems WITH CHECK ADD CONSTRAINT FK_PurchaseOrderItems_PotSize FOREIGN KEY (PotSize) REFERENCES dbo.PotSizes (Name);
GO

-- ============================================================================
-- 2. Variety colour
-- ============================================================================
IF COL_LENGTH('dbo.PlantSpecies', 'Color') IS NULL
    ALTER TABLE dbo.PlantSpecies ADD Color NVARCHAR(50) NULL;
GO

-- ============================================================================
-- 3. Whole-number stock (pre-validated: STOP instead of changing a row)
-- ============================================================================
IF EXISTS (SELECT 1 FROM dbo.CuttingStock WHERE PhysicalQuantity <> FLOOR(PhysicalQuantity) OR InTransitQuantity <> FLOOR(InTransitQuantity))
   OR EXISTS (SELECT 1 FROM dbo.CuttingStockTransactions WHERE Quantity <> FLOOR(Quantity) OR BeforeQuantity <> FLOOR(BeforeQuantity))
   OR EXISTS (SELECT 1 FROM dbo.EmptyPotInventory WHERE PhysicalQuantity <> FLOOR(PhysicalQuantity))
   OR EXISTS (SELECT 1 FROM dbo.EmptyPotInventoryTransactions WHERE Quantity <> FLOOR(Quantity) OR BeforeQuantity <> FLOOR(BeforeQuantity))
   OR EXISTS (SELECT 1 FROM dbo.PottedPlantStock WHERE PhysicalQuantity <> FLOOR(PhysicalQuantity) OR ReservedQuantity <> FLOOR(ReservedQuantity)
                 OR SoldDispatchedQuantity <> FLOOR(SoldDispatchedQuantity) OR WastedQuantity <> FLOOR(WastedQuantity) OR InTransitQuantity <> FLOOR(InTransitQuantity))
   OR EXISTS (SELECT 1 FROM dbo.PottedPlantStockTransactions WHERE Quantity <> FLOOR(Quantity) OR BeforeQuantity <> FLOOR(BeforeQuantity))
    THROW 50201, 'Stopped: an existing cutting / empty-pot / potted-plant stock row holds a fractional quantity.', 1;
GO
IF OBJECT_ID('dbo.CK_CuttingStock_WholeQuantities', 'C') IS NULL
    ALTER TABLE dbo.CuttingStock ADD CONSTRAINT CK_CuttingStock_WholeQuantities
        CHECK (PhysicalQuantity = FLOOR(PhysicalQuantity) AND InTransitQuantity = FLOOR(InTransitQuantity));
IF OBJECT_ID('dbo.CK_CuttingStockTx_WholeQuantities', 'C') IS NULL
    ALTER TABLE dbo.CuttingStockTransactions ADD CONSTRAINT CK_CuttingStockTx_WholeQuantities
        CHECK (Quantity = FLOOR(Quantity) AND BeforeQuantity = FLOOR(BeforeQuantity));
IF OBJECT_ID('dbo.CK_EmptyPotInventory_WholeQuantities', 'C') IS NULL
    ALTER TABLE dbo.EmptyPotInventory ADD CONSTRAINT CK_EmptyPotInventory_WholeQuantities
        CHECK (PhysicalQuantity = FLOOR(PhysicalQuantity));
IF OBJECT_ID('dbo.CK_EmptyPotInvTx_WholeQuantities', 'C') IS NULL
    ALTER TABLE dbo.EmptyPotInventoryTransactions ADD CONSTRAINT CK_EmptyPotInvTx_WholeQuantities
        CHECK (Quantity = FLOOR(Quantity) AND BeforeQuantity = FLOOR(BeforeQuantity));
IF OBJECT_ID('dbo.CK_PottedPlantStock_WholeQuantities', 'C') IS NULL
    ALTER TABLE dbo.PottedPlantStock ADD CONSTRAINT CK_PottedPlantStock_WholeQuantities
        CHECK (PhysicalQuantity = FLOOR(PhysicalQuantity) AND ReservedQuantity = FLOOR(ReservedQuantity)
               AND SoldDispatchedQuantity = FLOOR(SoldDispatchedQuantity) AND WastedQuantity = FLOOR(WastedQuantity)
               AND InTransitQuantity = FLOOR(InTransitQuantity));
IF OBJECT_ID('dbo.CK_PottedStockTx_WholeQuantities', 'C') IS NULL
    ALTER TABLE dbo.PottedPlantStockTransactions ADD CONSTRAINT CK_PottedStockTx_WholeQuantities
        CHECK (Quantity = FLOOR(Quantity) AND BeforeQuantity = FLOOR(BeforeQuantity));
GO

-- ============================================================================
-- 4. Keys used by the composite (consistency) foreign keys below
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_CuttingStock_IdSpecies')
    ALTER TABLE dbo.CuttingStock ADD CONSTRAINT UQ_CuttingStock_IdSpecies UNIQUE (Id, SpeciesId);
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_CuttingStock_IdSpeciesArea')
    ALTER TABLE dbo.CuttingStock ADD CONSTRAINT UQ_CuttingStock_IdSpeciesArea UNIQUE (Id, SpeciesId, AreaId);
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_MotherPlants_IdSpecies')
    ALTER TABLE dbo.MotherPlants ADD CONSTRAINT UQ_MotherPlants_IdSpecies UNIQUE (Id, SpeciesId);
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_EmptyPotInventory_IdAreaPotSize')
    ALTER TABLE dbo.EmptyPotInventory ADD CONSTRAINT UQ_EmptyPotInventory_IdAreaPotSize UNIQUE (Id, AreaId, PotSize);
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_PottedPlantStock_IdSpeciesPotSizeArea')
    ALTER TABLE dbo.PottedPlantStock ADD CONSTRAINT UQ_PottedPlantStock_IdSpeciesPotSizeArea UNIQUE (Id, SpeciesId, PotSize, AreaId);
GO

-- Cutting ledger: + 'Sown' (cutting tray sowing), 'TransitLoss' (shortfall
-- found by Main Office when a delivery is received).
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CuttingStockTx_Type' AND definition LIKE '%TransitLoss%')
BEGIN
    IF OBJECT_ID('dbo.CK_CuttingStockTx_Type', 'C') IS NOT NULL
        ALTER TABLE dbo.CuttingStockTransactions DROP CONSTRAINT CK_CuttingStockTx_Type;
    ALTER TABLE dbo.CuttingStockTransactions ADD CONSTRAINT CK_CuttingStockTx_Type
        CHECK (TransactionType IN ('Harvest', 'Transfer', 'Potted', 'Adjustment', 'ReversalRemoval', 'Transplanted',
                                   'ReversalReturn', 'Sown', 'TransitLoss'));
END
GO

-- A completed Cutting delivery records what Main Office actually received
-- and where the cuttings now are.
IF OBJECT_ID('dbo.CK_InternalTransfers_CuttingCompleted', 'C') IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.InternalTransfers WHERE StockType = 'Cutting' AND Status = 'Completed'
               AND (ConfirmedQuantity IS NULL OR DestinationAreaId IS NULL OR ConfirmedQuantity > Quantity))
        THROW 50202, 'Stopped: an existing completed Cutting transfer has no received quantity / destination.', 1;
    ALTER TABLE dbo.InternalTransfers ADD CONSTRAINT CK_InternalTransfers_CuttingCompleted
        CHECK (StockType <> 'Cutting' OR Status <> 'Completed'
               OR (ConfirmedQuantity IS NOT NULL AND DestinationAreaId IS NOT NULL AND ConfirmedQuantity <= Quantity));
END
GO

-- ============================================================================
-- 5. Cutting tray sowing: the same Sowing -> Approval -> Ready Stock chain,
--    sourced from Cutting Stock instead of a seed lot.
-- ============================================================================
IF COL_LENGTH('dbo.SeedSowings', 'SourceType') IS NULL
    ALTER TABLE dbo.SeedSowings ADD SourceType NVARCHAR(10) NOT NULL CONSTRAINT DF_SeedSowings_SourceType DEFAULT (N'Seed');
GO
IF COL_LENGTH('dbo.SeedSowings', 'SourceCuttingStockId') IS NULL
    ALTER TABLE dbo.SeedSowings ADD SourceCuttingStockId INT NULL;
GO
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'SourceSeedStockId' AND is_nullable = 0)
BEGIN
    -- ALTER COLUMN needs the FK and index on the column out of the way;
    -- both are recreated below with the same definition.
    IF OBJECT_ID('dbo.FK_SeedSowings_SourceSeedStock', 'F') IS NOT NULL
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT FK_SeedSowings_SourceSeedStock;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SeedSowings_SourceSeedStockId' AND object_id = OBJECT_ID('dbo.SeedSowings'))
        DROP INDEX IX_SeedSowings_SourceSeedStockId ON dbo.SeedSowings;
    ALTER TABLE dbo.SeedSowings ALTER COLUMN SourceSeedStockId INT NULL;
END
GO
IF OBJECT_ID('dbo.FK_SeedSowings_SourceSeedStock', 'F') IS NULL
    ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT FK_SeedSowings_SourceSeedStock FOREIGN KEY (SourceSeedStockId) REFERENCES dbo.SeedStock (Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SeedSowings_SourceSeedStockId' AND object_id = OBJECT_ID('dbo.SeedSowings'))
    CREATE NONCLUSTERED INDEX IX_SeedSowings_SourceSeedStockId ON dbo.SeedSowings (SourceSeedStockId);
IF OBJECT_ID('dbo.FK_SeedSowings_SourceCuttingStock', 'F') IS NULL
    ALTER TABLE dbo.SeedSowings WITH CHECK ADD CONSTRAINT FK_SeedSowings_SourceCuttingStock
        FOREIGN KEY (SourceCuttingStockId, SpeciesId) REFERENCES dbo.CuttingStock (Id, SpeciesId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SeedSowings_SourceCuttingStockId' AND object_id = OBJECT_ID('dbo.SeedSowings'))
    CREATE NONCLUSTERED INDEX IX_SeedSowings_SourceCuttingStockId ON dbo.SeedSowings (SourceCuttingStockId) WHERE SourceCuttingStockId IS NOT NULL;
IF OBJECT_ID('dbo.CK_SeedSowings_Source', 'C') IS NULL
    ALTER TABLE dbo.SeedSowings ADD CONSTRAINT CK_SeedSowings_Source
        CHECK ((SourceType = N'Seed' AND SourceSeedStockId IS NOT NULL AND SourceCuttingStockId IS NULL)
            OR (SourceType = N'Cutting' AND SourceCuttingStockId IS NOT NULL AND SourceSeedStockId IS NULL));
GO

CREATE OR ALTER TRIGGER dbo.TR_SeedSowings_ImmutableTrayData
ON dbo.SeedSowings
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- Fixed once sown: the source (seed lot or cutting stock), variety,
    -- cavity, trays, quantities, the ASSIGNED SUPERVISOR (the only person
    -- who may approve) and the recorder. Remarks, the approval totals and
    -- the status may still change.
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.CavityType <> d.CavityType
                  OR i.SourceType <> d.SourceType
                  OR ISNULL(i.SourceSeedStockId, -1) <> ISNULL(d.SourceSeedStockId, -1)
                  OR ISNULL(i.SourceCuttingStockId, -1) <> ISNULL(d.SourceCuttingStockId, -1)
                  OR i.SpeciesId <> d.SpeciesId
                  OR i.QuantitySown <> d.QuantitySown
                  OR ISNULL(i.NumberOfTrays, -1) <> ISNULL(d.NumberOfTrays, -1)
                  OR ISNULL(i.SeedQuantity, -1) <> ISNULL(d.SeedQuantity, -1)
                  OR ISNULL(i.SupervisorId, -1) <> ISNULL(d.SupervisorId, -1)
                  OR ISNULL(i.CreatedById, -1) <> ISNULL(d.CreatedById, -1))
        THROW 50123, 'Source, variety, cavity, trays, quantities, assigned supervisor and recorder of a sowing cannot be changed.', 1;
END
GO

CREATE OR ALTER TRIGGER dbo.TR_SeedSowings_SupervisorRole
ON dbo.SeedSowings
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    -- A new sowing is assigned to an active Sowing Supervisor who is not the
    -- person recording it (the assigned supervisor is the only approver).
    IF EXISTS (SELECT 1
               FROM inserted i
               WHERE i.SupervisorId IS NULL
                  OR (i.CreatedById IS NOT NULL AND i.SupervisorId = i.CreatedById)
                  OR NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.SupervisorId
                                   AND ISNULL(u.IsActive, 0) = 1
                                   AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Sowing Supervisor'))
        THROW 50203, 'A sowing must be assigned to an active Sowing Supervisor other than the person recording it.', 1;
END
GO

-- ============================================================================
-- 6. Mother Plant -> Cutting Production
-- ============================================================================
CREATE OR ALTER TRIGGER dbo.TR_MotherPlants_AreaAndSupervisor
ON dbo.MotherPlants
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- New Mother Plants belong to an Area. The supervisor, when set or
    -- changed (or when the Area changes), is an active Mother Plant
    -- Supervisor OF THAT AREA. The Polyhouse, when set or changed (or when
    -- the Area changes), belongs to the Mother Plant's Area through
    -- dbo.Polyhouses.AreaId. (Rows that existed before these rules are only
    -- checked when those fields change.)
    IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id = i.Id
               WHERE d.Id IS NULL AND i.AreaId IS NULL)
        THROW 50204, 'A Mother Plant must belong to an Area.', 1;
    IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id = i.Id
               WHERE (d.Id IS NULL OR ISNULL(i.SupervisorId, -1) <> ISNULL(d.SupervisorId, -1) OR ISNULL(i.AreaId, -1) <> ISNULL(d.AreaId, -1))
                 AND (i.SupervisorId IS NULL
                      OR NOT EXISTS (SELECT 1
                                     FROM dbo.UserRoles ur
                                     INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                     INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                     WHERE ur.UserId = i.SupervisorId
                                       AND ur.AreaId = i.AreaId
                                       AND ISNULL(u.IsActive, 0) = 1
                                       AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor')))
        THROW 50205, 'A Mother Plant supervisor must be an active Mother Plant Supervisor of the Mother Plant''s Area.', 1;
    IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id = i.Id
               LEFT JOIN dbo.Polyhouses ph ON ph.Id = i.PolyhouseId
               WHERE (d.Id IS NULL OR i.PolyhouseId <> d.PolyhouseId OR ISNULL(i.AreaId, -1) <> ISNULL(d.AreaId, -1))
                 AND (ph.AreaId IS NULL OR ph.AreaId <> i.AreaId))
        THROW 50219, 'The Polyhouse must belong to the Mother Plant''s Area (assign it in Admin > Polyhouses).', 1;
END
GO

IF OBJECT_ID('dbo.CuttingProductions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CuttingProductions
    (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CuttingProductions PRIMARY KEY,
        ProductionCode  NVARCHAR(40)  NOT NULL CONSTRAINT UQ_CuttingProductions_Code UNIQUE,
        MotherPlantId   INT           NOT NULL,
        SpeciesId       INT           NOT NULL,
        AreaId          INT           NOT NULL,
        CuttingStockId  INT           NOT NULL,
        CuttingDate     DATE          NOT NULL,
        Quantity        DECIMAL(18,2) NOT NULL,
        SupervisorId    INT           NOT NULL,
        Remarks         NVARCHAR(500) NULL,
        CreatedById     INT           NULL,
        CreatedBy       NVARCHAR(100) NULL,
        CreatedDate     DATETIME2     NOT NULL CONSTRAINT DF_CuttingProductions_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_CuttingProductions_Quantity CHECK (Quantity > 0 AND Quantity = FLOOR(Quantity)),
        -- the variety is the Mother Plant's; the stock credited is that
        -- variety's pool in the same Area
        CONSTRAINT FK_CuttingProductions_MotherPlant FOREIGN KEY (MotherPlantId, SpeciesId) REFERENCES dbo.MotherPlants (Id, SpeciesId),
        CONSTRAINT FK_CuttingProductions_CuttingStock FOREIGN KEY (CuttingStockId, SpeciesId, AreaId) REFERENCES dbo.CuttingStock (Id, SpeciesId, AreaId),
        CONSTRAINT FK_CuttingProductions_Area FOREIGN KEY (AreaId) REFERENCES dbo.Area (Id),
        CONSTRAINT FK_CuttingProductions_Supervisor FOREIGN KEY (SupervisorId) REFERENCES dbo.IMSUsers (Id),
        CONSTRAINT FK_CuttingProductions_CreatedBy FOREIGN KEY (CreatedById) REFERENCES dbo.IMSUsers (Id)
    );
    CREATE NONCLUSTERED INDEX IX_CuttingProductions_MotherPlant ON dbo.CuttingProductions (MotherPlantId);
    CREATE NONCLUSTERED INDEX IX_CuttingProductions_CuttingDate ON dbo.CuttingProductions (CuttingDate);
END
GO

CREATE OR ALTER TRIGGER dbo.TR_CuttingProductions_Rules
ON dbo.CuttingProductions
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- A production record is a ledger fact: nothing about it changes later.
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 50206, 'A cutting production record cannot be changed after it is saved.', 1;
    IF EXISTS (SELECT 1
               FROM inserted i
               WHERE NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.SupervisorId
                                   AND ur.AreaId = i.AreaId
                                   AND ISNULL(u.IsActive, 0) = 1
                                   AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor'))
        THROW 50207, 'The supervisor of a cutting production must be an active Mother Plant Supervisor of its Area.', 1;
END
GO

-- ============================================================================
-- 7. Empty pots: Office purchase -> Area stock
-- ============================================================================
IF OBJECT_ID('dbo.EmptyPotPurchases', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmptyPotPurchases
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmptyPotPurchases PRIMARY KEY,
        PurchaseCode        NVARCHAR(40)  NOT NULL CONSTRAINT UQ_EmptyPotPurchases_Code UNIQUE,
        PurchaseDate        DATE          NOT NULL,
        PotSize             NVARCHAR(50)  NOT NULL,
        Quantity            DECIMAL(18,2) NOT NULL,
        SupplierName        NVARCHAR(150) NOT NULL,
        InvoiceRef          NVARCHAR(100) NULL,
        AreaId              INT           NOT NULL,   -- the Office store that received the pots
        EmptyPotInventoryId INT           NOT NULL,   -- that store's pool for this pot size
        Remarks             NVARCHAR(500) NULL,
        CreatedById         INT           NULL,
        CreatedBy           NVARCHAR(100) NULL,
        CreatedDate         DATETIME2     NOT NULL CONSTRAINT DF_EmptyPotPurchases_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_EmptyPotPurchases_Quantity CHECK (Quantity > 0 AND Quantity = FLOOR(Quantity)),
        CONSTRAINT CK_EmptyPotPurchases_Supplier CHECK (LEN(LTRIM(RTRIM(SupplierName))) > 0),
        CONSTRAINT FK_EmptyPotPurchases_PotSize FOREIGN KEY (PotSize) REFERENCES dbo.PotSizes (Name),
        CONSTRAINT FK_EmptyPotPurchases_Area FOREIGN KEY (AreaId) REFERENCES dbo.Area (Id),
        CONSTRAINT FK_EmptyPotPurchases_Pool FOREIGN KEY (EmptyPotInventoryId, AreaId, PotSize) REFERENCES dbo.EmptyPotInventory (Id, AreaId, PotSize),
        CONSTRAINT FK_EmptyPotPurchases_CreatedBy FOREIGN KEY (CreatedById) REFERENCES dbo.IMSUsers (Id)
    );
    CREATE NONCLUSTERED INDEX IX_EmptyPotPurchases_PurchaseDate ON dbo.EmptyPotPurchases (PurchaseDate);
END
GO

CREATE OR ALTER TRIGGER dbo.TR_EmptyPotPurchases_Immutable
ON dbo.EmptyPotPurchases
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.PotSize <> d.PotSize OR i.Quantity <> d.Quantity OR i.AreaId <> d.AreaId
                  OR i.EmptyPotInventoryId <> d.EmptyPotInventoryId OR i.PurchaseDate <> d.PurchaseDate)
        THROW 50208, 'The pot size, quantity, store and date of a recorded purchase cannot be changed.', 1;
END
GO

-- ============================================================================
-- 8. Pot production: batch -> daily entries -> READY -> Potted Plant Stock
-- ============================================================================
IF OBJECT_ID('dbo.PotProductionBatches', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PotProductionBatches
    (
        Id                   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PotProductionBatches PRIMARY KEY,
        BatchCode            NVARCHAR(40)  NOT NULL CONSTRAINT UQ_PotProductionBatches_Code UNIQUE,
        SourceCuttingStockId INT           NOT NULL,
        SpeciesId            INT           NOT NULL,
        AreaId               INT           NOT NULL,   -- production Area (its empty pots are used)
        PotSize              NVARCHAR(50)  NOT NULL,
        EmptyPotInventoryId  INT           NOT NULL,   -- the production Area's pool of that pot size
        CuttingAllocated     DECIMAL(18,2) NOT NULL,
        ProductionStartDate  DATE          NOT NULL,
        ExpectedReadyDate    DATE          NOT NULL,
        SupervisorId         INT           NOT NULL,   -- assigned supervisor: the only one who confirms READY
        Status               NVARCHAR(20)  NOT NULL CONSTRAINT DF_PotProductionBatches_Status DEFAULT (N'InProduction'),   -- InProduction / Ready / Lost (complete loss) / Cancelled
        ReadyQuantity        DECIMAL(18,2) NULL,
        WastageReason        NVARCHAR(100) NULL,
        UnusedCuttingAction  NVARCHAR(20)  NULL,
        ReadyConfirmedById   INT           NULL,
        ReadyDate            DATETIME2     NULL,
        ReadyRemarks         NVARCHAR(500) NULL,
        PottedPlantStockId   INT           NULL,
        Remarks              NVARCHAR(500) NULL,
        CreatedById          INT           NOT NULL,
        CreatedBy            NVARCHAR(100) NULL,
        CreatedDate          DATETIME2     NOT NULL CONSTRAINT DF_PotProductionBatches_CreatedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedBy           NVARCHAR(100) NULL,
        ModifiedDate         DATETIME2     NULL,
        CONSTRAINT CK_PotBatches_CuttingAllocated CHECK (CuttingAllocated > 0 AND CuttingAllocated = FLOOR(CuttingAllocated)),
        CONSTRAINT CK_PotBatches_Dates CHECK (ExpectedReadyDate >= ProductionStartDate),
        CONSTRAINT CK_PotBatches_Status CHECK (Status IN (N'InProduction', N'Ready', N'Lost', N'Cancelled')),
        CONSTRAINT CK_PotBatches_ReadyQuantity CHECK (ReadyQuantity IS NULL OR (ReadyQuantity >= 0 AND ReadyQuantity = FLOOR(ReadyQuantity))),
        -- Ready: >= 1 pot went to Potted Plant Stock. Lost: complete loss --
        -- zero ready, nothing in stock, the loss reason is mandatory.
        CONSTRAINT CK_PotBatches_ReadyFields CHECK (
            (Status = N'Ready' AND ReadyQuantity >= 1 AND ReadyConfirmedById IS NOT NULL AND ReadyDate IS NOT NULL AND PottedPlantStockId IS NOT NULL)
         OR (Status = N'Lost' AND ReadyQuantity = 0 AND ReadyConfirmedById IS NOT NULL AND ReadyDate IS NOT NULL AND PottedPlantStockId IS NULL
             AND WastageReason IS NOT NULL)
         OR (Status IN (N'InProduction', N'Cancelled') AND ReadyQuantity IS NULL AND ReadyConfirmedById IS NULL AND ReadyDate IS NULL AND PottedPlantStockId IS NULL
             AND WastageReason IS NULL AND UnusedCuttingAction IS NULL)),
        -- separation of duties: the assigned supervisor confirms READY and is
        -- never the person who created the batch
        CONSTRAINT CK_PotBatches_SupervisorNotCreator CHECK (SupervisorId <> CreatedById),
        CONSTRAINT CK_PotBatches_ConfirmedBySupervisor CHECK (ReadyConfirmedById IS NULL OR ReadyConfirmedById = SupervisorId),
        CONSTRAINT CK_PotBatches_WastageReason CHECK (WastageReason IS NULL OR WastageReason IN
            (N'Germination failure', N'Disease', N'Damaged plants', N'Poor growth', N'Other')),
        CONSTRAINT CK_PotBatches_UnusedCuttingAction CHECK (UnusedCuttingAction IS NULL OR UnusedCuttingAction IN (N'ReturnedToStock', N'Wastage')),
        CONSTRAINT FK_PotBatches_CuttingStock FOREIGN KEY (SourceCuttingStockId, SpeciesId) REFERENCES dbo.CuttingStock (Id, SpeciesId),
        CONSTRAINT FK_PotBatches_Area FOREIGN KEY (AreaId) REFERENCES dbo.Area (Id),
        CONSTRAINT FK_PotBatches_PotSize FOREIGN KEY (PotSize) REFERENCES dbo.PotSizes (Name),
        -- empty pots can only come from the production Area's own pool
        CONSTRAINT FK_PotBatches_EmptyPotPool FOREIGN KEY (EmptyPotInventoryId, AreaId, PotSize) REFERENCES dbo.EmptyPotInventory (Id, AreaId, PotSize),
        CONSTRAINT FK_PotBatches_PottedStock FOREIGN KEY (PottedPlantStockId, SpeciesId, PotSize, AreaId) REFERENCES dbo.PottedPlantStock (Id, SpeciesId, PotSize, AreaId),
        CONSTRAINT FK_PotBatches_Supervisor FOREIGN KEY (SupervisorId) REFERENCES dbo.IMSUsers (Id),
        CONSTRAINT FK_PotBatches_ReadyConfirmedBy FOREIGN KEY (ReadyConfirmedById) REFERENCES dbo.IMSUsers (Id),
        CONSTRAINT FK_PotBatches_CreatedBy FOREIGN KEY (CreatedById) REFERENCES dbo.IMSUsers (Id)
    );
    CREATE NONCLUSTERED INDEX IX_PotBatches_Status ON dbo.PotProductionBatches (Status, ExpectedReadyDate);
    CREATE NONCLUSTERED INDEX IX_PotBatches_AreaId ON dbo.PotProductionBatches (AreaId);
END
GO

IF OBJECT_ID('dbo.PotProductionEntries', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PotProductionEntries
    (
        Id             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PotProductionEntries PRIMARY KEY,
        BatchId        INT           NOT NULL CONSTRAINT FK_PotEntries_Batch REFERENCES dbo.PotProductionBatches (Id),
        ProductionDate DATE          NOT NULL,
        Quantity       DECIMAL(18,2) NOT NULL,
        Remarks        NVARCHAR(500) NULL,
        CreatedById    INT           NULL CONSTRAINT FK_PotEntries_CreatedBy REFERENCES dbo.IMSUsers (Id),
        CreatedBy      NVARCHAR(100) NULL,
        CreatedDate    DATETIME2     NOT NULL CONSTRAINT DF_PotProductionEntries_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_PotEntries_Quantity CHECK (Quantity > 0 AND Quantity = FLOOR(Quantity))
    );
    CREATE NONCLUSTERED INDEX IX_PotEntries_Batch ON dbo.PotProductionEntries (BatchId, ProductionDate);
END
GO

CREATE OR ALTER TRIGGER dbo.TR_PotBatches_Insert
ON dbo.PotProductionBatches
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted WHERE Status <> N'InProduction')
        THROW 50209, 'A pot production batch starts In Production.', 1;
    -- the assigned supervisor is an active Mother Plant Supervisor of the
    -- production Area
    IF EXISTS (SELECT 1
               FROM inserted i
               WHERE NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.SupervisorId
                                   AND ur.AreaId = i.AreaId
                                   AND ISNULL(u.IsActive, 0) = 1
                                   AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor'))
        THROW 50210, 'The batch supervisor must be an active Mother Plant Supervisor assigned to the production Area.', 1;
END
GO

CREATE OR ALTER TRIGGER dbo.TR_PotBatches_Update
ON dbo.PotProductionBatches
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- identity of a batch never changes
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.BatchCode <> d.BatchCode OR i.SourceCuttingStockId <> d.SourceCuttingStockId OR i.SpeciesId <> d.SpeciesId
                  OR i.AreaId <> d.AreaId OR i.PotSize <> d.PotSize OR i.EmptyPotInventoryId <> d.EmptyPotInventoryId
                  OR i.CuttingAllocated <> d.CuttingAllocated OR i.ProductionStartDate <> d.ProductionStartDate
                  OR i.SupervisorId <> d.SupervisorId OR i.CreatedById <> d.CreatedById)
        THROW 50211, 'The cuttings, Area, pot size, supervisor and creator of a pot batch cannot be changed.', 1;
    -- a Ready, Lost or Cancelled batch is closed
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN deleted d ON d.Id = i.Id
               WHERE d.Status <> N'InProduction'
                 AND (i.Status <> d.Status OR ISNULL(i.ReadyQuantity, -1) <> ISNULL(d.ReadyQuantity, -1)
                      OR ISNULL(i.ExpectedReadyDate, '19000101') <> ISNULL(d.ExpectedReadyDate, '19000101')))
        THROW 50212, 'This pot batch is closed.', 1;
    -- READY: ready pots <= pots produced; pots lost need a reason; cuttings
    -- not potted are either returned to stock or recorded as wastage
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN deleted d ON d.Id = i.Id
               CROSS APPLY (SELECT ISNULL(SUM(e.Quantity), 0) AS Potted FROM dbo.PotProductionEntries e WHERE e.BatchId = i.Id) p
               WHERE i.Status IN (N'Ready', N'Lost') AND d.Status = N'InProduction'
                 AND (i.ReadyQuantity > p.Potted
                      OR (i.ReadyQuantity < p.Potted AND i.WastageReason IS NULL)
                      OR (p.Potted < i.CuttingAllocated AND i.UnusedCuttingAction IS NULL)
                      OR (p.Potted = i.CuttingAllocated AND i.UnusedCuttingAction IS NOT NULL)))
        THROW 50213, 'READY quantity cannot exceed the pots produced; lost pots need a reason; unused cuttings need an action.', 1;
    -- READY / complete loss is confirmed by the assigned supervisor, who
    -- must still be an active Mother Plant Supervisor of the batch Area
    IF EXISTS (SELECT 1
               FROM inserted i
               INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.Status IN (N'Ready', N'Lost') AND d.Status = N'InProduction'
                 AND NOT EXISTS (SELECT 1
                                 FROM dbo.UserRoles ur
                                 INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
                                 INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
                                 WHERE ur.UserId = i.ReadyConfirmedById
                                   AND ur.AreaId = i.AreaId
                                   AND ISNULL(u.IsActive, 0) = 1
                                   AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor'))
        THROW 50218, 'READY must be confirmed by an active Mother Plant Supervisor of the batch Area.', 1;
    -- a batch with production entries is finished with READY, not cancelled
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.Status = N'Cancelled' AND d.Status = N'InProduction'
                 AND EXISTS (SELECT 1 FROM dbo.PotProductionEntries e WHERE e.BatchId = i.Id))
        THROW 50214, 'A batch that already has production cannot be cancelled.', 1;
END
GO

CREATE OR ALTER TRIGGER dbo.TR_PotEntries_Rules
ON dbo.PotProductionEntries
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 50215, 'A daily production entry cannot be changed or deleted.', 1;
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN dbo.PotProductionBatches b ON b.Id = i.BatchId
               WHERE b.Status <> N'InProduction' OR i.ProductionDate < b.ProductionStartDate)
        THROW 50216, 'Production can only be entered for a batch In Production, on or after its start date.', 1;
    IF EXISTS (SELECT 1
               FROM (SELECT DISTINCT BatchId FROM inserted) x
               INNER JOIN dbo.PotProductionBatches b ON b.Id = x.BatchId
               CROSS APPLY (SELECT SUM(e.Quantity) AS Potted FROM dbo.PotProductionEntries e WHERE e.BatchId = b.Id) p
               WHERE p.Potted > b.CuttingAllocated)
        THROW 50217, 'Pots produced cannot exceed the cuttings allocated to the batch.', 1;
END
GO

-- ============================================================================
-- 9. Area -> Polyhouse: ONE relationship, dbo.Polyhouses.AreaId
-- ============================================================================
-- Each Polyhouse belongs to an Area (an Area has many Polyhouses). The old
-- reverse link dbo.Area.PolyhouseId is retired: the application no longer
-- reads or writes it. Its values are copied ONCE to Polyhouses.AreaId, only
-- where that is unambiguous (the Polyhouse has no Area yet and exactly one
-- Area points at it). Nothing is deleted: the old column and its values stay
-- (history), and TR_Area_PolyhouseIdRetired stops new values being written.
-- The retired column must also accept NULL, so a new Area needs no
-- Polyhouse (Phase 14 intended this, but its ALTER could not run while
-- indexes / the FK used the column: this database still has it NOT NULL).
-- The FK and the three indexes are dropped and recreated IDENTICALLY around
-- the ALTER; no value changes.
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'PolyhouseId' AND is_nullable = 0)
BEGIN
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Area_Polyhouse' AND parent_object_id = OBJECT_ID('dbo.Area'))
        ALTER TABLE dbo.Area DROP CONSTRAINT FK_Area_Polyhouse;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Area_PolyhouseId' AND object_id = OBJECT_ID('dbo.Area'))
        DROP INDEX IX_Area_PolyhouseId ON dbo.Area;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Area_PolyhouseId_Name' AND object_id = OBJECT_ID('dbo.Area'))
        DROP INDEX UQ_Area_PolyhouseId_Name ON dbo.Area;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Area_PolyhouseId_AreaCode' AND object_id = OBJECT_ID('dbo.Area'))
        DROP INDEX UQ_Area_PolyhouseId_AreaCode ON dbo.Area;

    ALTER TABLE dbo.Area ALTER COLUMN PolyhouseId INT NULL;

    CREATE NONCLUSTERED INDEX IX_Area_PolyhouseId ON dbo.Area (PolyhouseId);
    CREATE UNIQUE NONCLUSTERED INDEX UQ_Area_PolyhouseId_Name ON dbo.Area (PolyhouseId, Name) WHERE PolyhouseId IS NOT NULL;
    CREATE UNIQUE NONCLUSTERED INDEX UQ_Area_PolyhouseId_AreaCode ON dbo.Area (PolyhouseId, AreaCode) WHERE PolyhouseId IS NOT NULL AND AreaCode IS NOT NULL;
    ALTER TABLE dbo.Area WITH CHECK ADD CONSTRAINT FK_Area_Polyhouse FOREIGN KEY (PolyhouseId) REFERENCES dbo.Polyhouses (Id);
END
GO

UPDATE ph
SET    ph.AreaId = x.AreaId
FROM   dbo.Polyhouses ph
INNER JOIN (SELECT a.PolyhouseId, MIN(a.Id) AS AreaId
            FROM dbo.Area a
            WHERE a.PolyhouseId IS NOT NULL
            GROUP BY a.PolyhouseId
            HAVING COUNT(*) = 1) x ON x.PolyhouseId = ph.Id
WHERE  ph.AreaId IS NULL;
GO

IF EXISTS (SELECT 1 FROM dbo.Area a INNER JOIN dbo.Polyhouses ph ON ph.Id = a.PolyhouseId
           WHERE ph.AreaId IS NULL OR ph.AreaId <> a.Id)
    PRINT 'NOTE: some old Area.PolyhouseId links were NOT copied (ambiguous or the Polyhouse already has another Area). Assign them in Admin > Polyhouses.';
GO

CREATE OR ALTER TRIGGER dbo.TR_Area_PolyhouseIdRetired
ON dbo.Area
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- the retired link may keep its historical value but never gets a new one
    IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id = i.Id
               WHERE i.PolyhouseId IS NOT NULL AND (d.Id IS NULL OR ISNULL(d.PolyhouseId, -1) <> i.PolyhouseId))
        THROW 50220, 'Area.PolyhouseId is retired: assign the Polyhouse to the Area in Admin > Polyhouses (Polyhouses.AreaId).', 1;
END
GO

-- ============================================================================
-- 10. Roles for the production workflow (created empty -- no user assigned)
-- ============================================================================
-- Each action is mapped to the employee who performs it, with only the
-- permissions that job needs (no existing role gains anything):
--   Main Office Store Keeper  receives cuttings at Main Office, issues empty
--                             pots to Areas, sends potted plants on
--   Purchase Officer          records empty pot purchases / purchase orders
--   Pot Production Operator   starts pot batches, enters daily production,
--                             sends READY plants from the production Area
--   Outlet Sales              sells to customers at an Outlet, receives
--                             plants at the Outlet
-- Area scope comes from the Area chosen when the role is given to a user
-- (Admin > User Roles), exactly like the existing roles.
DECLARE @NewRoles TABLE (RoleName NVARCHAR(100) PRIMARY KEY, Description NVARCHAR(250));
INSERT INTO @NewRoles VALUES
    (N'Main Office Store Keeper', N'Main Office: confirms cuttings received, issues empty pots to Areas, sends potted plants to Outlets'),
    (N'Purchase Officer',         N'Records empty pot purchases and purchase orders'),
    (N'Pot Production Operator',  N'Production Area: starts pot batches, enters daily pot production, sends READY plants on'),
    (N'Outlet Sales',             N'Outlet: sells potted plants to customers and receives stock at the Outlet');

INSERT INTO dbo.Roles (Name, RoleName, Description, IsSystemRole)
SELECT n.RoleName, n.RoleName, n.Description, 0
FROM @NewRoles n
WHERE NOT EXISTS (SELECT 1 FROM dbo.Roles r
                  WHERE r.RoleName = n.RoleName OR COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = n.RoleName);

DECLARE @Grants TABLE (RoleName NVARCHAR(100), Code NVARCHAR(100));
INSERT INTO @Grants VALUES
    (N'Main Office Store Keeper', N'Dashboard.View'),
    (N'Main Office Store Keeper', N'MainOffice.View'),
    (N'Main Office Store Keeper', N'MainOffice.Confirm'),
    (N'Main Office Store Keeper', N'InternalTransfer.View'),
    (N'Main Office Store Keeper', N'InternalTransfer.Enter'),
    (N'Main Office Store Keeper', N'PotProduction.View'),
    (N'Purchase Officer',         N'Dashboard.View'),
    (N'Purchase Officer',         N'Purchase.View'),
    (N'Purchase Officer',         N'Purchase.Enter'),
    (N'Pot Production Operator',  N'Dashboard.View'),
    (N'Pot Production Operator',  N'PotProduction.View'),
    (N'Pot Production Operator',  N'PotProduction.Enter'),
    (N'Pot Production Operator',  N'InternalTransfer.View'),
    (N'Pot Production Operator',  N'InternalTransfer.Enter'),
    (N'Outlet Sales',             N'Dashboard.View'),
    (N'Outlet Sales',             N'Outlet.View'),
    (N'Outlet Sales',             N'Outlet.Sell'),
    (N'Outlet Sales',             N'Outlet.Confirm');

IF EXISTS (SELECT 1 FROM @Grants g WHERE NOT EXISTS (SELECT 1 FROM dbo.Permissions p WHERE p.Code = g.Code))
    THROW 50221, 'A permission code used by the new roles does not exist in dbo.Permissions.', 1;

INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id
FROM @Grants g
INNER JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = g.RoleName
INNER JOIN dbo.Permissions p ON p.Code = g.Code
WHERE NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
GO

-- ============================================================================
-- 11. Retire unused Mother Plant Supervisor grants (no active page uses them)
-- ============================================================================
-- CuttingDelivery.Enter / CuttingDelivery.View / CuttingPlan.View belonged to
-- the retired CuttingDelivery / CuttingPlan pages (removed by this same
-- restructure). No page in the application checks these permission codes
-- any more (Cutting is now Mother Plant -> Cutting Production -> Cutting
-- Stock, gated by MotherPlant.*). Only the GRANT to Mother Plant Supervisor
-- is removed; dbo.Permissions keeps the codes (so any historical audit that
-- joins on PermissionId still resolves), and no OTHER role's grants are
-- touched.
DECLARE @RemovedGrants TABLE (RoleName NVARCHAR(100), Code NVARCHAR(100));
INSERT INTO @RemovedGrants VALUES
    (N'Mother Plant Supervisor', N'CuttingDelivery.Enter'),
    (N'Mother Plant Supervisor', N'CuttingDelivery.View'),
    (N'Mother Plant Supervisor', N'CuttingPlan.View');

DELETE rp
FROM dbo.RolePermissions rp
INNER JOIN dbo.Roles r ON r.Id = rp.RoleId
INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
INNER JOIN @RemovedGrants g ON g.RoleName = COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AND g.Code = p.Code;
GO
