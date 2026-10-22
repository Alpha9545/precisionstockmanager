using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.Labour
{
    // Add / Edit Overtime (Labour.Enter). A new entry is always a NEW row -- several entries
    // on the same date and location (other periods, other tasks, overlapping times) are kept
    // separate. A double click / browser retry of the same form is stopped by
    // SubmissionTokenGuard. Editing (?id=) corrects workers, times, description and remark;
    // the entry's date and location are its identity (every correction is kept in the audit
    // history). Location rule and Area access are the Daily Attendance ones, checked on every
    // GET and POST -- posted ids are never trusted. Today and earlier dates, India time.
    public class OvertimeModel : PageModel
    {
        public const string TokenScope = "LabourOvertime";

        private readonly LabourOvertimeRepository _overtimeRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly SubmissionTokenGuard _tokens;

        public OvertimeModel(LabourOvertimeRepository overtimeRepo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo,
            AreaAccessService areaAccess, SubmissionTokenGuard tokens)
        {
            _overtimeRepo = overtimeRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
            _tokens = tokens;
        }

        [BindProperty(SupportsGet = true)] public int? Id { get; set; }
        [BindProperty] public DateTime? LabourDate { get; set; }
        [BindProperty] public int? AreaId { get; set; }
        [BindProperty] public int? PolyhouseId { get; set; }
        [BindProperty] public int? MaleWorkers { get; set; }
        [BindProperty] public int? FemaleWorkers { get; set; }
        [BindProperty] public TimeOnly? StartTime { get; set; }
        [BindProperty] public TimeOnly? EndTime { get; set; }
        [BindProperty] public string? WorkDescription { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }

        public LabourOvertimeEntry? Existing { get; set; }
        public bool IsEdit => Existing != null;
        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public DateTime Today => AppClock.Today;
        public bool SelectedAreaIsMainOffice => Areas.FirstOrDefault(a => a.Id == AreaId) is { } a && DailyLabourRules.RequiresPolyhouse(a.AreaType);

        public async Task<IActionResult> OnGetAsync()
        {
            SubmitToken = SubmissionTokenGuard.NewToken();
            if (Id.HasValue)
            {
                var load = await LoadExistingAsync(Id.Value);
                if (load != null)
                    return load;
                MaleWorkers = Existing!.MaleWorkers;
                FemaleWorkers = Existing.FemaleWorkers;
                StartTime = Existing.StartTime;
                EndTime = Existing.EndTime;
                WorkDescription = Existing.WorkDescription;
                Remarks = Existing.Remarks;
                return Page();
            }

            LabourDate = Today;
            await LoadAreasAsync();
            if (Areas.Count == 1)
                AreaId = Areas[0].Id;
            Polyhouses = await PolyhousesForAsync(AreaId);
            return Page();
        }

        // Dependent dropdown: the Polyhouses of a Main Office Area the user may enter
        // labour for; empty for any other Area (labour is Area-wise).
        public async Task<JsonResult> OnGetPolyhousesAsync(int areaId)
            => new JsonResult((await PolyhousesForAsync(areaId)).Select(p => new { id = p.Id, name = p.Name }));

        public async Task<IActionResult> OnPostAsync()
        {
            NormalizeBindingErrors();
            if (Id.HasValue)
                return await SaveEditAsync(Id.Value);

            await LoadAreasAsync();
            Polyhouses = await PolyhousesForAsync(AreaId);

            var dateError = DailyLabourRules.ValidateDate(LabourDate, Today);
            if (dateError != null)
                ModelState.AddModelError(nameof(LabourDate), dateError);

            // Location: re-loaded from the database, never taken from the form.
            var area = AreaId > 0 ? await _areaRepo.GetAreaById(AreaId.Value) : null;
            var polyhouse = PolyhouseId > 0 ? await _polyhouseRepo.GetByIdAsync(PolyhouseId.Value) : null;
            var authorized = area != null && _areaAccess.CanAccessRequiredArea(User, area.Id);
            var locationError = DailyLabourRules.ValidateLocation(area, authorized, PolyhouseId, polyhouse);
            if (locationError != null)
                ModelState.AddModelError(locationError.Value.Field, locationError.Value.Message);

            ValidateValues();
            if (!ModelState.IsValid)
                return PageWithNewToken();

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = LabourOvertimeRules.AlreadySubmittedMessage;
                // to the recorder's own entries: Overtime History is an administrator-only report since 2026-10-20
                return RedirectToPage(LabourOvertimeRules.MyEntriesPage);
            }

            int id;
            try
            {
                id = await _overtimeRepo.CreateAsync(LabourDate!.Value.Date, area!.Id,
                    DailyLabourRules.RequiresPolyhouse(area.AreaType) ? polyhouse!.Id : null, BuildInput(), User.GetUserId());
            }
            catch (SqlException ex) when (ex.Number is >= 51711 and <= 51714)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                return PageWithNewToken();
            }

            var saved = (await _overtimeRepo.GetByIdAsync(id))!;
            TempData["Success"] = $"Overtime saved: {saved.Duration}, {LabourOvertimeRules.FormatHours(saved.TotalWorkerHours)} worker-hours "
                + $"({saved.LabourDate:dd-MM-yyyy}, {saved.Location}).";
            return RedirectToPage("/Production/Labour/OvertimeDetails", new { id });
        }

        private async Task<IActionResult> SaveEditAsync(int id)
        {
            var load = await LoadExistingAsync(id);
            if (load != null)
                return load;

            ValidateValues();
            if (!ModelState.IsValid)
                return PageWithNewToken();

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = LabourOvertimeRules.AlreadySubmittedMessage;
                return RedirectToPage("/Production/Labour/OvertimeDetails", new { id });
            }

            var outcome = await _overtimeRepo.UpdateAsync(id, BuildInput(), User.GetUserId());
            if (outcome == LabourOvertimeRepository.UpdateOutcome.NotFound)
                return NotFound();
            TempData["Success"] = outcome == LabourOvertimeRepository.UpdateOutcome.Corrected
                ? "Overtime corrected. The previous values are kept in the history below."
                : "No changes -- the overtime entry already had these values.";
            return RedirectToPage("/Production/Labour/OvertimeDetails", new { id });
        }

        // The entry being edited (its own date/location, never the posted ones). Returns a
        // result when the page must not be shown.
        private async Task<IActionResult?> LoadExistingAsync(int id)
        {
            Existing = await _overtimeRepo.GetByIdAsync(id);
            if (Existing == null)
                return NotFound();
            if (!_areaAccess.CanAccessRequiredArea(User, Existing.AreaId) || !DailyLabourRules.IsLabourArea(Existing.AreaType))
                return Forbid();
            // 2026-10-20: a non-administrator corrects only an entry they recorded (checked on GET and on every POST)
            if (!LabourOvertimeRules.MayAccessEntry(User.IsFullAccess(), User.GetUserId(), Existing.CreatedById))
                return Forbid();
            LabourDate = Existing.LabourDate;
            AreaId = Existing.AreaId;
            PolyhouseId = Existing.PolyhouseId;
            return null;
        }

        private void ValidateValues()
        {
            foreach (var (field, message) in LabourOvertimeRules.Validate(MaleWorkers, FemaleWorkers, StartTime, EndTime, WorkDescription, Remarks))
            {
                if (ModelState.TryGetValue(field, out var entry) && entry.Errors.Count > 0)
                    continue;   // already reported by NormalizeBindingErrors
                ModelState.AddModelError(field, message);
            }
        }

        private LabourOvertimeInput BuildInput()
            => new(MaleWorkers!.Value, FemaleWorkers!.Value, StartTime!.Value, EndTime!.Value, WorkDescription!, Remarks);

        // A rejected form gets a fresh token so the corrected form can be sent again.
        private IActionResult PageWithNewToken()
        {
            ModelState.Remove(nameof(SubmitToken));
            SubmitToken = SubmissionTokenGuard.NewToken();
            return Page();
        }

        // "10.5" / "abc" / "25:99" fail model binding with the framework's generic message;
        // replace it with the business message.
        private void NormalizeBindingErrors()
        {
            foreach (var (key, message) in new[]
            {
                (nameof(MaleWorkers), $"Male Workers: {DailyLabourRules.WholeNumberMessage}"),
                (nameof(FemaleWorkers), $"Female Workers: {DailyLabourRules.WholeNumberMessage}"),
                (nameof(StartTime), "Start time is not a valid time."),
                (nameof(EndTime), "End time is not a valid time."),
            })
            {
                if (ModelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0)
                {
                    entry.Errors.Clear();
                    ModelState.AddModelError(key, message);
                }
            }
        }

        // Active, non-Outlet Areas this user may enter labour for (a full-access user sees every one).
        private async Task LoadAreasAsync()
        {
            Areas = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && DailyLabourRules.IsLabourArea(a.AreaType) && _areaAccess.CanAccessRequiredArea(User, a.Id))
                .OrderBy(a => a.Name)
                .ToList();
        }

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
