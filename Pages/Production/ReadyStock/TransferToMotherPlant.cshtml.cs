using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.ReadyStock
{
    // Option B for Main Area Ready Stock: send whole trays of a batch to a Mother Plant Area
    // Polyhouse for transplanting. The plants are held IN TRANSIT until the Mother Plant
    // Supervisor receives them (Production/MotherPlantTransfers). Everything is re-validated in
    // MotherPlantTransferRepository under lock; nothing posted by the browser is trusted.
    public class TransferToMotherPlantModel : PageModel
    {
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly MotherPlantTransferRepository _transferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly SeedlingAreaScope _areaAccess;
        private readonly IAuthorizationService _authorization;

        public const string Permission = "ReadyStock.TransferToMotherPlant";

        public TransferToMotherPlantModel(ReadyStockRepository readyStockRepo, MotherPlantTransferRepository transferRepo, AreaRepository areaRepo,
            PolyhouseRepository polyhouseRepo, SeedlingAreaScope areaAccess, IAuthorizationService authorization)
        {
            _readyStockRepo = readyStockRepo;
            _transferRepo = transferRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
            _authorization = authorization;
        }

        public ReadyStockModel Batch { get; set; } = new();
        public List<Area> MotherPlantAreas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public int Cavity { get; set; }
        public decimal AvailableTrays => Cavity > 0 ? Math.Floor(Batch.AvailableQuantity / Cavity) : 0;

        [BindProperty] public decimal Trays { get; set; }
        [BindProperty] public int? DestinationAreaId { get; set; }
        [BindProperty] public int? DestinationPolyhouseId { get; set; }
        [BindProperty] public DateTime TransferDate { get; set; } = AppClock.Today;
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public Guid SubmissionToken { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            if (!await LoadAsync(id)) return Denied();
            SubmissionToken = Guid.NewGuid();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            if (!await LoadAsync(id)) return Denied();
            if (!(await _authorization.AuthorizeAsync(User, Permission)).Succeeded)
                return Forbid();
            if (SubmissionToken == Guid.Empty)
                ModelState.AddModelError(string.Empty, "The form has expired. Open it again.");
            if (Trays <= 0 || !DirectSowingRules.IsWholeNumber(Trays))
                ModelState.AddModelError(nameof(Trays), "Enter a whole number of trays.");
            if (!ModelState.IsValid) return Page();

            var actor = new MotherPlantTransferRepository.Actor(User.GetUserId(), User.Identity?.Name);
            var (ok, msg, transferId, duplicate) = await _transferRepo.CreateAsync(
                new MotherPlantTransferRepository.CreateRequest(id, Trays * Cavity, DestinationAreaId, DestinationPolyhouseId, TransferDate.Date, Remarks, SubmissionToken),
                actor, areaId => _areaAccess.CanAccessArea(User, areaId));
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, msg ?? "The transfer could not be saved.");
                return Page();
            }
            TempData["Success"] = msg;
            return RedirectToPage("/Production/MotherPlantTransfers/Index", new { highlight = transferId });
        }

        private async Task<bool> LoadAsync(int id)
        {
            var batch = await _readyStockRepo.GetByIdAsync(id);
            if (batch == null || !_areaAccess.CanAccessArea(User, batch.AreaId))
                return false;
            Batch = batch;
            Cavity = batch.CavitySize ?? 0;
            MotherPlantAreas = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && a.AreaType == MotherPlantTransferRules.MotherPlantAreaType && a.Id != batch.AreaId)
                .OrderBy(a => a.Name).ToList();
            var areaIds = MotherPlantAreas.Select(a => a.Id).ToHashSet();
            Polyhouses = (await _polyhouseRepo.GetAllPolyhouses())
                .Where(p => p.AreaId.HasValue && areaIds.Contains(p.AreaId.Value)).OrderBy(p => p.Name).ToList();
            return true;
        }

        private IActionResult Denied()
        {
            TempData["Error"] = "Ready Stock batch not found, or it belongs to an Area you cannot access.";
            return RedirectToPage("/Production/ReadyStock/Index");
        }
    }
}
