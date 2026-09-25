/*
    Phase 28: Mother Plant / Kunjir Supervisors can complete Potted Plant
    Production directly from Cutting Stock.

    Finding (Phase 3 audit, "Mother Plant and Cutting Production"): the
    business workflow --
        Mother Plant Supervisor records cutting production (available
        quantity, at their own Area) and then chooses:
          A. Deliver to Main Office (InternalTransfers, StockType =
             'Cutting', confirmed there, transplanted to a destination
             Area), or
          B. Assign it to the appropriate Area and use it directly for
             Potted Plant Production
    already existed in full as "Model B" (Phase 15/16/19):
        Pages/Production/CuttingStock/EnterCutting.cshtml   (record cutting,
          at the supervisor's own Area -- MotherPlant/Kunjir/Kiran)
        Pages/Production/CuttingStock/GiveToMainOffice.cshtml (path A)
        Pages/Production/PotProduction/CreateFromCutting.cshtml (path B --
          consumes the Cutting Stock pool IN PLACE at its own Area, so the
          Area is preserved end to end with no extra transfer step)
    Nothing here was duplicated; the pre-existing Cutting Plan -> Actual
    Cutting -> Cutting Delivery -> Propagation Batch chain (Phase 3-6) is a
    separate, earlier pipeline this phase does not touch or remove (see
    PROJECT_DOCUMENTATION.md / the Phase 3 report for that open question).

    The one gap: '/Production/PotProduction/CreateFromCutting' (path B) is
    gated by 'PotProduction.Enter | Kiran.Enter' (FeatureAuthorization
    Conventions.cs). Of the three cutting-producing roles seeded in
    Phase 14, only KiranSupervisor holds either code (PotProduction.View/
    Enter) -- MotherPlantSupervisor and KunjirSupervisor hold neither, so
    neither can complete path B themselves; only path A (Give to Main
    Office) was reachable for them. This grants both roles the same
    'PotProduction.View'/'PotProduction.Enter' pair KiranSupervisor
    already has -- no new permission code, no new role.

    Safe to run multiple times. Grants ONLY; nothing is revoked, no table
    is created or altered, no existing data is touched. Run after
    Phase14_RoleFoundation_AreaExtension.sql; independent of every other
    phase.
*/

INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id
FROM dbo.Roles r
CROSS JOIN dbo.Permissions p
WHERE r.Name IN ('MotherPlantSupervisor', 'KunjirSupervisor')
  AND p.Code IN ('PotProduction.View', 'PotProduction.Enter')
  AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
GO
