using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.BedSowing
{
    // 2026-10-22: edit an UNTOUCHED Bed Sowing in place -- Sowing Date, Variety, Seed Lot, Quantity, Remarks. The bed keeps
    // its code, Polyhouse, recorder and created time. Page access: Sowing.Enter (as Create / Cancel); the bed's Area is
    // re-checked on every request. The form carries the values it was opened with (Original*): the repository refuses
    // the save if the bed changed meanwhile or this form was already saved, so an edit is never applied twice.
    // BedSowingRepository.EditAsync reverses the original seed deduction and records the revised one in one transaction;
    // changing anything but Remarks needs 2026-10-22_BedSowingEdit.sql (TR_BedSowings_Guard).
    public class EditModel : PageModel
    {
        private readonly BedSowingRepository _repo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly SeedlingAreaScope _scope;
        private readonly DatabaseHelper _db;

        public EditModel(BedSowingRepository repo, PlantTypeRepository plantTypeRepo, PlantSpeciesRepository plantSpeciesRepo,
            SeedlingAreaScope scope, DatabaseHelper db)
        {
            _repo = repo;
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _scope = scope;
            _db = db;
        }

        // Revised values
        [BindProperty] public DateTime SowingDate { get; set; }
        [BindProperty] public int PlantTypeId { get; set; }
        [BindProperty] public int SpeciesId { get; set; }
        [BindProperty] public int SeedStockId { get; set; }
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public string? Reason { get; set; }
        // The values the form was opened with (concurrency / repeat-submit guard)
        [BindProperty] public DateTime OriginalSowingDate { get; set; }
        [BindProperty] public int OriginalSpeciesId { get; set; }
        [BindProperty] public int OriginalSeedStockId { get; set; }
        [BindProperty] public decimal OriginalQuantity { get; set; }
        [BindProperty] public string? OriginalRemarks { get; set; }

        public Models.BedSowing? Bed { get; private set; }
        public bool EditSchemaReady { get; private set; }
        public List<PlantType> PlantTypes { get; private set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var guard = await LoadAsync(id);
            if (guard != null)
                return guard;
            var b = Bed!;
            SowingDate = OriginalSowingDate = b.SowingDate;
            SpeciesId = OriginalSpeciesId = b.SpeciesId;
            SeedStockId = OriginalSeedStockId = b.SeedStockId;
            Quantity = OriginalQuantity = b.Quantity;
            Remarks = OriginalRemarks = b.Remarks;
            PlantTypeId = await _plantSpeciesRepo.GetPlantTypeIdBySpeciesId(b.SpeciesId);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            ModelState.Clear();   // validated by BedSowingRules / the repository (server-side, under lock)
            var guard = await LoadAsync(id);
            if (guard != null)
                return guard;
            if (!EditSchemaReady)
            {
                // Only Remarks are editable here (the other fields are shown read-only and not posted).
                SowingDate = OriginalSowingDate; SpeciesId = OriginalSpeciesId; SeedStockId = OriginalSeedStockId; Quantity = OriginalQuantity;
            }
            var request = new BedSowingRepository.BedEditRequest(id,
                new BedSowingRules.BedEditValues(OriginalSowingDate, OriginalSpeciesId, OriginalSeedStockId, OriginalQuantity, OriginalRemarks),
                new BedSowingRules.BedEditValues(SowingDate, SpeciesId, SeedStockId, Quantity, Remarks),
                Reason);
            var actor = new BedSowingRepository.Actor(User.GetUserId(), User.Identity?.Name, User.IsFullAccess());
            var (ok, message, changed) = await _repo.EditAsync(request, actor, areaId => _scope.CanAccessArea(User, areaId));
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, message ?? "The bed sowing could not be updated.");
                if (PlantTypeId <= 0)
                    PlantTypeId = await _plantSpeciesRepo.GetPlantTypeIdBySpeciesId(SpeciesId);
                return Page();
            }
            TempData[changed ? "Success" : "Info"] = message;
            return RedirectToPage("/Production/BedSowing/Details", new { id });
        }

        public async Task<JsonResult> OnGetVarietiesAsync(int plantTypeId)
        {
            var list = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(list.Select(v => new { id = v.Id, name = v.Name.Trim() }));
        }

        // Main Office lots of the variety with stock, plus this bed's own lot (its seeds return when the bed is edited).
        public async Task<JsonResult> OnGetSeedLotsAsync(int id, int speciesId)
        {
            var bed = await _repo.GetAsync(id);
            if (bed == null || !_scope.CanAccessArea(User, bed.AreaId))
                return new JsonResult(Array.Empty<object>());
            var lots = await _repo.GetEditLotsAsync(speciesId, id);
            return new JsonResult(lots.Select(l => new
            {
                id = l.Id, lot = string.IsNullOrEmpty(l.Lot) ? "(no lot no.)" : l.Lot, source = l.Source, available = l.AvailableForBed, current = l.Current
            }));
        }

        // null = OK to show / save; otherwise where to go instead.
        private async Task<IActionResult?> LoadAsync(int id)
        {
            Bed = await _repo.GetAsync(id);
            if (Bed == null || !_scope.CanAccessArea(User, Bed.AreaId))
                return NotFound();
            var operations = await _repo.GetOperationsAsync(id);
            var (editable, why) = BedSowingRules.ValidateEditable(Bed.Status, Bed.TransplantedQuantity, Bed.LostQuantity, operations.Count, Bed.ReadyStockDays);
            if (!editable)
            {
                TempData["Error"] = why;
                return RedirectToPage("/Production/BedSowing/Details", new { id });
            }
            EditSchemaReady = await WorkflowCorrectionsSchema.BedSowingEditAsync(_db);
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            return null;
        }
    }
}
