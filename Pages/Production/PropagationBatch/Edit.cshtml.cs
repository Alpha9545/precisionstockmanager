using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PropagationBatchModel = PlantStockManager.Models.PropagationBatch;

namespace PlantStockManager.Pages.Production.PropagationBatch
{
    public class EditModel : PageModel
    {
        private readonly PropagationBatchRepository _propagationBatchRepo;
        private readonly AreaRepository _areaRepo;
        private readonly SupervisorSelectionService _supervisors;
        private readonly MotherPlantAreaScope _areaScope;

        public EditModel(PropagationBatchRepository propagationBatchRepo, AreaRepository areaRepo, SupervisorSelectionService supervisors, MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _propagationBatchRepo = propagationBatchRepo;
            _areaRepo = areaRepo;
            _supervisors = supervisors;
        }

        // The Cutting Delivery / Mother Plant / Species linkage and the
        // planted Quantity are fixed once a Propagation Batch exists --
        // editing lets you record the propagation OUTCOME (Survived /
        // Loss / Status / Area / people / remarks) as the batch
        // progresses through its lifecycle, but never reassign which
        // delivery it draws from or how much was originally planted.
        [BindProperty]
        public PropagationBatchModel PropagationBatch { get; set; } = new();

        public List<Area> Areas { get; set; } = new();
        // Phase 1: Production Area supervisors (plus the stored one),
        // narrowed client-side to the chosen Area.
        public List<SupervisorOption> Supervisors { get; set; } = new();

        private int? _currentSupervisorId;
        private string? _currentSupervisorName;

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _propagationBatchRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/PropagationBatch/Index");

            // F1: Area scope via the record's Mother Plant (MotherPlantAreaScope).
            if (!await _areaScope.CanAccessAsync(User, existing.MotherPlantId, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Propagation Batch record.";
                return RedirectToPage("/Production/PropagationBatch/Index");
            }

            PropagationBatch = existing;
            _currentSupervisorId = existing.SupervisorId;
            _currentSupervisorName = existing.SupervisorName;
            await LoadDropdownsAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("PropagationBatch.BatchCode");
            ModelState.Remove("PropagationBatch.CreatedBy");
            ModelState.Remove("PropagationBatch.CuttingDeliveryId");
            ModelState.Remove("PropagationBatch.MotherPlantId");
            ModelState.Remove("PropagationBatch.SpeciesId");
            ModelState.Remove("PropagationBatch.Quantity");

            if (PropagationBatch.SurvivedQuantity < 0 || PropagationBatch.LossQuantity < 0)
                ModelState.AddModelError(string.Empty, "Survived and Loss quantities cannot be negative.");

            if (PropagationBatch.AreaId.HasValue)
            {
                var area = await _areaRepo.GetAreaById(PropagationBatch.AreaId.Value);
                if (area == null || !area.IsActive)
                    ModelState.AddModelError("PropagationBatch.AreaId", "Selected Area is not valid or is inactive.");
            }

            // Re-load the immutable linkage fields (and Quantity) from
            // the DB record -- never trust hidden-field values from the
            // client for these.
            var existing = await _propagationBatchRepo.GetByIdAsync(PropagationBatch.Id);
            if (existing == null)
            {
                ModelState.AddModelError(string.Empty, "Propagation Batch record not found.");
                await LoadDropdownsAsync();
                return Page();
            }
            // F1: re-check Area scope against the STORED record (the posted
            // Id is otherwise the only input deciding which record changes).
            if (!await _areaScope.CanAccessAsync(User, existing.MotherPlantId, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Propagation Batch record.";
                return RedirectToPage("/Production/PropagationBatch/Index");
            }
            _currentSupervisorId = existing.SupervisorId;
            _currentSupervisorName = existing.SupervisorName;
            PropagationBatch.CuttingDeliveryId = existing.CuttingDeliveryId;
            PropagationBatch.MotherPlantId = existing.MotherPlantId;
            PropagationBatch.SpeciesId = existing.SpeciesId;
            PropagationBatch.Quantity = existing.Quantity;

            // F1: moving the batch to a different Area requires access to it too.
            if (PropagationBatch.AreaId.HasValue && PropagationBatch.AreaId != existing.AreaId
                && !await _areaScope.CanAccessAsync(User, null, PropagationBatch.AreaId))
                ModelState.AddModelError("PropagationBatch.AreaId", "You are not authorized to use the selected Area.");

            if ((PropagationBatch.Status == "ReadyForPotting" || PropagationBatch.Status == "Completed")
                && !PropagationBatchModel.CanReconcile(PropagationBatch.SurvivedQuantity, PropagationBatch.LossQuantity, PropagationBatch.Quantity))
            {
                ModelState.AddModelError(string.Empty, "Survived + Loss must add up exactly to the batch Quantity before it can move to Ready for Potting or Completed.");
            }

            // Phase 1: the supervisor must be eligible for the batch's Area (its
            // own Area, else the Mother Plant's); an unchanged value is kept.
            var supervisorError = await _supervisors.ValidateAsync(
                SupervisorKind.ProductionArea, await _areaScope.ResolveAreaIdAsync(existing.MotherPlantId, PropagationBatch.AreaId),
                PropagationBatch.SupervisorId, existing.SupervisorId);
            if (supervisorError != null)
                ModelState.AddModelError("PropagationBatch.SupervisorId", supervisorError);

            if (!ModelState.IsValid)
            {
                PropagationBatch.CuttingDeliveryCode = existing.CuttingDeliveryCode;
                PropagationBatch.MotherPlantCode = existing.MotherPlantCode;
                PropagationBatch.SpeciesName = existing.SpeciesName;
                await LoadDropdownsAsync();
                return Page();
            }

            PropagationBatch.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _propagationBatchRepo.UpdateAsync(PropagationBatch);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Propagation Batch.");
                PropagationBatch.CuttingDeliveryCode = existing.CuttingDeliveryCode;
                PropagationBatch.MotherPlantCode = existing.MotherPlantCode;
                PropagationBatch.SpeciesName = existing.SpeciesName;
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Propagation Batch {PropagationBatch.BatchCode} updated successfully.";
            return RedirectToPage("/Production/PropagationBatch/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            Areas = await _areaRepo.GetAllAreas();
            Supervisors = SupervisorRules.IncludeCurrent(
                await _supervisors.OptionsAsync(SupervisorKind.ProductionArea), _currentSupervisorId, _currentSupervisorName);
        }
    }
}
