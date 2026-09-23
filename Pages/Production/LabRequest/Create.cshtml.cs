using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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

        public CreateModel(
            LabRequestRepository labRequestRepo,
            PottedPlantStockRepository pottedPlantStockRepo,
            EmployeeRepository employeeRepo)
        {
            _labRequestRepo = labRequestRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _employeeRepo = employeeRepo;
        }

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
            StockPools = allStock.Where(s => s.AvailableQuantity > 0).ToList();
            PersonOptions = await _employeeRepo.GetAllActiveUsers();
        }
    }
}
