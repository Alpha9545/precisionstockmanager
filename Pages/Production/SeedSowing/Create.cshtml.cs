using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;
using SeedStockModel = PlantStockManager.Models.SeedStock;

namespace PlantStockManager.Pages.Production.SeedSowing
{
    // "Sow Seed" -- Phase 23 (Phase I). A single-actor production event:
    // the Supervisor of a Kunjir/Kiran Area sows some of their OWN
    // already-received Seed Stock. No counter-party confirmation exists
    // or is needed (unlike Seed Issue) -- SeedSowingRepository.InsertAsync
    // consumes the source pool and records the Sowing in one step.
    public class CreateModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly SeedStockRepository _seedStockRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;

        private static readonly string[] ValidSowingAreaTypes = { "Kunjir", "Kiran" };

        public CreateModel(
            SeedSowingRepository seedSowingRepo,
            SeedStockRepository seedStockRepo,
            EmployeeRepository employeeRepo,
            AreaAccessService areaAccessService)
        {
            _seedSowingRepo = seedSowingRepo;
            _seedStockRepo = seedStockRepo;
            _employeeRepo = employeeRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public SeedSowingModel SeedSowing { get; set; } = new();

        public List<SeedStockModel> StockPools { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        // Post-review correction: the exact same closed list
        // SeedSowingRepository.InsertAsync enforces server-side (and
        // CK_SeedSowings_CavityType enforces at the database level) --
        // read from the repository's own public accessor so the
        // dropdown can never drift out of sync with what the repository
        // will actually accept.
        public IReadOnlyList<string> CavityTypes => SeedSowingRepository.CavityTypes;

        public async Task OnGetAsync()
        {
            SeedSowing.SowingDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("SeedSowing.SowingCode");
            ModelState.Remove("SeedSowing.CreatedBy");
            ModelState.Remove("SeedSowing.SpeciesId");   // server-derived from the source Seed Stock
            ModelState.Remove("SeedSowing.AreaId");       // server-derived from the source Seed Stock
            ModelState.Remove("SeedSowing.BatchNo");      // server-derived from the source Seed Stock
            ModelState.Remove("SeedSowing.Status");

            if (SeedSowing.SourceSeedStockId <= 0)
                ModelState.AddModelError("SeedSowing.SourceSeedStockId", "Source Seed Stock is required.");
            if (SeedSowing.QuantitySown <= 0)
                ModelState.AddModelError("SeedSowing.QuantitySown", "Quantity Sown must be greater than zero.");
            if (string.IsNullOrWhiteSpace(SeedSowing.CavityType))
                ModelState.AddModelError("SeedSowing.CavityType", "Cavity/Tray Type is required.");
            // Post-review correction: page-level defense-in-depth,
            // matching how other closed-set fields in this app are
            // validated at multiple layers. The dropdown only ever
            // offers these five values, but a hand-crafted POST (or a
            // future template that copies this dropdown) must not be
            // trusted just because ModelState binding succeeded --
            // SeedSowingRepository.InsertAsync and
            // CK_SeedSowings_CavityType are the true authorities, this
            // is just an earlier, friendlier rejection.
            else if (!SeedSowingRepository.CavityTypes.Contains(SeedSowing.CavityType))
                ModelState.AddModelError("SeedSowing.CavityType", $"Cavity/Tray Type must be one of: {string.Join(", ", SeedSowingRepository.CavityTypes)}.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // Re-fetch the source Seed Stock server-side and check Area
            // access BEFORE calling the repository -- a posted
            // SourceSeedStockId is never trusted to imply an Area the
            // current user is actually allowed to sow from. The
            // repository itself independently re-verifies the Area is an
            // active Kunjir/Kiran Area regardless of this check.
            var sourceStock = await _seedStockRepo.GetByIdAsync(SeedSowing.SourceSeedStockId);
            if (sourceStock == null)
            {
                ModelState.AddModelError("SeedSowing.SourceSeedStockId", "Selected Seed Stock no longer exists.");
                await LoadDropdownsAsync();
                return Page();
            }
            if (!_areaAccessService.CanAccessArea(User, sourceStock.AreaId))
            {
                ModelState.AddModelError(string.Empty, "You are not authorized to sow Seed Stock from the selected Area.");
                await LoadDropdownsAsync();
                return Page();
            }

            SeedSowing.CreatedBy = User.Identity?.Name ?? "System";
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message, _) = await _seedSowingRepo.InsertAsync(SeedSowing, userId);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Sowing.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Sowing {SeedSowing.SowingCode} recorded successfully. Seed Stock updated.";
            return RedirectToPage("/Production/SeedSowing/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            var allStock = await _seedStockRepo.GetAllAsync();
            // Only Seed Stock pools the caller can access, held at an
            // active Kunjir/Kiran Area, with something left to sow.
            // CanAccessArea already grants full-access roles (Admin/
            // Management/MainOfficeOfficer) every Area.
            StockPools = allStock
                .Where(s => _areaAccessService.CanAccessArea(User, s.AreaId)
                    && s.AvailableQuantity > 0
                    && s.AreaType != null
                    && ValidSowingAreaTypes.Contains(s.AreaType))
                .ToList();

            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
