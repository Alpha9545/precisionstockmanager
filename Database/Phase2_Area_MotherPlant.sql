/* ============================================================
   Phase 2: Area master + Mother Plant module
   ------------------------------------------------------------
   ADDITIVE ONLY.
     - Does not touch, alter, or drop any existing table.
     - Every CREATE is guarded with an existence check, so this
       script is safe to run more than once.
     - New tables FK to the EXISTING Polyhouses, PlantSpecies and
       IMSUsers tables (no duplicate master data).

   Run this against the PlantsIMS2 database (see appsettings.json
   "DefaultConnection") BEFORE building/running the app with the
   new Area / Mother Plant code, and take a backup first per your
   own DB safety rules.
   ============================================================ */

-- 1) Area master.
--    REVISED (post-review): the actual requirement is
--    Polyhouse -> multiple Areas, with Area.PolyhouseId -> Polyhouses.Id.
--    The table already exists from the first version of this script
--    (Id, Name, IsActive only) -- everything below is an ADDITIVE,
--    idempotent ALTER migration. It never drops/recreates dbo.Area and
--    never deletes existing rows; every step is guarded so this script
--    remains safe to run more than once and safe to run against a
--    database that already has Area rows.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Area' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Area
    (
        Id       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Area PRIMARY KEY,
        Name     NVARCHAR(200)     NOT NULL,
        IsActive BIT               NOT NULL CONSTRAINT DF_Area_IsActive DEFAULT (1)
    );
END
GO

-- 1a) The original design made Name globally unique across ALL areas.
--     That is wrong once multiple Polyhouses each have their own Areas
--     (two different Polyhouses may each legitimately have an "Area 1").
--     Drop the old global unique index; it is superseded by the
--     per-Polyhouse unique index created in step 1e below.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Area_Name' AND object_id = OBJECT_ID('dbo.Area'))
BEGIN
    DROP INDEX UQ_Area_Name ON dbo.Area;
END
GO

-- 1b) Add the new columns the real requirement needs. Each is nullable
--     at the ALTER stage (so adding it can never fail or delete existing
--     rows), guarded so re-running this script is a no-op once applied.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'PolyhouseId')
BEGIN
    ALTER TABLE dbo.Area ADD PolyhouseId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'AreaCode')
BEGIN
    ALTER TABLE dbo.Area ADD AreaCode NVARCHAR(50) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'AreaSize')
BEGIN
    ALTER TABLE dbo.Area ADD AreaSize DECIMAL(18,2) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'AreaUnit')
BEGIN
    ALTER TABLE dbo.Area ADD AreaUnit NVARCHAR(20) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'Capacity')
BEGIN
    ALTER TABLE dbo.Area ADD Capacity DECIMAL(18,2) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'CapacityUnit')
BEGIN
    ALTER TABLE dbo.Area ADD CapacityUnit NVARCHAR(20) NULL;
END
GO

-- CreatedAt: the original table never recorded a creation time, so this
-- backfills existing rows with the time this migration runs (WITH VALUES
-- applies the default to pre-existing rows too, instead of leaving them
-- NULL) and defaults every new row going forward.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'CreatedAt')
BEGIN
    ALTER TABLE dbo.Area ADD CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Area_CreatedAt DEFAULT (SYSUTCDATETIME()) WITH VALUES;
END
GO

-- 1c) Foreign key: Area.PolyhouseId -> Polyhouses.Id (the requirement's
--     "Required relationship"). PolyhouseId stays nullable at the column
--     level (see 1d) so this FK can be added without failing even if the
--     table currently has legacy rows with no Polyhouse assigned yet --
--     a NULL PolyhouseId trivially satisfies the FK.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Area_Polyhouse')
BEGIN
    ALTER TABLE dbo.Area
        ADD CONSTRAINT FK_Area_Polyhouse FOREIGN KEY (PolyhouseId) REFERENCES dbo.Polyhouses(Id);
END
GO

-- 1d) Tighten PolyhouseId to NOT NULL -- but ONLY when every existing row
--     already has one. If any legacy row is still missing a Polyhouse,
--     this step is skipped rather than failing the whole script or
--     forcing data loss; assign those rows a Polyhouse and re-run this
--     script to finish tightening the constraint.
IF NOT EXISTS (SELECT 1 FROM dbo.Area WHERE PolyhouseId IS NULL)
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'PolyhouseId' AND is_nullable = 1)
BEGIN
    ALTER TABLE dbo.Area ALTER COLUMN PolyhouseId INT NOT NULL;
END
GO

-- 1e) Indexes.
--     - IX_Area_PolyhouseId: lookups by Polyhouse (the Polyhouse -> Area
--       cascading dropdown, and the Polyhouse admin's per-Polyhouse
--       Area/size/capacity totals).
--     - UQ_Area_PolyhouseId_Name / UQ_Area_PolyhouseId_AreaCode: Name and
--       AreaCode only need to be unique WITHIN a Polyhouse now, not
--       globally. Filtered to PolyhouseId IS NOT NULL so any transitional
--       legacy rows without a Polyhouse yet can never block these indexes
--       from being created.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Area_PolyhouseId' AND object_id = OBJECT_ID('dbo.Area'))
BEGIN
    CREATE INDEX IX_Area_PolyhouseId ON dbo.Area(PolyhouseId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Area_PolyhouseId_Name' AND object_id = OBJECT_ID('dbo.Area'))
BEGIN
    CREATE UNIQUE INDEX UQ_Area_PolyhouseId_Name
        ON dbo.Area(PolyhouseId, Name)
        WHERE PolyhouseId IS NOT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Area_PolyhouseId_AreaCode' AND object_id = OBJECT_ID('dbo.Area'))
BEGIN
    CREATE UNIQUE INDEX UQ_Area_PolyhouseId_AreaCode
        ON dbo.Area(PolyhouseId, AreaCode)
        WHERE PolyhouseId IS NOT NULL AND AreaCode IS NOT NULL;
END
GO

-- 1f) Validation / check constraints. AreaSize and Capacity remain
--     nullable (pre-migration rows have no value yet), so these only
--     fire once a value is actually supplied -- they can never reject
--     existing data.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Area_AreaSize')
BEGIN
    ALTER TABLE dbo.Area ADD CONSTRAINT CK_Area_AreaSize CHECK (AreaSize IS NULL OR AreaSize > 0);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Area_Capacity')
BEGIN
    ALTER TABLE dbo.Area ADD CONSTRAINT CK_Area_Capacity CHECK (Capacity IS NULL OR Capacity > 0);
END
GO

-- 2) Generic batch-number sequence table.
--    Produces batch numbers like "MP-2026-00001". Reused by later
--    phases for their own prefixes (CUT-, CD-, PROP-, POT-, BK-,
--    DIS-) against this same table -- no per-phase sequence table
--    needed.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'BatchNumberSequences' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.BatchNumberSequences
    (
        Prefix     NVARCHAR(10) NOT NULL,
        [Year]     INT          NOT NULL,
        LastNumber INT          NOT NULL CONSTRAINT DF_BatchNumberSequences_LastNumber DEFAULT (0),
        CONSTRAINT PK_BatchNumberSequences PRIMARY KEY (Prefix, [Year])
    );
END
GO

-- 3) Mother Plant.
--    FKs to the EXISTING Polyhouses / PlantSpecies / IMSUsers
--    tables, and to the new Area table above. Quantities use
--    DECIMAL per the spec's "don't assume INT" rule; everything
--    else (Id, dates, audit columns) follows the pattern already
--    used by SeedEntries / Bookings.
--
--    REVISED (pre-Phase-3 patch): the business identifier is
--    MotherPlantCode (not the ambiguous "BatchNumber"), and a
--    separate SupervisorId (distinct from ResponsiblePersonId) is
--    required so Cutting Plan/Actual Cutting can carry their own
--    Supervisor.
--
--    REVISED AGAIN (this patch): a real TEST database was found with
--    dbo.MotherPlants already PARTIALLY created under the OLD, pre-patch
--    column names (BatchNumber / SpeciesId / MotherPlantQuantity, plus
--    legacy CreatedBy/CreatedDate/ModifiedBy/ModifiedDate/DaysSincePlanting
--    audit columns). Section 3 below is now a single, unified,
--    step-by-step migration that handles BOTH a brand-new database (no
--    MotherPlants table at all) AND this partially-created legacy table,
--    converging both to the same final shape -- without ever dropping the
--    table or deleting a row.
--
--    IMPORTANT -- why the previous version of this script failed with
--    "Msg 207 ... Invalid column name 'MotherPlantCode'":
--    SQL Server compiles/binds every statement in a batch against the
--    table metadata AS IT EXISTS BEFORE THE BATCH RUNS, not against the
--    metadata as it will be after earlier statements in that same batch
--    have executed. Deferred name resolution (the thing that lets a
--    stored procedure reference a table that doesn't exist yet) only
--    covers whole missing OBJECTS -- it does NOT cover a column that is
--    merely missing from a table that already exists. The old script's
--    ELSE branch had the sp_rename call AND the later
--    "WHERE MotherPlantCode IS NULL" check in the SAME batch (no GO
--    between them). Because MotherPlantCode did not exist on the table
--    when that batch was compiled, the compile step rejected the later
--    statement with "Invalid column name 'MotherPlantCode'" even though
--    the rename earlier in the same batch would have created it by the
--    time execution got there. The fix is exactly requirement #8: every
--    step below that depends on a column an earlier step may have just
--    added or renamed is its own batch, separated by GO, so it is
--    compiled only after that earlier step has already run.
--
--    SpeciesId and MotherPlantQuantity are NOT renamed by this script
--    (see the accompanying explanation) -- renaming them would silently
--    break the already-built, already-reviewed Phase 3 species-consistency
--    function (dbo.fn_CuttingPlan_SpeciesMatchesMotherPlant, which queries
--    dbo.MotherPlants.SpeciesId by name) and every repository already
--    reading/writing dbo.MotherPlants.SpeciesId / .MotherPlantQuantity by
--    those exact names. That is flagged separately; nothing here silently
--    forces the decision either way.

-- 3a) Fresh install: no MotherPlants table at all yet. Create it directly
--     with the final shape (MotherPlantCode, SupervisorId included from
--     the start). This path is unaffected by the legacy-column migration
--     below, since there is no legacy data to migrate.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'MotherPlants' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.MotherPlants
    (
        Id                             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MotherPlants PRIMARY KEY,
        MotherPlantCode                NVARCHAR(20)   NOT NULL,
        PolyhouseId                    INT            NOT NULL,
        SpeciesId                      INT            NOT NULL,
        AreaId                         INT            NULL,
        ResponsiblePersonId            INT            NULL,
        SupervisorId                   INT            NULL,
        PlantingDate                   DATE           NOT NULL,
        MotherPlantQuantity            DECIMAL(18,2)  NOT NULL,
        CuttingPeriodDays              INT            NOT NULL,
        CuttingRate                    DECIMAL(18,4)  NOT NULL,
        ExpectedCuttingQuantity        DECIMAL(18,2)  NOT NULL,
        ExpectedMonthlyCuttingQuantity DECIMAL(18,2)  NOT NULL,
        Status                         NVARCHAR(30)   NOT NULL CONSTRAINT DF_MotherPlants_Status DEFAULT ('Active'),
        Remarks                        NVARCHAR(500)  NULL,
        CreatedDate                    DATETIME2      NOT NULL CONSTRAINT DF_MotherPlants_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy                      NVARCHAR(100)  NULL,
        ModifiedDate                   DATETIME2      NULL,
        ModifiedBy                     NVARCHAR(100)  NULL,

        CONSTRAINT UQ_MotherPlants_MotherPlantCode   UNIQUE (MotherPlantCode),
        CONSTRAINT FK_MotherPlants_Polyhouse         FOREIGN KEY (PolyhouseId)          REFERENCES dbo.Polyhouses(Id),
        CONSTRAINT FK_MotherPlants_Species           FOREIGN KEY (SpeciesId)            REFERENCES dbo.PlantSpecies(Id),
        CONSTRAINT FK_MotherPlants_Area              FOREIGN KEY (AreaId)               REFERENCES dbo.Area(Id),
        CONSTRAINT FK_MotherPlants_ResponsiblePerson FOREIGN KEY (ResponsiblePersonId)  REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_MotherPlants_Supervisor        FOREIGN KEY (SupervisorId)         REFERENCES dbo.IMSUsers(Id),

        CONSTRAINT CK_MotherPlants_Quantity          CHECK (MotherPlantQuantity > 0),
        CONSTRAINT CK_MotherPlants_CuttingRate       CHECK (CuttingRate >= 0),
        CONSTRAINT CK_MotherPlants_CuttingPeriodDays CHECK (CuttingPeriodDays > 0)
    );

    CREATE INDEX IX_MotherPlants_PolyhouseId   ON dbo.MotherPlants(PolyhouseId);
    CREATE INDEX IX_MotherPlants_SpeciesId     ON dbo.MotherPlants(SpeciesId);
    CREATE INDEX IX_MotherPlants_Status        ON dbo.MotherPlants(Status);
    CREATE INDEX IX_MotherPlants_SupervisorId  ON dbo.MotherPlants(SupervisorId);
END
GO

-- 3b) Legacy patch, step 1: if the table exists with the OLD "BatchNumber"
--     column and does NOT yet have "MotherPlantCode", rename it. This is
--     its own batch (ends at the GO below) so nothing after it in this
--     script gets compiled until the rename has actually happened.
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'MotherPlants' AND schema_id = SCHEMA_ID('dbo'))
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MotherPlants') AND name = 'BatchNumber')
   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MotherPlants') AND name = 'MotherPlantCode')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_MotherPlants_BatchNumber' AND parent_object_id = OBJECT_ID('dbo.MotherPlants'))
    BEGIN
        ALTER TABLE dbo.MotherPlants DROP CONSTRAINT UQ_MotherPlants_BatchNumber;
    END
    EXEC sp_rename 'dbo.MotherPlants.BatchNumber', 'MotherPlantCode', 'COLUMN';
END
GO

-- 3c) Legacy patch, step 2: cover the (unexpected, but handled) case where
--     the table exists with NEITHER BatchNumber NOR MotherPlantCode --
--     add MotherPlantCode fresh. Also its own batch: existing rows will
--     have MotherPlantCode = NULL until backfilled by hand or by the app,
--     so the uniqueness constraint in step 3d is deliberately NOT added
--     here, and is skipped automatically until every row has a value.
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'MotherPlants' AND schema_id = SCHEMA_ID('dbo'))
   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MotherPlants') AND name = 'MotherPlantCode')
BEGIN
    ALTER TABLE dbo.MotherPlants ADD MotherPlantCode NVARCHAR(20) NULL;
END
GO

-- 3d) Now that MotherPlantCode is guaranteed (by the two batches above,
--     already executed) to exist on any pre-existing table, it is safe to
--     compile a statement that references it by name. Add the UNIQUE
--     constraint only once every row already has a non-NULL code --
--     otherwise this step is a no-op and simply re-runs on the next
--     execution of this script once the data is backfilled.
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MotherPlants') AND name = 'MotherPlantCode')
   AND NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_MotherPlants_MotherPlantCode' AND parent_object_id = OBJECT_ID('dbo.MotherPlants'))
   AND NOT EXISTS (SELECT 1 FROM dbo.MotherPlants WHERE MotherPlantCode IS NULL)
BEGIN
    ALTER TABLE dbo.MotherPlants ADD CONSTRAINT UQ_MotherPlants_MotherPlantCode UNIQUE (MotherPlantCode);
END
GO

-- 3e) SupervisorId: add the column (own batch)...
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'MotherPlants' AND schema_id = SCHEMA_ID('dbo'))
   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MotherPlants') AND name = 'SupervisorId')
BEGIN
    ALTER TABLE dbo.MotherPlants ADD SupervisorId INT NULL;
END
GO

-- ...then the FK against it (separate batch -- same compile-order reason
--    as 3c -> 3d above: the FK's column reference must not be compiled
--    before the column is known to exist)...
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MotherPlants') AND name = 'SupervisorId')
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_MotherPlants_Supervisor')
BEGIN
    ALTER TABLE dbo.MotherPlants ADD CONSTRAINT FK_MotherPlants_Supervisor FOREIGN KEY (SupervisorId) REFERENCES dbo.IMSUsers(Id);
END
GO

-- ...then its index.
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MotherPlants') AND name = 'SupervisorId')
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MotherPlants_SupervisorId' AND object_id = OBJECT_ID('dbo.MotherPlants'))
BEGIN
    CREATE INDEX IX_MotherPlants_SupervisorId ON dbo.MotherPlants(SupervisorId);
END
GO

-- 3f) Belt-and-braces: make sure the three "always expected" indexes
--     exist too, for a table that reached this point via the legacy path
--     (the legacy path never had a chance to create them, unlike the
--     fresh-install path in step 3a). Guarded/additive, safe to re-run.
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'MotherPlants' AND schema_id = SCHEMA_ID('dbo'))
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MotherPlants_PolyhouseId' AND object_id = OBJECT_ID('dbo.MotherPlants'))
BEGIN
    CREATE INDEX IX_MotherPlants_PolyhouseId ON dbo.MotherPlants(PolyhouseId);
END
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MotherPlants') AND name = 'SpeciesId')
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MotherPlants_SpeciesId' AND object_id = OBJECT_ID('dbo.MotherPlants'))
BEGIN
    CREATE INDEX IX_MotherPlants_SpeciesId ON dbo.MotherPlants(SpeciesId);
END
GO

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'MotherPlants' AND schema_id = SCHEMA_ID('dbo'))
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MotherPlants_Status' AND object_id = OBJECT_ID('dbo.MotherPlants'))
BEGIN
    CREATE INDEX IX_MotherPlants_Status ON dbo.MotherPlants(Status);
END
GO

-- 3g) SpeciesId -> PlantSpeciesId and MotherPlantQuantity -> Quantity are
--     DELIBERATELY NOT renamed here. See the explanation delivered
--     alongside this script for why, and what a full rename would
--     actually require. Nothing to do in this step; it is a placeholder
--     marker so a future patch that DOES perform the rename has an
--     obvious place to add it, right after the SupervisorId/index work
--     above and before the audit-column note below.

-- 3h) Legacy audit/date columns (CreatedBy, CreatedDate, ModifiedBy,
--     ModifiedDate, DaysSincePlanting): left exactly as they are. See the
--     explanation delivered alongside this script for why these remain
--     as legacy columns rather than being renamed/migrated to a new
--     "CreatedAt" column. Nothing to do in this step -- no ALTER, no
--     rename, no data touched.
