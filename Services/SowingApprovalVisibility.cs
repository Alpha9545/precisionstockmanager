using System.Security.Claims;
using PlantStockManager.Authorization;

namespace PlantStockManager.Services
{
    // 2026-10-20 (owner requirement): ONE visibility policy for both Sowing Approvals pages (Seed Production and Mother Plant &
    // Cutting). Who sees what -- never who may approve (the Confirm page, ReadyConfirmationRepository.ConfirmAsync and the
    // TR_ReadyConfirmations_* triggers decide that, unchanged):
    //   Administrator (the application's authoritative full-access check, ClaimsPrincipalSecurityExtensions.IsFullAccess --
    //   the "FullAccess" claim stamped at login for SecurityOptions.FullAccessRoleNames, default "System Administrator"):
    //       Waiting   every pending batch (every supervisor, every Area), each with who approves it
    //       History   every approval record of every user, with the recorded approver on each row
    //   Everyone else:
    //       Waiting   only the batches THEY are authorised to approve (SowingApprovalRules.Eligibility.Authorized); every other
    //                 batch is left out -- never shown as "Not your approval" / "View only"
    //       History   only the approvals THEY made (ReadyConfirmations.ApprovedById = their id)
    // Identity and role come from the authenticated principal only -- never from the request.
    public static class SowingApprovalVisibility
    {
        public static bool IsAdministrator(ClaimsPrincipal user)
            => user.Identity?.IsAuthenticated == true && user.IsFullAccess();

        public static bool PendingApprovableOnly(ClaimsPrincipal user) => !IsAdministrator(user);

        public static HistoryScope History(ClaimsPrincipal user)
            => IsAdministrator(user) ? HistoryScope.All
             : user.Identity?.IsAuthenticated == true && user.GetUserId() is int me ? HistoryScope.ApprovedBy(me)
             : HistoryScope.Nobody;
    }

    // Whose approval records a history read returns. Built only by SowingApprovalVisibility.History (or by tests): All is the
    // administrator's view; ApprovedBy(id) one user's own approvals; Nobody an unidentified user (an empty history).
    public sealed record HistoryScope
    {
        public bool IncludesAllApprovers { get; private init; }
        public int? ApproverId { get; private init; }
        private HistoryScope() { }

        public static readonly HistoryScope All = new() { IncludesAllApprovers = true };
        public static readonly HistoryScope Nobody = new();
        public static HistoryScope ApprovedBy(int approverId) => new() { ApproverId = approverId };
    }
}
