using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using LabourLogModel = PlantStockManager.Models.LabourLog;

namespace PlantStockManager.Pages.Production.LabourLog
{
    public class EditModel : PageModel
    {
        private readonly LabourLogRepository _labourLogRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        public EditModel(LabourLogRepository labourLogRepo, EmployeeRepository employeeRepo, AreaRepository areaRepo, AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _labourLogRepo = labourLogRepo;
            _employeeRepo = employeeRepo;
            _areaRepo = areaRepo;
        }

        // WorkerId/WageRate/UnitsWorked/TotalWage are immutable after
        // creation -- this is a wage record, not a draft. Only
        // Area/WorkType/Reference/Supervisor/Remarks can be edited.
        [BindProperty]
        public LabourLogModel Log { get; set; } = new();

        public List<Employee> Workers { get; set; } = new();
        public List<Area> Areas { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _labourLogRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/LabourLog/Index");
            // F1: Area scope.
            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Labour Log.";
                return RedirectToPage("/Production/LabourLog/Index");
            }

            Log = existing;
            await LoadDropdownsAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("Log.LabourLogCode");
            ModelState.Remove("Log.WorkerId");
            ModelState.Remove("Log.WageType");
            ModelState.Remove("Log.WageRate");
            ModelState.Remove("Log.UnitsWorked");
            ModelState.Remove("Log.TotalWage");
            ModelState.Remove("Log.CreatedBy");
            ModelState.Remove("Log.Status");

            var existing = await _labourLogRepo.GetByIdAsync(Log.Id);
            if (existing == null)
            {
                ModelState.AddModelError(string.Empty, "Labour Log not found.");
                await LoadDropdownsAsync();
                return Page();
            }
            // F1: check the STORED record's Area, and the new Area if changed.
            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Labour Log.";
                return RedirectToPage("/Production/LabourLog/Index");
            }
            if (Log.AreaId.HasValue && Log.AreaId != existing.AreaId && !_areaAccessService.CanAccessArea(User, Log.AreaId))
                ModelState.AddModelError("Log.AreaId", "You are not authorized to use the selected Area.");
            if (existing.Status == "Cancelled")
            {
                ModelState.AddModelError(string.Empty, "This Labour Log is already Cancelled and cannot be edited further.");
                Log = existing;
                await LoadDropdownsAsync();
                return Page();
            }
            if (string.IsNullOrWhiteSpace(Log.WorkType))
                ModelState.AddModelError("Log.WorkType", "Work Type is required.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            Log.ModifiedBy = User.Identity?.Name ?? "System";
            var (success, message) = await _labourLogRepo.UpdateDetailsAsync(Log);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Labour Log.");
                Log.LabourLogCode = existing.LabourLogCode;
                Log.WorkerId = existing.WorkerId;
                Log.WorkerName = existing.WorkerName;
                Log.WorkDate = existing.WorkDate;
                Log.WageType = existing.WageType;
                Log.WageRate = existing.WageRate;
                Log.UnitsWorked = existing.UnitsWorked;
                Log.TotalWage = existing.TotalWage;
                Log.Status = existing.Status;
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Labour Log {existing.LabourLogCode} updated successfully.";
            return RedirectToPage("/Production/LabourLog/Index");
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
            // F1: cancel previously trusted the posted id alone.
            var target = await _labourLogRepo.GetByIdAsync(id);
            if (target == null || !_areaAccessService.CanAccessArea(User, target.AreaId))
            {
                TempData["Error"] = "You are not authorized to cancel this Labour Log.";
                return RedirectToPage("/Production/LabourLog/Index");
            }

            var (success, message) = await _labourLogRepo.CancelAsync(id, User.Identity?.Name ?? "System");
            if (!success)
            {
                TempData["Error"] = message ?? "Failed to cancel Labour Log.";
                return RedirectToPage("/Production/LabourLog/Edit", new { id });
            }

            TempData["Success"] = "Labour Log cancelled.";
            return RedirectToPage("/Production/LabourLog/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            Workers = await _employeeRepo.GetAllActiveUsers();
            Areas = _areaAccessService.FilterByArea(User, await _areaRepo.GetAllAreas(), a => (int?)a.Id); // F1
        }
    }
}
