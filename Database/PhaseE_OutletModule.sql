-- ============================================================================
-- Phase E: Outlet module (direct customer sale, customer booking, external
-- purchase, wastage) on top of the EXISTING stock architecture.
-- ============================================================================
-- TARGET: PlantsIMS2_Test ONLY (the guard below refuses any other database).
-- Run AFTER PhaseD_ProductionRestructure.sql. Idempotent: every object is
-- guarded, safe to re-run. (This script has only ever been dry-run tested,
-- never applied, so its objects are defined here in their final shape --
-- there is no earlier live version to migrate away from.)
--
-- An Outlet is simply an Area with AreaType = 'Outlet' -- there is NO new
-- Outlet stock table. Outlet potted-plant stock is the existing
-- dbo.PottedPlantStock; Outlet tray stock is the existing dbo.ReadyStock
-- (already transferable there). Every Sale/Booking/Wastage item identifies
-- EXACTLY ONE of the two by a StockType discriminator ('Potted' | 'Tray'),
-- enforced by a CHECK, and a composite FK to the matching stock table so
-- the row can never point at stock from a different Outlet or a mismatched
-- variety/pot-size/cavity.
--   dbo.OutletPurchases            external purchase -> PottedPlantStock
--                                  ('Purchase' ledger type)
--   dbo.OutletSales /
--   dbo.OutletSaleItems            one customer, any number of items, EACH
--                                  potted OR tray, one atomic transaction
--   dbo.OutletBookings /
--   dbo.OutletBookingItems         a customer order across potted AND/OR
--                                  tray items, reserved now, collected
--                                  (fully or partially) later, cancel
--                                  releases the reservation -- reusing the
--                                  existing ReservedQuantity (potted) and
--                                  the existing Reservation/
--                                  ReservationRelease ledger (trays)
--   dbo.OutletWastages              Outlet-recorded loss of its OWN potted
--                                  or tray stock ('Wastage' ledger type,
--                                  already distinct from Sale/Dispatch/
--                                  Reservation on both ledgers) -- a
--                                  DIFFERENT concept from nursery
--                                  production wastage; no automatic
--                                  percentage is ever applied
--   PottedPlantStockTransactions gains 'Purchase'
--   ReadyStockTransactions gains 'Transfer' and 'Wastage'
--   dbo.ReadyStock gains a composite unique key (Id, SpeciesId, CavityType,
--   AreaId) so a Tray item's FK can pin it to its own Outlet's own batch
--   dbo.Permissions gains Outlet.Purchase; Outlet.View/Outlet.Sell (already
--   exist) are reused for everything else -- no new role
--
-- Existing data: no existing row is changed. New CHECKs only apply to new
-- rows.
-- ============================================================================

IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50299, 'PhaseE_OutletModule.sql may only be run against PlantsIMS2_Test.', 1;
GO

-- ============================================================================
-- 1. Ledger types: 'Purchase' (potted), 'Transfer' + 'Wastage' (trays)
-- ============================================================================
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedStockTx_Type')
    ALTER TABLE dbo.PottedPlantStockTransactions DROP CONSTRAINT CK_PottedStockTx_Type;
ALTER TABLE dbo.PottedPlantStockTransactions WITH CHECK ADD CONSTRAINT CK_PottedStockTx_Type
    CHECK (TransactionType IN (N'LabReceived', N'LabSent', N'ReversalRemoval', N'Adjustment', N'Transfer',
                                N'Wastage', N'Dispatch', N'ReservationRelease', N'Reservation', N'Production', N'Purchase'));
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStockTx_Type')
    ALTER TABLE dbo.ReadyStockTransactions DROP CONSTRAINT CK_ReadyStockTx_Type;
ALTER TABLE dbo.ReadyStockTransactions WITH CHECK ADD CONSTRAINT CK_ReadyStockTx_Type
    CHECK (TransactionType IN (N'Dispatch', N'ReservationRelease', N'Reservation', N'ReversalRemoval', N'Confirmed', N'Transfer', N'Wastage'));
GO

-- ============================================================================
-- 2. dbo.ReadyStock: a composite unique key so a Tray sale/booking/wastage
--    item's FK can pin it to its own batch's variety/cavity/Area (mirrors
--    dbo.PottedPlantStock's own UQ_PottedPlantStock_IdSpeciesPotSizeArea).
--    Id is already the PK (unique alone); adding it to a wider unique
--    constraint changes nothing about existing rows.
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_ReadyStock_IdSpeciesCavityArea')
BEGIN
    ALTER TABLE dbo.ReadyStock ADD CONSTRAINT UQ_ReadyStock_IdSpeciesCavityArea UNIQUE (Id, SpeciesId, CavityType, AreaId);
END
GO

-- ============================================================================
-- 3. dbo.Permissions: Outlet.Purchase (Outlet.View / Outlet.Sell already exist)
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'Outlet.Purchase')
BEGIN
    INSERT INTO dbo.Permissions (Code, Description) VALUES (N'Outlet.Purchase', N'Record an Outlet''s direct purchase from an external supplier');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp
               INNER JOIN dbo.Roles r ON r.Id = rp.RoleId
               INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
               WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Outlet Sales' AND p.Code = N'Outlet.Purchase')
BEGIN
    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id FROM dbo.Roles r, dbo.Permissions p
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Outlet Sales' AND p.Code = N'Outlet.Purchase';
END
GO

-- ============================================================================
-- 4. dbo.OutletPurchases: an Outlet's own direct purchase of POTTED PLANTS
--    (immutable ledger fact). Trays are never bought externally -- they only
--    ever arrive by transfer from Main Office (section 12 of the business
--    requirement); nothing here contradicts that.
-- ============================================================================
IF OBJECT_ID('dbo.OutletPurchases', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OutletPurchases
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OutletPurchases PRIMARY KEY,
        PurchaseCode        NVARCHAR(40)  NOT NULL CONSTRAINT UQ_OutletPurchases_Code UNIQUE,
        PurchaseDate        DATE          NOT NULL,
        SupplierName        NVARCHAR(200) NOT NULL,
        OutletAreaId        INT           NOT NULL,
        SpeciesId           INT           NOT NULL,
        PotSize             NVARCHAR(50)  NOT NULL,
        Quantity            DECIMAL(18,2) NOT NULL,
        PottedPlantStockId  INT           NOT NULL,
        Remarks             NVARCHAR(500) NULL,
        CreatedById         INT           NULL,
        CreatedBy           NVARCHAR(100) NULL,
        CreatedDate         DATETIME2     NOT NULL CONSTRAINT DF_OutletPurchases_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_OutletPurchases_Quantity CHECK (Quantity > 0 AND Quantity = FLOOR(Quantity)),
        CONSTRAINT FK_OutletPurchases_Area FOREIGN KEY (OutletAreaId) REFERENCES dbo.Area (Id),
        CONSTRAINT FK_OutletPurchases_Species FOREIGN KEY (SpeciesId) REFERENCES dbo.PlantSpecies (Id),
        CONSTRAINT FK_OutletPurchases_PotSize FOREIGN KEY (PotSize) REFERENCES dbo.PotSizes (Name),
        CONSTRAINT FK_OutletPurchases_Stock FOREIGN KEY (PottedPlantStockId, SpeciesId, PotSize, OutletAreaId)
            REFERENCES dbo.PottedPlantStock (Id, SpeciesId, PotSize, AreaId),
        CONSTRAINT FK_OutletPurchases_CreatedBy FOREIGN KEY (CreatedById) REFERENCES dbo.IMSUsers (Id)
    );
    CREATE NONCLUSTERED INDEX IX_OutletPurchases_Area ON dbo.OutletPurchases (OutletAreaId, PurchaseDate);
END
GO

CREATE OR ALTER TRIGGER dbo.TR_OutletPurchases_Rules
ON dbo.OutletPurchases
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 50222, 'A recorded Outlet purchase cannot be changed or deleted.', 1;
    IF EXISTS (SELECT 1 FROM inserted i
               WHERE NOT EXISTS (SELECT 1 FROM dbo.Area a WHERE a.Id = i.OutletAreaId AND a.AreaType = N'Outlet' AND a.IsActive = 1))
        THROW 50223, 'Outlet purchases must be received into an active Outlet.', 1;
END
GO

-- ============================================================================
-- 5. dbo.OutletSales / dbo.OutletSaleItems: one customer, any mix of
--    potted-plant and ready-tray items, one atomic transaction. Immutable
--    once recorded. Exactly one of PottedPlantStockId/ReadyStockId is set,
--    matching StockType -- enforced by CK_OutletSaleItems_StockType.
-- ============================================================================
IF OBJECT_ID('dbo.OutletSales', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OutletSales
    (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OutletSales PRIMARY KEY,
        SaleCode        NVARCHAR(40)  NOT NULL CONSTRAINT UQ_OutletSales_Code UNIQUE,
        OutletAreaId    INT           NOT NULL,
        CustomerName    NVARCHAR(200) NOT NULL,
        CustomerContact NVARCHAR(50)  NULL,
        SaleDate        DATE          NOT NULL,
        Remarks         NVARCHAR(500) NULL,
        CreatedById     INT           NULL,
        CreatedBy       NVARCHAR(100) NULL,
        CreatedDate     DATETIME2     NOT NULL CONSTRAINT DF_OutletSales_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_OutletSales_Area FOREIGN KEY (OutletAreaId) REFERENCES dbo.Area (Id),
        CONSTRAINT FK_OutletSales_CreatedBy FOREIGN KEY (CreatedById) REFERENCES dbo.IMSUsers (Id)
    );
    CREATE NONCLUSTERED INDEX IX_OutletSales_Area ON dbo.OutletSales (OutletAreaId, SaleDate);
END
GO

IF OBJECT_ID('dbo.OutletSaleItems', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OutletSaleItems
    (
        Id                 INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OutletSaleItems PRIMARY KEY,
        SaleId             INT           NOT NULL CONSTRAINT FK_OutletSaleItems_Sale REFERENCES dbo.OutletSales (Id),
        OutletAreaId       INT           NOT NULL,
        StockType          NVARCHAR(10)  NOT NULL,   -- 'Potted' | 'Tray'
        PottedPlantStockId INT           NULL,
        ReadyStockId       INT           NULL,
        SpeciesId          INT           NOT NULL,
        PotSize            NVARCHAR(50)  NULL,        -- 'Potted' only
        CavityType         NVARCHAR(30)  NULL,        -- 'Tray' only (must match dbo.ReadyStock.CavityType's length for the composite FK)
        Quantity           DECIMAL(18,2) NOT NULL,     -- pots ('Potted') or WHOLE TRAYS ('Tray')
        CreatedDate        DATETIME2     NOT NULL CONSTRAINT DF_OutletSaleItems_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_OutletSaleItems_Quantity CHECK (Quantity > 0 AND Quantity = FLOOR(Quantity)),
        CONSTRAINT CK_OutletSaleItems_StockType CHECK (
            (StockType = N'Potted' AND PottedPlantStockId IS NOT NULL AND ReadyStockId IS NULL AND PotSize IS NOT NULL AND CavityType IS NULL)
         OR (StockType = N'Tray' AND ReadyStockId IS NOT NULL AND PottedPlantStockId IS NULL AND CavityType IS NOT NULL AND PotSize IS NULL)),
        CONSTRAINT FK_OutletSaleItems_PottedStock FOREIGN KEY (PottedPlantStockId, SpeciesId, PotSize, OutletAreaId)
            REFERENCES dbo.PottedPlantStock (Id, SpeciesId, PotSize, AreaId),
        CONSTRAINT FK_OutletSaleItems_ReadyStock FOREIGN KEY (ReadyStockId, SpeciesId, CavityType, OutletAreaId)
            REFERENCES dbo.ReadyStock (Id, SpeciesId, CavityType, AreaId)
    );
    CREATE NONCLUSTERED INDEX IX_OutletSaleItems_Sale ON dbo.OutletSaleItems (SaleId);
END
GO

CREATE OR ALTER TRIGGER dbo.TR_OutletSales_Immutable
ON dbo.OutletSales
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50224, 'A recorded Outlet sale cannot be changed or deleted.', 1;
END
GO

CREATE OR ALTER TRIGGER dbo.TR_OutletSaleItems_Rules
ON dbo.OutletSaleItems
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 50225, 'A recorded Outlet sale item cannot be changed or deleted.', 1;
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN dbo.OutletSales s ON s.Id = i.SaleId WHERE s.OutletAreaId <> i.OutletAreaId)
        THROW 50226, 'A sale item must be stock from the sale''s own Outlet.', 1;
END
GO

-- ============================================================================
-- 6. dbo.OutletBookings / dbo.OutletBookingItems: reserve now, collect
--    (fully or partially) later, cancel releases the reservation -- across
--    a mix of potted and tray items in one booking.
-- ============================================================================
IF OBJECT_ID('dbo.OutletBookings', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OutletBookings
    (
        Id                 INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OutletBookings PRIMARY KEY,
        BookingCode        NVARCHAR(40)  NOT NULL CONSTRAINT UQ_OutletBookings_Code UNIQUE,
        OutletAreaId       INT           NOT NULL,
        CustomerName       NVARCHAR(200) NOT NULL,
        CustomerContact    NVARCHAR(50)  NULL,
        BookingDate        DATE          NOT NULL,
        RequiredDate       DATE          NULL,
        Status             NVARCHAR(20)  NOT NULL CONSTRAINT DF_OutletBookings_Status DEFAULT (N'Pending'),
        Remarks            NVARCHAR(500) NULL,
        CreatedById        INT           NULL,
        CreatedBy          NVARCHAR(100) NULL,
        CreatedDate        DATETIME2     NOT NULL CONSTRAINT DF_OutletBookings_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CancelledById      INT           NULL,
        CancelledDate      DATETIME2     NULL,
        CancellationReason NVARCHAR(200) NULL,
        ModifiedBy         NVARCHAR(100) NULL,
        ModifiedDate       DATETIME2     NULL,
        CONSTRAINT CK_OutletBookings_Status CHECK (Status IN (N'Pending', N'PartiallyCollected', N'Completed', N'Cancelled')),
        CONSTRAINT CK_OutletBookings_Dates CHECK (RequiredDate IS NULL OR RequiredDate >= BookingDate),
        CONSTRAINT CK_OutletBookings_CancelFields CHECK (
            (Status = N'Cancelled' AND CancelledById IS NOT NULL AND CancelledDate IS NOT NULL AND CancellationReason IS NOT NULL)
         OR (Status <> N'Cancelled' AND CancelledById IS NULL AND CancelledDate IS NULL AND CancellationReason IS NULL)),
        CONSTRAINT FK_OutletBookings_Area FOREIGN KEY (OutletAreaId) REFERENCES dbo.Area (Id),
        CONSTRAINT FK_OutletBookings_CreatedBy FOREIGN KEY (CreatedById) REFERENCES dbo.IMSUsers (Id),
        CONSTRAINT FK_OutletBookings_CancelledBy FOREIGN KEY (CancelledById) REFERENCES dbo.IMSUsers (Id)
    );
    CREATE NONCLUSTERED INDEX IX_OutletBookings_Area ON dbo.OutletBookings (OutletAreaId, Status);
END
GO

IF OBJECT_ID('dbo.OutletBookingItems', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OutletBookingItems
    (
        Id                 INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OutletBookingItems PRIMARY KEY,
        BookingId          INT           NOT NULL CONSTRAINT FK_OutletBookingItems_Booking REFERENCES dbo.OutletBookings (Id),
        OutletAreaId       INT           NOT NULL,
        StockType          NVARCHAR(10)  NOT NULL,   -- 'Potted' | 'Tray'
        PottedPlantStockId INT           NULL,
        ReadyStockId       INT           NULL,
        SpeciesId          INT           NOT NULL,
        PotSize            NVARCHAR(50)  NULL,
        CavityType         NVARCHAR(30)  NULL,
        Quantity           DECIMAL(18,2) NOT NULL,     -- pots or WHOLE TRAYS, ordered
        CollectedQuantity  DECIMAL(18,2) NOT NULL CONSTRAINT DF_OutletBookingItems_Collected DEFAULT (0),
        CreatedDate        DATETIME2     NOT NULL CONSTRAINT DF_OutletBookingItems_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_OutletBookingItems_Quantity CHECK (Quantity > 0 AND Quantity = FLOOR(Quantity)),
        CONSTRAINT CK_OutletBookingItems_Collected CHECK (CollectedQuantity >= 0 AND CollectedQuantity <= Quantity AND CollectedQuantity = FLOOR(CollectedQuantity)),
        CONSTRAINT CK_OutletBookingItems_StockType CHECK (
            (StockType = N'Potted' AND PottedPlantStockId IS NOT NULL AND ReadyStockId IS NULL AND PotSize IS NOT NULL AND CavityType IS NULL)
         OR (StockType = N'Tray' AND ReadyStockId IS NOT NULL AND PottedPlantStockId IS NULL AND CavityType IS NOT NULL AND PotSize IS NULL)),
        CONSTRAINT FK_OutletBookingItems_PottedStock FOREIGN KEY (PottedPlantStockId, SpeciesId, PotSize, OutletAreaId)
            REFERENCES dbo.PottedPlantStock (Id, SpeciesId, PotSize, AreaId),
        CONSTRAINT FK_OutletBookingItems_ReadyStock FOREIGN KEY (ReadyStockId, SpeciesId, CavityType, OutletAreaId)
            REFERENCES dbo.ReadyStock (Id, SpeciesId, CavityType, AreaId)
    );
    CREATE NONCLUSTERED INDEX IX_OutletBookingItems_Booking ON dbo.OutletBookingItems (BookingId);
END
GO

CREATE OR ALTER TRIGGER dbo.TR_OutletBookings_Update
ON dbo.OutletBookings
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.OutletAreaId <> d.OutletAreaId OR i.BookingCode <> d.BookingCode OR i.CreatedById <> d.CreatedById)
        THROW 50227, 'The Outlet and creator of a booking cannot be changed.', 1;
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN deleted d ON d.Id = i.Id
               WHERE d.Status IN (N'Completed', N'Cancelled') AND i.Status <> d.Status)
        THROW 50228, 'This booking is closed.', 1;
END
GO

CREATE OR ALTER TRIGGER dbo.TR_OutletBookingItems_Rules
ON dbo.OutletBookingItems
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.Id = d.Id WHERE i.Id IS NULL)
        THROW 50229, 'A booking item cannot be deleted.', 1;
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN deleted d ON d.Id = i.Id
               WHERE i.BookingId <> d.BookingId OR i.OutletAreaId <> d.OutletAreaId OR i.StockType <> d.StockType
                  OR ISNULL(i.PottedPlantStockId, -1) <> ISNULL(d.PottedPlantStockId, -1) OR ISNULL(i.ReadyStockId, -1) <> ISNULL(d.ReadyStockId, -1)
                  OR i.SpeciesId <> d.SpeciesId OR ISNULL(i.PotSize, N'') <> ISNULL(d.PotSize, N'') OR ISNULL(i.CavityType, N'') <> ISNULL(d.CavityType, N'')
                  OR i.Quantity <> d.Quantity)
        THROW 50230, 'A booking item''s stock and quantity cannot be changed once recorded -- only CollectedQuantity may change.', 1;
    IF EXISTS (SELECT 1 FROM inserted i INNER JOIN dbo.OutletBookings b ON b.Id = i.BookingId WHERE b.OutletAreaId <> i.OutletAreaId)
        THROW 50231, 'A booking item must be stock from the booking''s own Outlet.', 1;
END
GO

-- ============================================================================
-- 7. dbo.OutletWastages: an Outlet recording the loss of its OWN potted or
--    tray stock -- a DIFFERENT concept from nursery production wastage; no
--    automatic percentage is ever applied. Immutable once recorded.
-- ============================================================================
IF OBJECT_ID('dbo.OutletWastages', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OutletWastages
    (
        Id                 INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OutletWastages PRIMARY KEY,
        WastageCode        NVARCHAR(40)  NOT NULL CONSTRAINT UQ_OutletWastages_Code UNIQUE,
        WastageDate        DATE          NOT NULL,
        OutletAreaId       INT           NOT NULL,
        StockType          NVARCHAR(10)  NOT NULL,   -- 'Potted' | 'Tray'
        PottedPlantStockId INT           NULL,
        ReadyStockId       INT           NULL,
        SpeciesId          INT           NOT NULL,
        PotSize            NVARCHAR(50)  NULL,
        CavityType         NVARCHAR(30)  NULL,
        Quantity           DECIMAL(18,2) NOT NULL,     -- pots or WHOLE TRAYS
        Reason             NVARCHAR(100) NOT NULL,
        Remarks            NVARCHAR(500) NULL,
        CreatedById        INT           NULL,
        CreatedBy          NVARCHAR(100) NULL,
        CreatedDate        DATETIME2     NOT NULL CONSTRAINT DF_OutletWastages_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_OutletWastages_Quantity CHECK (Quantity > 0 AND Quantity = FLOOR(Quantity)),
        CONSTRAINT CK_OutletWastages_Reason CHECK (Reason IN (N'Damaged', N'Died / Wilted', N'Pest or Disease', N'Breakage', N'Other')),
        CONSTRAINT CK_OutletWastages_StockType CHECK (
            (StockType = N'Potted' AND PottedPlantStockId IS NOT NULL AND ReadyStockId IS NULL AND PotSize IS NOT NULL AND CavityType IS NULL)
         OR (StockType = N'Tray' AND ReadyStockId IS NOT NULL AND PottedPlantStockId IS NULL AND CavityType IS NOT NULL AND PotSize IS NULL)),
        CONSTRAINT FK_OutletWastages_PottedStock FOREIGN KEY (PottedPlantStockId, SpeciesId, PotSize, OutletAreaId)
            REFERENCES dbo.PottedPlantStock (Id, SpeciesId, PotSize, AreaId),
        CONSTRAINT FK_OutletWastages_ReadyStock FOREIGN KEY (ReadyStockId, SpeciesId, CavityType, OutletAreaId)
            REFERENCES dbo.ReadyStock (Id, SpeciesId, CavityType, AreaId),
        CONSTRAINT FK_OutletWastages_CreatedBy FOREIGN KEY (CreatedById) REFERENCES dbo.IMSUsers (Id)
    );
    CREATE NONCLUSTERED INDEX IX_OutletWastages_Area ON dbo.OutletWastages (OutletAreaId, WastageDate);
END
GO

CREATE OR ALTER TRIGGER dbo.TR_OutletWastages_Rules
ON dbo.OutletWastages
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 50232, 'A recorded Outlet wastage cannot be changed or deleted.', 1;
    IF EXISTS (SELECT 1 FROM inserted i
               WHERE NOT EXISTS (SELECT 1 FROM dbo.Area a WHERE a.Id = i.OutletAreaId AND a.AreaType = N'Outlet' AND a.IsActive = 1))
        THROW 50233, 'Outlet wastage must be recorded against an active Outlet.', 1;
END
GO
