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
    // chosen bed, records a transplant of the ENTERED quantity into COMPLETE cavity trays; the rest of
    // the bed is recorded as waste in the same save (2026-10-14 -- the separate bed-loss form is retired).
    // Page access: BedSowing.Transplant (FeatureAuthorizationConventions); BedSowingRepository
    // re-checks, inside the transaction, that the user holds it for THIS bed's Area.
    // 2026-10-19: three steps on this one page -- 1) the list of beds (no bedId), 2) the transplant details of the chosen bed
    // (?bedId=, the list hidden, the source bed shown on top), 3) Review (OnPostReview): the server re-reads the bed and plans
    // the transplant with the SAME rules as the save (BedSowingRules.PlanTransplant, the bed's own targets, Ready Days, growing
    // material, empty trays) and shows the summary; Confirm posts the same values to OnPostTransplant, where
    // BedSowingRepository.TransplantAsync re-checks everything under the bed's lock, once per form token. Back (OnPostEdit)
    // returns to step 2 with the entered values and the same token.
    public class TransplantModel : PageModel
    {
        public const string MaterialField = "Entry.GrowingMaterialTypeId";

        private readonly BedSowingRepository _repo;
        private readonly PlantSpeciesRepository _species;
        private readonly MaterialStockRepository _materialRepo;
        private readonly TrayStockRepository _trayStockRepo;

        public TransplantModel(BedSowingRepository repo, PlantSpeciesRepository species, MaterialStockRepository materialRepo, TrayStockRepository trayStockRepo)
        {
            _repo = repo;
            _trayStockRepo = trayStockRepo;
            _species = species;
            _materialRepo = materialRepo;
        }

        [BindProperty(SupportsGet = true)]
        public int? BedId { get; set; }

        [BindProperty]
        public BedTransplantRequest Entry { get; set; } = new();

        // 2026-10-22: step 1's Variety (dbo.BedSowings.SpeciesId) and Sowing Date (dbo.BedSowings.SowingDate, a DATE) filters.
        // They only narrow the list of beds (BedSowingRepository.ListAsync, inside the user's Transplanter Areas) and are carried
        // through steps 2 and 3 so Change bed / Cancel / after Confirm return to the same filtered list. The chosen bed is
        // looked up WITHOUT them -- a filter never decides, hides or changes the bed being transplanted.
        [BindProperty(SupportsGet = true)]
        public int? Variety { get; set; }
        [BindProperty(SupportsGet = true)]
        public DateTime? SowingDate { get; set; }
        public SowingListFilter Filter { get; private set; } = SowingListFilter.None;
        public List<VarietyOption> VarietyOptions { get; private set; } = new();
        public string? FilterNote { get; private set; }
        // Route values that keep the filters on links / redirects (null values are left out of the URL).
        public object FilterRoute(int? bedId = null)
            => new { bedId, variety = Variety, sowingDate = SowingDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) };

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

        // 2026-10-19: step 3 (Review) -- what the server worked out from the posted values and the bed as it is now.
        public bool Reviewing { get; private set; }
        public BedSowingRules.TransplantPlan? ReviewPlan { get; private set; }
        public BedSowingLocation? ReviewTarget { get; private set; }
        public DateTime? ReviewReadyDate { get; private set; }
        public MaterialSowingPreview? ReviewMaterial { get; private set; }
        public decimal? ReviewTraysAvailable { get; private set; }

        private BedSowingRepository.Actor Actor => new(User.GetUserId(), User.Identity?.Name, User.IsFullAccess());

        public async Task OnGetAsync()
        {
            Entry = new BedTransplantRequest { TransplantDate = AppClock.Today, SubmissionToken = Guid.NewGuid() };
            await LoadAsync();
            if (Targets.Count == 1)
                Entry.TargetPolyhouseId = Targets[0].PolyhouseId;   // preselected
            // Default Quantity to Transplant: everything left; the page lowers it to the complete trays of
            // the chosen cavity (remaining / cavity x cavity) until the Transplanter changes it.
            if (Selected != null)
                Entry.Quantity = Selected.RemainingQuantity;
        }

        // Growing material preview for the target Polyhouse (Area from the Bed Sowing location).
        public async Task<JsonResult> OnGetMaterialPreviewAsync(int polyhouseId, string? cavityType, decimal quantity)
        {
            var target = (await _repo.GetLocationsAsync()).FirstOrDefault(l => l.PolyhouseId == polyhouseId);
            if (target == null)
                return new JsonResult(new { applicable = false, trays = 0, materials = Array.Empty<object>() });
            return new JsonResult(await GrowingMaterialForm.PreviewAsync(_materialRepo, target.AreaId, target.PolyhouseId, cavityType, quantity));
        }

        // Empty trays of the cavity at every transplant target of a bed this user may see (the bed's own Area; a Mother
        // Plant Area resolves to its one Area pool), the chosen target's balance, and the complete trays needed.
        public async Task<JsonResult> OnGetTrayAvailabilityAsync(int bedId, string? cavityType, int? polyhouseId, decimal quantity, bool wasteOnly)
        {
            var eligible = new List<TrayLocation>();
            if (await _repo.SchemaReadyAsync())
            {
                var bed = (await _repo.ListAsync(await _repo.TransplantAreaIdsAsync(User.GetUserId(), User.IsFullAccess()), openOnly: true))
                    .FirstOrDefault(b => b.Id == bedId);
                if (bed != null)
                    eligible = (await _repo.GetTransplantTargetsAsync(bed.AreaId)).Select(t => new TrayLocation(t.AreaId, t.PolyhouseId)).ToList();
            }
            var selected = eligible.FirstOrDefault(l => l.PolyhouseId == polyhouseId);
            return new JsonResult(await TrayAvailabilityForm.BuildAsync(_trayStockRepo, cavityType, eligible, selected,
                wasteOnly ? 0 : TrayAvailabilityForm.CompleteTrays(quantity, cavityType)));
        }

        // Step 3: review before saving. Nothing is written; every check here is repeated by the save.
        public async Task<IActionResult> OnPostReviewAsync()
        {
            ModelState.Clear();
            BedId = Entry.BedSowingId;
            var materialPostError = GrowingMaterialForm.ValidatePosted(HttpContext.Request, MaterialField);
            await LoadAsync();
            if (Selected == null)
            {
                Error = "That bed is not waiting for transplanting in your Areas.";
                BedId = null;
                return Page();
            }
            if (!CanOperate)
                return Page();
            var error = materialPostError ?? await ReviewAsync(Selected);
            if (error != null)
            {
                Error = error;
                return Page();
            }
            Reviewing = true;
            return Page();
        }

        // Back from Review to the details, keeping the entered values and the form's token.
        public async Task<IActionResult> OnPostEditAsync()
        {
            ModelState.Clear();
            BedId = Entry.BedSowingId;
            await LoadAsync();
            if (Selected == null)
                BedId = null;
            return Page();
        }

        // The plan the save would record, from the bed as it is NOW (remaining re-read) -- null = fine, else why not.
        private async Task<string?> ReviewAsync(Models.BedSowing bed)
        {
            var (ok, plan, error) = BedSowingRules.PlanTransplant(bed.RemainingQuantity, Entry.Quantity, Entry.CavityType, Entry.OutputTrays,
                Entry.TransplantDate, bed.SowingDate, AppClock.Today, Entry.CloseAsWaste, Entry.WasteReason);
            if (!ok)
                return error;
            ReviewPlan = plan;
            if (!plan!.MakesTrayBatch)
                return null;
            // The target only from this bed's own list (never the posted Area); the same message as the save.
            ReviewTarget = Targets.FirstOrDefault(t => t.PolyhouseId == Entry.TargetPolyhouseId);
            if (ReviewTarget == null)
                return "Transplant into the Bed Sowing Polyhouse of this bed's Area.";
            ReviewReadyDate = TrayBatchRules.ReadyDate(Entry.TransplantDate, ReadyDays);
            if (!ReviewReadyDate.HasValue)
                return string.Format(TrayBatchRules.NoReadyDaysMessageFormat, bed.SpeciesName?.Trim());
            var trays = (await _trayStockRepo.GetCavityAvailabilityAsync(Entry.CavityType!, new[] { new TrayLocation(ReviewTarget.AreaId, ReviewTarget.PolyhouseId) }))
                .FirstOrDefault();
            ReviewTraysAvailable = trays?.Available ?? 0;
            if (ReviewTraysAvailable < plan.Trays)
                return $"Only {QuantityFormat.Qty(ReviewTraysAvailable.Value)} empty {Entry.CavityType} trays are at {ReviewTarget.PolyhouseName}; this transplant needs {plan.Trays}.";
            // Growing material: required in a Main Office Polyhouse, never elsewhere (MaterialStockRepository.ValidateSowingMaterialAsync).
            var materialRequired = ReviewTarget.AreaType == MaterialStockRules.MainOfficeAreaType;
            if (!materialRequired)
                return Entry.GrowingMaterialTypeId is > 0 ? MaterialStockRules.NotAtThisLocationMessage : null;
            if (Entry.GrowingMaterialTypeId is not > 0)
                return MaterialStockRules.MaterialRequiredMessage;
            var (_, materials) = await _materialRepo.GetSowingPreviewAsync(ReviewTarget.AreaId, ReviewTarget.PolyhouseId, Entry.CavityType, plan.Trays);
            ReviewMaterial = materials.FirstOrDefault(m => m.MaterialTypeId == Entry.GrowingMaterialTypeId);
            if (ReviewMaterial == null)
                return MaterialStockRules.MaterialRequiredMessage;
            if (!ReviewMaterial.QuantityPerTray.HasValue)
                return $"No consumption rule is set for {ReviewMaterial.MaterialName} in {Entry.CavityType} trays.";
            if (!ReviewMaterial.Enough)
                return $"Not enough {ReviewMaterial.MaterialName} at {ReviewTarget.PolyhouseName}: {QuantityFormat.Qty(ReviewMaterial.Required)} {ReviewMaterial.Unit.ToLowerInvariant()} needed, {QuantityFormat.Qty(ReviewMaterial.Available)} available.";
            return null;
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
            return RedirectToPage(FilterRoute(Entry.BedSowingId));   // the GET checks the filter values again
        }

        // The filter values from the request, checked on the server: the variety must be one of the open beds' varieties in the
        // user's own Areas (the dropdown's options), the date a real calendar date.
        private async Task LoadFilterAsync(IReadOnlyCollection<int>? areas)
        {
            var invalidDate = ModelState.TryGetValue(nameof(SowingDate), out var dateState) && dateState.Errors.Count > 0;
            VarietyOptions = await _repo.ListOpenVarietiesAsync(areas);
            (Filter, FilterNote) = SowingListFilter.Normalize(Variety, SowingDate, VarietyOptions.Select(v => v.Id));
            if (invalidDate)
                FilterNote = FilterNote == null ? SowingListFilter.InvalidDateMessage : FilterNote + " " + SowingListFilter.InvalidDateMessage;
            (Variety, SowingDate) = (Filter.SpeciesId, Filter.SowingDate);
        }

        private async Task<string> CreateModelMaterialTextAsync(int sowingId)
            => sowingId > 0 ? await SeedSowing.CreateModel.MaterialUsedTextAsync(_materialRepo, sowingId) : "";

        private async Task LoadAsync()
        {
            SchemaReady = await _repo.SchemaReadyAsync();
            if (!SchemaReady)
                return;
            var areas = await _repo.TransplantAreaIdsAsync(User.GetUserId(), User.IsFullAccess());
            await LoadFilterAsync(areas);
            // the chosen bed: from the user's open beds, never through the list filters
            if (BedId is > 0)
                Selected = (await _repo.ListAsync(areas, openOnly: true)).FirstOrDefault(b => b.Id == BedId);
            // step 1's list (only shown while no bed is chosen): the filters AND-ed onto the same Area scope, in the query
            if (Selected == null)
                Beds = await _repo.ListAsync(areas, openOnly: true, filter: Filter);
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
            }
        }
    }
}
