    using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Data
{
    public class WastedStockModel : PageModel
    {

        private readonly InventoryRepository _inventoryRepository;
        private readonly PolyhouseRepository _polyhouseRepository;
        private readonly PlantTypeRepository _plantTypeRepository;
        private readonly PlantSpeciesRepository _plantSpeciesRepository;
        private readonly StockLedgerRepository _stockLedgerRepository;

        public WastedStockModel(
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

        public List<Inventory> Inventory { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<PlantSpecies> PlantSpecies { get; set; } = new();

        // Phase 8: the legacy Inventory-based report above stays
        // untouched. These two sections close the gap the audit found --
        // PottedPlantStock's own 'Wastage' ledger entries (Phase 8 finally
        // writes them, see PottedPlantStockRepository.RecordWastageAsync)
        // and Seed/Cutting Sowing "process wastage" (already-existing
        // WastageQuantity business columns, never a ledger row -- there is
        // no physical stock row for in-process seedlings to decrement).
        public List<UnifiedStockTransaction> PottedPlantWastage { get; set; } = new();
        public List<ProcessWastageRow> ProcessWastage { get; set; } = new();

        [BindProperty(SupportsGet = true)] public int? SelectedPolyhouse { get; set; }
        [BindProperty(SupportsGet = true)] public int? SelectedPlantType { get; set; }
        [BindProperty(SupportsGet = true)] public int? SelectedSpecies { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? DateFrom { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? DateTo { get; set; }

        public async Task OnGetAsync()
        {
            DateFrom ??= DateTime.Today.AddDays(-30);
            DateTo ??= DateTime.Today;

            Polyhouses = await _polyhouseRepository.GetAllPolyhouses();
            PlantTypes = await _plantTypeRepository.GetAllPlantTypes();
            if (SelectedPlantType.HasValue)
            {
                PlantSpecies = await _plantSpeciesRepository.GetSpeciesByPlantType(SelectedPlantType.Value);
            }

            Inventory = await _inventoryRepository.GetWastedStockReport(
                SelectedPolyhouse,
                SelectedPlantType,
                SelectedSpecies,
                DateFrom,
                DateTo);

            var pottedPlantHistory = await _stockLedgerRepository.GetUnifiedHistoryAsync(DateFrom.Value, DateTo.Value, "PottedPlant");
            PottedPlantWastage = pottedPlantHistory.Where(t => t.TransactionType == "Wastage").ToList();
            ProcessWastage = await _stockLedgerRepository.GetProcessWastageAsync(DateFrom.Value, DateTo.Value);
        }

        public async Task<JsonResult> OnGetSpeciesByPlantTypeAsync(int plantTypeId)
        {
            var speciesList = await _plantSpeciesRepository.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(speciesList);
        }

        public async Task<IActionResult> OnGetGeneratePdfAsync()
        {
            Inventory = await _inventoryRepository.GetWastedStockReport(
                SelectedPolyhouse,
                SelectedPlantType,
                SelectedSpecies,
                DateFrom,
                DateTo);

            using var ms = new MemoryStream();
            Document doc = new Document(PageSize.A4.Rotate(), 10, 10, 10, 10);
            PdfWriter.GetInstance(doc, ms);
            doc.Open();

            var titleFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 14);
            var headerFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 9, BaseColor.WHITE);
            var cellFont = FontFactory.GetFont(FontFactory.HELVETICA, 9);
            var headerBg = new BaseColor(0, 102, 204);

            doc.Add(new Paragraph("Wasted Stock Report", titleFont) { Alignment = Element.ALIGN_CENTER });
            doc.Add(new Paragraph("Generated on: " + DateTime.Now.ToString("dd-MMM-yyyy hh:mm tt")));
            doc.Add(new Paragraph(" "));

            PdfPTable table = new PdfPTable(10) { WidthPercentage = 100 };
            table.SetWidths(new float[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 });

            void AddHeader(string text) => table.AddCell(new PdfPCell(new Phrase(text, headerFont)) { BackgroundColor = headerBg, HorizontalAlignment = Element.ALIGN_CENTER });
            void AddCell(string text) => table.AddCell(new PdfPCell(new Phrase(text, cellFont)));

            AddHeader("Seeding Date"); AddHeader("Polyhouse"); AddHeader("Plant"); AddHeader("Species");
            AddHeader("Tray Waste"); AddHeader("Hardening Waste"); AddHeader("Inventory Waste"); AddHeader("Sorting Waste"); AddHeader("Total Waste"); AddHeader("Supervisor");

            foreach (var r in Inventory)
            {
                AddCell(r.SeedingDate.ToShortDateString().ToString());
                AddCell(r.PolyhouseName);
                AddCell(r.PlantTypeName);
                AddCell(r.SpeciesName);
                AddCell(r.WastedInTrays.ToString());
                AddCell(r.WastedInHardening.ToString());
                AddCell(r.WastedInInventory.ToString());
                AddCell(r.WastedInSorting.ToString());
                AddCell(r.TotalWasted.ToString());
                AddCell(r.Supervisor.ToString());
            }

            doc.Add(table);
            doc.Close();

            byte[] pdfBytes = ms.ToArray();
            Response.Headers.Add("Content-Disposition", "inline; filename=WastedStock.pdf");

            // Return the PDF without specifying a download name
            return File(pdfBytes, "application/pdf");
        }
    }
}