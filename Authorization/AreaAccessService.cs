using System.Security.Claims;

namespace PlantStockManager.Authorization
{
    // Phase 17/B: the reusable Area-scope check that pages consult before
    // reading or writing anything tied to a specific dbo.Area row. This is
    // deliberately separate from the Permission-code system
    // (MinimumAuthorizationLevelHandler/PermissionAuthorizationPolicyProvider):
    // Permission claims answer "can this user do X at all," this answers
    // "can this user do it for THIS Area" -- the two compose (a page checks
    // both), neither replaces the other.
    //
    // Backed by two new claim types stamped at login (Login.cshtml.cs), in
    // addition to -- not instead of -- the existing "Permission" claims:
    //   "RoleName"   -- one per dbo.UserRoles row assigned to this user
    //                   (e.g. "KunjirSupervisor"), regardless of AreaId.
    //   "AreaAccess" -- one per dbo.UserRoles row that has a non-null
    //                   AreaId, value = that Area's Id as a string.
    // Both are purely additive: a user with no UserRoles rows gets none of
    // either and this service then denies every Area-scoped check for
    // them, exactly like the Permission-claim system already does for
    // permission checks -- no behavior regresses for anyone.
    //
    // dbo.UserRoles remains the single source of truth (per the explicit
    // instruction not to add a redundant GrowingPartnerId to it, or a
    // parallel scope table) -- this service only reads the claims computed
    // from it at login. Growing Partner is never checked directly here:
    // the chain is User -> UserRoles.AreaId -> Area.GrowingPartnerId, so
    // scoping a user to their assigned Area(s) already scopes them to
    // "their" Growing Partner's stock, without a Growing Partner concept
    // anywhere in this class (Decision 12 / architecture analysis §6).
    public class AreaAccessService
    {
        public const string RoleNameClaimType = "RoleName";
        public const string AreaAccessClaimType = "AreaAccess";

        // Roles that see/operate across every Area by design, not just
        // their own assignment(s):
        //   Admin            -- full administration (existing role).
        //   Management       -- cross-location read-only visibility,
        //                       already the approved role table's intent
        //                       (Phase 14 grants it every "*.View"
        //                       permission cross-location).
        //   MainOfficeOfficer -- Main Office is a controlled central
        //                       operation, not a Growing-Partner-owned
        //                       Area (explicit instruction: do not treat
        //                       Main Office as a normal Growing Partner
        //                       Area, and do not incorrectly restrict this
        //                       role as though it were a partner
        //                       supervisor). Main Office routinely
        //                       receives/confirms/routes stock arriving
        //                       from every other Area, so it needs
        //                       cross-Area reach to do its existing job.
        private static readonly string[] FullAccessRoleNames = { "Admin", "Management", "MainOfficeOfficer" };

        // Phase 1: the same list, read by the supervisor-eligibility query
        // (UserRoleRepository.GetSupervisorCandidatesAsync) -- one source.
        public static IReadOnlyList<string> AllAreaRoleNames => FullAccessRoleNames;

        // Phase A: a full-access user (e.g. the "System Administrator" role,
        // see SecurityOptions.FullAccessRoleNames -> "FullAccess" claim)
        // sees every Area as well. Area access stays SEPARATE from feature
        // permissions: holding a feature permission never widens Area scope.
        public bool HasFullAreaAccess(ClaimsPrincipal user)
            => user.IsFullAccess()
               || FullAccessRoleNames.Any(role => user.HasClaim(RoleNameClaimType, role));

        // True when the user may access the given Area. A null areaId
        // (a record with no Area assigned at all) is always allowed --
        // there is nothing to scope, matching how AreaId is already an
        // optional field throughout the app (Mother Plant, Area itself).
        public bool CanAccessArea(ClaimsPrincipal user, int? areaId)
        {
            if (!areaId.HasValue)
                return true;

            if (HasFullAreaAccess(user))
                return true;

            var target = areaId.Value.ToString();
            return user.HasClaim(c => c.Type == AreaAccessClaimType && c.Value == target);
        }

        // F1: strict variant for records whose Area is REQUIRED for the
        // operation (e.g. the Main Office Area a Cutting transfer is waiting
        // at). Unlike CanAccessArea, a missing Area is NOT treated as
        // "nothing to scope" -- only a full-access user may act on it.
        public bool CanAccessRequiredArea(ClaimsPrincipal user, int? areaId)
            => areaId.HasValue ? CanAccessArea(user, areaId) : HasFullAreaAccess(user);

        // F1: for records that touch several Areas (e.g. an Internal
        // Transfer's Source / Destination / Main Office Area): true when the
        // user has cross-Area access or is assigned to at least one of the
        // NON-null Areas given. Unlike CanAccessArea, a null Area never
        // grants access by itself.
        public bool CanAccessAnyArea(ClaimsPrincipal user, params int?[] areaIds)
            => HasFullAreaAccess(user)
                || areaIds.Any(id => id.HasValue && CanAccessArea(user, id));

        // F1: in-memory list filter used by Index/queue pages -- identical
        // to the "HasFullAreaAccess ? all : all.Where(CanAccessArea)"
        // pattern already repeated across the Production pages.
        public List<T> FilterByArea<T>(ClaimsPrincipal user, IEnumerable<T> items, Func<T, int?> areaSelector)
            => HasFullAreaAccess(user)
                ? items.ToList()
                : items.Where(i => CanAccessArea(user, areaSelector(i))).ToList();

        // The full set of Area Ids this user is explicitly scoped to
        // (via UserRoles.AreaId), for pages that need to filter a list
        // rather than check one record at a time. Meaningless for a
        // full-access user -- callers should check HasFullAreaAccess
        // first and skip filtering entirely in that case.
        public IReadOnlyCollection<int> GetAccessibleAreaIds(ClaimsPrincipal user)
        {
            return user.FindAll(AreaAccessClaimType)
                .Select(c => int.TryParse(c.Value, out var id) ? id : (int?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();
        }
    }
}
