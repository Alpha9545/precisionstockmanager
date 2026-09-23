using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.ReadyConfirmation
{
    // Phase 25 (Phase K): the Ready Confirmation entry page. GET shows
    // the Sowing's read-only info (including its immutable, historical
    // Ready Stock Days / Expected Ready Date from Phase J/24 -- this
    // page never lets those be edited) plus the Ready Quantity input;
    // POST performs the actual confirmation via
    // ReadyConfirmationRepository.ConfirmAsync.
    //
    // Area authorization: this form posts NO AreaId field at all --
    // every check is against the Sowing's own AreaId, freshly re-read
    // from the database on every GET and every POST via
    // SeedSowingRepository.GetByIdAsync (never a cached/posted value),
    // exactly mirroring SeedSowing/Edit.cshtml.cs's own discipline.
    public class ConfirmModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly ReadyConfirmationRepository _readyConfirmationRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;

        public ConfirmModel(
            SeedSowingRepository seedSowingRepo, ReadyConfirmationRepository readyConfirmationRepo,
            EmployeeRepository employeeRepo, AreaAccessService areaAccessService)
        {
            _seedSowingRepo = seedSowingRepo;
            _readyConfirmationRepo = readyConfirmationRepo;
            _employeeRepo = employeeRepo;
            _areaAccessService = areaAccessService;
        }

        public SeedSowingModel SeedSowing { get; set; } = new();

        [BindProperty]
        public int SeedSowingId { get; set; }

        [BindProperty]
        public decimal ReadyQuantity { get; set; }

        [BindProperty]
        public int? ResponsiblePersonId { get; set; }

        [BindProperty]
        public int? SupervisorId { get; set; }

        [BindProperty]
        public string? Remarks { get; set; }

        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var sowing = await _seedSowingRepo.GetByIdAsync(id);
            if (sowing == null)
            {
                TempData["Error"] = "Seed Sowing record not found.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }
            if (!_areaAccessService.CanAccessArea(User, sowing.AreaId))
            {
                TempData["Error"] = "You are not authorized to Ready-Confirm this Sowing.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }
            if (sowing.Status != "Sown")
            {
                TempData["Error"] = $"This Sowing is '{sowing.Status}' and cannot be Ready-Confirmed.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }
            if (sowing.RemainingReadyQuantity <= 0)
            {
                TempData["Error"] = "This Sowing has already been fully Ready-Confirmed.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }

            SeedSowing = sowing;
            SeedSowingId = sowing.Id;
            await LoadDropdownsAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // Never trust a posted AreaId (there isn't one on this form
            // at all) -- always re-derive from the freshly fetched
            // Sowing, exactly like SeedSowing/Edit.cshtml.cs.
            var sowing = await _seedSowingRepo.GetByIdAsync(SeedSowingId);
            if (sowing == null)
            {
                TempData["Error"] = "Seed Sowing record not found.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }
            if (!_areaAccessService.CanAccessArea(User, sowing.AreaId))
            {
                TempData["Error"] = "You are not authorized to Ready-Confirm this Sowing.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }

            if (ReadyQuantity <= 0)
            {
                ModelState.AddModelError(string.Empty, "Ready Quantity must be greater than zero.");
                SeedSowing = sowing;
                await LoadDropdownsAsync();
                return Page();
            }
            if (ReadyQuantity > sowing.RemainingReadyQuantity)
            {
                ModelState.AddModelError(string.Empty,
                    $"Ready Quantity ({ReadyQuantity:N2}) exceeds this Sowing's remaining un-confirmed quantity ({sowing.RemainingReadyQuantity:N2}).");
                SeedSowing = sowing;
                await LoadDropdownsAsync();
                return Page();
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";

            var (success, message, _) = await _readyConfirmationRepo.ConfirmAsync(
                sowing.Id, ReadyQuantity, ResponsiblePersonId, SupervisorId, Remarks, createdBy, userId);

            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Ready Confirmation.");
                SeedSowing = sowing;
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Ready Confirmation recorded for {sowing.SowingCode}: {ReadyQuantity:N2} confirmed ready.";
            return RedirectToPage("/Production/ReadyConfirmation/History", new { id = sowing.Id });
        }

        private async Task LoadDropdownsAsync()
        {
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
