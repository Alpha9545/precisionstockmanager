using PlantStockManager.Authorization;

namespace PlantStockManager.Services
{
    // One dbo.UserRoles row of a user: the Area it is scoped to (null = no Area)
    // and the permission codes carried by that row's role.
    public sealed record AreaUserGrant(int UserId, string UserName, int? AreaId, bool IsActive, IReadOnlyCollection<string> PermissionCodes);

    // CUTTING TRAY SOWING -- who may be chosen as the Sowing Supervisor.
    //
    // The assigned supervisor is the ONLY person who can approve the sowing
    // (Supervisor Approval -> Ready Stock). So an eligible supervisor is a user
    // who can actually do that:
    //   * ACTIVE;
    //   * ASSIGNED to the sowing's growing Area (a dbo.UserRoles row with that
    //     AreaId) -- the approval page checks the same Area access;
    //   * holds the permission that gates the approval page
    //     (/Production/ReadyConfirmation/Confirm -> "ReadyStock.Confirm"), from
    //     any of their roles -- read from the page map, not copied here;
    //
    // The person recording the sowing is NOT excluded: a recorder who meets the
    // rules above may choose themselves and, as the assigned supervisor, approve
    // that cutting sowing (DirectSowingRules.CanApprove / the approval trigger).
    // (Direct SEED sowing keeps "the recorder cannot be its supervisor".)
    // NOT tied to any role NAME: a Mother Plant Supervisor, a Sowing Supervisor
    // or any other role qualifies when it carries the permission and the Area.
    //
    // Permission codes are pooled across ALL of a user's role rows and the Area
    // comes from ANY of them, exactly as the login claims are built.
    //
    // Seed (direct) sowing is NOT covered by this class: it keeps its own
    // "active Sowing Supervisor" rule (DirectSowingRules.ValidateSupervisorAssignment).
    public static class SowingSupervisorRules
    {
        public static string ApprovalPermissions
            => FeatureAuthorizationConventions.GetRule("/Production/ReadyConfirmation/Confirm").Read;

        public static List<(int UserId, string UserName)> EligibleForArea(
            IEnumerable<AreaUserGrant> grants, int areaId)
        {
            var required = PermissionPolicy.Split(ApprovalPermissions);
            return grants
                .Where(g => g.IsActive)
                .GroupBy(g => g.UserId)
                .Where(u => u.Any(g => g.AreaId == areaId)
                            && u.SelectMany(g => g.PermissionCodes).Any(c => required.Contains(c, StringComparer.OrdinalIgnoreCase)))
                .Select(u => (u.Key, u.First().UserName))
                .OrderBy(u => u.Item2, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // supervisorId must be one of the users eligible for the sowing's Area.
        // The chosen user's Id -- not a name -- is what gets stored.
        public static (bool Ok, string? Error) ValidateAssignment(
            int? supervisorId, IReadOnlyCollection<int> eligibleSupervisorIds)
        {
            if (!supervisorId.HasValue || supervisorId.Value <= 0)
                return (false, "Supervisor is required: choose the Sowing Supervisor who will approve this batch.");
            if (!eligibleSupervisorIds.Contains(supervisorId.Value))
                return (false, "The selected supervisor is not an active user of the sowing's Area who can approve sowings.");
            return (true, null);
        }
    }
}
