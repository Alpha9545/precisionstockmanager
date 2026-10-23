using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.Labour
{
    // Daily Labour Attendance (Labour.Enter, or Labour.Attendance -- attendance only): the 9:00 AM and 2:00 PM headcounts
    // of one location and date (Main Office Areas per Polyhouse, every other Area
    // Area-wise; Outlet Areas only once 2026-10-26_LabourAttendanceOutlet.sql is applied -- DailyLabourRules). Choosing a date + location
    // loads what is already recorded; saving inserts a time not yet recorded and
    // CORRECTS a recorded one (every correction is kept in the audit history).
    // Today and earlier dates (India time, AppClock) can be entered or corrected;
    // entry is not tied to the clock time. Area access is AreaAccessService's
    // regular rule, checked on every GET and POST -- posted ids are never trusted.
    public class AttendanceModel : PageModel
    {
        private readonly DailyLabourAttendanceRepository _attendanceRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly DatabaseHelper _db;
        private bool? _outletAllowed;

        public AttendanceModel(DailyLabourAttendanceRepository attendanceRepo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess,
            DatabaseHelper db)
        {
            _db = db;
            _attendanceRepo = attendanceRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty(SupportsGet = true)] public DateTime? LabourDate { get; set; }
        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? PolyhouseId { get; set; }
        [BindProperty] public int? Male9 { get; set; }
        [BindProperty] public int? Female9 { get; set; }
        [BindProperty] public string? Remarks9 { get; set; }
        [BindProperty] public int? Male2 { get; set; }
        [BindProperty] public int? Female2 { get; set; }
        [BindProperty] public string? Remarks2 { get; set; }

        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public Area? SelectedArea { get; set; }
        public Polyhouse? SelectedPolyhouse { get; set; }
        // True once date + location are chosen, valid and allowed: the counts can be entered.
        public bool LocationReady { get; set; }
        public bool ManualRecordExists { get; set; }
        public List<DailyLabourAttendanceEntry> Entries { get; set; } = new();
        public List<DailyLabourAttendanceChange> Changes { get; set; } = new();
        public DateTime Today => AppClock.Today;
        public bool SelectedAreaIsMainOffice => SelectedArea != null && DailyLabourRules.RequiresPolyhouse(SelectedArea.AreaType);

        public DailyLabourAttendanceEntry? Entry9 => Entries.FirstOrDefault(e => e.AttendanceTime == DailyLabourRules.Morning);
        public DailyLabourAttendanceEntry? Entry2 => Entries.FirstOrDefault(e => e.AttendanceTime == DailyLabourRules.Afternoon);

        // The saved day, as Attendance History shows it.
        public DailyLabourDay SavedDay => new()
        {
            LabourDate = LabourDate ?? Today,
            Male9 = Entry9?.MaleCount, Female9 = Entry9?.FemaleCount,
            Male2 = Entry2?.MaleCount, Female2 = Entry2?.FemaleCount,
        };

        public async Task OnGetAsync()
        {
            LabourDate ??= Today;
            await LoadAreasAsync();
            if (!AreaId.HasValue && Areas.Count == 1)
                AreaId = Areas[0].Id;
            await ResolveLocationAsync(addErrors: false);
            if (!LocationReady)
                return;

            await LoadDayAsync();
            Male9 = Entry9?.MaleCount; Female9 = Entry9?.FemaleCount; Remarks9 = Entry9?.Remarks;
            Male2 = Entry2?.MaleCount; Female2 = Entry2?.FemaleCount; Remarks2 = Entry2?.Remarks;
        }

        // Dependent dropdown: the Polyhouses of a Main Office Area the user may
        // enter labour for; empty for any other Area (labour is Area-wise).
        public async Task<JsonResult> OnGetPolyhousesAsync(int areaId)
            => new JsonResult((await PolyhousesForAsync(areaId)).Select(p => new { id = p.Id, name = p.Name }));

        public async Task<IActionResult> OnPostAsync()
        {
            NormalizeCountBindingErrors();
            await LoadAreasAsync();

            var dateError = DailyLabourRules.ValidateDate(LabourDate, Today);
            if (dateError != null)
                ModelState.AddModelError(nameof(LabourDate), dateError);
            await ResolveLocationAsync(addErrors: true);
            if (!LocationReady || dateError != null)
            {
                LocationReady = false;
                return Page();
            }

            await LoadDayAsync();
            if (ManualRecordExists)
            {
                ModelState.AddModelError(string.Empty, DailyLabourRules.LegacyRecordExistsMessage);
                return Page();
            }

            var inputs = new List<DailyLabourAttendanceInput>();
            ValidateTime(DailyLabourRules.Morning, Male9, Female9, Remarks9, Entry9 != null, inputs);
            ValidateTime(DailyLabourRules.Afternoon, Male2, Female2, Remarks2, Entry2 != null, inputs);
            if (ModelState.IsValid && inputs.Count == 0)
                ModelState.AddModelError(string.Empty, DailyLabourRules.AttendanceRequiredMessage);
            if (!ModelState.IsValid)
                return Page();

            DailyLabourAttendanceRepository.SaveResult result;
            try
            {
                result = await _attendanceRepo.SaveAsync(LabourDate!.Value.Date, SelectedArea!.Id, SelectedPolyhouse?.Id, inputs, User.GetUserId());
            }
            catch (SqlException ex) when (ex.Number is >= 51711 and <= 51714)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                return Page();
            }
            if (result.Outcome == DailyLabourAttendanceRepository.SaveOutcome.ManualRecordExists)
            {
                ModelState.AddModelError(string.Empty, DailyLabourRules.LegacyRecordExistsMessage);
                return Page();
            }

            TempData["Success"] = Describe(result);
            return RedirectToPage(new { labourDate = LabourDate.Value.ToString("yyyy-MM-dd"), areaId = SelectedArea.Id, polyhouseId = SelectedPolyhouse?.Id });
        }

        private void ValidateTime(string code, int? male, int? female, string? remarks, bool alreadyRecorded, List<DailyLabourAttendanceInput> inputs)
        {
            var key = code == DailyLabourRules.Morning ? nameof(Male9) : nameof(Male2);
            if (ModelState.TryGetValue(key, out var e1) && e1.Errors.Count > 0)
                return;
            var otherKey = code == DailyLabourRules.Morning ? nameof(Female9) : nameof(Female2);
            if (ModelState.TryGetValue(otherKey, out var e2) && e2.Errors.Count > 0)
                return;

            var errors = DailyLabourRules.ValidateAttendanceTime(male, female, code, alreadyRecorded);
            foreach (var error in errors)
                ModelState.AddModelError(key, error);
            var remarksError = DailyLabourRules.ValidateRemarks(remarks);
            if (remarksError != null)
                ModelState.AddModelError(code == DailyLabourRules.Morning ? nameof(Remarks9) : nameof(Remarks2), remarksError);
            if (errors.Count == 0 && remarksError == null && male.HasValue && female.HasValue)
                inputs.Add(new DailyLabourAttendanceInput(code, male.Value, female.Value, remarks));
        }

        private string Describe(DailyLabourAttendanceRepository.SaveResult result)
        {
            static string Times(IEnumerable<string> codes) => string.Join(" and ", codes.Select(DailyLabourRules.AttendanceTimeLabel));
            var parts = new List<string>();
            if (result.Created.Count > 0) parts.Add($"{Times(result.Created)} attendance saved");
            if (result.Corrected.Count > 0) parts.Add($"{Times(result.Corrected)} attendance corrected");
            if (parts.Count == 0) parts.Add("No changes -- the attendance was already recorded with these values");
            var where = $"{LabourDate:dd-MM-yyyy}, {SelectedArea!.Name}" + (SelectedPolyhouse != null ? $", {SelectedPolyhouse.Name}" : "");
            return $"{string.Join("; ", parts)} for {where}.";
        }

        // Validates the chosen Area / Polyhouse against the database and the user's
        // Area access. addErrors = false on GET (an incomplete choice is not an error yet).
        private async Task ResolveLocationAsync(bool addErrors)
        {
            LocationReady = false;
            SelectedArea = AreaId > 0 ? await _areaRepo.GetAreaById(AreaId.Value) : null;
            SelectedPolyhouse = PolyhouseId > 0 ? await _polyhouseRepo.GetByIdAsync(PolyhouseId.Value) : null;
            var authorized = SelectedArea != null && _areaAccess.CanAccessRequiredArea(User, SelectedArea.Id);
            var outletAllowed = await OutletAllowedAsync();
            if (SelectedArea != null && (!authorized || !SelectedArea.IsActive || !DailyLabourRules.IsAttendanceArea(SelectedArea.AreaType, outletAllowed)) && !addErrors)
            {
                // A hand-typed URL for another Area shows nothing of that Area.
                SelectedArea = null;
                AreaId = null;
            }
            if (!addErrors && (SelectedArea == null || !DailyLabourRules.RequiresPolyhouse(SelectedArea.AreaType)))
            {
                PolyhouseId = null;
                SelectedPolyhouse = null;
            }
            Polyhouses = await PolyhousesForAsync(SelectedArea?.Id);

            var error = DailyLabourRules.ValidateLocation(SelectedArea, authorized, PolyhouseId, SelectedPolyhouse, outletAllowed);
            if (error != null)
            {
                if (addErrors)
                    ModelState.AddModelError(error.Value.Field, error.Value.Message);
                return;
            }
            if (!DailyLabourRules.RequiresPolyhouse(SelectedArea!.AreaType))
                SelectedPolyhouse = null;
            LocationReady = LabourDate.HasValue && LabourDate.Value.Date <= Today;
        }

        private async Task LoadDayAsync()
        {
            var date = LabourDate!.Value.Date;
            Entries = await _attendanceRepo.GetEntriesAsync(date, SelectedArea!.Id, SelectedPolyhouse?.Id);
            Changes = await _attendanceRepo.GetChangesAsync(date, SelectedArea.Id, SelectedPolyhouse?.Id);
            ManualRecordExists = await _attendanceRepo.ManualRecordExistsAsync(date, SelectedArea.Id, SelectedPolyhouse?.Id);
        }

        // "10.5" / "abc" fail int binding with the framework's generic message;
        // replace it with the business message.
        private void NormalizeCountBindingErrors()
        {
            foreach (var (key, label) in new[] { (nameof(Male9), "9:00 AM Male"), (nameof(Female9), "9:00 AM Female"), (nameof(Male2), "2:00 PM Male"), (nameof(Female2), "2:00 PM Female") })
            {
                if (ModelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0)
                {
                    entry.Errors.Clear();
                    ModelState.AddModelError(key, $"{label}: {DailyLabourRules.WholeNumberMessage}");
                }
            }
        }

        // 2026-10-23: Outlet Areas take attendance too once 2026-10-26_LabourAttendanceOutlet.sql is applied.
        private async Task<bool> OutletAllowedAsync() => _outletAllowed ??= await LabourAttendanceOutletSchema.ReadyAsync(_db);

        // Active Areas this user may enter labour for (Outlet only with the migration; a full-access
        // user sees every one).
        private async Task LoadAreasAsync()
        {
            var outletAllowed = await OutletAllowedAsync();
            Areas = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && DailyLabourRules.IsAttendanceArea(a.AreaType, outletAllowed) && _areaAccess.CanAccessRequiredArea(User, a.Id))
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
