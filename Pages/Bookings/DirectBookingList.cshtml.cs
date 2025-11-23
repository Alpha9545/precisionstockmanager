using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;
using System.Text;

namespace PlantStockManager.Pages.Bookings
{
    public class DirectBookingListModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;
        public List<BookingList> Bookings { get; set; } = new();

        // Bind the query string parameters
        [BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)]
        public string? StatusFilter { get; set; }
        [BindProperty(SupportsGet = true)]
        public DateTime? FromDate { get; set; }
        [BindProperty(SupportsGet = true)]
        public DateTime? ToDate { get; set; }

        public class BookingList
        {
            public int Id { get; set; }
            public string CustomerName { get; set; }
            public string PlantType { get; set; }
            public string Species { get; set; }
            public int Quantity { get; set; }
            public string Status { get; set; }
            public DateTime BookingDate { get; set; }
            public DateTime TentativeDeliveryDate { get; set; }
            public DateTime? ActualDeliveryDate { get; set; }
            // Optional: Add Address and Contact if they exist in your schema
            public string? Address { get; set; }
            public string? Contact { get; set; }
        }

        public DirectBookingListModel(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadBookings();
            return Page();
        }

        private async Task LoadBookings()
        {
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = null;

            if (!string.IsNullOrEmpty(userIdClaim))
                userId = int.Parse(userIdClaim);
            // Get the current user’s ID from claims

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();

                // Build the query dynamically based on filters.
                StringBuilder sql = new StringBuilder(@"
                    SELECT b.Id, b.CustomerName, pt.Name AS PlantType, ps.Name AS Species, 
                           b.Quantity, b.Status, b.BookingDate, b.DeliveryDate, b.ActualDeliveryDate
                    FROM Bookings b
                    JOIN PlantSpecies ps ON b.SpeciesId = ps.Id
                    JOIN PlantTypes pt ON ps.PlantTypeId = pt.Id
                    WHERE 1=1 and Status = 'Pending'  
                ");

                // List to hold SQL parameters
                List<SqlParameter> parameters = new();

                if (userId.HasValue)
                {
                    sql.Append(" AND b.BookedById = @UserId");
                    parameters.Add(new SqlParameter("@UserId", userId.Value));
                }

                // Apply status filter if specified
                if (!string.IsNullOrWhiteSpace(StatusFilter))
                {
                    sql.Append(" AND b.Status = @StatusFilter");
                    parameters.Add(new SqlParameter("@StatusFilter", StatusFilter));
                }

                // Apply search filter if specified (searching in CustomerName, Address, and Contact)
                if (!string.IsNullOrWhiteSpace(SearchTerm))
                {
                    sql.Append(" AND (b.CustomerName LIKE @SearchTerm OR b.Address LIKE @SearchTerm OR b.Contact LIKE @SearchTerm OR ps.Name LIKE @SearchTerm)");
                    parameters.Add(new SqlParameter("@SearchTerm", "%" + SearchTerm + "%"));
                }

                // Apply date filters if specified (filtering on the DeliveryDate column)
                if (FromDate.HasValue)
                {
                    sql.Append(" AND b.DeliveryDate >= @FromDate");
                    parameters.Add(new SqlParameter("@FromDate", FromDate.Value));
                }
                if (ToDate.HasValue)
                {
                    sql.Append(" AND b.DeliveryDate <= @ToDate");
                    parameters.Add(new SqlParameter("@ToDate", ToDate.Value));
                }

                // Add ordering (you can modify this as needed)
                sql.Append(" ORDER BY b.DeliveryDate");

                var cmd = new SqlCommand(sql.ToString(), conn);
                cmd.Parameters.AddRange(parameters.ToArray());

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        Bookings.Add(new BookingList
                        {
                            Id = reader.GetInt32(0),
                            CustomerName = reader.GetString(1),
                            PlantType = reader.GetString(2),
                            Species = reader.GetString(3),
                            Quantity = reader.GetInt32(4),
                            Status = reader.GetString(5),
                            BookingDate = reader.GetDateTime(6),
                            TentativeDeliveryDate = reader.GetDateTime(7),
                            ActualDeliveryDate = reader.IsDBNull(8) ? null : reader.GetDateTime(8)
                            // If Address and Contact columns are part of your query, read them accordingly.
                        });
                    }
                }
            }
        }

        public async Task<IActionResult> OnPostCancelBookingAsync([FromBody] int BookingId)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                string sql = "UPDATE Bookings SET Status = 'Cancelled' WHERE Id = @BookingId";
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@BookingId", BookingId);
                    int rowsAffected = await cmd.ExecuteNonQueryAsync();

                    if (rowsAffected > 0)
                    {
                        return new JsonResult(new { success = true });
                    }
                }
            }
            return new JsonResult(new { success = false });
        }



        // Helper methods for rendering status badges and icons in the view.
        public string GetStatusBadge(string status)
        {
            return status switch
            {
                "Completed" => "bg-success",
                "Pending" => "bg-warning",
                "Cancelled" => "bg-danger",
                _ => "bg-secondary"
            };
        }

        public string GetStatusIcon(string status)
        {
            return status switch
            {
                "Completed" => "fa-check-circle",
                "Pending" => "fa-clock",
                "Cancelled" => "fa-times-circle",
                _ => "fa-info-circle"
            };
        }

        public async Task<IActionResult> OnGetGeneratePdfAsync()
        {
            // Load the filtered data.
            await LoadBookings();

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
                Paragraph title = new Paragraph("Booking List", titleFont);
                title.Alignment = Element.ALIGN_CENTER;
                document.Add(title);
                document.Add(new Paragraph("Generated on " + DateTime.Now.ToString("dd-MMM-yyyy HH:mm"), FontFactory.GetFont(FontFactory.HELVETICA, 10)));
                document.Add(new Paragraph(" ")); // empty line

                // Create a table with 8 columns.
                PdfPTable table = new PdfPTable(8)
                {
                    WidthPercentage = 100
                };
                table.SetWidths(new float[] { 1, 2, 2, 2, 1, 2, 2, 2 });

                // Header background color.
                BaseColor headerBgColor = new BaseColor(0, 102, 204); // a blue shade

                // Add table header.
                AddCellToHeader(table, "Booking ID", headerFont, headerBgColor);
                AddCellToHeader(table, "Customer", headerFont, headerBgColor);
                AddCellToHeader(table, "Plant Type", headerFont, headerBgColor);
                AddCellToHeader(table, "Variety", headerFont, headerBgColor);
                AddCellToHeader(table, "Qty", headerFont, headerBgColor);
                AddCellToHeader(table, "Status", headerFont, headerBgColor);
                AddCellToHeader(table, "Tentative Delivery Date", headerFont, headerBgColor);
                AddCellToHeader(table, "Actual Delivery Date", headerFont, headerBgColor);

                // Add table rows.
                foreach (var booking in Bookings)
                {
                    AddCellToBody(table, "#" + booking.Id.ToString(), cellFont);
                    AddCellToBody(table, booking.CustomerName, cellFont);
                    AddCellToBody(table, booking.PlantType, cellFont);
                    AddCellToBody(table, booking.Species, cellFont);
                    AddCellToBody(table, booking.Quantity.ToString(), cellFont);
                    AddCellToBody(table, booking.Status, cellFont);
                    AddCellToBody(table, booking.TentativeDeliveryDate.ToString("dd-MMM-yy"), cellFont);
                    AddCellToBody(table, booking.ActualDeliveryDate.HasValue ? booking.ActualDeliveryDate.Value.ToString("dd-MMM-yy") : "-", cellFont);
                }
                document.Add(table);
                document.Close();

                byte[] pdfBytes = ms.ToArray();

                // Set header to display PDF inline.
                Response.Headers.Add("Content-Disposition", "inline; filename=Bookings.pdf");

                return File(pdfBytes, "application/pdf");
            }
        }

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
