using System.Security.Claims;
using PlantStockManager.Models;

namespace PlantStockManager.Authorization
{
    // WHO may do what in the Inventory Store. Composes the existing Permission claims with
    // AreaAccessService -- no separate mechanism.
    //
    //   Store.Manage (Main Office Store Keeper; full-access users)
    //       item master, stock entry, approve / issue, confirm returns, transfers -- for EVERY
    //       Area. This central-store reach is a deliberate exception that exists ONLY inside the
    //       Store module (these methods); it does not widen AreaAccessService, so the Store Keeper's
    //       access to every other module is unchanged.
    //   Store.Request (+ Area access)  raise requests for own Areas; settle what was issued to them
    //   Store.View (+ Area access)     see the store requests / holdings of own Areas
    //   Requester or Store.Manage      cancel while Pending / Approved
    public class StoreAuthorization
    {
        public const string RequestPermission = "Store.Request";
        public const string ViewPermission = "Store.View";
        public const string ManagePermission = "Store.Manage";

        private readonly AreaAccessService _areaAccess;

        public StoreAuthorization(AreaAccessService areaAccess)
        {
            _areaAccess = areaAccess;
        }

        public bool IsManager(ClaimsPrincipal user) => user.HasPermission(ManagePermission);

        public bool CanRequestFor(ClaimsPrincipal user, int areaId)
            => user.HasPermission(RequestPermission) && _areaAccess.CanAccessRequiredArea(user, areaId);

        public bool CanSeeArea(ClaimsPrincipal user, int areaId)
            => IsManager(user) || (user.HasPermission($"{ViewPermission}|{RequestPermission}") && _areaAccess.CanAccessRequiredArea(user, areaId));

        // Area ids for store lists: null = every Area (Store Keeper / full access).
        public IReadOnlyCollection<int>? AreaScope(ClaimsPrincipal user)
            => IsManager(user) || _areaAccess.HasFullAreaAccess(user) ? null : _areaAccess.GetAccessibleAreaIds(user);

        public bool CanView(ClaimsPrincipal user, StoreRequest r) => CanSeeArea(user, r.AreaId);

        public bool CanReview(ClaimsPrincipal user, StoreRequest r) => IsManager(user) && r.Status == Services.StoreRules.Pending;
        public bool CanIssue(ClaimsPrincipal user, StoreRequest r) => IsManager(user) && r.Status == Services.StoreRules.Approved;
        public bool CanSettle(ClaimsPrincipal user, StoreRequest r) => CanRequestFor(user, r.AreaId) && r.Status == Services.StoreRules.Issued;
        public bool CanConfirmReturn(ClaimsPrincipal user, StoreRequest r) => IsManager(user) && r.Status == Services.StoreRules.Issued;
        public bool CanCancel(ClaimsPrincipal user, StoreRequest r)
            => Services.StoreRules.CanCancel(r.Status) && (IsManager(user) || (r.RequestedById == user.GetUserId() && CanRequestFor(user, r.AreaId)));
    }
}
