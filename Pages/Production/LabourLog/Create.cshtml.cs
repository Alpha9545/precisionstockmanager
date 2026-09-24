using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using LabourLogModel = PlantStockManager.Models.LabourLog;

namespace PlantStockManager.Pages.Production.LabourLog
{
    public class CreateModel : PageModel
    {
        private readonly LabourLogRepository _labourLogRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(LabourLogRepository labourLogRepo, EmployeeRepository employeeRepo, AreaRepository areaRepo, AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _labourLogRepo = labourLogRepo;
            _employeeRepo = employeeRepo;
            _areaRepo = areaRepo;
        }

        [BindProperty]
        public LabourLogModel Log { get; set; } = new();

        public List<Employee> Workers { get; set; } = new();
        public List<Area> Areas { get; set; } = new();

        public async Task OnGetAsync()
        {
            Log.WorkDate = DateTime.Today;
            Log.WageType = "Daily";
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("Log.LabourLogCode");
            ModelState.Remove("Log.CreatedBy");
            ModelState.Remove("Log.Status");
            ModelState.Remove("Log.TotalWage");

            if (Log.WorkerId <= 0)
                ModelState.AddModelError("Log.WorkerId", "Worker is required.");
            if (string.IsNullOrWhiteSpace(Log.WorkType))
                ModelState.AddModelError("Log.WorkType", "Work Type is required.");
            if (Log.WageRate <= 0)
                ModelState.AddModelError("Log.WageRate", "Wage Rate must be greater than zero.");
            if (Log.UnitsWorked <= 0)
                ModelState.AddModelError("Log.UnitsWorked", "Units Worked must be greater than zero.");

            // F1: the posted AreaId must be an Area the user is assigned to.
            if (Log.AreaId.HasValue && !_areaAccessService.CanAccessArea(User, Log.AreaId))
                ModelState.AddModelError("Log.AreaId", "You are not authorized to record labour for the selected Area.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            Log.CreatedBy = User.Identity?.Name ?? "System";
            var (success, message, _) = await _labourLogRepo.InsertAsync(Log);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Labour Log.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Labour Log {Log.LabourLogCode} recorded successfully.";
            return RedirectToPage("/Production/LabourLog/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            Workers = await _employeeRepo.GetAllActiveUsers();
            Areas = _areaAccessService.FilterByArea(User, await _areaRepo.GetAllAreas(), a => (int?)a.Id); // F1
        }
    }
}
