using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
   // [Authorize(Policy = "Admin")]

    public class PlantModel : PageModel
    {
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;

        public PlantModel(PlantTypeRepository plantTypeRepo, PlantSpeciesRepository plantSpeciesRepo)
        {
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
        }

        public List<PlantTypeSpeciesViewModel> PlantTypeSpeciesList { get; set; } = new();

        // For Plant Type Add/Edit
        [BindProperty]
        public string NewPlantTypeName { get; set; }
        [BindProperty]
        public int EditTypeId { get; set; }
        [BindProperty]
        public string EditTypeName { get; set; }

        // For Plant Species Add/Edit
        [BindProperty]
        public int NewSpeciesTypeId { get; set; }
        [BindProperty]
        public string NewSpeciesName { get; set; }
        [BindProperty]
        public string NewSpeciesScientificName { get; set; }
        // Phase 24 (Phase J): the variety-specific "days from Sowing to
        // expected readiness" master value that SeedSowingRepository
        // now reuses to compute ExpectedReadyDate. Optional -- a species
        // left blank here simply falls back to Phase I's original
        // manual-entry behavior on the Sow Seed page.
        [BindProperty]
        public int? NewSpeciesReadyStockDays { get; set; }

        [BindProperty]
        public int EditSpeciesId { get; set; }
        [BindProperty]
        public string EditSpeciesName { get; set; }
        [BindProperty]
        public string EditSpeciesScientificName { get; set; }
        [BindProperty]
        public int? EditSpeciesReadyStockDays { get; set; }

        public async Task OnGetAsync()
        {
            await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            var plantTypes = await _plantTypeRepo.GetAllPlantTypes();
            PlantTypeSpeciesList.Clear();
            foreach (var type in plantTypes)
            {
                var species = await _plantSpeciesRepo.GetSpeciesByPlantType(type.Id);
                PlantTypeSpeciesList.Add(new PlantTypeSpeciesViewModel
                {
                    PlantType = type,
                    Species = species
                });
            }
        }

        // Handler for adding a new plant type
        public async Task<IActionResult> OnPostAddPlantTypeAsync()
        {
            if (!string.IsNullOrWhiteSpace(NewPlantTypeName))
            {
                await _plantTypeRepo.AddPlantType(NewPlantTypeName);
            }
            return RedirectToPage();
        }

        // Handler for editing an existing plant type
        public async Task<IActionResult> OnPostEditPlantTypeAsync()
        {
            if (EditTypeId > 0 && !string.IsNullOrWhiteSpace(EditTypeName))
            {
                await _plantTypeRepo.UpdatePlantType(EditTypeId, EditTypeName);
            }
            return RedirectToPage();
        }

        // Handler for adding a new plant species
        public async Task<IActionResult> OnPostAddSpeciesAsync()
        {
            if (NewSpeciesTypeId > 0 && !string.IsNullOrWhiteSpace(NewSpeciesName))
            {
                // Phase 24: reject a nonsensical (<= 0) Ready Days value
                // at the same layer every other closed/validated field in
                // this app is checked -- never persisted to the master.
                if (NewSpeciesReadyStockDays.HasValue && NewSpeciesReadyStockDays.Value <= 0)
                {
                    TempData["Error"] = "Ready Stock Days must be greater than zero when provided.";
                    return RedirectToPage();
                }
                await _plantSpeciesRepo.AddPlantSpecies(NewSpeciesTypeId, NewSpeciesName, NewSpeciesScientificName, NewSpeciesReadyStockDays);
            }
            return RedirectToPage();
        }

        // Handler for editing an existing plant species
        public async Task<IActionResult> OnPostEditSpeciesAsync()
        {
            if (EditSpeciesId > 0 && !string.IsNullOrWhiteSpace(EditSpeciesName))
            {
                if (EditSpeciesReadyStockDays.HasValue && EditSpeciesReadyStockDays.Value <= 0)
                {
                    TempData["Error"] = "Ready Stock Days must be greater than zero when provided.";
                    return RedirectToPage();
                }
                // Note (Phase J's own "historical date" requirement):
                // changing this value here only ever affects FUTURE
                // Sowings. SeedSowingRepository.InsertAsync copies the
                // ReadyStockDays value it read at the moment of sowing
                // onto the SeedSowings row itself, so no past Sowing's
                // ExpectedReadyDate/ReadyStockDays is touched by this edit.
                await _plantSpeciesRepo.UpdatePlantSpecies(EditSpeciesId, EditSpeciesName, EditSpeciesScientificName, EditSpeciesReadyStockDays);
            }
            return RedirectToPage();
        }



        public async Task<IActionResult> OnPostDeletePlantTypeAsync(int id)
        {
            bool deleted = await _plantTypeRepo.DeletePlantType(id);

            if (!deleted)
            {
                TempData["Error"] = "Cannot delete. Plant Type is used in Seed Entries.";
            }

            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDeleteSpeciesAsync(int id)
        {
            bool deleted = await _plantSpeciesRepo.DeletePlantSpecies(id);

            if (!deleted)
            {
                TempData["Error"] = "Cannot delete. Species is used in Seed Entries.";
            }

            return RedirectToPage();
        }

    }
}
