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

        [BindProperty]
        public int EditSpeciesId { get; set; }
        [BindProperty]
        public string EditSpeciesName { get; set; }
        [BindProperty]
        public string EditSpeciesScientificName { get; set; }

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
                await _plantSpeciesRepo.AddPlantSpecies(NewSpeciesTypeId, NewSpeciesName, NewSpeciesScientificName);
            }
            return RedirectToPage();
        }

        // Handler for editing an existing plant species
        public async Task<IActionResult> OnPostEditSpeciesAsync()
        {
            if (EditSpeciesId > 0 && !string.IsNullOrWhiteSpace(EditSpeciesName))
            {
                await _plantSpeciesRepo.UpdatePlantSpecies(EditSpeciesId, EditSpeciesName, EditSpeciesScientificName);
            }
            return RedirectToPage();
        }
    }
}
