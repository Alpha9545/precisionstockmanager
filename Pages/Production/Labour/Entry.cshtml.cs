using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.Labour
{
    // Manual Daily Labour record (dbo.DailyLabourCounts) -- RETIRED for new
    // records on 2026-10-06: Daily Labour is now entered as 9:00 AM / 2:00 PM
    // attendance (Production/Labour/Attendance), so a request to create a new
    // manual record (GET or POST without ?id=) is sent there and saves nothing.
    // The older manual records stay readable (Attendance History, Labour Records)
    // and only a full-access user (System Administrator) can still correct one
    // (?id=): the four counts and the remark; the record's date and location are
    // its identity. Area access is AreaAccessService's regular rule, checked on
    // every GET and POST -- posted ids are never trusted.
    public class EntryModel : PageModel
    {
        private static readonly (string Key, string Label)[] CountFields =
        {
            (nameof(MaleFullDay), "Male Full Day"),
            (nameof(MaleHalfDay), "Male Half Day"),
            (nameof(FemaleFullDay), "Female Full Day"),
            (nameof(FemaleHalfDay), "Female Half Day"),
        };

        private readonly DailyLabourCountRepository _labourRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;

        public EntryModel(DailyLabourCountRepository labourRepo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess)
        {
            _labourRepo = labourRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty(SupportsGet = true)] public int? Id { get; set; }
        [BindProperty] public DateTime? LabourDate { get; set; }
        [BindProperty] public int? AreaId { get; set; }
        [BindProperty] public int? PolyhouseId { get; set; }
        [BindProperty] public int? MaleFullDay { get; set; }
        [BindProperty] public int? MaleHalfDay { get; set; }
        [BindProperty] public int? FemaleFullDay { get; set; }
        [BindProperty] public int? FemaleHalfDay { get; set; }
        [BindProperty] public string? Remarks { get; set; }

        public DailyLabourCount? Existing { get; set; }
        public bool IsEdit => Existing != null;
        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public DateTime Today => DateTime.Today;
        public bool SelectedAreaIsMainOffice => Areas.FirstOrDefault(a => a.Id == AreaId) is { } a && DailyLabourRules.RequiresPolyhouse(a.AreaType);

        public async Task<IActionResult> OnGetAsync(bool loaded = false)
        {
            if (!Id.HasValue)
                return RetiredForNewRecords();

            var load = await LoadExistingAsync(Id.Value);
            if (load != null)
                return load;
            if (loaded)
                ViewData["Info"] = DailyLabourRules.AlreadyExistsMessage;
            return Page();
        }

        // New manual records are no longer created: Daily Attendance replaces them.
        private IActionResult RetiredForNewRecords()
        {
            TempData["Success"] = DailyLabourRules.ManualEntryRetiredMessage;
            return RedirectToPage("/Production/Labour/Attendance");
        }

        // Dependent dropdown: the Polyhouses of a Main Office Area the user may
        // enter labour for; empty for any other Area (labour is Area-wise).
        public async Task<JsonResult> OnGetPolyhousesAsync(int areaId)
            => new JsonResult((await PolyhousesForAsync(areaId)).Select(p => new { id = p.Id, name = p.Name }));

        // "Is there already a record for this date + location?" -- lets the
        // page open the existing record before the supervisor types counts.
        public async Task<JsonResult> OnGetFindAsync(DateTime date, int areaId, int? polyhouseId)
        {
            if (areaId <= 0 || !_areaAccess.CanAccessRequiredArea(User, areaId) || !await IsLabourAreaAsync(areaId))
                return new JsonResult(new { id = (int?)null });
            var id = await _labourRepo.FindIdAsync(date, areaId, polyhouseId > 0 ? polyhouseId : null);
            return new JsonResult(new { id });
        }

        public async Task<IActionResult> OnPostAsync()
        {
            NormalizeCountBindingErrors();

            if (Id.HasValue)
                return await SaveEditAsync(Id.Value);
            return RetiredForNewRecords();
        }

        private async Task<IActionResult> SaveEditAsync(int id)
        {
            var load = await LoadExistingAsync(id);
            if (load != null)
                return load;

            ValidateCounts();
            ValidateRemarks();
            if (!ModelState.IsValid)
                return Page();

            var counts = BuildCounts();
            if (!await _labourRepo.UpdateCountsAsync(id, counts, User.GetUserId()))
                return NotFound();

            TempData["Success"] = $"Labour updated for {Describe(Existing!.LabourDate, Existing.AreaName, Existing.PolyhouseName, Existing.PolyhouseId)}: "
                + $"{counts.TotalWorkers} workers, {counts.LabourDays:0.#} labour days.";
            return RedirectToPage("/Production/Labour/Index");
        }

        // Loads the record being edited into the page (its own date/location,
        // never the posted ones). Returns a result when the page must not be shown.
        private async Task<IActionResult?> LoadExistingAsync(int id)
        {
            // Correcting a retired manual record is an administrator action.
            if (!User.IsFullAccess())
                return Forbid();
            Existing = await _labourRepo.GetByIdAsync(id);
            if (Existing == null)
                return NotFound();
            if (!_areaAccess.CanAccessRequiredArea(User, Existing.AreaId) || !DailyLabourRules.IsLabourArea(Existing.AreaType))
                return Forbid();

            LabourDate = Existing.LabourDate;
            AreaId = Existing.AreaId;
            PolyhouseId = Existing.PolyhouseId;
            if (Request.Method == HttpMethods.Get)
            {
                MaleFullDay = Existing.MaleFullDay;
                MaleHalfDay = Existing.MaleHalfDay;
                FemaleFullDay = Existing.FemaleFullDay;
                FemaleHalfDay = Existing.FemaleHalfDay;
                Remarks = Existing.Remarks;
            }
            return null;
        }

        // "10.5" / "abc" fail int binding with the framework's generic message;
        // replace it with the business message.
        private void NormalizeCountBindingErrors()
        {
            foreach (var (key, label) in CountFields)
            {
                if (ModelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0)
                {
                    entry.Errors.Clear();
                    ModelState.AddModelError(key, $"{label}: {DailyLabourRules.WholeNumberMessage}");
                }
            }
        }

        private void ValidateCounts()
        {
            var values = new[] { MaleFullDay, MaleHalfDay, FemaleFullDay, FemaleHalfDay };
            for (var i = 0; i < CountFields.Length; i++)
            {
                var (key, label) = CountFields[i];
                if (ModelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0)
                    continue;   // already reported as "not a whole number"
                var error = DailyLabourRules.ValidateCount(values[i], label);
                if (error != null)
                    ModelState.AddModelError(key, error);
            }
        }

        private void ValidateRemarks()
        {
            var error = DailyLabourRules.ValidateRemarks(Remarks);
            if (error != null)
                ModelState.AddModelError(nameof(Remarks), error);
        }

        private DailyLabourCount BuildCounts() => new()
        {
            Remarks = string.IsNullOrWhiteSpace(Remarks) ? null : Remarks.Trim(),
            MaleFullDay = MaleFullDay!.Value,
            MaleHalfDay = MaleHalfDay!.Value,
            FemaleFullDay = FemaleFullDay!.Value,
            FemaleHalfDay = FemaleHalfDay!.Value,
        };

        private static string Describe(DateTime date, string? areaName, string? polyhouseName, int? polyhouseId)
            => $"{date:dd/MM/yyyy}, {areaName}" + (polyhouseId.HasValue ? $", {polyhouseName}" : "");

        // Active, non-Outlet Areas this user may enter labour for (a full-access
        // user sees every one).
        private async Task LoadAreasAsync()
        {
            Areas = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && DailyLabourRules.IsLabourArea(a.AreaType) && _areaAccess.CanAccessRequiredArea(User, a.Id))
                .OrderBy(a => a.Name)
                .ToList();
        }

        private async Task<bool> IsLabourAreaAsync(int areaId)
            => await _areaRepo.GetAreaById(areaId) is { } area && DailyLabourRules.IsLabourArea(area.AreaType);

        private async Task<List<Polyhouse>> PolyhousesForAsync(int? areaId)
        {
            if (!(areaId > 0) || !_areaAccess.CanAccessRequiredArea(User, areaId))
                return new List<Polyhouse>();
            var area = await _areaRepo.GetAreaById(areaId.Value);
            if (area == null || !area.IsActive || !DailyLabourRules.RequiresPolyhouse(area.AreaType))
                return new List<Polyhouse>();
            return (await _polyhouseRepo.GetByAreaIdAsync(area.Id)).OrderBy(p => p.Name).ToList();
        }
    }
}
