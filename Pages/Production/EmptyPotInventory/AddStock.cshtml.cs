using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.EmptyPotInventory
{
    // Add Empty Pot Stock: the Mother Plant Supervisor adds empty pots directly
    // to one of their own Areas (replaces the retired Main Office Purchase ->
    // Issue workflow). Page access: MotherPlant.Enter. The posted AreaId is
    // never trusted -- it must be one of the Areas this page itself offers the
    // user (server-side AreaAccess claims), and the repository re-checks the
    // Area (active, not Main Office) inside the stock transaction.
    public class AddStockModel : PageModel
    {
        private const string TokenScope = "EmptyPotAddStock";

        private readonly EmptyPotInventoryRepository _emptyPotRepo;
        private readonly PotSizeRepository _potSizeRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly SubmissionTokenGuard _tokens;

        public AddStockModel(EmptyPotInventoryRepository emptyPotRepo, PotSizeRepository potSizeRepo, AreaRepository areaRepo,
            AreaAccessService areaAccess, SubmissionTokenGuard tokens)
        {
            _emptyPotRepo = emptyPotRepo;
            _potSizeRepo = potSizeRepo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
            _tokens = tokens;
        }

        [BindProperty] public int? AreaId { get; set; }
        [BindProperty] public string? PotSize { get; set; }
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public DateTime StockDate { get; set; } = DateTime.Today;
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }

        public List<Area> Areas { get; set; } = new();
        public List<PotSize> PotSizes { get; set; } = new();

        public async Task OnGetAsync()
        {
            await LoadAsync();
            SubmitToken = SubmissionTokenGuard.NewToken();
            if (Areas.Count == 1)
                AreaId = Areas[0].Id;
        }

        public async Task<IActionResult> OnPostAsync()
        {
            await LoadAsync();

            // Server-side Area check: only an Area from the user's own list.
            var area = AreaId.HasValue ? Areas.FirstOrDefault(a => a.Id == AreaId.Value) : null;
            if (area == null || !_areaAccess.CanAccessRequiredArea(User, AreaId))
                ModelState.AddModelError(string.Empty, EmptyPotStockRules.AreaNotAllowedMessage);
            if (!string.IsNullOrWhiteSpace(Remarks) && Remarks.Trim().Length > 500)
                ModelState.AddModelError(string.Empty, "Remark can be at most 500 characters.");

            var (valid, error) = EmptyPotStockRules.ValidateAddStock(PotSize, Quantity, StockDate, DateTime.Today);
            if (!valid)
                ModelState.AddModelError(string.Empty, error!);

            if (!ModelState.IsValid)
                return Page();

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = "This form was already submitted. Check Empty Pot Stock before adding again.";
                return RedirectToPage("/Production/EmptyPotInventory/Index");
            }

            var (success, message, _) = await _emptyPotRepo.AddAreaStockAsync(
                area!.Id, PotSize, Quantity, StockDate, Remarks, User.GetUserId(), User.Identity?.Name ?? "System");
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to add the empty pots.");
                // The token was used up; issue a fresh one so the corrected form can be sent again.
                ModelState.Remove(nameof(SubmitToken));
                SubmitToken = SubmissionTokenGuard.NewToken();
                return Page();
            }

            TempData["Success"] = $"{QuantityFormat.Qty(Quantity)} x {PotSize!.Trim()} empty pots added to {area.Name}.";
            return RedirectToPage("/Production/EmptyPotInventory/Index");
        }

        private async Task LoadAsync()
        {
            PotSizes = await _potSizeRepo.GetAllAsync(activeOnly: true);
            Areas = EmptyPotStockRules.SelectableAreas(
                await _areaRepo.GetAllAreas(), _areaAccess.HasFullAreaAccess(User), _areaAccess.GetAccessibleAreaIds(User));
        }
    }
}
