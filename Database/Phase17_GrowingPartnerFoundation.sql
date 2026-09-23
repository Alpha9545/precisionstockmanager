-- ============================================================================
-- Phase 17: Growing Partner Foundation
-- ============================================================================
-- Purpose: lay the database foundation for the Growing Partner business
-- model (see claude/plantstockmanager-vendor-architecture-analysis.md).
-- This script ONLY adds a new table and one nullable column + its FK/index;
-- it does not touch any Phase 2-16 table's existing columns, rows, or
-- behavior, and it does not wire up any access control -- that is Phase B.
--
-- Additive, idempotent, safe to re-run: every CREATE/ALTER is guarded with
-- an existence check, exactly like every prior phase script. No DROP, no
-- DELETE, no data loss possible from running this script.
--
-- Decisions this script implements (confirmed with the business owner):
--   1) dbo.Vendors (Phase 11) remains procurement-only and is NOT reused
--      or extended. GrowingPartners is a new, separate entity.
--   2) dbo.Area gets a single nullable GrowingPartnerId FK. Nullable so
--      every existing Area row is unaffected (NULL = internally run,
--      today's behavior, unchanged). Existing Area-scoped stock tables
--      (PottedPlantStock, EmptyPotInventory, CuttingStock -- all already
--      AreaId-scoped from Phases 7/8/15) therefore gain Growing-Partner
--      stock separation "for free," with no new stock tables.
--   3) dbo.UserRoles / Roles / Permissions / RolePermissions are NOT
--      touched in this phase. Access-control scoping by Growing Partner
--      is explicitly deferred to Phase B.
--   4) Phase 16 (Cutting Model B) is NOT touched by this script in any way.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- PART 1: dbo.GrowingPartners (new master table)
-- ----------------------------------------------------------------------------
-- Mirrors the shape of dbo.Vendors (Phase 11) for a familiar admin-CRUD
-- pattern, but is a distinct table with no relationship to dbo.Vendors --
-- see the architecture analysis doc for why the two must not be conflated.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GrowingPartners' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.GrowingPartners
    (
        Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_GrowingPartners PRIMARY KEY,
        Name          NVARCHAR(200)     NOT NULL,
        ContactPerson NVARCHAR(100)     NULL,
        Phone         NVARCHAR(20)      NULL,
        Email         NVARCHAR(100)     NULL,
        Address       NVARCHAR(300)     NULL,
        IsActive      BIT               NOT NULL CONSTRAINT DF_GrowingPartners_IsActive DEFAULT (1),
        CreatedDate   DATETIME2         NOT NULL CONSTRAINT DF_GrowingPartners_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy     NVARCHAR(100)     NULL,
        ModifiedDate  DATETIME2         NULL,
        ModifiedBy    NVARCHAR(100)     NULL,
        CONSTRAINT UQ_GrowingPartners_Name UNIQUE (Name)
    );
END
GO


-- ----------------------------------------------------------------------------
-- PART 2: dbo.Area extension
-- ----------------------------------------------------------------------------
-- 2a) GrowingPartnerId: nullable so all existing Area rows are unaffected.
--     NULL = internally run (today's behavior, unchanged). Not NULL = the
--     Area's stock, production, and (in later phases) sales belong to the
--     referenced Growing Partner.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'GrowingPartnerId')
BEGIN
    ALTER TABLE dbo.Area ADD GrowingPartnerId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Area_GrowingPartner')
BEGIN
    ALTER TABLE dbo.Area
        ADD CONSTRAINT FK_Area_GrowingPartner FOREIGN KEY (GrowingPartnerId) REFERENCES dbo.GrowingPartners(Id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Area_GrowingPartnerId' AND object_id = OBJECT_ID('dbo.Area'))
BEGIN
    CREATE INDEX IX_Area_GrowingPartnerId ON dbo.Area(GrowingPartnerId);
END
GO

-- ============================================================================
-- End of Phase 17. No changes made to: dbo.Vendors, dbo.Roles,
-- dbo.Permissions, dbo.RolePermissions, dbo.UserRoles, dbo.CuttingStock,
-- dbo.CuttingStockTransactions, dbo.PottedPlantStock, dbo.EmptyPotInventory,
-- dbo.InternalTransfer, or any other existing table.
-- ============================================================================
