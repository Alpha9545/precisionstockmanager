-- ============================================================================
-- 2026-10-06_StoreInventory.sql
--
-- WHY: a dedicated internal STORE (tools, cable, pipe, consumables) at Main
-- Office: item master, an insert-only stock ledger, multi-item requests from
-- the Areas, approval, issue, and settlement of every issued line as
-- CONSUMED / KEPT (permanently at the Area) / RETURNED (stock back only when
-- the Store Keeper confirms the physical return), plus direct permanent
-- transfers. NOT plant / seed / cutting / empty-pot / fertilizer stock, and
-- NOT the retired seedling pipeline: dbo.Inventory / dbo.InventoryTransactions
-- are neither read nor changed (their row counts are printed before / after).
--
-- STOCK MODEL
--   StoreItems.AvailableQuantity   Main Office stock on the shelf -- changes ONLY through the ledger:
--   StoreTransactions              PURCHASE (+), ISSUE (-), RETURN (+), PERMANENT_TRANSFER (-), ADJUSTMENT (+/-),
--                                  each with BalanceBefore / BalanceAfter. Enforced in the database:
--     TR_StoreTransactions_Ledger   insert-only (no UPDATE / DELETE), one row per item per statement,
--                                   BalanceBefore must equal the item's CURRENT stock, issue / return amounts
--                                   must match their request line
--     TR_StoreItems_Balance         an item's AvailableQuantity may only change to exactly SUM(its ledger);
--                                   a new item starts at 0
--     CK_StoreItems_Available       >= 0 (never negative)
--   The application locks the item row (UPDLOCK) before reading the balance, so two simultaneous stock
--   operations on the same item run one after the other.
--   Issued (out with the Area), consumed, kept (assigned to an Area / Polyhouse) and returned quantities
--   are kept per request line; direct transfers are ledger rows with their Area / Polyhouse. Nothing
--   "disappears": every unit is on the shelf, out on issue, consumed, or assigned to a location.
--
-- REQUEST WORKFLOW (dbo.StoreRequests + dbo.StoreRequestLines, one line per item)
--   Pending -> Approved (some line approved, partial allowed) | Rejected (nothing approved)
--   Approved -> Issued (issued <= approved and <= available, per line) -> Closed (every line settled)
--   Pending / Approved -> Cancelled
--
-- ALSO: Permissions Store.Request, Store.View (Sowing / Mother Plant / Fertilizer Supervisor, Office
-- Coordinator) and Store.Manage (Main Office Store Keeper; full-access users have every permission).
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the server named with
-- -v TARGET_SERVER=<@@SERVERNAME> (the live database is ALSO named PlantsIMS2_Test). One transaction.
-- Idempotent (re-run = no-op). Undo: 2026-10-06_StoreInventory_Rollback.sql
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51801, '2026-10-06_StoreInventory.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51802, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51803, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION StoreInventory2026_10_06;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    PRINT '=== Before: row counts of tables this script must NOT change (Permissions / RolePermissions only grow) ===';
    SELECT 'Inventory (legacy)' AS TableName, COUNT(*) AS Rows FROM dbo.Inventory
    UNION ALL SELECT 'InventoryTransactions (legacy)', COUNT(*) FROM dbo.InventoryTransactions
    UNION ALL SELECT 'UnitMaster', COUNT(*) FROM dbo.UnitMaster
    UNION ALL SELECT 'Vendors', COUNT(*) FROM dbo.Vendors
    UNION ALL SELECT 'Area', COUNT(*) FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'Roles', COUNT(*) FROM dbo.Roles
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions;

    DECLARE @RequestRoles TABLE (Name NVARCHAR(100) PRIMARY KEY);
    INSERT INTO @RequestRoles VALUES (N'Sowing Supervisor'), (N'Mother Plant Supervisor'), (N'Fertilizer Supervisor'), (N'Office Coordinator');
    IF (SELECT COUNT(*) FROM dbo.Roles r JOIN @RequestRoles s ON s.Name = COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName)) <> 4
        THROW 51804, 'One of the supervisor roles (Sowing / Mother Plant / Fertilizer Supervisor, Office Coordinator) is missing.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Main Office Store Keeper')
        THROW 51805, 'Role Main Office Store Keeper is missing.', 1;

    ------------------------------------------------------------------
    -- 1. Permissions (Id identity or not -- the live database may differ) and grants
    ------------------------------------------------------------------
    DECLARE @Perms TABLE (Code NVARCHAR(200) PRIMARY KEY, Description NVARCHAR(400), SortOrder INT);
    INSERT INTO @Perms VALUES
        (N'Store.Request', N'Raise store inventory requests for own Areas and settle what was issued to them', 1),
        (N'Store.View',    N'View store requests and store inventory of own Areas', 2),
        (N'Store.Manage',  N'Store Keeper: item master, stock entry, approve / issue requests of every Area, confirm returns, transfers', 3);
    DECLARE @Code NVARCHAR(200), @PermDescription NVARCHAR(400);
    DECLARE perm_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Code, Description FROM @Perms ORDER BY SortOrder;
    OPEN perm_cursor;
    FETCH NEXT FROM perm_cursor INTO @Code, @PermDescription;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = @Code)
        BEGIN
            IF COLUMNPROPERTY(OBJECT_ID('dbo.Permissions'), 'Id', 'IsIdentity') = 1
                INSERT INTO dbo.Permissions (Code, Description) VALUES (@Code, @PermDescription);
            ELSE
                INSERT INTO dbo.Permissions (Id, Code, Description) SELECT ISNULL(MAX(Id), 0) + 1, @Code, @PermDescription FROM dbo.Permissions;
            PRINT CONCAT('=== Permission ', @Code, ' added ===');
        END
        ELSE PRINT CONCAT('=== Permission ', @Code, ' already exists -- no-op ===');
        FETCH NEXT FROM perm_cursor INTO @Code, @PermDescription;
    END
    CLOSE perm_cursor;
    DEALLOCATE perm_cursor;

    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM dbo.Roles r
    JOIN @RequestRoles s ON s.Name = COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName)
    CROSS JOIN dbo.Permissions p
    WHERE p.Code IN (N'Store.Request', N'Store.View')
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    PRINT CONCAT('Supervisor grants inserted: ', @@ROWCOUNT, ' (expect 8 on first run, 0 on re-run)');

    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM dbo.Roles r CROSS JOIN dbo.Permissions p
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Main Office Store Keeper'
      AND p.Code = N'Store.Manage'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    PRINT CONCAT('Store Keeper grants inserted: ', @@ROWCOUNT, ' (expect 1 on first run, 0 on re-run)');

    ------------------------------------------------------------------
    -- 2. Item master
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.StoreItemCategories', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.StoreItemCategories (
            Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StoreItemCategories PRIMARY KEY,
            Name         NVARCHAR(100) NOT NULL CONSTRAINT UQ_StoreItemCategories_Name UNIQUE,
            IsActive     BIT           NOT NULL CONSTRAINT DF_StoreItemCategories_IsActive DEFAULT (1),
            CreatedById  INT           NULL CONSTRAINT FK_StoreItemCategories_CreatedBy REFERENCES dbo.IMSUsers(Id),
            CreatedAt    DATETIME2     NOT NULL CONSTRAINT DF_StoreItemCategories_CreatedAt DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT CK_StoreItemCategories_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0)
        );
        PRINT '=== dbo.StoreItemCategories created ===';
    END
    ELSE PRINT '=== dbo.StoreItemCategories already exists -- no-op ===';

    IF OBJECT_ID('dbo.StoreItems', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.StoreItems (
            Id                 INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StoreItems PRIMARY KEY,
            ItemCode           NVARCHAR(30)   NOT NULL CONSTRAINT UQ_StoreItems_ItemCode UNIQUE,
            Name               NVARCHAR(150)  NOT NULL,
            CategoryId         INT            NOT NULL CONSTRAINT FK_StoreItems_Category REFERENCES dbo.StoreItemCategories(Id),
            Description        NVARCHAR(500)  NULL,
            UnitId             INT            NOT NULL CONSTRAINT FK_StoreItems_Unit REFERENCES dbo.UnitMaster(UnitId),
            ItemType           VARCHAR(12)    NOT NULL,
            MinimumStock       DECIMAL(18,3)  NOT NULL CONSTRAINT DF_StoreItems_MinimumStock DEFAULT (0),
            AvailableQuantity  DECIMAL(18,3)  NOT NULL CONSTRAINT DF_StoreItems_Available DEFAULT (0),
            Location           NVARCHAR(100)  NULL,
            IsActive           BIT            NOT NULL CONSTRAINT DF_StoreItems_IsActive DEFAULT (1),
            Remarks            NVARCHAR(500)  NULL,
            CreatedById        INT            NULL CONSTRAINT FK_StoreItems_CreatedBy REFERENCES dbo.IMSUsers(Id),
            CreatedAt          DATETIME2      NOT NULL CONSTRAINT DF_StoreItems_CreatedAt DEFAULT (SYSUTCDATETIME()),
            UpdatedById        INT            NULL CONSTRAINT FK_StoreItems_UpdatedBy REFERENCES dbo.IMSUsers(Id),
            UpdatedAt          DATETIME2      NULL,
            CONSTRAINT CK_StoreItems_ItemType  CHECK (ItemType IN ('Consumable', 'Returnable')),
            CONSTRAINT CK_StoreItems_Available CHECK (AvailableQuantity >= 0),
            CONSTRAINT CK_StoreItems_Minimum   CHECK (MinimumStock >= 0),
            CONSTRAINT CK_StoreItems_Text      CHECK (LEN(LTRIM(RTRIM(ItemCode))) > 0 AND LEN(LTRIM(RTRIM(Name))) > 0)
        );
        CREATE INDEX IX_StoreItems_Category ON dbo.StoreItems (CategoryId, IsActive);
        PRINT '=== dbo.StoreItems created ===';
    END
    ELSE PRINT '=== dbo.StoreItems already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 3. Requests and lines
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.StoreRequests', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.StoreRequests (
            Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StoreRequests PRIMARY KEY,
            RequestNo       AS (CAST('IR-' + RIGHT(REPLICATE('0', 6) + CAST(Id AS VARCHAR(10)),
                                    CASE WHEN Id > 999999 THEN LEN(CAST(Id AS VARCHAR(10))) ELSE 6 END) AS VARCHAR(16))) PERSISTED,
            AreaId          INT            NOT NULL CONSTRAINT FK_StoreRequests_Area REFERENCES dbo.Area(Id),
            PolyhouseId     INT            NULL     CONSTRAINT FK_StoreRequests_Polyhouse REFERENCES dbo.Polyhouses(Id),
            Purpose         NVARCHAR(500)  NOT NULL,
            Status          VARCHAR(12)    NOT NULL CONSTRAINT DF_StoreRequests_Status DEFAULT ('Pending'),
            RequestedById   INT            NOT NULL CONSTRAINT FK_StoreRequests_RequestedBy REFERENCES dbo.IMSUsers(Id),
            RequestedAt     DATETIME2      NOT NULL CONSTRAINT DF_StoreRequests_RequestedAt DEFAULT (SYSUTCDATETIME()),
            ReviewedById    INT            NULL     CONSTRAINT FK_StoreRequests_ReviewedBy REFERENCES dbo.IMSUsers(Id),
            ReviewedAt      DATETIME2      NULL,
            ReviewRemarks   NVARCHAR(500)  NULL,
            IssuedById      INT            NULL     CONSTRAINT FK_StoreRequests_IssuedBy REFERENCES dbo.IMSUsers(Id),
            IssuedAt        DATETIME2      NULL,
            ClosedAt        DATETIME2      NULL,
            CancelledById   INT            NULL     CONSTRAINT FK_StoreRequests_CancelledBy REFERENCES dbo.IMSUsers(Id),
            CancelledAt     DATETIME2      NULL,
            CancelReason    NVARCHAR(500)  NULL,
            UpdatedById     INT            NULL     CONSTRAINT FK_StoreRequests_UpdatedBy REFERENCES dbo.IMSUsers(Id),
            UpdatedAt       DATETIME2      NULL,
            CONSTRAINT CK_StoreRequests_Status   CHECK (Status IN ('Pending', 'Approved', 'Rejected', 'Issued', 'Closed', 'Cancelled')),
            CONSTRAINT CK_StoreRequests_Purpose  CHECK (LEN(LTRIM(RTRIM(Purpose))) > 0),
            CONSTRAINT CK_StoreRequests_Reviewed CHECK (Status IN ('Pending', 'Cancelled') OR (ReviewedById IS NOT NULL AND ReviewedAt IS NOT NULL)),
            CONSTRAINT CK_StoreRequests_Issued   CHECK ((Status IN ('Issued', 'Closed') AND IssuedById IS NOT NULL AND IssuedAt >= ReviewedAt)
                                                     OR (Status NOT IN ('Issued', 'Closed') AND IssuedAt IS NULL)),
            CONSTRAINT CK_StoreRequests_Closed   CHECK ((Status = 'Closed' AND ClosedAt IS NOT NULL) OR (Status <> 'Closed' AND ClosedAt IS NULL)),
            CONSTRAINT CK_StoreRequests_Cancelled CHECK ((Status = 'Cancelled' AND CancelledById IS NOT NULL AND CancelledAt IS NOT NULL AND LEN(LTRIM(RTRIM(CancelReason))) > 0)
                                                     OR (Status <> 'Cancelled' AND CancelledAt IS NULL))
        );
        CREATE UNIQUE INDEX UX_StoreRequests_RequestNo ON dbo.StoreRequests (RequestNo);
        CREATE INDEX IX_StoreRequests_Area ON dbo.StoreRequests (AreaId, Status) INCLUDE (PolyhouseId, RequestedAt);
        CREATE INDEX IX_StoreRequests_Status ON dbo.StoreRequests (Status, RequestedAt);
        PRINT '=== dbo.StoreRequests created ===';
    END
    ELSE PRINT '=== dbo.StoreRequests already exists -- no-op ===';

    IF OBJECT_ID('dbo.StoreRequestLines', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.StoreRequestLines (
            Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StoreRequestLines PRIMARY KEY,
            RequestId           INT            NOT NULL CONSTRAINT FK_StoreRequestLines_Request REFERENCES dbo.StoreRequests(Id),
            ItemId              INT            NOT NULL CONSTRAINT FK_StoreRequestLines_Item REFERENCES dbo.StoreItems(Id),
            ItemType            VARCHAR(12)    NOT NULL,   -- the item's type when requested (a Returnable line can never be CONSUMED)
            RequestedQty        DECIMAL(18,3)  NOT NULL,
            ApprovedQty         DECIMAL(18,3)  NULL,       -- NULL = not reviewed yet; 0 = rejected
            ReviewRemarks       NVARCHAR(500)  NULL,
            IssuedQty           DECIMAL(18,3)  NOT NULL CONSTRAINT DF_StoreRequestLines_Issued DEFAULT (0),
            ConsumedQty         DECIMAL(18,3)  NOT NULL CONSTRAINT DF_StoreRequestLines_Consumed DEFAULT (0),
            KeptQty             DECIMAL(18,3)  NOT NULL CONSTRAINT DF_StoreRequestLines_Kept DEFAULT (0),
            ReturnDeclaredQty   DECIMAL(18,3)  NOT NULL CONSTRAINT DF_StoreRequestLines_ReturnDeclared DEFAULT (0),   -- declared, awaiting the Store Keeper
            ReturnedQty         DECIMAL(18,3)  NOT NULL CONSTRAINT DF_StoreRequestLines_Returned DEFAULT (0),         -- confirmed, back in stock
            ReturnCondition     VARCHAR(12)    NULL,
            ReturnRemarks       NVARCHAR(500)  NULL,
            SettledById         INT            NULL CONSTRAINT FK_StoreRequestLines_SettledBy REFERENCES dbo.IMSUsers(Id),
            SettledAt           DATETIME2      NULL,       -- = ReturnedAt (declared) for the returned part
            ReturnConfirmedById INT            NULL CONSTRAINT FK_StoreRequestLines_ReturnConfirmedBy REFERENCES dbo.IMSUsers(Id),
            ReturnConfirmedAt   DATETIME2      NULL,
            ConfirmRemarks      NVARCHAR(500)  NULL,
            CONSTRAINT UQ_StoreRequestLines_Item UNIQUE (RequestId, ItemId),
            CONSTRAINT CK_StoreRequestLines_ItemType  CHECK (ItemType IN ('Consumable', 'Returnable')),
            CONSTRAINT CK_StoreRequestLines_Requested CHECK (RequestedQty > 0),
            CONSTRAINT CK_StoreRequestLines_Approved  CHECK (ApprovedQty IS NULL OR (ApprovedQty >= 0 AND ApprovedQty <= RequestedQty)),
            CONSTRAINT CK_StoreRequestLines_Issued    CHECK (IssuedQty >= 0 AND IssuedQty <= ISNULL(ApprovedQty, 0)),
            CONSTRAINT CK_StoreRequestLines_Outcomes  CHECK (ConsumedQty >= 0 AND KeptQty >= 0 AND ReturnDeclaredQty >= 0 AND ReturnedQty >= 0
                                                          AND ConsumedQty + KeptQty + ReturnDeclaredQty + ReturnedQty <= IssuedQty),
            CONSTRAINT CK_StoreRequestLines_NoConsumeReturnable CHECK (ItemType <> 'Returnable' OR ConsumedQty = 0),
            CONSTRAINT CK_StoreRequestLines_Settled   CHECK ((SettledAt IS NULL AND ConsumedQty = 0 AND KeptQty = 0 AND ReturnDeclaredQty = 0 AND ReturnedQty = 0)
                                                          OR (SettledAt IS NOT NULL AND SettledById IS NOT NULL AND ConsumedQty + KeptQty + ReturnDeclaredQty + ReturnedQty = IssuedQty)),
            CONSTRAINT CK_StoreRequestLines_ReturnConfirmed CHECK ((ReturnConfirmedAt IS NULL AND ReturnedQty = 0)
                                                          OR (ReturnConfirmedAt IS NOT NULL AND ReturnConfirmedById IS NOT NULL AND ReturnedQty > 0 AND ReturnDeclaredQty = 0 AND ReturnConfirmedAt >= SettledAt)),
            CONSTRAINT CK_StoreRequestLines_ReturnCondition CHECK (ReturnCondition IS NULL OR ReturnCondition IN ('Good', 'Damaged'))
        );
        CREATE INDEX IX_StoreRequestLines_Item ON dbo.StoreRequestLines (ItemId) INCLUDE (RequestId, IssuedQty, ConsumedQty, KeptQty, ReturnDeclaredQty, ReturnedQty);
        PRINT '=== dbo.StoreRequestLines created ===';
    END
    ELSE PRINT '=== dbo.StoreRequestLines already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 4. The ledger
    ------------------------------------------------------------------
    IF OBJECT_ID('dbo.StoreTransactions', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.StoreTransactions (
            Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StoreTransactions PRIMARY KEY,
            ItemId          INT            NOT NULL CONSTRAINT FK_StoreTransactions_Item REFERENCES dbo.StoreItems(Id),
            TransactionType VARCHAR(20)    NOT NULL,
            Quantity        DECIMAL(18,3)  NOT NULL,   -- signed change of Main Office stock
            BalanceBefore   DECIMAL(18,3)  NOT NULL,
            BalanceAfter    DECIMAL(18,3)  NOT NULL,
            TransactionDate DATE           NOT NULL,
            RequestId       INT            NULL CONSTRAINT FK_StoreTransactions_Request REFERENCES dbo.StoreRequests(Id),
            RequestLineId   INT            NULL CONSTRAINT FK_StoreTransactions_RequestLine REFERENCES dbo.StoreRequestLines(Id),
            AreaId          INT            NULL CONSTRAINT FK_StoreTransactions_Area REFERENCES dbo.Area(Id),
            PolyhouseId     INT            NULL CONSTRAINT FK_StoreTransactions_Polyhouse REFERENCES dbo.Polyhouses(Id),
            VendorId        INT            NULL CONSTRAINT FK_StoreTransactions_Vendor REFERENCES dbo.Vendors(Id),
            Reference       NVARCHAR(100)  NULL,
            UnitCost        DECIMAL(18,2)  NULL,
            Remarks         NVARCHAR(500)  NULL,
            CreatedById     INT            NULL CONSTRAINT FK_StoreTransactions_CreatedBy REFERENCES dbo.IMSUsers(Id),
            CreatedAt       DATETIME2      NOT NULL CONSTRAINT DF_StoreTransactions_CreatedAt DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT CK_StoreTransactions_Type    CHECK (TransactionType IN ('PURCHASE', 'ISSUE', 'RETURN', 'PERMANENT_TRANSFER', 'ADJUSTMENT')),
            CONSTRAINT CK_StoreTransactions_Balance CHECK (BalanceBefore >= 0 AND BalanceAfter >= 0 AND BalanceAfter = BalanceBefore + Quantity),
            CONSTRAINT CK_StoreTransactions_Sign    CHECK ((TransactionType IN ('PURCHASE', 'RETURN') AND Quantity > 0)
                                                        OR (TransactionType IN ('ISSUE', 'PERMANENT_TRANSFER') AND Quantity < 0)
                                                        OR (TransactionType = 'ADJUSTMENT' AND Quantity <> 0)),
            -- what each movement must say about itself
            CONSTRAINT CK_StoreTransactions_Links   CHECK ((TransactionType IN ('ISSUE', 'RETURN') AND RequestId IS NOT NULL AND RequestLineId IS NOT NULL AND AreaId IS NOT NULL)
                                                        OR (TransactionType = 'PERMANENT_TRANSFER' AND RequestId IS NULL AND AreaId IS NOT NULL)
                                                        OR (TransactionType IN ('PURCHASE', 'ADJUSTMENT') AND RequestId IS NULL AND RequestLineId IS NULL AND AreaId IS NULL AND PolyhouseId IS NULL)),
            CONSTRAINT CK_StoreTransactions_Vendor  CHECK (VendorId IS NULL OR TransactionType = 'PURCHASE'),
            CONSTRAINT CK_StoreTransactions_UnitCost CHECK (UnitCost IS NULL OR (UnitCost >= 0 AND TransactionType = 'PURCHASE'))
        );
        CREATE INDEX IX_StoreTransactions_Item ON dbo.StoreTransactions (ItemId, Id) INCLUDE (Quantity, BalanceAfter, TransactionType);
        CREATE INDEX IX_StoreTransactions_Date ON dbo.StoreTransactions (TransactionDate, TransactionType) INCLUDE (ItemId, Quantity, AreaId, PolyhouseId);
        CREATE INDEX IX_StoreTransactions_Area ON dbo.StoreTransactions (AreaId, PolyhouseId) WHERE AreaId IS NOT NULL;
        PRINT '=== dbo.StoreTransactions created ===';
    END
    ELSE PRINT '=== dbo.StoreTransactions already exists -- no-op ===';

    ------------------------------------------------------------------
    -- 5. Triggers
    ------------------------------------------------------------------
    -- Triggers are CREATE OR ALTER (re-running the script brings them to this version). Their
    -- lookups are FORCESEEK: a scan of a small table would touch rows that a concurrent store
    -- operation holds locked and could deadlock with it (found by the concurrent-issue test).
    BEGIN
        EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_StoreTransactions_Ledger ON dbo.StoreTransactions
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- Insert-only. A scratch copy (never PlantsIMS2_Test / production) may remove its own test rows
    -- when the session explicitly asks for it (end-to-end test clean-up).
    IF EXISTS (SELECT 1 FROM deleted)
    BEGIN
        IF EXISTS (SELECT 1 FROM inserted)
            THROW 51811, ''Store transactions cannot be changed. Record a correcting ADJUSTMENT instead.'', 1;
        -- (ISNULL: an unset SESSION_CONTEXT is NULL, and NOT (... AND NULL = 1) would be UNKNOWN -- no THROW)
        IF NOT (DB_NAME() LIKE N''PlantsIMS2[_]Scratch[_]%'' AND ISNULL(CAST(SESSION_CONTEXT(N''StoreTestCleanup'') AS INT), 0) = 1)
            THROW 51812, ''Store transactions cannot be deleted.'', 1;
        RETURN;
    END
    IF EXISTS (SELECT ItemId FROM inserted GROUP BY ItemId HAVING COUNT(*) > 1)
        THROW 51813, ''One store transaction per item per statement.'', 1;
    -- The balance before must be the item''s current Main Office stock (the item is updated afterwards).
    IF EXISTS (SELECT 1 FROM inserted t JOIN dbo.StoreItems i WITH (FORCESEEK) ON i.Id = t.ItemId WHERE t.BalanceBefore <> i.AvailableQuantity)
        THROW 51814, ''Store stock changed meanwhile -- the transaction balance does not match the current stock. Please try again.'', 1;
    -- Issues and returns must match their request line (and its item and destination).
    IF EXISTS (SELECT 1 FROM inserted t JOIN dbo.StoreRequestLines l WITH (FORCESEEK) ON l.Id = t.RequestLineId JOIN dbo.StoreRequests r WITH (FORCESEEK) ON r.Id = l.RequestId
               WHERE l.RequestId <> t.RequestId OR l.ItemId <> t.ItemId OR r.AreaId <> t.AreaId OR ISNULL(r.PolyhouseId, -1) <> ISNULL(t.PolyhouseId, -1)
                  OR (t.TransactionType = ''ISSUE'' AND -t.Quantity <> l.IssuedQty)
                  OR (t.TransactionType = ''RETURN'' AND t.Quantity <> l.ReturnedQty))
        THROW 51815, ''An issue / return transaction must match its request line.'', 1;
    -- A Polyhouse must belong to the transaction''s Area.
    IF EXISTS (SELECT 1 FROM inserted t JOIN dbo.Polyhouses p WITH (FORCESEEK) ON p.Id = t.PolyhouseId WHERE p.AreaId <> t.AreaId)
        THROW 51713, ''This Polyhouse does not belong to the selected Area.'', 1;
END');
        PRINT '=== TR_StoreTransactions_Ledger created / updated ===';
    END

    BEGIN
        EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_StoreItems_Balance ON dbo.StoreItems
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    -- A new item starts empty: stock arrives only through the ledger (PURCHASE / ADJUSTMENT).
    IF EXISTS (SELECT 1 FROM inserted i WHERE i.AvailableQuantity <> 0 AND NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id))
        THROW 51816, ''A new store item starts with 0 stock -- add stock with a Stock Entry.'', 1;
    -- Stock may only change to exactly the sum of the item''s ledger.
    IF UPDATE(AvailableQuantity) AND EXISTS (
        SELECT 1 FROM inserted i JOIN deleted d ON d.Id = i.Id
        WHERE i.AvailableQuantity <> d.AvailableQuantity
          AND i.AvailableQuantity <> ISNULL((SELECT SUM(t.Quantity) FROM dbo.StoreTransactions t WITH (FORCESEEK) WHERE t.ItemId = i.Id), 0))
        THROW 51817, ''Store stock can only change through a store transaction.'', 1;
END');
        PRINT '=== TR_StoreItems_Balance created / updated ===';
    END

    BEGIN
        EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_StoreRequests_Location ON dbo.StoreRequests
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT (UPDATE(AreaId) OR UPDATE(PolyhouseId))
        RETURN;
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.Polyhouses p WITH (FORCESEEK) ON p.Id = i.PolyhouseId WHERE p.AreaId <> i.AreaId)
        THROW 51713, ''This Polyhouse does not belong to the selected Area.'', 1;
    IF EXISTS (SELECT 1 FROM inserted i LEFT JOIN deleted d ON d.Id = i.Id JOIN dbo.Area a WITH (FORCESEEK) ON a.Id = i.AreaId WHERE d.Id IS NULL AND a.IsActive = 0)
        THROW 51818, ''The selected Area is not active.'', 1;
END');
        PRINT '=== TR_StoreRequests_Location created / updated ===';
    END

    PRINT '=== After: grants / tables / constraints / indexes / triggers ===';
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, p.Code
    FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code LIKE N'Store.%' ORDER BY RoleName, p.Code;
    SELECT OBJECT_NAME(parent_object_id) AS TableName, name AS CheckConstraint FROM sys.check_constraints
    WHERE parent_object_id IN (OBJECT_ID('dbo.StoreItemCategories'), OBJECT_ID('dbo.StoreItems'), OBJECT_ID('dbo.StoreRequests'), OBJECT_ID('dbo.StoreRequestLines'), OBJECT_ID('dbo.StoreTransactions'))
    ORDER BY 1, 2;
    SELECT OBJECT_NAME(object_id) AS TableName, name AS IndexName FROM sys.indexes
    WHERE object_id IN (OBJECT_ID('dbo.StoreItemCategories'), OBJECT_ID('dbo.StoreItems'), OBJECT_ID('dbo.StoreRequests'), OBJECT_ID('dbo.StoreRequestLines'), OBJECT_ID('dbo.StoreTransactions')) AND name IS NOT NULL
    ORDER BY 1, 2;
    SELECT OBJECT_NAME(parent_id) AS TableName, name AS TriggerName FROM sys.triggers
    WHERE parent_id IN (OBJECT_ID('dbo.StoreItems'), OBJECT_ID('dbo.StoreRequests'), OBJECT_ID('dbo.StoreTransactions')) ORDER BY 1, 2;

    PRINT '=== After: row counts (legacy / master tables must equal Before) ===';
    SELECT 'Inventory (legacy)' AS TableName, COUNT(*) AS Rows FROM dbo.Inventory
    UNION ALL SELECT 'InventoryTransactions (legacy)', COUNT(*) FROM dbo.InventoryTransactions
    UNION ALL SELECT 'UnitMaster', COUNT(*) FROM dbo.UnitMaster
    UNION ALL SELECT 'Vendors', COUNT(*) FROM dbo.Vendors
    UNION ALL SELECT 'Area', COUNT(*) FROM dbo.Area
    UNION ALL SELECT 'Polyhouses', COUNT(*) FROM dbo.Polyhouses
    UNION ALL SELECT 'Roles', COUNT(*) FROM dbo.Roles
    UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
    UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
    UNION ALL SELECT 'RolePermissions', COUNT(*) FROM dbo.RolePermissions
    UNION ALL SELECT 'StoreItems', COUNT(*) FROM dbo.StoreItems
    UNION ALL SELECT 'StoreTransactions', COUNT(*) FROM dbo.StoreTransactions;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION StoreInventory2026_10_06;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION StoreInventory2026_10_06;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION StoreInventory2026_10_06;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
