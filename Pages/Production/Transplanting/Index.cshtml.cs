using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.Transplanting
{
    // Transplanting -- step 1: Ready Stock in the user's Mother Plant Areas that can be
    // transplanted (available plants > 0: received from a Main Area, an earlier
    // transplant's output, or the Area's own sowing). Read-only; the operation itself is
    // Create (Transplant.Enter), re-validated in TransplantRepository under lock.
    public class IndexModel : PageModel
    {
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly MotherPlantTransferRepository _transferRepo;
        private readonly AreaAccessService _areaAccess;   // Mother Plant Area work: always Area-scoped (no Development switch), as the Mother Plant module

        public IndexModel(ReadyStockRepository readyStockRepo, MotherPlantTransferRepository transferRepo, AreaAccessService areaAccess)
        {
            _readyStockRepo = readyStockRepo;
            _transferRepo = transferRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? PolyhouseId { get; set; }
        [BindProperty(SupportsGet = true)] public int? SpeciesId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Batch { get; set; }
        [BindProperty(SupportsGet = true)] public string? Cavity { get; set; }

        public List<ReadyStockModel> Eligible { get; private set; } = new();     // after filters
        public List<ReadyStockModel> AllEligible { get; private set; } = new();  // filter dropdown values
        public int PendingIncoming { get; private set; }

        public async Task OnGetAsync()
        {
            // 2026-10-14: stock received for Pot Production is not offered for Transplant into Trays.
            var potProduction = await _transferRepo.PotProductionBatchIdsAsync();
            AllEligible = (await _readyStockRepo.GetAllAsync())
                .Where(r => r.InMotherPlantArea && r.AvailableQuantity > 0 && _areaAccess.CanAccessArea(User, r.AreaId) && !potProduction.Contains(r.Id))
                .OrderBy(r => r.AreaName).ThenBy(r => r.PolyhouseName).ThenBy(r => r.SpeciesName).ThenBy(r => r.Id)
                .ToList();
            Cavity = DirectSowingRules.IsValidCavityType(Cavity) ? Cavity : null;
            var batch = string.IsNullOrWhiteSpace(Batch) ? null : Batch.Trim();
            Eligible = AllEligible
                .Where(r => !AreaId.HasValue || r.AreaId == AreaId)
                .Where(r => !PolyhouseId.HasValue || r.PolyhouseId == PolyhouseId)
                .Where(r => !SpeciesId.HasValue || r.SpeciesId == SpeciesId)
                .Where(r => Cavity == null || r.CavityType == Cavity)
                .Where(r => batch == null || (r.BatchNo ?? "").Contains(batch, StringComparison.OrdinalIgnoreCase)
                                          || (r.SowingCode ?? "").Contains(batch, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (User.HasPermission(MotherPlantTransfers.IndexModel.ReceivePermission))
                PendingIncoming = (await _transferRepo.ListAsync((_areaAccess.HasFullAreaAccess(User) ? null : _areaAccess.GetAccessibleAreaIds(User)), MotherPlantTransferRules.StatusPending))
                    .Count(t => _areaAccess.CanAccessArea(User, t.DestinationAreaId));
        }
    }
}
