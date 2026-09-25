using PlantStockManager.Data;

namespace PlantStockManager.Services
{
    // A supervisor option tagged with the kind it qualifies for.
    public sealed record KindedSupervisorOption(SupervisorKind Kind, SupervisorOption Option)
    {
        public string KindKey => SupervisorRules.KindKey(Kind);
    }

    // Phase 1: what the pages call to fill a supervisor dropdown and to
    // re-check the posted choice on save. Thin wrapper over the pure
    // SupervisorRules + UserRoleRepository.GetSupervisorCandidatesAsync, so
    // every page applies exactly the same rule.
    public class SupervisorSelectionService
    {
        private readonly UserRoleRepository _userRoleRepo;
        private readonly AreaRepository _areaRepo;

        public SupervisorSelectionService(UserRoleRepository userRoleRepo, AreaRepository areaRepo)
        {
            _userRoleRepo = userRoleRepo;
            _areaRepo = areaRepo;
        }

        // Every eligible supervisor of the kind, with their Areas.
        public async Task<List<SupervisorOption>> OptionsAsync(SupervisorKind kind, IEnumerable<int>? excludeUserIds = null)
            => SupervisorRules.Options(await _userRoleRepo.GetSupervisorCandidatesAsync(kind), excludeUserIds);

        // Supervisors of every Area-based kind, tagged with their kind (for
        // forms whose source Area may be a Production Area, Main Office or
        // Outlet; site.js shows only the kind of the chosen source Area).
        public async Task<List<KindedSupervisorOption>> AllAreaKindOptionsAsync(IEnumerable<int>? excludeUserIds = null)
        {
            var list = new List<KindedSupervisorOption>();
            foreach (var kind in SupervisorRules.AreaKinds)
                foreach (var option in await OptionsAsync(kind, excludeUserIds))
                    list.Add(new KindedSupervisorOption(kind, option));
            return list;
        }

        // The kind that supervises this Area (from its AreaType); the fallback
        // when the Area is unknown, has no type, or does not exist.
        public async Task<SupervisorKind> KindForAreaAsync(int? areaId, SupervisorKind fallback)
        {
            if (!areaId.HasValue)
                return fallback;
            var area = await _areaRepo.GetAreaById(areaId.Value);
            return SupervisorRules.KindForAreaType(area?.AreaType) ?? fallback;
        }

        // For forms whose Area is already fixed (Edit pages, confirmations):
        // the supervisors of that Area's kind who may supervise it, plus the
        // record's current supervisor (kept selectable, see IncludeCurrent).
        public async Task<List<SupervisorOption>> ForAreaAsync(
            int? areaId, SupervisorKind fallback, int? currentId = null, string? currentName = null, IEnumerable<int>? excludeUserIds = null)
        {
            var kind = await KindForAreaAsync(areaId, fallback);
            var options = (await OptionsAsync(kind, excludeUserIds)).Where(o => o.CanServe(areaId)).ToList();
            return SupervisorRules.IncludeCurrent(options, currentId, currentName);
        }

        // Same as ForAreaAsync but for a fixed kind (e.g. the Mother Plant
        // cutting chain is always supervised by Production Area supervisors).
        public async Task<List<SupervisorOption>> ForKindAsync(
            SupervisorKind kind, int? areaId, int? currentId = null, string? currentName = null, IEnumerable<int>? excludeUserIds = null)
        {
            var options = (await OptionsAsync(kind, excludeUserIds)).Where(o => o.CanServe(areaId)).ToList();
            return SupervisorRules.IncludeCurrent(options, currentId, currentName);
        }

        // Save-time check for a fixed Area, using that Area's kind.
        public async Task<string?> ValidateForAreaAsync(int? areaId, SupervisorKind fallback, int? chosenId, int? currentId = null, bool required = false)
            => await ValidateAsync(await KindForAreaAsync(areaId, fallback), areaId, chosenId, currentId, required);

        // Save-time check: null when the choice is allowed, otherwise the
        // error message. An unchanged value (currentId) is always allowed.
        public async Task<string?> ValidateAsync(
            SupervisorKind kind, int? areaId, int? chosenId, int? currentId = null, bool required = false, IEnumerable<int>? excludeUserIds = null)
        {
            var options = await OptionsAsync(kind, excludeUserIds);
            var (ok, error) = SupervisorRules.ValidateChoice(chosenId, currentId, options, areaId, kind, required);
            return ok ? null : error;
        }
    }
}
