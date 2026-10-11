using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Pages.Production.SeedSowing;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.BedSowing
{
    // 2026-10-12: the Bed Sowing Transplanter's page. Lists the beds still waiting in the Areas
    // where the user holds BedSowing.Transplant (all Areas for a System Administrator) and, for the
    // chosen bed, records a transplant into COMPLETE cavity trays or a bed loss.
    // Page access: BedSowing.Transplant (FeatureAuthorizationConventions); BedSowingRepository
    // re-checks, inside the transaction, that the user holds it for THIS bed's Area.
    public class TransplantModel : PageModel
    {
        public const string MaterialField = "Entry.GrowingMaterialTypeId";

        private readonly BedSowingRepository _repo;
        private readonly PlantSpeciesRepository _species;
        private readonly MaterialStockRepository _materialRepo;

        public TransplantModel(BedSowingRepository repo, PlantSpeciesRepository species, MaterialStockRepository materialRepo)
        {
            _repo = repo;
            _species = species;
            _materialRepo = materialRepo;
        }

        [BindProperty(SupportsGet = true)]
        public int? BedId { get; set; }

        [BindProperty]
        public BedTransplantRequest Entry { get; set; } = new();

        [BindProperty] public decimal LossQuantity { get; set; }
        [BindProperty] public DateTime LossDate { get; set; }
        [BindProperty] public string? LossReason { get; set; }
        [BindProperty] public string? LossRemarks { get; set; }
        [BindProperty] public Guid? LossToken { get; set; }

        public bool SchemaReady { get; private set; }
        public List<Models.BedSowing> Beds { get; private set; } = new();
        public Models.BedSowing? Selected { get; private set; }
        public List<BedSowingLocation> Targets { get; private set; } = new();
        // The selected bed's variety Ready Days (preview only: Ready Date = transplant date + these).
        public int? ReadyDays { get; private set; }
        public GrowingMaterialViewModel GrowingMaterial { get; private set; } = new();
        public IReadOnlyList<string> CavityTypes => DirectSowingRules.CavityTypes;
        public string? Error { get; private set; }
        // Only the Bed Sowing Transplanter of the selected bed's Area gets the forms. A System
        // Administrator can open the page and see the beds, but never transplants (no exception path).
        public bool CanOperate { get; private set; }

        private BedSowingRepository.Actor Actor => new(User.GetUserId(), User.Identity?.Name, User.IsFullAccess());

        public async Task OnGetAsync()
        {
            Entry = new BedTransplantRequest { TransplantDate = AppClock.Today, SubmissionToken = Guid.NewGuid() };
            LossDate = AppClock.Today;
            LossToken = Guid.NewGuid();
            await LoadAsync();
            if (Targets.Count == 1)
                Entry.TargetPolyhouseId = Targets[0].PolyhouseId;   // preselected
            if (Selected != null)
                Entry.Quantity = Selected.RemainingQuantity;          // default: all that is left in the bed
        }

        // Growing material preview for the target Polyhouse (Area from the Bed Sowing location).
        public async Task<JsonResult> OnGetMaterialPreviewAsync(int polyhouseId, string? cavityType, decimal quantity)
        {
            var target = (await _repo.GetLocationsAsync()).FirstOrDefault(l => l.PolyhouseId == polyhouseId);
            if (target == null)
                return new JsonResult(new { applicable = false, trays = 0, materials = Array.Empty<object>() });
            return new JsonResult(await GrowingMaterialForm.PreviewAsync(_materialRepo, target.AreaId, target.PolyhouseId, cavityType, quantity));
        }

        public async Task<IActionResult> OnPostTransplantAsync()
        {
            ModelState.Clear();
            var materialPostError = GrowingMaterialForm.ValidatePosted(HttpContext.Request, MaterialField);
            if (materialPostError != null)
            {
                Error = materialPostError;
                BedId = Entry.BedSowingId;
                await LoadAsync();
                return Page();
            }
            var (ok, message, _, sowingId, duplicate) = await _repo.TransplantAsync(Entry, Actor);
            if (!ok)
            {
                Error = message;
                BedId = Entry.BedSowingId;
                await LoadAsync();
                return Page();
            }
            // The server's own account of what was recorded (trays, any remainder recorded as waste, Ready Date).
            TempData["Success"] = duplicate
                ? "This transplant was already saved from this form -- nothing was recorded twice."
                : message + (sowingId > 0 ? " The tray batch is now waiting in Sowing Approvals." : "") + await CreateModelMaterialTextAsync(sowingId);
            return RedirectToPage(new { bedId = Entry.BedSowingId });
        }

        public async Task<IActionResult> OnPostLossAsync()
        {
            ModelState.Clear();
            var (ok, message, _, duplicate) = await _repo.RecordLossAsync(BedId ?? 0, LossQuantity, LossDate, LossReason, LossRemarks, LossToken, Actor);
            if (!ok)
            {
                Error = message;
                Entry = new BedTransplantRequest { TransplantDate = AppClock.Today, SubmissionToken = Guid.NewGuid() };
                await LoadAsync();
                return Page();
            }
            TempData["Success"] = duplicate
                ? "This loss was already saved from this form -- nothing was recorded twice."
                : $"Bed loss of {QuantityFormat.Qty(LossQuantity)} seedlings recorded.";
            return RedirectToPage(new { bedId = BedId });
        }

        private async Task<string> CreateModelMaterialTextAsync(int sowingId)
            => sowingId > 0 ? await SeedSowing.CreateModel.MaterialUsedTextAsync(_materialRepo, sowingId) : "";

        private async Task LoadAsync()
        {
            SchemaReady = await _repo.SchemaReadyAsync();
            if (!SchemaReady)
                return;
            var areas = await _repo.TransplantAreaIdsAsync(User.GetUserId(), User.IsFullAccess());
            Beds = await _repo.ListAsync(areas, openOnly: true);
            if (BedId is > 0)
                Selected = Beds.FirstOrDefault(b => b.Id == BedId);
            if (Selected != null)
            {
                Entry.BedSowingId = Selected.Id;
                CanOperate = (await _repo.TransplantAreaIdsAsync(User.GetUserId(), isFullAccess: false))!.Contains(Selected.AreaId);
                Targets = await _repo.GetTransplantTargetsAsync(Selected.AreaId);
                ReadyDays = (await _species.GetByIdAsync(Selected.SpeciesId))?.ReadyStockDays is int d && d > 0 ? d : null;
                GrowingMaterial = new GrowingMaterialViewModel
                {
                    Materials = await _materialRepo.GetTypesAsync(),
                    SelectedId = Entry.GrowingMaterialTypeId,
                    FieldName = MaterialField
                };
                if (Entry.SubmissionToken is not { } t || t == Guid.Empty)
                    Entry.SubmissionToken = Guid.NewGuid();
                if (Entry.TransplantDate == default)
                    Entry.TransplantDate = AppClock.Today;
                if (LossToken is not { } lt || lt == Guid.Empty)
                    LossToken = Guid.NewGuid();
                if (LossDate == default)
                    LossDate = AppClock.Today;
            }
        }
    }
}
