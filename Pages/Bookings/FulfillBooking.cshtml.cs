using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using System.ComponentModel.DataAnnotations;


namespace PlantStockManager.Pages.Bookings
{
    [Authorize]
    public class FulfillBookingModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;

        [BindProperty(SupportsGet = true)]
        public int BookingId { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? From { get; set; } // "direct" or "list"

        [BindProperty]
        [DataType(DataType.Date)]
        public DateTime ActualBookingDate { get; set; }


        public BookingDetails Booking { get; set; }
        public List<AvailableInventory> InventoryItems { get; set; } = new();
        public int TotalAvailable => InventoryItems.Sum(i => i.AvailableQuantity);

        [BindProperty]
        public List<InventoryAllocation> Allocations { get; set; } = new();

        public class BookingDetails
        {
            public int Id { get; set; }
            public string PlantName { get; set; }
            public string SpeciesName { get; set; }
            public int Quantity { get; set; }
            public string CustomerName { get; set; }
            public DateTime TentativeDeliveryDate { get; set; }
        }

        public class AvailableInventory
        {
            public int InventoryId { get; set; }
            public string PolyhouseName { get; set; }
            public string SpeciesName { get; set; }

            public DateTime SeedingDate { get; set; }
            public int AvailableQuantity { get; set; }
            public DateTime AddedOn { get; set; }
            public int SeedEntryId { get; set; }
            public string? LocationDesc { get; set; }
        }

        public class InventoryAllocation
        {
            [Required]
            public int InventoryId { get; set; }

            [Range(0, int.MaxValue)]
            public int QuantityToUse { get; set; }
        }

        private string GetReturnUrl()
        {
            return string.Equals(From, "direct", StringComparison.OrdinalIgnoreCase)
                ? Url.Page("/Bookings/DirectBooking")!
                : Url.Page("/Bookings/Bookinglist")!;
        }
        public FulfillBookingModel(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!await LoadBookingDetails())
                return RedirectToPage("/Bookings/Bookinglist");

            await LoadAvailableInventory();
            return Page();
        }

        private async Task<bool> LoadBookingDetails()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var cmd = new SqlCommand(@"
                SELECT b.Id, pt.Name, ps.Name, b.Quantity, 
                       b.CustomerName, b.DeliveryDate
                FROM Bookings b
                JOIN PlantSpecies ps ON b.SpeciesId = ps.Id
                JOIN PlantTypes pt ON ps.PlantTypeId = pt.Id
                WHERE b.Id = @BookingId AND b.Status = 'Pending'", conn);

            cmd.Parameters.AddWithValue("@BookingId", BookingId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return false;

            Booking = new BookingDetails
            {
                Id = reader.GetInt32(0),
                PlantName = reader.GetString(1),
                SpeciesName = reader.GetString(2),
                Quantity = reader.GetInt32(3),
                CustomerName = reader.GetString(4),
                TentativeDeliveryDate = reader.GetDateTime(5)
            };
            return true;
        }

        private async Task LoadAvailableInventory()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var cmd = new SqlCommand(@"
                SELECT i.Id, ps.Name, i.RemainingQuantity, i.LastUpdated, pl.Name, se.SeedingDate, se.Id as SeedEntryId, se.locationDesc
                FROM Inventory i
                JOIN PlantSpecies ps ON i.SpeciesId = ps.Id
                JOIN PlantTypes pt ON ps.PlantTypeId = pt.Id
                Join Polyhouses pl On i.PolyhouseId = pl.Id
                Join SeedEntries se On i.SeedEntryId = se.id
                WHERE pt.Id = (SELECT PlantId FROM Bookings WHERE Id = @BookingId)
                  AND i.IsUtilized = 'N'
                ORDER BY i.LastUpdated ASC", conn);

            cmd.Parameters.AddWithValue("@BookingId", BookingId);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                InventoryItems.Add(new AvailableInventory
                {
                    InventoryId = reader.GetInt32(0),
                    SpeciesName = reader.GetString(1),
                    AvailableQuantity = reader.GetInt32(2),
                    AddedOn = reader.GetDateTime(3),
                    PolyhouseName = reader.GetString(4),
                    SeedingDate = reader.GetDateTime(5),
                    SeedEntryId = reader.GetInt32(6),
                    LocationDesc = reader.GetString(7)
                });

                Allocations.Add(new InventoryAllocation
                {
                    InventoryId = reader.GetInt32(0),
                    QuantityToUse = 0
                });
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!await LoadBookingDetails())
                return RedirectToPage("/Home/Index");
            //return RedirectToPage("/Bookings/Bookinglist");

            if (!ModelState.IsValid)
            {
                await LoadAvailableInventory();
                return Page();
            }

            //var totalAllocated = Allocations.Sum(a => a.QuantityToUse);
            //if (totalAllocated != Booking.Quantity)
            //{
            //    ModelState.AddModelError("",
            //        $"Total allocated ({totalAllocated}) must match order quantity ({Booking.Quantity})");
            //    await LoadAvailableInventory();
            //    return Page();
            //}

            //try
            //{
            //    await ProcessAllocations();
            //    TempData["SuccessMessage"] = "Order fulfilled successfully!";
            //    if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            //        return new JsonResult(new { success = true, redirectUrl = Url.Page("/Bookings/Bookinglist", new { id = BookingId }) });
            //    return RedirectToPage("/Bookings/Bookinglist", new { id = BookingId });
            //}
            //catch (Exception ex)
            //{
            //    ModelState.AddModelError("", $"Error processing order: {ex.Message}");
            //    await LoadAvailableInventory();
            //    return Page();
            //}

            try
            {
                await ProcessAllocations();

                // AJAX case
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                    return new JsonResult(new { success = true, redirectUrl = GetReturnUrl() });

                // normal form post
                return Redirect(GetReturnUrl());
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Error processing order: {ex.Message}");
                await LoadAvailableInventory();
                return Page();
            }
        }

        private async Task ProcessAllocations()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();

            try
            {
                foreach (var allocation in Allocations.Where(a => a.QuantityToUse > 0))
                {
                    await UpdateInventory(conn, transaction, allocation);
                    await CreateTransaction(conn, transaction, allocation);
                }

                await UpdateBookingStatus(conn, transaction);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private async Task UpdateInventory(SqlConnection conn, SqlTransaction transaction,
            InventoryAllocation allocation)
        {
            var cmd = new SqlCommand(@"
                UPDATE Inventory 
                SET RemainingQuantity = RemainingQuantity - @Quantity,
                IsUtilized = CASE 
                            WHEN (RemainingQuantity - @Quantity) = 0 THEN 'Y' 
                            ELSE IsUtilized 
                          END
                WHERE Id = @InventoryId
                  AND RemainingQuantity >= @Quantity", conn, transaction);

            cmd.Parameters.AddWithValue("@Quantity", allocation.QuantityToUse);
            cmd.Parameters.AddWithValue("@InventoryId", allocation.InventoryId);

            if (await cmd.ExecuteNonQueryAsync() == 0)
                throw new Exception($"Failed to update inventory {allocation.InventoryId}");
        }

        private async Task CreateTransaction(SqlConnection conn, SqlTransaction transaction,
            InventoryAllocation allocation)
        {
            var cmd = new SqlCommand(@"
                INSERT INTO InventoryTransactions 
                (BookingId, InventoryId, QuantityUtilized, TransactionType, AddedBy, UpdatedOn)
                VALUES (@BookingId, @InventoryId, @Quantity, 'Allocation', @User, getdate())",
                conn, transaction);

            cmd.Parameters.AddWithValue("@BookingId", BookingId);
            cmd.Parameters.AddWithValue("@InventoryId", allocation.InventoryId);
            cmd.Parameters.AddWithValue("@Quantity", allocation.QuantityToUse);
            cmd.Parameters.AddWithValue("@User", "Lalit");

            await cmd.ExecuteNonQueryAsync();
        }

        private async Task UpdateBookingStatus(SqlConnection conn, SqlTransaction transaction)
        {
            var cmd = new SqlCommand(@"
                UPDATE Bookings 
                SET Status = 'Completed', ActualDeliveryDate = @ActualBookingDate
                WHERE Id = @BookingId", conn, transaction);

            cmd.Parameters.AddWithValue("@BookingId", BookingId);
            cmd.Parameters.AddWithValue("@ActualBookingDate", ActualBookingDate);

            await cmd.ExecuteNonQueryAsync();
        }

     
    }
}
