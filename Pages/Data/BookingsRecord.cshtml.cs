using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;


namespace PlantStockManager.Pages.Data
{
    public class BookingsRecordModel : PageModel
    {
        private readonly BookingRepository _bookingRepository;
        private readonly PlantTypeRepository _plantTypeRepository;
        private readonly PlantSpeciesRepository _plantSpeciesRepository;
        private readonly BookingOwnershipService _ownership;

        // "Own bookings only" users (BookingOwnershipService): list and PDF show only their own bookings.
        public bool ShowingOwnBookingsOnly { get; set; }

        public List<Booking> Bookings { get; set; } = new();

        // The variety lines grouped into customer orders (Booking #EffectiveOrderId).
        public List<BookingOrders.OrderGroup<Booking>> Orders => BookingOrders.Group(Bookings, b => b.EffectiveOrderId, b => b.Id);
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<PlantSpecies> PlantSpecies { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public int? SelectedPlantType { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? SelectedSpecies { get; set; }

        [BindProperty(SupportsGet = true)]
        public int SelectedMonth { get; set; } = DateTime.Now.Month;

        [BindProperty(SupportsGet = true)]
        public int SelectedYear { get; set; } = DateTime.Now.Year;

        [BindProperty(SupportsGet = true)]
        public string SelectedStatus { get; set; } = "Pending"; // Default status

        // Correction #6: "Booking By" -- "" = all, "U:<id>" a person, "O:<name>" a free-text name, "X:none" not recorded.
        [BindProperty(SupportsGet = true)]
        public string? SelectedBookedBy { get; set; }

        public List<BookedByFilter.Option> BookedByOptions { get; set; } = new();
        public string? Notice { get; set; }
        public bool BookedByActive { get; set; }

        public BookingsRecordModel(
            BookingRepository bookingRepository,
            PlantTypeRepository plantTypeRepository,
            PlantSpeciesRepository plantSpeciesRepository,
            BookingOwnershipService ownership)
        {
            _bookingRepository = bookingRepository;
            _ownership = ownership;
            _plantTypeRepository = plantTypeRepository;
            _plantSpeciesRepository = plantSpeciesRepository;
        }

        [BindProperty]
        public Booking Booking { get; set; }

        public async Task<JsonResult> OnGetSpeciesByPlantType(int plantTypeId)
        {
            var species = await _plantSpeciesRepository.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(species);
        }
        public async Task OnGetAsync()
        {
            if (SelectedMonth == 0)
            {
                SelectedMonth = DateTime.Now.Month;
            }
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = null;
            if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out var parsedUserId))
            {
                userId = parsedUserId;
            }

            PlantTypes = await _plantTypeRepository.GetAllPlantTypes();


            if (SelectedPlantType.HasValue)
            {
                PlantSpecies = await _plantSpeciesRepository.GetSpeciesByPlantType(SelectedPlantType.Value);
            }

            // Booking By: parsed and normalised; the value stays selected after searching. A value that is not understood
            // is ignored (everyone shown) with a notice. It only narrows the list the user is already allowed to see.
            var (bookedBy, notice) = BookedByFilter.Parse(SelectedBookedBy);
            SelectedBookedBy = bookedBy.Value;
            BookedByActive = bookedBy.IsActive;
            Notice = notice;
            var owner = _ownership.OwnerFilter(User);
            ShowingOwnBookingsOnly = owner.HasValue;
            BookedByOptions = await _bookingRepository.GetBookedByOptionsAsync(owner);

            Bookings = await _bookingRepository.GetBookingRecords(SelectedPlantType, SelectedSpecies, SelectedMonth, SelectedYear, SelectedStatus, userId, bookedBy, owner);
        }

        public async Task<IActionResult> OnGetGeneratePdfAsync()
        {
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = null;
            if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out var parsedUserId))
            {
                userId = parsedUserId;
            }
            // Load filtered data with the same parameters used by the page
            Bookings = await _bookingRepository.GetBookingRecords(
                SelectedPlantType, SelectedSpecies, SelectedMonth, SelectedYear, SelectedStatus, userId,
                BookedByFilter.Parse(SelectedBookedBy).Filter, _ownership.OwnerFilter(User));

            using (var ms = new MemoryStream())
            {
                var document = new Document(PageSize.A4, 10, 10, 10, 10);
                PdfWriter.GetInstance(document, ms);
                document.Open();

                // Fonts
                var titleFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 14);
                var headerFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 10, BaseColor.WHITE);
                var cellFont = FontFactory.GetFont(FontFactory.HELVETICA, 9, BaseColor.BLACK);

                // Title
                var title = new Paragraph("Booking Records", titleFont) { Alignment = Element.ALIGN_CENTER };
                document.Add(title);
                document.Add(new Paragraph("Generated on " + DateTime.Now.ToString("dd-MMM-yyyy HH:mm"),
                    FontFactory.GetFont(FontFactory.HELVETICA, 10)));
                document.Add(new Paragraph(" ")); // spacer

                // One block per customer order: the order header once, its varieties under it.
                var orders = Orders;
                document.Add(new Paragraph($"{orders.Count} booking{(orders.Count == 1 ? "" : "s")}, {Bookings.Count} variety line{(Bookings.Count == 1 ? "" : "s")}",
                    FontFactory.GetFont(FontFactory.HELVETICA, 9)));
                document.Add(new Paragraph(" "));

                var table = new PdfPTable(5) { WidthPercentage = 100 };
                table.SetWidths(new float[] { 3.6f, 1.3f, 1.5f, 1.3f, 1.2f });
                table.HeaderRows = 1;

                var headerBg = new BaseColor(0, 102, 204);
                var orderBg = new BaseColor(225, 232, 240);
                var orderFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 9, BaseColor.BLACK);

                AddCellToHeader(table, "Variety", headerFont, headerBg);
                AddCellToHeader(table, "Quantity", headerFont, headerBg);
                AddCellToHeader(table, "Delivery Date", headerFont, headerBg);
                AddCellToHeader(table, "Status", headerFont, headerBg);
                AddCellToHeader(table, "Advance", headerFont, headerBg);

                foreach (var order in orders)
                {
                    var h = order.First;
                    var more = h.OrderLineCount > order.Lines.Count ? $" (of {h.OrderLineCount} in the booking)" : "";
                    var header = new PdfPCell(new Phrase(
                        $"Booking #{order.OrderId}   {h.CustomerName}   Booked {h.BookingDate:dd-MMM-yyyy}   Mob {h.Contact}   " +
                        $"{order.Lines.Count} variet{(order.Lines.Count == 1 ? "y" : "ies")}{more}   Total {QuantityFormat.Qty(order.Lines.Sum(l => l.Quantity))}", orderFont))
                    { Colspan = 5, BackgroundColor = orderBg, Padding = 5 };
                    table.AddCell(header);

                    foreach (var b in order.Lines)
                    {
                        AddCellToBody(table, $"{b.PlantTypeName} / {b.SpeciesName}", cellFont);
                        AddCellToBody(table, QuantityFormat.Qty(b.Quantity), cellFont);
                        AddCellToBody(table, b.DeliveryDate.ToString("dd-MMM-yyyy"), cellFont);
                        AddCellToBody(table, b.Status ?? string.Empty, cellFont);
                        AddCellToBody(table, (b.AdvanceTakenAmount ?? 0) > 0 ? (b.AdvanceTakenAmount ?? 0).ToString("0.##") : string.Empty, cellFont);
                    }
                }

                document.Add(table);
                document.Close();

                var bytes = ms.ToArray();
                Response.Headers.Add("Content-Disposition", "inline; filename=BookingRecords.pdf");
                return File(bytes, "application/pdf");
            }
        }

        // Helpers
        private void AddCellToHeader(PdfPTable table, string text, Font font, BaseColor backgroundColor)
        {
            var cell = new PdfPCell(new Phrase(text, font))
            {
                BackgroundColor = backgroundColor,
                HorizontalAlignment = Element.ALIGN_CENTER,
                Padding = 5
            };
            table.AddCell(cell);
        }

        private void AddCellToBody(PdfPTable table, string text, Font font)
        {
            var cell = new PdfPCell(new Phrase(text, font))
            {
                HorizontalAlignment = Element.ALIGN_LEFT,
                Padding = 5
            };
            table.AddCell(cell);
        }

    }
}
