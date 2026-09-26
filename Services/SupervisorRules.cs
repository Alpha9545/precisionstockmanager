namespace PlantStockManager.Services
{
    // Supervisor dropdowns show only people who actually hold the role the
    // field means -- never the whole employee list.
    public static class SupervisorRules
    {
        public const string SowingSupervisor = "Sowing Supervisor";
        public const string MotherPlantSupervisor = "Mother Plant Supervisor";
        public const string SowingOperator = "Sowing Operator";
        public const string DispatchExecutive = "Dispatch Executive";

        // "Labour / Responsible Person" on a sowing approval: the sowing staff.
        public static readonly string[] SowingStaffRoles = { SowingOperator, SowingSupervisor };

        public sealed record RoleAssignment(int UserId, string UserName, string RoleName, int? AreaId, bool IsActive);

        // areaId = null: anyone holding the role (any or no Area).
        // areaId set: only people holding the role FOR that Area.
        public static List<(int UserId, string UserName)> Eligible(
            IEnumerable<RoleAssignment> assignments, string roleName, int? areaId = null)
            => assignments
                .Where(a => a.IsActive
                            && string.Equals(a.RoleName?.Trim(), roleName, StringComparison.OrdinalIgnoreCase)
                            && (!areaId.HasValue || a.AreaId == areaId.Value))
                .GroupBy(a => a.UserId)
                .Select(g => (g.Key, g.First().UserName))
                .OrderBy(u => u.Item2, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Any role whose name ends with "Supervisor" (Area supervisor field).
        public static bool IsSupervisorRole(string? roleName)
            => roleName != null && roleName.Trim().EndsWith("Supervisor", StringComparison.OrdinalIgnoreCase);
    }
}
