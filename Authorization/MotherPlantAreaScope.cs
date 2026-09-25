using System.Security.Claims;
using PlantStockManager.Data;

namespace PlantStockManager.Authorization
{
    // F1 (Security hardening): the Phase 3-6 traceability records
    // (Cutting Plan -> Actual Cutting -> Cutting Delivery -> Propagation
    // Batch) have no Area column of their own (Propagation Batch's AreaId is
    // optional) -- they belong to an Area THROUGH their Mother Plant
    // (MotherPlant.AreaId), which is exactly how the existing Mother Plant
    // pages are already Area-scoped. Those pages previously had no Area
    // check at all, so any user could list, open, create or edit any
    // Area's records by id.
    //
    // This is only a lookup that resolves "which Area does this record
    // belong to"; the actual decision is still AreaAccessService.CanAccessArea
    // (same claims, same null-Area semantics as MotherPlant/Details), so no
    // second authorization system is introduced.
    public class MotherPlantAreaScope
    {
        private readonly MotherPlantRepository _motherPlantRepo;
        private readonly AreaAccessService _areaAccessService;

        public MotherPlantAreaScope(MotherPlantRepository motherPlantRepo, AreaAccessService areaAccessService)
        {
            _motherPlantRepo = motherPlantRepo;
            _areaAccessService = areaAccessService;
        }

        // MotherPlant.Id -> MotherPlant.AreaId for every Mother Plant (any
        // status), for filtering lists without one query per row.
        public async Task<IReadOnlyDictionary<int, int?>> GetAreaMapAsync()
            => (await _motherPlantRepo.GetAllAsync()).ToDictionary(m => m.Id, m => m.AreaId);

        // ownAreaId (e.g. PropagationBatch.AreaId) wins when set; otherwise
        // the Mother Plant's Area decides. A Mother Plant id that does not
        // resolve is only visible to cross-Area roles.
        public bool CanAccess(ClaimsPrincipal user, IReadOnlyDictionary<int, int?> areaMap, int? motherPlantId, int? ownAreaId = null)
        {
            if (ownAreaId.HasValue)
                return _areaAccessService.CanAccessArea(user, ownAreaId);
            if (motherPlantId.HasValue && areaMap.TryGetValue(motherPlantId.Value, out var areaId))
                return _areaAccessService.CanAccessArea(user, areaId);
            return _areaAccessService.HasFullAreaAccess(user);
        }

        public async Task<bool> CanAccessAsync(ClaimsPrincipal user, int? motherPlantId, int? ownAreaId = null)
        {
            if (ownAreaId.HasValue)
                return _areaAccessService.CanAccessArea(user, ownAreaId);
            if (_areaAccessService.HasFullAreaAccess(user))
                return true;
            if (!motherPlantId.HasValue)
                return false;
            var motherPlant = await _motherPlantRepo.GetByIdAsync(motherPlantId.Value);
            return motherPlant != null && _areaAccessService.CanAccessArea(user, motherPlant.AreaId);
        }

        // Phase 1: the Area a record belongs to (same rule as CanAccess):
        // its own Area when set, else its Mother Plant's Area.
        public async Task<int?> ResolveAreaIdAsync(int? motherPlantId, int? ownAreaId = null)
        {
            if (ownAreaId.HasValue)
                return ownAreaId;
            if (!motherPlantId.HasValue)
                return null;
            return (await _motherPlantRepo.GetByIdAsync(motherPlantId.Value))?.AreaId;
        }

        public async Task<List<T>> FilterAsync<T>(ClaimsPrincipal user, IEnumerable<T> items, Func<T, int?> motherPlantIdSelector, Func<T, int?>? ownAreaSelector = null)
        {
            if (_areaAccessService.HasFullAreaAccess(user))
                return items.ToList();
            var map = await GetAreaMapAsync();
            return items.Where(i => CanAccess(user, map, motherPlantIdSelector(i), ownAreaSelector?.Invoke(i))).ToList();
        }
    }
}
