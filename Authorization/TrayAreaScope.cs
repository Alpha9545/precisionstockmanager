using System.Security.Claims;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Authorization
{
    // Which Areas' EMPTY-TRAY stock (dbo.TrayStock) a user may add to or see -- the one rule
    // used by Add Material > Trays, the Material Stock list and its history.
    //   TrayStock.Enter             : any active, non-Outlet Area the user is assigned to
    //                                 (AreaAccessService's regular rule -- unchanged)
    //   TrayStock.EnterMotherPlant  : additionally every ACTIVE Mother Plant Area -- and nothing
    //                                 else (owner decision 2026-10-09: Office Coordinators and
    //                                 Booking Executives, assigned to the Main Office only, stock the
    //                                 Mother Plant polyhouses' trays for transplanting)
    // Viewing follows the same Areas, so whoever stocks a Mother Plant polyhouse can see it.
    //   TrayStock.ViewMotherPlant   : (2026-10-11, Mother Plant Supervisor) READ-ONLY view of the user's
    //                                 OWN active Mother Plant Areas -- never other Areas, never adding.
    // It never grants growing material (cocopeat / pitmos), which keeps MaterialStock.Add.
    public sealed class TrayAreaScope
    {
        public const string MotherPlantAreaType = "MotherPlant";

        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccess;
        private HashSet<int>? _motherPlantAreaIds;

        public TrayAreaScope(AreaRepository areaRepo, AreaAccessService areaAccess)
        {
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
        }

        public static bool IsAddableArea(ClaimsPrincipal user, Area area, Func<int, bool> canAccessRequiredArea)
        {
            if (!area.IsActive || area.AreaType == PlantStockManager.Services.OutletRules.AreaType)
                return false;
            if (user.HasPermission(MaterialStockAccess.TrayAdd) && canAccessRequiredArea(area.Id))
                return true;
            return user.CanAddMotherPlantTrays() && area.AreaType == MotherPlantAreaType;
        }

        // 2026-10-10: may this user RECEIVE / REJECT a tray transfer into this Area? Location-aware:
        // the permission for the Area's type (TrayTransferRules.ReceivePermissionFor) AND an assignment
        // to that Area -- never every Area. Full-access users: any active, non-Outlet Area.
        public static bool IsReceivableBy(ClaimsPrincipal user, Area area, Func<int, bool> canAccessRequiredArea, bool fullAreaAccess)
        {
            if (!area.IsActive || area.AreaType == PlantStockManager.Services.OutletRules.AreaType)
                return false;
            if (fullAreaAccess)
                return true;
            var permission = PlantStockManager.Services.TrayTransferRules.ReceivePermissionFor(area.AreaType);
            return permission != null && user.HasPermission(permission) && canAccessRequiredArea(area.Id);
        }

        public bool CanReceiveIn(ClaimsPrincipal user, Area area)
            => IsReceivableBy(user, area, id => _areaAccess.CanAccessRequiredArea(user, id), _areaAccess.HasFullAreaAccess(user));

        public bool CanAddIn(ClaimsPrincipal user, Area area)
            => IsAddableArea(user, area, id => _areaAccess.CanAccessRequiredArea(user, id));

        public async Task<List<Area>> AddableAreasAsync(ClaimsPrincipal user)
            => (await _areaRepo.GetAllAreas()).Where(a => CanAddIn(user, a)).OrderBy(a => a.Name).ToList();

        // Viewing: the user's own Areas (the tray-view permissions before 2026-10-11), plus every active
        // Mother Plant Area for EnterMotherPlant, plus -- for TrayStock.ViewMotherPlant -- only the
        // user's OWN active Mother Plant Areas. Server-side: Material Stock, Material History and the
        // Tray Transfers list all read through this rule.
        public async Task<Func<int, bool>> CanViewPredicateAsync(ClaimsPrincipal user)
        {
            if (_areaAccess.HasFullAreaAccess(user))
                return _ => true;
            var ids = await ViewableAreaIdsAsync(user);
            return ids.Contains;
        }

        // For SQL-scoped reads: null = every Area (full access).
        public async Task<IReadOnlyCollection<int>?> ViewScopeAsync(ClaimsPrincipal user)
        {
            if (_areaAccess.HasFullAreaAccess(user))
                return null;
            return await ViewableAreaIdsAsync(user);
        }

        private async Task<HashSet<int>> ViewableAreaIdsAsync(ClaimsPrincipal user)
        {
            var ids = new HashSet<int>();
            var own = _areaAccess.GetAccessibleAreaIds(user);
            if (user.HasPermission(MaterialStockAccess.TrayViewOwnAreas))
                ids.UnionWith(own);
            if (user.CanAddMotherPlantTrays())
                ids.UnionWith(await MotherPlantAreaIdsAsync());
            if (user.HasPermission(MaterialStockAccess.TrayViewMotherPlant))
                ids.UnionWith(own.Where((await MotherPlantAreaIdsAsync()).Contains));
            return ids;
        }

        private async Task<HashSet<int>> MotherPlantAreaIdsAsync()
            => _motherPlantAreaIds ??= (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && a.AreaType == MotherPlantAreaType).Select(a => a.Id).ToHashSet();
    }
}
