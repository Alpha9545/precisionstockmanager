using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
   // [Authorize(Policy = "Admin")]

    public class PlantModel : PageModel
    {
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;

        public PlantModel(PlantTypeRepository plantTypeRepo, PlantSpeciesRepository plantSpeciesRepo)
        {
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
        }

        public List<PlantTypeSpeciesViewModel> PlantTypeSpeciesList { get; set; } = new();

        // 2026-10-15: inactive (retired "DUP-...") varieties are listed only with this filter on.
        [BindProperty(SupportsGet = true)]
        public bool ShowInactive { get; set; }
        public int InactiveCount { get; set; }

        // For Plant Type Add/Edit
        [BindProperty]
        public string NewPlantTypeName { get; set; }
        [BindProperty]
        public int EditTypeId { get; set; }
        [BindProperty]
        public string EditTypeName { get; set; }

        // For Plant Species Add/Edit
        [BindProperty]
        public int NewSpeciesTypeId { get; set; }
        [BindProperty]
        public string NewSpeciesName { get; set; }
        [BindProperty]
        public string NewSpeciesScientificName { get; set; }
        // Phase 24 (Phase J): the variety-specific "days from Sowing to
        // expected readiness" master value that SeedSowingRepository
        // now reuses to compute ExpectedReadyDate. Optional -- a species
        // left blank here simply falls back to Phase I's original
        // manual-entry behavior on the Sow Seed page.
        [BindProperty]
        public int? NewSpeciesReadyStockDays { get; set; }

        [BindProperty]
        public string? NewSpeciesColor { get; set; }

        [BindProperty]
        public string? EditSpeciesColor { get; set; }

        [BindProperty]
        public int EditSpeciesId { get; set; }
        [BindProperty]
        public string EditSpeciesName { get; set; }
        [BindProperty]
        public string EditSpeciesScientificName { get; set; }
        [BindProperty]
        public int? EditSpeciesReadyStockDays { get; set; }

        public async Task OnGetAsync()
        {
            await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            var plantTypes = await _plantTypeRepo.GetAllPlantTypes();
            PlantTypeSpeciesList.Clear();
            InactiveCount = 0;
            foreach (var type in plantTypes)
            {
                var all = await _plantSpeciesRepo.GetSpeciesByPlantType(type.Id, includeInactive: true);
                InactiveCount += all.Count(s => !s.IsActive);
                var species = ShowInactive ? all : all.Where(s => s.IsActive).ToList();
                if (ShowInactive)
                    foreach (var dup in all.Where(s => !s.IsActive && PlantStockManager.Services.PlantSpeciesNameRules.IsMergedDuplicate(s.Name)))
                        if (await _plantSpeciesRepo.FindMergedIntoAsync(dup.Id) is { } k)
                            MergedInto[dup.Id] = k.Name;
                PlantTypeSpeciesList.Add(new PlantTypeSpeciesViewModel
                {
                    PlantType = type,
                    Species = species
                });
            }
        }

        // Handler for adding a new plant type
        public async Task<IActionResult> OnPostAddPlantTypeAsync()
        {
            if (!string.IsNullOrWhiteSpace(NewPlantTypeName))
            {
                await _plantTypeRepo.AddPlantType(NewPlantTypeName);
            }
            return RedirectToPage();
        }

        // Handler for editing an existing plant type
        public async Task<IActionResult> OnPostEditPlantTypeAsync()
        {
            if (EditTypeId > 0 && !string.IsNullOrWhiteSpace(EditTypeName))
            {
                await _plantTypeRepo.UpdatePlantType(EditTypeId, EditTypeName);
            }
            return RedirectToPage();
        }

        // Handler for adding a new plant species
        public async Task<IActionResult> OnPostAddSpeciesAsync()
        {
            if (NewSpeciesTypeId > 0 && !string.IsNullOrWhiteSpace(NewSpeciesName))
            {
                // Phase 24: reject a nonsensical (<= 0) Ready Days value
                // at the same layer every other closed/validated field in
                // this app is checked -- never persisted to the master.
                if (NewSpeciesReadyStockDays.HasValue && NewSpeciesReadyStockDays.Value <= 0)
                {
                    TempData["Error"] = "Ready Stock Days must be greater than zero when provided.";
                    return RedirectToPage();
                }
                if (PlantStockManager.Services.PlantSpeciesNameRules.IsMergedDuplicate(NewSpeciesName))
                {
                    TempData["Error"] = $"Variety not added: names starting with \"{PlantStockManager.Services.PlantSpeciesNameRules.MergedPrefix}\" are reserved for merged duplicates.";
                    return RedirectToPage();
                }
                var existing = await _plantSpeciesRepo.AddPlantSpecies(NewSpeciesTypeId, NewSpeciesName, NewSpeciesScientificName, NewSpeciesReadyStockDays, NewSpeciesColor);
                if (existing is { } e)
                {
                    TempData["Error"] = $"Variety not added: '{e.Name}' already exists (Id {e.Id}{(e.PlantTypeName == null ? "" : ", " + e.PlantTypeName.Trim())}). "
                        + "Names are compared ignoring capital letters and extra spaces. Use the existing variety, or enter a different name.";
                    return RedirectToPage();
                }
            }
            return RedirectToPage();
        }

        // Handler for editing an existing plant species
        public async Task<IActionResult> OnPostEditSpeciesAsync()
        {
            if (EditSpeciesId > 0 && !string.IsNullOrWhiteSpace(EditSpeciesName))
            {
                if (EditSpeciesReadyStockDays.HasValue && EditSpeciesReadyStockDays.Value <= 0)
                {
                    TempData["Error"] = "Ready Stock Days must be greater than zero when provided.";
                    return RedirectToPage();
                }
                // Note (Phase J's own "historical date" requirement):
                // changing this value here only ever affects FUTURE
                // Sowings. SeedSowingRepository.InsertAsync copies the
                // ReadyStockDays value it read at the moment of sowing
                // onto the SeedSowings row itself, so no past Sowing's
                // ExpectedReadyDate/ReadyStockDays is touched by this edit.
                // 2026-10-15: a merged duplicate ("DUP-...") is history only -- it cannot be edited, and no variety may be
                // renamed to the reserved prefix (that would make it look merged and block reactivation).
                var current = await _plantSpeciesRepo.GetByIdAsync(EditSpeciesId);
                if (current != null && PlantStockManager.Services.PlantSpeciesNameRules.IsMergedDuplicate(current.Name))
                {
                    TempData["Error"] = "Variety not saved: merged duplicates (\"DUP-...\") are kept unchanged for history.";
                    return RedirectToPage(new { ShowInactive = true });
                }
                if (PlantStockManager.Services.PlantSpeciesNameRules.IsMergedDuplicate(EditSpeciesName))
                {
                    TempData["Error"] = $"Variety not saved: names starting with \"{PlantStockManager.Services.PlantSpeciesNameRules.MergedPrefix}\" are reserved for merged duplicates.";
                    return RedirectToPage();
                }
                var clash = await _plantSpeciesRepo.UpdatePlantSpecies(EditSpeciesId, EditSpeciesName, EditSpeciesScientificName, EditSpeciesReadyStockDays, EditSpeciesColor);
                if (clash is { } c)
                {
                    TempData["Error"] = $"Variety not saved: another variety is already named '{c.Name}' (Id {c.Id}{(c.PlantTypeName == null ? "" : ", " + c.PlantTypeName.Trim())}). "
                        + "Names are compared ignoring capital letters and extra spaces. Choose a different name.";
                    return RedirectToPage();
                }
            }
            return RedirectToPage();
        }



        public async Task<IActionResult> OnPostDeletePlantTypeAsync(int id)
        {
            bool deleted = await _plantTypeRepo.DeletePlantType(id);

            if (!deleted)
            {
                TempData["Error"] = "Cannot delete. Plant Type is used in Seed Entries.";
            }

            return RedirectToPage();
        }

        // 2026-10-15 (owner rule: no physical deletes): the variety's Delete is replaced by Deactivate (IsActive = 0),
        // refused with a clear message while active stock / bookings / open batches still use it.
        public async Task<IActionResult> OnPostDeactivateSpeciesAsync(int id)
        {
            var (ok, message) = await _plantSpeciesRepo.DeactivatePlantSpecies(id);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(ok ? new { ShowInactive = true } : null);
        }

        // 2026-10-15: brings a normally DEACTIVATED variety back into every dropdown. Refused for a merged duplicate
        // ("This variety was merged into ...") and while an active variety has the same name.
        public async Task<IActionResult> OnPostReactivateSpeciesAsync(int id)
        {
            var (ok, message) = await _plantSpeciesRepo.ReactivatePlantSpecies(id);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(new { ShowInactive = true });
        }

        // For the Variety master's inactive rows: what each merged duplicate went into (Id -> kept name).
        public Dictionary<int, string> MergedInto { get; set; } = new();

    }
}
