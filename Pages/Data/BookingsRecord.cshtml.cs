using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;


namespace PlantStockManager.Pages.Data
{
    public class BookingsRecordModel : PageModel
    {
        private readonly BookingRepository _bookingRepository;
        private readonly PlantTypeRepository _plantTypeRepository;
        private readonly PlantSpeciesRepository _plantSpeciesRepository;
        private readonly EmployeeRepository _employeeRepo;

        public List<Booking> Bookings { get; set; } = new();
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<PlantSpecies> PlantSpecies { get; set; } = new();
        public List<Employee> Employees { get; set; } = new();

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

        public BookingsRecordModel(
            BookingRepository bookingRepository,
            PlantTypeRepository plantTypeRepository,
            PlantSpeciesRepository plantSpeciesRepository,
            EmployeeRepository employeeRepo)
        {
            _bookingRepository = bookingRepository;
            _plantTypeRepository = plantTypeRepository;
            _plantSpeciesRepository = plantSpeciesRepository;
            _employeeRepo = employeeRepo;
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
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = null;
            if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out var parsedUserId))
            {
                userId = parsedUserId;
            }

            PlantTypes = await _plantTypeRepository.GetAllPlantTypes();
            Employees = await _employeeRepo.GetAllEmployees(); // Fetch employees for dropdown


            if (SelectedPlantType.HasValue)
            {
                PlantSpecies = await _plantSpeciesRepository.GetSpeciesByPlantType(SelectedPlantType.Value);
            }

            Bookings = await _bookingRepository.GetBookingRecords(SelectedPlantType, SelectedSpecies, SelectedMonth, SelectedYear, SelectedStatus, userId);
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
                SelectedPlantType, SelectedSpecies, SelectedMonth, SelectedYear, SelectedStatus, userId);

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

                // Table with 6 columns
                var table = new PdfPTable(7) { WidthPercentage = 100 };
                table.SetWidths(new float[] { 1.0f, 2.8f, 1.8f, 2.0f, 1.1f, 1.4f, 1.7f }); // tweak as you like

                var headerBg = new BaseColor(0, 102, 204);

                AddCellToHeader(table, "Booking Id", headerFont, headerBg);
                AddCellToHeader(table, "Customer Name", headerFont, headerBg);
                AddCellToHeader(table, "Booking Date", headerFont, headerBg);
                AddCellToHeader(table, "Species", headerFont, headerBg);
                AddCellToHeader(table, "Quantity", headerFont, headerBg);
                AddCellToHeader(table, "Advance", headerFont, headerBg);
                AddCellToHeader(table, "Delivery Date", headerFont, headerBg);

                foreach (var b in Bookings)
                {
                    AddCellToBody(table, b.Id.ToString(), cellFont);
                    AddCellToBody(table, b.CustomerName ?? string.Empty, cellFont);
                    AddCellToBody(table, b.BookingDate.ToString("dd-MMM-yyyy"), cellFont);
                    AddCellToBody(table, b.SpeciesName ?? string.Empty, cellFont);
                    AddCellToBody(table, b.Quantity.ToString(), cellFont);
                    AddCellToBody(table, (b.AdvanceTakenAmount ?? 0).ToString("0.##"), cellFont);
                    AddCellToBody(table, b.DeliveryDate.ToString("dd-MMM-yyyy"), cellFont);

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
