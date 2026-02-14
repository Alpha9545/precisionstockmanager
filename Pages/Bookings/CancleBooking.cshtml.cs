using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Bookings
{
    public class CancleBookingModel : PageModel
    {
        private readonly InventoryRepository _inventoryRepository;
        private readonly PolyhouseRepository _polyhouseRepository;
        private readonly PlantTypeRepository _plantTypeRepository;
        private readonly PlantSpeciesRepository _plantSpeciesRepository;

        public List<Inventory> Inventory { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<PlantSpecies> PlantSpecies { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public int? SelectedPolyhouse { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? SelectedPlantType { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? SelectedSpecies { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? DateFrom { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? DateTo { get; set; }

        public CancleBookingModel(
            InventoryRepository inventoryRepository,
            PolyhouseRepository polyhouseRepository,
            PlantTypeRepository plantTypeRepository,
            PlantSpeciesRepository plantSpeciesRepository)
        {
            _inventoryRepository = inventoryRepository;
            _polyhouseRepository = polyhouseRepository;
            _plantTypeRepository = plantTypeRepository;
            _plantSpeciesRepository = plantSpeciesRepository;
        }


        public async Task<JsonResult> OnGetSpeciesByPlantTypeAsync(int plantTypeId)
        {
            var speciesList = await _plantSpeciesRepository.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(speciesList);
        }


        public async Task OnGetAsync()
        {
            DateFrom ??= DateTime.Today.AddDays(-7);
            DateTo ??= DateTime.Today;

            Polyhouses = await _polyhouseRepository.GetAllPolyhouses();
            PlantTypes = await _plantTypeRepository.GetAllPlantTypes();

            if (SelectedPlantType.HasValue)
            {
                PlantSpecies = await _plantSpeciesRepository.GetSpeciesByPlantType(SelectedPlantType.Value);
            }

            Inventory = await _inventoryRepository.GetAllocatedBookingsInventory(SelectedPolyhouse, SelectedPlantType, SelectedSpecies, DateFrom, DateTo);
        }
    }
}
