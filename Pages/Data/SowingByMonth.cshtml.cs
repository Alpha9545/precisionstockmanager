using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Data
{
    public class SowingByMonthModel : PageModel
    {
        private readonly SeedEntryRepository _seedEntryRepository;
        private readonly PolyhouseRepository _polyhouseRepository;
        private readonly PlantTypeRepository _plantTypeRepository;
        private readonly PlantSpeciesRepository _plantSpeciesRepository;

        public List<SeedEntries> SeedEntries { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<PlantSpecies> PlantSpecies { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public int? SelectedPolyhouse { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? SelectedPlantType { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? SelectedSpecies { get; set; }
        public int SelectedMonth { get; set; }
        public int SelectedYear { get; set; }

        public SowingByMonthModel(
            SeedEntryRepository seedEntryRepository,
            PolyhouseRepository polyhouseRepository,
            PlantTypeRepository plantTypeRepository,
            PlantSpeciesRepository plantSpeciesRepository)
        {
            _seedEntryRepository = seedEntryRepository;
            _polyhouseRepository = polyhouseRepository;
            _plantTypeRepository = plantTypeRepository;
            _plantSpeciesRepository = plantSpeciesRepository;
        }

        [BindProperty(SupportsGet = true)]
        public DateTime? FromDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? ToDate { get; set; }



        public async Task OnGetAsync(int? month, int? year)
        {
            SelectedMonth = month ?? DateTime.Now.Month;
            SelectedYear = year ?? DateTime.Now.Year;
            Polyhouses = await _polyhouseRepository.GetAllPolyhouses();
            PlantTypes = await _plantTypeRepository.GetAllPlantTypes();

            if (SelectedPlantType.HasValue)
            {
                PlantSpecies = await _plantSpeciesRepository.GetSpeciesByPlantType(SelectedPlantType.Value);
            }

            SeedEntries = await _seedEntryRepository.GetSowingRecordsByMonth(SelectedPolyhouse, SelectedPlantType, SelectedSpecies, SelectedMonth, SelectedYear);
        }

        public async Task<IActionResult> OnGetGeneratePdfAsync(int month, int year)
        {
            SelectedMonth = month;
            SelectedYear = year;
            // Load the filtered inventory data.
            SeedEntries = await _seedEntryRepository.GetSowingRecordsByMonth(SelectedPolyhouse, SelectedPlantType, SelectedSpecies, SelectedMonth, SelectedYear);

            using (MemoryStream ms = new MemoryStream())
            {
                // Create the PDF document.
                Document document = new Document(PageSize.A4, 10, 10, 10, 10);
                PdfWriter writer = PdfWriter.GetInstance(document, ms);
                document.Open();

                // Define fonts.
                Font titleFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 14);
                Font headerFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 10, BaseColor.WHITE);
                Font cellFont = FontFactory.GetFont(FontFactory.HELVETICA, 9, BaseColor.BLACK);

                // Add report title.
                Paragraph title = new Paragraph("Sowing Phase Report", titleFont)
                {
                    Alignment = Element.ALIGN_CENTER
                };
                document.Add(title);
                document.Add(new Paragraph("Generated on " + DateTime.Now.ToString("dd-MMM-yyyy HH:mm"), FontFactory.GetFont(FontFactory.HELVETICA, 10)));
                document.Add(new Paragraph(" ")); // empty line

                // Create a table. Adjust the column count as per your Inventory properties.
                PdfPTable table = new PdfPTable(9)
                {
                    WidthPercentage = 100
                };
                // Set column widths (adjust these ratios as needed)
                table.SetWidths(new float[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 });

                // Define header background color.
                BaseColor headerBgColor = new BaseColor(0, 102, 204); // blue shade

                // Add table header.
                AddCellToHeader(table, "Batch No", headerFont, headerBgColor);
                AddCellToHeader(table, "Polyhouse", headerFont, headerBgColor);
                AddCellToHeader(table, "Plant Type", headerFont, headerBgColor);
                AddCellToHeader(table, "Supervisor", headerFont, headerBgColor);
                AddCellToHeader(table, "Species", headerFont, headerBgColor);
                AddCellToHeader(table, "Seeding Date", headerFont, headerBgColor);
                AddCellToHeader(table, "In Trays", headerFont, headerBgColor);
                AddCellToHeader(table, "Harderning", headerFont, headerBgColor);
                AddCellToHeader(table, "Inventory", headerFont, headerBgColor);


                // Add table rows.
                foreach (var record in SeedEntries)
                {
                    AddCellToBody(table, "#" + record.Id, cellFont);
                    // Adjust these property names to match your Inventory model.
                    AddCellToBody(table, record.PolyhouseName, cellFont);
                    AddCellToBody(table, record.PlantTypeName, cellFont);
                    AddCellToBody(table, record.Supervisor, cellFont);
                    AddCellToBody(table, record.SpeciesName, cellFont);
                    AddCellToBody(table, record.SeedingDate.ToString("dd-MMMM-yyyy"), cellFont);
                    AddCellToBody(table, record.TraysAlive.ToString() == "" ? "Balance" : record.TraysAlive.ToString(), cellFont);
                    AddCellToBody(table, record.HardeningAlive.ToString() == "" ? "Balance" : record.HardeningAlive.ToString(), cellFont);
                    AddCellToBody(table, record.AliveCount.ToString() == "" ? "Balance" : record.AliveCount.ToString(), cellFont);

                }
                document.Add(table);
                document.Close();

                byte[] pdfBytes = ms.ToArray();
                Response.Headers.Add("Content-Disposition", "inline; filename=InventoryReport.pdf");

                // Return the PDF without specifying a download name
                return File(pdfBytes, "application/pdf");
            }
        }

        // Helper method to add a cell to the table header.
        private void AddCellToHeader(PdfPTable table, string text, Font font, BaseColor backgroundColor)
        {
            PdfPCell cell = new PdfPCell(new Phrase(text, font))
            {
                BackgroundColor = backgroundColor,
                HorizontalAlignment = Element.ALIGN_CENTER,
                Padding = 5
            };
            table.AddCell(cell);
        }

        // Helper method to add a cell to the table body.
        private void AddCellToBody(PdfPTable table, string text, Font font)
        {
            PdfPCell cell = new PdfPCell(new Phrase(text, font))
            {
                HorizontalAlignment = Element.ALIGN_LEFT,
                Padding = 5
            };
            table.AddCell(cell);
        }
    }
}
