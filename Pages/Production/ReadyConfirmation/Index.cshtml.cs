using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.ReadyConfirmation
{
    // Phase 25 (Phase K): the eligible-batches list -- every ACTIVE
    // ('Sown') Seed Sowing that still has remaining un-confirmed
    // quantity, Area-scoped exactly like every other Production list
    // page (AreaAccessService, never UI-only filtering). Deliberately
    // not restricted to only currently-alerted Sowings -- see
    // SeedSowingRepository.GetReadyForConfirmationAsync's own comment
    // for why. Read-only: nothing on this page ever writes anything.
    public class IndexModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public IndexModel(SeedSowingRepository seedSowingRepo, SeedlingAreaScope areaAccessService)
        {
            _seedSowingRepo = seedSowingRepo;
            _areaAccessService = areaAccessService;
        }

        public List<SeedSowingModel> EligibleSowings { get; set; } = new();

        public async Task OnGetAsync()
        {
            var candidates = await _seedSowingRepo.GetReadyForConfirmationAsync();

            // Same Area-scoping convention as ReadyAlerts/Index.cshtml.cs
            // and every other Production list page: a user without
            // full/cross-Area access only ever sees batches for Areas
            // they can actually access.
            EligibleSowings = _areaAccessService.HasFullAreaAccess(User)
                ? candidates
                : candidates.Where(s => _areaAccessService.CanAccessArea(User, s.AreaId)).ToList();
        }
    }
}
