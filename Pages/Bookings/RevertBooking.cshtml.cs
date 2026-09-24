using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Bookings
{
    public class RevertBookingModel : PageModel
    {
        private readonly BookingRepository _bookingRepo;
        private readonly InventoryRepository _inventoryRepo;
        private readonly InventoryTransactionRepository _transactionRepo;
        private readonly SeedlingFulfilmentRepository _fulfilmentRepo;

        public RevertBookingModel(
            BookingRepository bookingRepo,
            InventoryRepository inventoryRepo,
            InventoryTransactionRepository transactionRepo,
            SeedlingFulfilmentRepository fulfilmentRepo)
        {
            _fulfilmentRepo = fulfilmentRepo;
            _bookingRepo = bookingRepo;
            _inventoryRepo = inventoryRepo;
            _transactionRepo = transactionRepo;
        }

        [BindProperty(SupportsGet = true)]
        public int? BookingId { get; set; }

        public Booking? Booking { get; set; }
        public List<InventoryTransaction> Transactions { get; set; } = new();

        public async Task OnGetAsync()
        {
            if (BookingId.HasValue)
            {
                Booking = await _bookingRepo.GetBookingById(BookingId.Value);
                Transactions = await _transactionRepo.GetTransactionsByBookingId(BookingId.Value);
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!BookingId.HasValue)
                return Page();

            var booking = await _bookingRepo.GetBookingById(BookingId.Value);
            if (booking == null)
                return NotFound();

            // Phase C: this page only reverses LEGACY Inventory fulfilment.
            // A booking handled through Ready Stock is managed on Booking Fulfilment.
            if (await _fulfilmentRepo.GetLegacyFulfilmentBlockReasonAsync(BookingId.Value) != null)
            {
                TempData["Message"] = "This booking was fulfilled from Ready Stock; it cannot be reverted here.";
                return RedirectToPage();
            }

            var transactions = await _transactionRepo.GetTransactionsByBookingId(BookingId.Value);

            foreach (var tx in transactions)
            {
                if (tx.InventoryId != 0 && tx.QuantityUtilized > 0)
                {
                    await _inventoryRepo.RestoreInventoryQuantity(tx.InventoryId, tx.QuantityUtilized);
                }

                await _transactionRepo.MarkTransactionAsAdjustment(tx.Id);
            }

            await _bookingRepo.UpdateStatus(booking.Id, "Pending");
            await _fulfilmentRepo.ClearLegacySourceAsync(booking.Id);

            TempData["Message"] = "Booking reverted successfully.";
            return RedirectToPage(); // Reload
        }
    }
}
