using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using ClosedXML.Excel;

namespace PlantStockManager.Pages.Data
{
    public class ReadyStockModel : PageModel
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

        public ReadyStockModel(
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

            Inventory = await _inventoryRepository.GetInventoryRecords(SelectedPolyhouse, SelectedPlantType, SelectedSpecies);
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
                table.SetWidths(new float[] { 1, 2, 2, 2, 2, 1, 1 });

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


        public async Task<IActionResult> OnGetGenerateExcelAsync()
        {
            // Load the filtered inventory data (same filters as the page/PDF).
            Inventory = await _inventoryRepository.GetInventoryRecords(
                SelectedPolyhouse, SelectedPlantType, SelectedSpecies);

            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Ready Stock");

            // Headers (exactly 3 columns)
            ws.Cell(1, 1).Value = "Species";
            ws.Cell(1, 2).Value = "Seeding Date";
            ws.Cell(1, 3).Value = "Quantity";

            // Data
            int row = 2;
            foreach (var r in Inventory)
            {
                ws.Cell(row, 1).Value = r.SpeciesName ?? string.Empty;
                ws.Cell(row, 2).Value = r.SeedingDate;             // keep as DateTime
                ws.Cell(row, 3).Value = r.Quantity;                // numeric
                row++;
            }

            // Styling & UX niceties
            var header = ws.Range(1, 1, 1, 3);
            header.Style.Font.Bold = true;
            ws.SheetView.FreezeRows(1);
            if (ws.RangeUsed() != null) ws.RangeUsed().SetAutoFilter();

            // Formats
            ws.Column(2).Style.DateFormat.Format = "dd-MMMM-yyyy";
            ws.Columns(1, 3).AdjustToContents();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            var bytes = ms.ToArray();

            var fileName = $"ReadyStock_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }

    }
}
