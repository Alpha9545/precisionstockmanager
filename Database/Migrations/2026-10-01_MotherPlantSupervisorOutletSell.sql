-- ============================================================================
-- 2026-10-01_MotherPlantSupervisorOutletSell.sql
--
-- Grants the role "Mother Plant Supervisor" the permission 'Outlet.Sell' so
-- they can use /Production/OutletSale/Create ("Direct Customer Sale" --
-- the "send pots to customers" workflow).
--
-- ROOT CAUSE (confirmed by inspection, not guessed): Authorization/
-- FeatureAuthorizationConventions.cs maps /Production/OutletSale/Create to
-- the single permission 'Outlet.Sell'. dbo.RolePermissions currently grants
-- Mother Plant Supervisor only: Dashboard.View, MotherPlant.View/Enter,
-- CuttingPlan.View, CuttingDelivery.View/Enter, InternalTransfer.View,
-- PotProduction.View, ReadyStock.View/Confirm (the last two added by
-- 2026-09-28_CuttingSowingSupervisorByArea.sql) -- 'Outlet.Sell' was never
-- among them, so the page's authorization check (and the "Direct Sale" nav
-- link, which is driven by the EXACT SAME check via NavAuthorizationTagHelpers.
-- FeatureAccessService -- confirmed by inspection, there is no separate
-- UI-only permission system) both correctly refuse this role today.
--
-- WHAT CHANGES: dbo.RolePermissions gets exactly ONE new row (only if
-- missing): (Mother Plant Supervisor, Outlet.Sell). Nothing else -- no other
-- role, no other permission, no schema/trigger/table change, no customer or
-- stock data touched. The page's own existing Area-access check
-- (AreaAccessService.CanAccessArea against the chosen Outlet) is completely
-- separate from this and is unaffected: a Mother Plant Supervisor can only
-- sell from an Outlet Area they are already assigned to, exactly like every
-- other role.
--
-- NOT changed: Outlet.View is NOT granted (not asked for, and not required
-- by OnPostAsync/OnGetAsync of Create.cshtml.cs -- confirmed by reading it;
-- it loads Outlets via AreaAccessService.FilterByArea, not a permission
-- check) -- Mother Plant Supervisor will NOT see "Sales History" in the menu
-- from this change, only "Direct Sale". No other role's permissions change.
--
-- SAFETY: guarded to PlantsIMS2_Test only; one transaction; idempotent
-- (NOT EXISTS), the same pattern 2026-09-28_CuttingSowingSupervisorByArea.sql
-- and PhaseD_ProductionRestructure.sql already use for role grants. Logged-in
-- users pick this up at their next claims revalidation
-- (SecurityOptions.PrincipalRevalidationMinutes), same as every other
-- permission grant in this codebase -- not a new behavior.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 51201, '2026-10-01_MotherPlantSupervisorOutletSell.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION MotherPlantSupervisorOutletSell;

BEGIN TRY

    IF (SELECT COUNT(*) FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Mother Plant Supervisor') <> 1
        THROW 51202, 'Expected exactly one role named Mother Plant Supervisor -- aborting rather than guess.', 1;
    IF (SELECT COUNT(*) FROM dbo.Permissions WHERE Code = N'Outlet.Sell') <> 1
        THROW 51203, 'Permission Outlet.Sell not found -- aborting.', 1;

    -- Before-snapshot: this role's existing permissions, untouched by this script.
    SELECT p.Code AS ExistingPermission
    FROM dbo.RolePermissions rp
    JOIN dbo.Roles r ON r.Id = rp.RoleId
    JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor'
    ORDER BY p.Code;

    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM dbo.Roles r
    CROSS JOIN dbo.Permissions p
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor'
      AND p.Code = N'Outlet.Sell'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    DECLARE @GrantsAdded INT = @@ROWCOUNT;

    ----------------------------------------------------------------------
    -- VERIFICATION
    ----------------------------------------------------------------------
    PRINT '=== Rows added (0 on a re-run; 1 the first time) ===';
    SELECT @GrantsAdded AS GrantsAdded;

    PRINT '=== Mother Plant Supervisor now has Outlet.Sell (must be 1) ===';
    SELECT COUNT(*) AS HasOutletSell
    FROM dbo.RolePermissions rp
    JOIN dbo.Roles r ON r.Id = rp.RoleId
    JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor' AND p.Code = N'Outlet.Sell';

    PRINT '=== Mother Plant Supervisor does NOT have Outlet.View (must be 0 -- not granted by this script) ===';
    SELECT COUNT(*) AS HasOutletView
    FROM dbo.RolePermissions rp
    JOIN dbo.Roles r ON r.Id = rp.RoleId
    JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor' AND p.Code = N'Outlet.View';

    PRINT '=== Mother Plant Supervisor full permission set after (must be exactly the before-set PLUS Outlet.Sell) ===';
    SELECT p.Code
    FROM dbo.RolePermissions rp
    JOIN dbo.Roles r ON r.Id = rp.RoleId
    JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor'
    ORDER BY p.Code;

    PRINT '=== No other role''s permission count changed (must all equal their current counts -- compare manually) ===';
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, COUNT(*) AS PermissionCount
    FROM dbo.RolePermissions rp
    JOIN dbo.Roles r ON r.Id = rp.RoleId
    GROUP BY COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName)
    ORDER BY RoleName;

    PRINT '=== Total RolePermissions row count (before total + 1, or unchanged on a re-run) ===';
    SELECT COUNT(*) AS TotalRolePermissionRows FROM dbo.RolePermissions;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MotherPlantSupervisorOutletSell;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-10-01: user reviewed the dry run (exactly 1 row added, only
-- to Mother Plant Supervisor, only Outlet.Sell; every other role's
-- permission count unchanged) and explicitly approved committing.
-- Verified backup: PlantsIMS2_Test_PreMotherPlantSupervisorOutletSell_20261001.bak
----------------------------------------------------------------------------
COMMIT TRANSACTION MotherPlantSupervisorOutletSell;
GO
