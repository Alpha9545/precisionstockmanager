using iTextSharp.text.pdf;
using iTextSharp.text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.SeedEntry
{
   
    public class InventoryModel : PageModel
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
        public DateTime? SeedingFrom { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? SeedingTo { get; set; }


        public InventoryModel(
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

        public async Task OnGetAsync()
        {
            Polyhouses = await _polyhouseRepository.GetAllPolyhouses();
            PlantTypes = await _plantTypeRepository.GetAllPlantTypes();

            if (SelectedPlantType.HasValue)
            {
                PlantSpecies = await _plantSpeciesRepository.GetSpeciesByPlantType(SelectedPlantType.Value);
            }
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int userId = 0;

            if (!string.IsNullOrWhiteSpace(userIdClaim))
                int.TryParse(userIdClaim, out userId);

            Inventory = await _inventoryRepository.GetInventoryRecordsByDate(SelectedPolyhouse, SelectedPlantType, SelectedSpecies, SeedingFrom, SeedingTo, userId);
        }

        public async Task<IActionResult> OnPostUpdateSortedQuantityAsync(int id, int quantity)
        {
            var result = await _inventoryRepository.UpdateSorting(id, quantity);
            if (!result.Success)
            {
                return new JsonResult(new { success = false, message = result.Message });
            }
            return new JsonResult(new { success = true, newQuantity = result.NewQuantity });
        }

        public async Task<IActionResult> OnGetGeneratePdfAsync()
        {
            // Load the filtered inventory data.
            Inventory = await _inventoryRepository.GetInventoryRecords(SelectedPolyhouse, SelectedPlantType, SelectedSpecies);

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
                Paragraph title = new Paragraph("Inventory Report", titleFont)
                {
                    Alignment = Element.ALIGN_CENTER
                };
                document.Add(title);
                document.Add(new Paragraph("Generated on " + DateTime.Now.ToString("dd-MMM-yyyy HH:mm"), FontFactory.GetFont(FontFactory.HELVETICA, 10)));
                document.Add(new Paragraph(" ")); // empty line

                // Create a table. Adjust the column count as per your Inventory properties.
                PdfPTable table = new PdfPTable(7)
                {
                    WidthPercentage = 100
                };
                // Set column widths (adjust these ratios as needed)
                table.SetWidths(new float[] { 1, 2, 2, 2, 2,1,1 });

                // Define header background color.
                BaseColor headerBgColor = new BaseColor(0, 102, 204); // blue shade

                // Add table header.
                AddCellToHeader(table, "Sowing ID", headerFont, headerBgColor);
                AddCellToHeader(table, "Polyhouse", headerFont, headerBgColor);
                AddCellToHeader(table, "Plant Type", headerFont, headerBgColor);
                AddCellToHeader(table, "Species", headerFont, headerBgColor);
                AddCellToHeader(table, "Seeding Date", headerFont, headerBgColor);
                AddCellToHeader(table, "Quantity", headerFont, headerBgColor);
                AddCellToHeader(table, "Location", headerFont, headerBgColor);
              

                // Add table rows.
                foreach (var record in Inventory)
                {
                    AddCellToBody(table, "#" + record.SeedEntryId, cellFont);
                    // Adjust these property names to match your Inventory model.
                    AddCellToBody(table, record.PolyhouseName, cellFont);
                    AddCellToBody(table, record.PlantTypeName, cellFont);
                    AddCellToBody(table, record.SpeciesName, cellFont);
                    AddCellToBody(table, record.SeedingDate.ToString("dd-MMMM-yyyy"), cellFont);
                    AddCellToBody(table, record.Quantity.ToString(), cellFont);
                    AddCellToBody(table, record.LocationDesc ?? "", cellFont);
                   
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

        public async Task<IActionResult> OnPostRevertLastSortingAsync(int id)
        {
            var result = await _inventoryRepository.RevertLastSorting(id /*, userName if you have it */);
            if (!result.Success) return new JsonResult(new { success = false, message = result.Message });
            return new JsonResult(new { success = true, newQuantity = result.NewQuantity });
        }


    }
}
