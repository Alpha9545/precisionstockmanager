using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.SeedEntry
{
    [Authorize]
    public class EditSowingModel : PageModel
    {
        private readonly SeedEntryRepository _seedEntryRepository;
        private readonly PolyhouseRepository _polyhouseRepository;
        private readonly PlantTypeRepository _plantTypeRepository;
        private readonly PlantSpeciesRepository _plantSpeciesRepository;
        private readonly SeedSourcesRepository _seedSourceRepository;
        private readonly EmployeeRepository _employeeRepo;

        public List<SeedEntries> SeedEntries { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<PlantSpecies> PlantSpecies { get; set; } = new();
        public List<Employee> Employees { get; set; } = new();

        [BindProperty(SupportsGet = true)] public int? SelectedPolyhouse { get; set; }
        [BindProperty(SupportsGet = true)] public int? SelectedPlantType { get; set; }
        [BindProperty(SupportsGet = true)] public int? SelectedSpecies { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? ToDate { get; set; }

        [BindProperty] public SeedEntries SeedEntry { get; set; } = new();

        public EditSowingModel(
            SeedEntryRepository seedEntryRepository,
            PolyhouseRepository polyhouseRepository,
            PlantTypeRepository plantTypeRepository,
            PlantSpeciesRepository plantSpeciesRepository,
            EmployeeRepository employeeRepo)
        {
            _seedEntryRepository = seedEntryRepository;
            _polyhouseRepository = polyhouseRepository;
            _plantTypeRepository = plantTypeRepository;
            _plantSpeciesRepository = plantSpeciesRepository;
            _employeeRepo = employeeRepo;
        }

        public async Task OnGetAsync()
        {
            Polyhouses = await _polyhouseRepository.GetAllPolyhouses();
            PlantTypes = await _plantTypeRepository.GetAllPlantTypes();
           // SeedSources = await _seedSourceRepository.GetAllSeedSources();
            Employees = await _employeeRepo.GetAllEmployeesSowing();

            if (SelectedPlantType.HasValue)
                PlantSpecies = await _plantSpeciesRepository.GetSpeciesByPlantType(SelectedPlantType.Value);

            SeedEntries = await _seedEntryRepository.GetSowingRecords(SelectedPolyhouse, SelectedPlantType, SelectedSpecies, FromDate, ToDate);
        }

        public async Task<JsonResult> OnGetSpeciesByPlantType(int plantTypeId)
        {
            var species = await _plantSpeciesRepository.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(species);
        }

        public async Task<IActionResult> OnPostEditSowingAsync()
        {
            var userName = string.IsNullOrWhiteSpace(User?.Identity?.Name) ? "System" : User.Identity.Name;
            SeedEntry.CreatedBy = userName;
            ModelState.Remove("SeedEntry.CreatedBy");

            if (!ModelState.IsValid)
            {
                return new JsonResult(new { success = false, message = "Invalid input." });
            }

            var (ok, message) = await _seedEntryRepository.UpdateSowingWithStockAdjustAsync(SeedEntry, userName);
            if (ok) return new JsonResult(new { success = true });

            return new JsonResult(new { success = false, message });
        }

        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostDeleteSowingAsync(int id)
        {
            var userName = string.IsNullOrWhiteSpace(User?.Identity?.Name) ? "System" : User.Identity.Name;
            var (ok, message) = await _seedEntryRepository.DeleteSowingWithRevertAsync(id, userName);
            if (ok) return new JsonResult(new { success = true });

            return new JsonResult(new { success = false, message });
        }
    }


}
