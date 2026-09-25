using iTextSharp.text.pdf;
using iTextSharp.text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace PlantStockManager.Pages.Data
{
    public class StockHistoryModel : PageModel
    {
        private readonly InventoryRepository _inventoryRepository;
        private readonly PolyhouseRepository _polyhouseRepository;
        private readonly PlantTypeRepository _plantTypeRepository;
        private readonly PlantSpeciesRepository _plantSpeciesRepository;
        private readonly StockLedgerRepository _stockLedgerRepository;

        public List<Inventory> Inventory { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<PlantSpecies> PlantSpecies { get; set; } = new();

        // Phase 8 (Stock History and Wastage Integration): the legacy
        // section above (Inventory) is the OLD seedling-Booking-allocation
        // pipeline and stays completely untouched -- no historical data is
        // altered. This is the missing half: a unified view over the five
        // MODERN stock ledgers (Seed/Cutting/EmptyPot/PottedPlant/Ready),
        // built read-only from what those repositories already wrote --
        // see Data/StockLedgerRepository.cs for why no new history table
        // was needed.
        public List<UnifiedStockTransaction> ModernHistory { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public int? SelectedPolyhouse { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? SelectedPlantType { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? SelectedSpecies { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? SelectedStockType { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? DateFrom { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? DateTo { get; set; }

        public StockHistoryModel(
            InventoryRepository inventoryRepository,
            PolyhouseRepository polyhouseRepository,
            PlantTypeRepository plantTypeRepository,
            PlantSpeciesRepository plantSpeciesRepository,
            StockLedgerRepository stockLedgerRepository)
        {
            _inventoryRepository = inventoryRepository;
            _polyhouseRepository = polyhouseRepository;
            _plantTypeRepository = plantTypeRepository;
            _plantSpeciesRepository = plantSpeciesRepository;
            _stockLedgerRepository = stockLedgerRepository;
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

            Inventory = await _inventoryRepository.GetUtilizedInventory(SelectedPolyhouse, SelectedPlantType, SelectedSpecies, DateFrom, DateTo);
            ModernHistory = await _stockLedgerRepository.GetUnifiedHistoryAsync(DateFrom.Value, DateTo.Value, SelectedStockType);
        }

        public async Task<IActionResult> OnGetGeneratePdfAsync()
        {
            Inventory = await _inventoryRepository.GetUtilizedInventory(
                SelectedPolyhouse, SelectedPlantType, SelectedSpecies, DateFrom, DateTo);

            using (MemoryStream ms = new MemoryStream())
            {
                Document document = new Document(PageSize.A4, 10, 10, 10, 10);
                PdfWriter writer = PdfWriter.GetInstance(document, ms);
                document.Open();

                Font titleFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 14);
                Font headerFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 10, BaseColor.WHITE);
                Font cellFont = FontFactory.GetFont(FontFactory.HELVETICA, 9, BaseColor.BLACK);

                Paragraph title = new Paragraph("Utilized Stock Report", titleFont)
                {
                    Alignment = Element.ALIGN_CENTER
                };
                document.Add(title);
                document.Add(new Paragraph("Generated on " +
                    DateTime.Now.ToString("dd-MMM-yyyy HH:mm"),
                    FontFactory.GetFont(FontFactory.HELVETICA, 10)));

                document.Add(new Paragraph(" "));

                // 🔹 Changed from 8 → 9 columns
                PdfPTable table = new PdfPTable(9)
                {
                    WidthPercentage = 100
                };

                table.SetWidths(new float[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 });

                BaseColor headerBgColor = new BaseColor(0, 102, 204);

                // 🔹 Header Row
                AddCellToHeader(table, "Sowing ID", headerFont, headerBgColor);
                AddCellToHeader(table, "Polyhouse", headerFont, headerBgColor);
                AddCellToHeader(table, "Plant Type", headerFont, headerBgColor);
                AddCellToHeader(table, "Species", headerFont, headerBgColor);

                AddCellToHeader(table, "Seeding Date", headerFont, headerBgColor);  // ✅ NEW COLUMN

                AddCellToHeader(table, "Ready On", headerFont, headerBgColor);
                AddCellToHeader(table, "Used", headerFont, headerBgColor);
                AddCellToHeader(table, "Used On", headerFont, headerBgColor);
                AddCellToHeader(table, "Customer Name", headerFont, headerBgColor);

                foreach (var record in Inventory)
                {
                    AddCellToBody(table, "#" + record.SeedEntryId, cellFont);
                    AddCellToBody(table, record.PolyhouseName, cellFont);
                    AddCellToBody(table, record.PlantTypeName, cellFont);
                    AddCellToBody(table, record.SpeciesName, cellFont);

                    // ✅ NEW VALUE
                    AddCellToBody(table,
                        record.SeedingDate.ToString("dd-MMMM-yyyy") ?? "-",
                        cellFont);

                    AddCellToBody(table, record.LastUpdated.ToString("dd-MMMM-yyyy"), cellFont);
                    AddCellToBody(table, record.Quantity.ToString(), cellFont);
                    AddCellToBody(table, record.InventoryTransactionDate.ToString("dd-MMMM-yyyy"), cellFont);
                    AddCellToBody(table, record.CustomerName ?? "-", cellFont);
                }

                document.Add(table);
                document.Close();

                byte[] pdfBytes = ms.ToArray();
                Response.Headers.Add("Content-Disposition", "inline; filename=InventoryReport.pdf");

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
