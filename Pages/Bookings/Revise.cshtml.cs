using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Bookings
{
    // Phase C: booking revision with history (dbo.BookingRevisions).
    // Example: Begonia 1,000 -> Begonia 800 + Petunia 200:
    //   revise this booking to 800 (200 reserved plants are released) and add
    //   Petunia 200 as a linked split booking. The previous values are kept.
    //   Page access: Booking.Enter (FeatureAuthorizationConventions).
    public class ReviseModel : PageModel
    {
        private readonly SeedlingFulfilmentRepository _repo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;

        public ReviseModel(SeedlingFulfilmentRepository repo, PlantTypeRepository plantTypeRepo, PlantSpeciesRepository plantSpeciesRepo)
        {
            _repo = repo;
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
        }

        [BindProperty(SupportsGet = true)] public int Id { get; set; }
        [BindProperty] public int NewPlantId { get; set; }
        [BindProperty] public int NewSpeciesId { get; set; }
        [BindProperty] public int NewQuantity { get; set; }
        [BindProperty] public DateTime? NewDeliveryDate { get; set; }
        [BindProperty] public string? Reason { get; set; }
        [BindProperty] public bool AddVariety { get; set; }
        [BindProperty] public int? SplitPlantId { get; set; }
        [BindProperty] public int? SplitSpeciesId { get; set; }
        [BindProperty] public int? SplitQuantity { get; set; }

        public SeedlingBookingSummary? Booking { get; set; }
        public List<PlantType> PlantTypes { get; set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            Booking = await _repo.GetBookingAsync(Id);
            if (Booking == null)
            {
                TempData["Error"] = "Booking not found.";
                return RedirectToPage("/Bookings/Fulfilment");
            }
            if (Booking.Status != "Pending")
            {
                TempData["Error"] = $"Only Pending bookings can be revised (this one is {Booking.Status}).";
                return RedirectToPage("/Bookings/BookingDetails", new { id = Id });
            }
            NewPlantId = Booking.PlantId;
            NewSpeciesId = Booking.SpeciesId ?? 0;
            NewQuantity = Booking.Quantity;
            NewDeliveryDate = Booking.DeliveryDate;
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            return Page();
        }

        public async Task<JsonResult> OnGetVarietiesAsync(int plantTypeId)
        {
            var list = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(list.Select(v => new { id = v.Id, name = v.Name.Trim() }));
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var request = new SeedlingFulfilmentRepository.RevisionRequest
            {
                NewPlantId = NewPlantId,
                NewSpeciesId = NewSpeciesId,
                NewQuantity = NewQuantity,
                NewDeliveryDate = NewDeliveryDate,
                Reason = Reason ?? string.Empty,
                SplitPlantId = AddVariety ? SplitPlantId : null,
                SplitSpeciesId = AddVariety ? SplitSpeciesId : null,
                SplitQuantity = AddVariety ? SplitQuantity : null
            };
            var (ok, message) = await _repo.ReviseAsync(Id, request, new SeedlingFulfilmentRepository.Actor(User.Identity?.Name, User.GetUserId()));
            if (ok)
            {
                TempData["Success"] = message;
                return RedirectToPage("/Bookings/BookingDetails", new { id = Id });
            }
            ModelState.AddModelError(string.Empty, message ?? "The revision could not be saved.");
            Booking = await _repo.GetBookingAsync(Id);
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            return Page();
        }
    }
}
