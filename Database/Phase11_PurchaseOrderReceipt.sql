/* ============================================================
   Phase 11: Purchase Order / Purchase Receipt
   ------------------------------------------------------------
   Per Decision 2, dbo.VendorPurchases and its C# stub
   (Models/VendorPurchase.cs, Data/VendorPurchaseRepository.cs,
   Program.cs' VendorPurchaseRepository DI registration) are left
   COMPLETELY ALONE -- not read, altered, migrated, or reinterpreted
   by anything in this file. This is a brand new, separate system.

   Business-rule scope (confirmed with the user before writing this
   migration): a Purchase Order is multi-category --
     - 'Fertilizer' lines feed the EXISTING dbo.FertilizerStock
       table on receipt, using the exact same columns/insert shape
       Pages/Fertilizer/Stock.cshtml.cs already writes manually
       (FertilizerId, Quantity, LatestAvailableQuantity, UnitId,
       PurchaseDate, ExpiryDate, SourceId, BatchNumber, IsUtilized).
       FertilizerStock has no ledger of its own (it's a per-batch
       table, not a running-balance table like Phase 7+'s stock
       entities) -- a receipt simply inserts a new batch row, just
       as if someone had entered it by hand on that page. Existing
       Fertilizer Stock/Usage pages are completely unmodified and
       will simply see the new batch rows appear.
     - 'EmptyPot' lines feed dbo.EmptyPotInventory (Phase 7/8) via
       EmptyPotInventoryRepository.RecordTransactionAsync/
       GetOrCreateLockedAsync, TransactionType = 'StockIn', reusing
       that ledger exactly as manual "Add Stock" already does.
     - 'Other' lines are recorded for procurement paperwork/
       traceability only -- no stock table exists for miscellaneous
       supplies, so nothing is posted anywhere on receipt.
   No new "generic stock" table is invented for any of this, per
   Decision 1 (dedicated ledger per stock domain, and here: reuse
   the domain's OWN existing table, don't build a parallel one).

   New master: dbo.Vendors. No general-purpose vendor table existed
   before this (VendorPurchase.VendorName was free text; FertilizerSource
   is a Fertilizer-specific supplier list, not a general vendor master,
   and is intentionally left as-is and reused as its own dropdown for
   Fertilizer lines rather than merged into Vendors).

   Purchase Orders are received in one or more Purchase Receipt events
   (partial receipt across multiple deliveries is supported), each
   receipt line increasing the parent PurchaseOrderItem's
   ReceivedQuantity (capped at OrderedQuantity -- never over-receive,
   consistent with the project's "never exceed available" stock rule).
   PO Status is recomputed after each receipt: Pending -> PartiallyReceived
   -> Completed. A PO can only be Cancelled while still 'Pending' (no
   receipts posted yet) -- once any stock has moved via a receipt,
   reversing it is out of this phase's scope, exactly like Phase 10
   documented full-dispatch-only as a deliberate scope decision;
   a future phase could add receipt-level reversal as its own
   additive migration.

   ADDITIVE ONLY. Run this AFTER Phase10_Dispatch.sql has been
   applied. Take a backup first per your own DB safety rules.
   ============================================================ */

-- ---------- dbo.Vendors ----------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Vendors' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Vendors
    (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Vendors PRIMARY KEY,
        Name            NVARCHAR(200)  NOT NULL,
        ContactPerson   NVARCHAR(200)  NULL,
        Phone           NVARCHAR(50)   NULL,
        Email           NVARCHAR(200)  NULL,
        Address         NVARCHAR(500)  NULL,
        GSTIN           NVARCHAR(50)   NULL,
        IsActive        BIT            NOT NULL CONSTRAINT DF_Vendors_IsActive DEFAULT (1),
        CreatedDate     DATETIME2      NOT NULL CONSTRAINT DF_Vendors_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy       NVARCHAR(100)  NULL,
        ModifiedDate    DATETIME2      NULL,
        ModifiedBy      NVARCHAR(100)  NULL
    );
    CREATE INDEX IX_Vendors_Name ON dbo.Vendors(Name);
END
GO

-- ---------- dbo.PurchaseOrders ----------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PurchaseOrders' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PurchaseOrders
    (
        Id                      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseOrders PRIMARY KEY,
        PurchaseOrderCode       NVARCHAR(20)   NOT NULL,
        VendorId                INT            NOT NULL,
        OrderDate               DATETIME2      NOT NULL CONSTRAINT DF_PurchaseOrders_OrderDate DEFAULT (SYSUTCDATETIME()),
        ExpectedDeliveryDate    DATE           NULL,
        Status                  NVARCHAR(30)   NOT NULL CONSTRAINT DF_PurchaseOrders_Status DEFAULT ('Pending'),
        Remarks                 NVARCHAR(500)  NULL,
        CreatedDate             DATETIME2      NOT NULL CONSTRAINT DF_PurchaseOrders_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy               NVARCHAR(100)  NULL,
        ModifiedDate            DATETIME2      NULL,
        ModifiedBy              NVARCHAR(100)  NULL,

        CONSTRAINT UQ_PurchaseOrders_Code UNIQUE (PurchaseOrderCode),
        CONSTRAINT FK_PurchaseOrders_Vendor FOREIGN KEY (VendorId) REFERENCES dbo.Vendors(Id),
        CONSTRAINT CK_PurchaseOrders_Status CHECK (Status IN ('Pending', 'PartiallyReceived', 'Completed', 'Cancelled'))
    );
    CREATE INDEX IX_PurchaseOrders_Vendor ON dbo.PurchaseOrders(VendorId);
    CREATE INDEX IX_PurchaseOrders_Status ON dbo.PurchaseOrders(Status);
END
GO

-- ---------- dbo.PurchaseOrderItems ----------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PurchaseOrderItems' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PurchaseOrderItems
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseOrderItems PRIMARY KEY,
        PurchaseOrderId     INT            NOT NULL,
        ItemCategory        NVARCHAR(20)   NOT NULL,

        -- Populated only when ItemCategory = 'Fertilizer'. FertilizerSourceId
        -- and FertilizerUnitId are required so a receipt can insert a
        -- correctly-populated dbo.FertilizerStock row (SourceId/UnitId are
        -- NOT NULL on that existing table).
        FertilizerId        INT            NULL,
        FertilizerSourceId  INT            NULL,
        FertilizerUnitId    INT            NULL,
        ExpiryDate          DATE           NULL,

        -- Populated only when ItemCategory = 'EmptyPot'. AreaId is the
        -- destination pool -- NULL means the legacy/"unassigned location"
        -- pool, same convention as every other Area-aware table since
        -- Phase 8.
        PotSize              NVARCHAR(50)  NULL,
        AreaId               INT           NULL,

        -- Populated only when ItemCategory = 'Other'.
        ItemName             NVARCHAR(200) NULL,

        OrderedQuantity      DECIMAL(18,2) NOT NULL,
        ReceivedQuantity     DECIMAL(18,2) NOT NULL CONSTRAINT DF_PurchaseOrderItems_ReceivedQuantity DEFAULT (0),
        UnitPrice            DECIMAL(18,2) NULL,
        Remarks              NVARCHAR(500) NULL,

        CreatedDate          DATETIME2     NOT NULL CONSTRAINT DF_PurchaseOrderItems_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy             NVARCHAR(100) NULL,
        ModifiedDate          DATETIME2     NULL,
        ModifiedBy            NVARCHAR(100) NULL,

        CONSTRAINT FK_PurchaseOrderItems_Order       FOREIGN KEY (PurchaseOrderId)    REFERENCES dbo.PurchaseOrders(Id),
        CONSTRAINT FK_PurchaseOrderItems_Fertilizer   FOREIGN KEY (FertilizerId)       REFERENCES dbo.FertilizerMaster(FertilizerId),
        CONSTRAINT FK_PurchaseOrderItems_FertSource   FOREIGN KEY (FertilizerSourceId) REFERENCES dbo.FertilizerSource(SourceId),
        CONSTRAINT FK_PurchaseOrderItems_FertUnit     FOREIGN KEY (FertilizerUnitId)   REFERENCES dbo.UnitMaster(UnitId),
        CONSTRAINT FK_PurchaseOrderItems_Area         FOREIGN KEY (AreaId)             REFERENCES dbo.Area(Id),

        CONSTRAINT CK_PurchaseOrderItems_Category  CHECK (ItemCategory IN ('Fertilizer', 'EmptyPot', 'Other')),
        CONSTRAINT CK_PurchaseOrderItems_OrderedQty CHECK (OrderedQuantity > 0),
        CONSTRAINT CK_PurchaseOrderItems_ReceivedQty CHECK (ReceivedQuantity >= 0 AND ReceivedQuantity <= OrderedQuantity)
    );
    CREATE INDEX IX_PurchaseOrderItems_Order ON dbo.PurchaseOrderItems(PurchaseOrderId);
END
GO

-- Backstop: exactly the fields matching ItemCategory are populated,
-- nothing from another category leaks in. Same idempotent
-- fn_/CK_ pattern used at every phase since Phase 6.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_PurchaseOrderItems_CategoryFields' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_PurchaseOrderItems_CategoryFields(@ItemCategory NVARCHAR(20), @FertilizerId INT, @FertilizerSourceId INT, @FertilizerUnitId INT, @PotSize NVARCHAR(50), @AreaId INT, @ItemName NVARCHAR(200))
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        IF @ItemCategory = ''Fertilizer'' AND @FertilizerId IS NOT NULL AND @FertilizerSourceId IS NOT NULL AND @FertilizerUnitId IS NOT NULL
           AND @PotSize IS NULL AND @AreaId IS NULL AND @ItemName IS NULL
            SET @Result = 1;
        ELSE IF @ItemCategory = ''EmptyPot'' AND @PotSize IS NOT NULL
           AND @FertilizerId IS NULL AND @FertilizerSourceId IS NULL AND @FertilizerUnitId IS NULL AND @ItemName IS NULL
            SET @Result = 1;
        ELSE IF @ItemCategory = ''Other'' AND @ItemName IS NOT NULL
           AND @FertilizerId IS NULL AND @FertilizerSourceId IS NULL AND @FertilizerUnitId IS NULL AND @PotSize IS NULL AND @AreaId IS NULL
            SET @Result = 1;
        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PurchaseOrderItems_CategoryFields')
BEGIN
    ALTER TABLE dbo.PurchaseOrderItems
        ADD CONSTRAINT CK_PurchaseOrderItems_CategoryFields
        CHECK (dbo.fn_PurchaseOrderItems_CategoryFields(ItemCategory, FertilizerId, FertilizerSourceId, FertilizerUnitId, PotSize, AreaId, ItemName) = 1);
END
GO

-- ---------- dbo.PurchaseReceipts ----------
-- No dedicated code prefix was approved for receipts (only "PO" was
-- given for this phase) -- a receipt is identified by its parent PO's
-- code plus its own Id/date, the same way a stock ledger row has no
-- code of its own.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PurchaseReceipts' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PurchaseReceipts
    (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseReceipts PRIMARY KEY,
        PurchaseOrderId     INT            NOT NULL,
        ReceiptDate         DATETIME2      NOT NULL CONSTRAINT DF_PurchaseReceipts_ReceiptDate DEFAULT (SYSUTCDATETIME()),
        ReceivedById        INT            NULL,
        Remarks             NVARCHAR(500)  NULL,
        CreatedDate         DATETIME2      NOT NULL CONSTRAINT DF_PurchaseReceipts_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy           NVARCHAR(100)  NULL,

        CONSTRAINT FK_PurchaseReceipts_Order    FOREIGN KEY (PurchaseOrderId) REFERENCES dbo.PurchaseOrders(Id),
        CONSTRAINT FK_PurchaseReceipts_Received FOREIGN KEY (ReceivedById)    REFERENCES dbo.IMSUsers(Id)
    );
    CREATE INDEX IX_PurchaseReceipts_Order ON dbo.PurchaseReceipts(PurchaseOrderId);
END
GO

-- ---------- dbo.PurchaseReceiptItems ----------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PurchaseReceiptItems' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PurchaseReceiptItems
    (
        Id                      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseReceiptItems PRIMARY KEY,
        PurchaseReceiptId       INT            NOT NULL,
        PurchaseOrderItemId     INT            NOT NULL,
        ReceivedQuantity        DECIMAL(18,2)  NOT NULL,
        CreatedDate             DATETIME2      NOT NULL CONSTRAINT DF_PurchaseReceiptItems_CreatedDate DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT FK_PurchaseReceiptItems_Receipt FOREIGN KEY (PurchaseReceiptId)   REFERENCES dbo.PurchaseReceipts(Id),
        CONSTRAINT FK_PurchaseReceiptItems_Item    FOREIGN KEY (PurchaseOrderItemId) REFERENCES dbo.PurchaseOrderItems(Id),
        CONSTRAINT CK_PurchaseReceiptItems_Qty      CHECK (ReceivedQuantity > 0)
    );
    CREATE INDEX IX_PurchaseReceiptItems_Receipt ON dbo.PurchaseReceiptItems(PurchaseReceiptId);
    CREATE INDEX IX_PurchaseReceiptItems_Item    ON dbo.PurchaseReceiptItems(PurchaseOrderItemId);
END
GO

-- Backstop: a receipt line's PurchaseOrderItem must belong to the SAME
-- PurchaseOrder as its parent PurchaseReceipt -- prevents ever
-- receiving against the wrong PO's line by mistake, mirroring the
-- fn_..._MatchesBooking/MatchesParent pattern used elsewhere.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_PurchaseReceiptItems_MatchesOrder' AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.fn_PurchaseReceiptItems_MatchesOrder(@PurchaseReceiptId INT, @PurchaseOrderItemId INT)
    RETURNS BIT
    AS
    BEGIN
        DECLARE @Result BIT = 0;
        IF EXISTS (
            SELECT 1
            FROM dbo.PurchaseReceipts r
            INNER JOIN dbo.PurchaseOrderItems poi ON poi.PurchaseOrderId = r.PurchaseOrderId
            WHERE r.Id = @PurchaseReceiptId AND poi.Id = @PurchaseOrderItemId
        )
            SET @Result = 1;
        RETURN @Result;
    END');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PurchaseReceiptItems_MatchesOrder')
BEGIN
    ALTER TABLE dbo.PurchaseReceiptItems
        ADD CONSTRAINT CK_PurchaseReceiptItems_MatchesOrder
        CHECK (dbo.fn_PurchaseReceiptItems_MatchesOrder(PurchaseReceiptId, PurchaseOrderItemId) = 1);
END
GO
