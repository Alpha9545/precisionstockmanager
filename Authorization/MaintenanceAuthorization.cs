using System.Security.Claims;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Authorization
{
    // WHO may see a maintenance ticket and perform each workflow step. Composes the existing
    // Permission claims with AreaAccessService exactly like every other module -- never a
    // separate mechanism. Every page handler calls this on the ticket RE-LOADED from the
    // database, so posted ids or a stale page can never widen what a user may do.
    //
    //   Supervisor (Maintenance.Raise)  raise tickets for own Areas; confirm arrival and
    //                                   completion of own-Area tickets -- never as the worker
    //   Viewer (Maintenance.View)       see own-Area tickets and reports
    //   Worker (Maintenance.Work + the category's worker ROLE, e.g. Electrician)
    //                                   see and accept Open tickets of own trade; visit / work /
    //                                   resolve tickets assigned to them. Being a worker is
    //                                   decided by the role claim and an EXPLICIT Maintenance.Work
    //                                   claim, so a full-access administrator is never "the worker".
    //   Manager (Maintenance.Manage; full-access users)
    //                                   assign / reassign before arrival; all Areas
    //   Requester or administrator      cancel while Open / Accepted
    public class MaintenanceAuthorization
    {
        public const string RaisePermission = "Maintenance.Raise";
        public const string ViewPermission = "Maintenance.View";
        public const string WorkPermission = "Maintenance.Work";
        public const string ManagePermission = "Maintenance.Manage";

        private readonly AreaAccessService _areaAccess;

        public MaintenanceAuthorization(AreaAccessService areaAccess)
        {
            _areaAccess = areaAccess;
        }

        public bool IsManager(ClaimsPrincipal user) => user.HasPermission(ManagePermission);

        public bool CanRaiseFor(ClaimsPrincipal user, int areaId)
            => user.HasPermission(RaisePermission) && _areaAccess.CanAccessRequiredArea(user, areaId);

        // The worker roles (trades) this user holds, e.g. { "Electrician" }.
        public IReadOnlyCollection<string> WorkerRoles(ClaimsPrincipal user)
            => user.HasClaim(MinimumAuthorizationLevelHandler.PermissionClaimType, WorkPermission)
                ? user.FindAll(AreaAccessService.RoleNameClaimType).Select(c => c.Value).ToList()
                : Array.Empty<string>();

        public bool IsWorkerFor(ClaimsPrincipal user, string workerRoleName)
            => WorkerRoles(user).Contains(workerRoleName, StringComparer.OrdinalIgnoreCase);

        public bool IsAssignedWorker(ClaimsPrincipal user, MaintenanceTicket t)
            => t.AssignedWorkerId.HasValue && t.AssignedWorkerId == user.GetUserId()
               && user.HasClaim(MinimumAuthorizationLevelHandler.PermissionClaimType, WorkPermission);

        public bool CanSeeArea(ClaimsPrincipal user, int areaId)
            => user.HasPermission($"{ViewPermission}|{RaisePermission}|{ManagePermission}") && _areaAccess.CanAccessRequiredArea(user, areaId);

        public bool CanView(ClaimsPrincipal user, MaintenanceTicket t)
            => IsManager(user)
               || CanSeeArea(user, t.AreaId)
               || IsAssignedWorker(user, t)
               || (t.Status == MaintenanceRules.Open && IsWorkerFor(user, t.WorkerRoleName));

        // A supervisor of the ticket's Area -- and NOT the assigned worker (the database repeats this).
        public bool CanSupervise(ClaimsPrincipal user, MaintenanceTicket t)
            => user.HasPermission(RaisePermission) && _areaAccess.CanAccessRequiredArea(user, t.AreaId)
               && t.AssignedWorkerId != user.GetUserId();

        public bool CanPerform(ClaimsPrincipal user, MaintenanceTicket t, MaintenanceRules.Action action)
        {
            if (!MaintenanceRules.IsAllowed(action, t.Status))
                return false;
            return action switch
            {
                MaintenanceRules.Action.Accept => IsWorkerFor(user, t.WorkerRoleName),
                MaintenanceRules.Action.Assign or MaintenanceRules.Action.Reassign => IsManager(user),
                MaintenanceRules.Action.Arrive or MaintenanceRules.Action.StartWork or MaintenanceRules.Action.Resolve => IsAssignedWorker(user, t),
                MaintenanceRules.Action.ConfirmArrival or MaintenanceRules.Action.ConfirmCompletion or MaintenanceRules.Action.RejectCompletion => CanSupervise(user, t),
                MaintenanceRules.Action.Cancel => t.RequestedById == user.GetUserId() || user.IsFullAccess(),
                _ => false,
            };
        }
    }
}
