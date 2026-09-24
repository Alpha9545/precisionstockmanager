using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using LabRequestModel = PlantStockManager.Models.LabRequest;

namespace PlantStockManager.Pages.Production.LabRequest
{
    public class CreateModel : PageModel
    {
        private readonly LabRequestRepository _labRequestRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(
            LabRequestRepository labRequestRepo,
            PottedPlantStockRepository pottedPlantStockRepo,
            EmployeeRepository employeeRepo,
            AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _labRequestRepo = labRequestRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _employeeRepo = employeeRepo;
        }

        // F1: a Lab Request belongs to the Area of the stock pool its sample
        // came from (Request.AreaId, server-derived). Previously every page
        // here trusted the id alone. Access = that Area (AreaAccessService),
        // OR the existing Phase 14 lab permissions -- the LabWorker role is
        // deliberately NOT Area-scoped (Phase 17/B: "assignments with no
        // AreaId, e.g. Admin/Management/LabWorker"), so lab staff keep
        // working across Areas exactly as designed.
        private bool CanAccessLabRequest(int? areaId, bool write)
            => _areaAccessService.CanAccessArea(User, areaId)
               || User.HasPermission("Lab.Enter")
               || (!write && User.HasPermission("Lab.View"));

        [BindProperty]
        public LabRequestModel Request { get; set; } = new();

        // Only pools with Available quantity > 0 are offered -- a
        // sample can never exceed what's actually available.
        public List<PlantStockManager.Models.PottedPlantStock> StockPools { get; set; } = new();
        public List<Employee> PersonOptions { get; set; } = new();

        public async Task OnGetAsync()
        {
            Request.SentDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("Request.LabRequestCode");
            ModelState.Remove("Request.CreatedBy");
            ModelState.Remove("Request.Status");
            ModelState.Remove("Request.SpeciesId");   // server-derived from the selected pool
            ModelState.Remove("Request.PotSize");     // server-derived
            ModelState.Remove("Request.AreaId");      // server-derived

            if (Request.PottedPlantStockId <= 0)
                ModelState.AddModelError("Request.PottedPlantStockId", "Species / Pot Size / Area is required.");
            if (Request.SentQuantity <= 0)
                ModelState.AddModelError("Request.SentQuantity", "Sent Quantity must be greater than zero.");
            if (string.IsNullOrWhiteSpace(Request.LabName))
                ModelState.AddModelError("Request.LabName", "Lab Name is required.");

            // F1: the sample is deducted from the chosen pool -- check that
            // pool's ACTUAL Area (read from the DB, not the form).
            if (ModelState.IsValid)
            {
                var pool = await _pottedPlantStockRepo.GetByIdAsync(Request.PottedPlantStockId);
                if (pool == null || !CanAccessLabRequest(pool.AreaId, write: true))
                    ModelState.AddModelError("Request.PottedPlantStockId", "You are not authorized to send a sample from the selected stock.");
            }

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            Request.CreatedBy = User.Identity?.Name ?? "System";
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message, _) = await _labRequestRepo.InsertAsync(Request, userId);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Lab Request.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Lab Request {Request.LabRequestCode} recorded. Sample deducted from stock.";
            return RedirectToPage("/Production/LabRequest/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            var allStock = await _pottedPlantStockRepo.GetAllAsync();
            StockPools = allStock.Where(s => s.AvailableQuantity > 0 && CanAccessLabRequest(s.AreaId, write: true)).ToList(); // F1
            PersonOptions = await _employeeRepo.GetAllActiveUsers();
        }
    }
}
