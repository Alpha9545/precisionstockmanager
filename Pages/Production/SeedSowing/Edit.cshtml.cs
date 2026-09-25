using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.SeedSowing
{
    public class EditModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        private readonly UserRoleRepository _userRoleRepo;

        // The assigned supervisor can only change while nothing is approved.
        public bool CanChangeSupervisor { get; private set; }

        public EditModel(SeedSowingRepository seedSowingRepo, EmployeeRepository employeeRepo, SeedlingAreaScope areaAccessService,
            UserRoleRepository userRoleRepo)
        {
            _userRoleRepo = userRoleRepo;
            _seedSowingRepo = seedSowingRepo;
            _employeeRepo = employeeRepo;
            _areaAccessService = areaAccessService;
        }

        // SourceSeedStockId / CavityType / QuantitySown are permanently
        // immutable -- they already moved real seed stock. PHASE J
        // CORRECTION: ExpectedReadyDate / ReadyStockDays are ALSO
        // permanently immutable after creation -- they are the
        // historical SowingDate + ReadyStockDays-at-sowing-time
        // calculation from InsertAsync, and rewriting either afterward
        // would create an inconsistent historical record. Editing here
        // only ever touches Responsible Person / Supervisor / Remarks.
        // The only way to undo the stock effect is the explicit Cancel
        // action (SeedSowingRepository.CancelAsync).
        [BindProperty]
        public SeedSowingModel SeedSowing { get; set; } = new();

        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _seedSowingRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/SeedSowing/Index");

            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Sowing record.";
                return RedirectToPage("/Production/SeedSowing/Index");
            }

            SeedSowing = existing;
            await LoadDropdownsAsync(existing);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("SeedSowing.SowingCode");
            ModelState.Remove("SeedSowing.CreatedBy");
            ModelState.Remove("SeedSowing.SourceSeedStockId");
            ModelState.Remove("SeedSowing.SpeciesId");
            ModelState.Remove("SeedSowing.AreaId");
            ModelState.Remove("SeedSowing.CavityType");
            ModelState.Remove("SeedSowing.QuantitySown");
            ModelState.Remove("SeedSowing.Status");
            // PHASE J CORRECTION: ExpectedReadyDate/ReadyStockDays are
            // now historical, immutable-after-creation values (the
            // exact SowingDate + ReadyStockDays-at-sowing-time
            // calculation from InsertAsync) -- never bound from the
            // posted form, exactly like CavityType/QuantitySown above.
            ModelState.Remove("SeedSowing.ExpectedReadyDate");
            ModelState.Remove("SeedSowing.ReadyStockDays");

            var existing = await _seedSowingRepo.GetByIdAsync(SeedSowing.Id);
            if (existing == null)
            {
                ModelState.AddModelError(string.Empty, "Seed Sowing record not found.");
                await LoadDropdownsAsync(null);
                return Page();
            }

            // PHASE J CORRECTION: force-overwrite whatever value a
            // crafted POST may have bound onto ExpectedReadyDate/
            // ReadyStockDays with the actual, already-stored historical
            // values -- never trust the posted ones. This is
            // defense-in-depth on top of the real guarantee, which is
            // that SeedSowingRepository.UpdateDetailsAsync's own UPDATE
            // statement structurally never references either column at
            // all (see its updated comment), so neither value could
            // reach the database through this call regardless.
            SeedSowing.ExpectedReadyDate = existing.ExpectedReadyDate;
            SeedSowing.ReadyStockDays = existing.ReadyStockDays;

            // Re-derive the Area from the fetched record itself -- never
            // trust a posted AreaId (there isn't one on this form, but
            // this also guards against a tampered Id routing to another
            // Area's record).
            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Sowing record.";
                return RedirectToPage("/Production/SeedSowing/Index");
            }

            if (existing.Status == "Cancelled")
            {
                ModelState.AddModelError(string.Empty, "This Sowing record is already Cancelled and cannot be edited further.");
                SeedSowing = existing;
                await LoadDropdownsAsync(existing);
                return Page();
            }

            // Once approved, the supervisor is fixed (the repository enforces
            // this too); keep the stored value whatever the form sent.
            if (!DirectSowingRules.CanChangeSupervisor(existing.Status, existing.ConfirmedReadyQuantity, existing.WastageQuantity))
                SeedSowing.SupervisorId = existing.SupervisorId;

            SeedSowing.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _seedSowingRepo.UpdateDetailsAsync(SeedSowing);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Sowing.");
                SeedSowing.SowingCode = existing.SowingCode;
                SeedSowing.SpeciesName = existing.SpeciesName;
                SeedSowing.AreaName = existing.AreaName;
                SeedSowing.CavityType = existing.CavityType;
                SeedSowing.QuantitySown = existing.QuantitySown;
                SeedSowing.Status = existing.Status;
                SeedSowing.ExpectedReadyDate = existing.ExpectedReadyDate;
                SeedSowing.ReadyStockDays = existing.ReadyStockDays;
                SeedSowing.ConfirmedReadyQuantity = existing.ConfirmedReadyQuantity;
                SeedSowing.WastageQuantity = existing.WastageQuantity;
                SeedSowing.SupervisorName = existing.SupervisorName;
                await LoadDropdownsAsync(existing);
                return Page();
            }

            TempData["Success"] = $"Sowing {existing.SowingCode} updated successfully.";
            return RedirectToPage("/Production/SeedSowing/Index");
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
            var target = await _seedSowingRepo.GetByIdAsync(id);
            if (target == null)
            {
                TempData["Error"] = "Seed Sowing record not found.";
                return RedirectToPage("/Production/SeedSowing/Index");
            }
            if (!_areaAccessService.CanAccessArea(User, target.AreaId))
            {
                TempData["Error"] = "You are not authorized to cancel this Sowing record.";
                return RedirectToPage("/Production/SeedSowing/Index");
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _seedSowingRepo.CancelAsync(id, User.Identity?.Name ?? "System", userId);
            if (!success)
            {
                TempData["Error"] = message ?? "Failed to cancel Sowing.";
                return RedirectToPage("/Production/SeedSowing/Edit", new { id });
            }

            TempData["Success"] = "Sowing cancelled. Seed Stock returned.";
            return RedirectToPage("/Production/SeedSowing/Index");
        }

        private async Task LoadDropdownsAsync(SeedSowingModel? existing)
        {
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            // Eligible approvers, never the person who recorded the sowing.
            Supervisors = (await _userRoleRepo.GetSowingApproversAsync())
                .Where(a => existing?.CreatedById == null || a.EmployeeID != existing.CreatedById)
                .ToList();
            CanChangeSupervisor = existing != null
                && DirectSowingRules.CanChangeSupervisor(existing.Status, existing.ConfirmedReadyQuantity, existing.WastageQuantity);
        }
    }
}
