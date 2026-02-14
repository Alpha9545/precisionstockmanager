using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using System.Text;

namespace PlantStockManager.Pages.Data
{
    public class BookingHistoryModel : PageModel
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
        public DateTime? DateFrom { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? DateTo { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? SearchCustomer { get; set; }

        public BookingHistoryModel(
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

        private async Task LoadDataAsync()
        {
            DateFrom ??= DateTime.Today.AddDays(-7);
            DateTo ??= DateTime.Today;

            Polyhouses = await _polyhouseRepository.GetAllPolyhouses();
            PlantTypes = await _plantTypeRepository.GetAllPlantTypes();

            if (SelectedPlantType.HasValue)
            {
                PlantSpecies = await _plantSpeciesRepository
                    .GetSpeciesByPlantType(SelectedPlantType.Value);
            }

            Inventory = await _inventoryRepository.GetAllocatedBookings(
                SelectedPolyhouse,
                SelectedPlantType,
                SelectedSpecies,
                DateFrom,
                DateTo,
                SearchCustomer);
        }

        public async Task OnGetAsync()
        {
            await LoadDataAsync();
        }

        public async Task<IActionResult> OnGetGeneratePdfAsync()
        {
            await LoadDataAsync();

            using MemoryStream ms = new();
            Document document = new(PageSize.A4.Rotate(), 10, 10, 10, 10);
            PdfWriter.GetInstance(document, ms);
            document.Open();

            Font titleFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 14);
            Font headerFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 9, BaseColor.WHITE);
            Font cellFont = FontFactory.GetFont(FontFactory.HELVETICA, 8);

            document.Add(new Paragraph("Booking History Report", titleFont)
            {
                Alignment = Element.ALIGN_CENTER
            });

            document.Add(new Paragraph(
                $"Generated: {DateTime.Now:dd-MMM-yyyy HH:mm}",
                FontFactory.GetFont(FontFactory.HELVETICA, 9)));

            document.Add(new Paragraph(
                $"Filters: From {DateFrom:dd-MMM-yyyy} To {DateTo:dd-MMM-yyyy} | Customer: {SearchCustomer ?? "All"}",
                FontFactory.GetFont(FontFactory.HELVETICA, 9)));

            document.Add(new Paragraph(" "));

            PdfPTable table = new(10) { WidthPercentage = 100 };
            table.SetWidths(new float[] { 1, 2, 2, 2, 2, 2, 2, 1, 1, 2 });

            BaseColor headerBg = new(0, 102, 204);

            string[] headers = {
                "Booking No", "Customer", "District", "Booking Date",
                "Polyhouse", "Plant Type", "Variety",
                "Booked", "Issued", "Delivery Date"
            };

            foreach (var h in headers)
                AddHeaderCell(table, h, headerFont, headerBg);

            foreach (var record in Inventory)
            {
                AddBodyCell(table, record.BookingId.ToString(), cellFont);
                AddBodyCell(table, record.CustomerName, cellFont);
                AddBodyCell(table, record.District ?? "", cellFont);
                AddBodyCell(table, record.BookingDate.ToString("dd-MMM-yyyy"), cellFont);
                AddBodyCell(table, record.PolyhouseName, cellFont);
                AddBodyCell(table, record.PlantTypeName, cellFont);
                AddBodyCell(table, record.SpeciesName, cellFont);
                AddBodyCell(table, record.BookingQuantity.ToString(), cellFont);
                AddBodyCell(table, record.UtilizedQuantity.ToString(), cellFont);
                AddBodyCell(table, record.ActualDeliveryDate.ToString("dd-MMM-yyyy"), cellFont);
            }

            document.Add(table);
            document.Close();

            //return File(ms.ToArray(), "application/pdf", "BookingHistory.pdf");
            return File(ms.ToArray(), "application/pdf");

        }

        private void AddHeaderCell(PdfPTable table, string text, Font font, BaseColor bg)
        {
            table.AddCell(new PdfPCell(new Phrase(text, font))
            {
                BackgroundColor = bg,
                HorizontalAlignment = Element.ALIGN_CENTER,
                Padding = 4
            });
        }

        private void AddBodyCell(PdfPTable table, string text, Font font)
        {
            table.AddCell(new PdfPCell(new Phrase(text ?? "", font))
            {
                Padding = 4
            });
        }
    }
}
