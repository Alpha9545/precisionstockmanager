using ClosedXML.Excel;
using iTextSharp.text;
using iTextSharp.text.pdf;

namespace PlantStockManager.Services
{
    // Shared Excel/PDF file-building for Stock section exports. Every page
    // keeps its own data query and Area/Outlet-scoped filtering exactly as
    // it already is for the on-screen list -- this only turns the SAME rows
    // the page already computed into a file. Not a second reporting system:
    // no new data path, no new repository, no bypass of the page's own
    // authorization/Area filter.
    public static class ExportHelper
    {
        public static byte[] BuildExcel(string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add(string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName);
            for (var c = 0; c < headers.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.Bold = true;
            }
            var r = 2;
            foreach (var row in rows)
            {
                for (var c = 0; c < row.Count; c++)
                {
                    var value = row[c];
                    var cell = ws.Cell(r, c + 1);
                    switch (value)
                    {
                        case null:
                            cell.Value = string.Empty;
                            break;
                        case decimal dec:
                            cell.Value = dec;
                            break;
                        case int i:
                            cell.Value = i;
                            break;
                        case DateTime dt:
                            cell.Value = dt;
                            cell.Style.DateFormat.Format = "dd-MMM-yyyy";
                            break;
                        default:
                            cell.Value = value.ToString();
                            break;
                    }
                }
                r++;
            }
            ws.Columns().AdjustToContents();
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        public static byte[] BuildPdf(string title, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, float[]? relativeColumnWidths = null)
        {
            using var ms = new MemoryStream();
            var document = new Document(headers.Count > 6 ? PageSize.A4.Rotate() : PageSize.A4, 12, 12, 12, 12);
            PdfWriter.GetInstance(document, ms);
            document.Open();

            var titleFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 14);
            var headerFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 9, BaseColor.WHITE);
            var cellFont = FontFactory.GetFont(FontFactory.HELVETICA, 8, BaseColor.BLACK);

            document.Add(new Paragraph(title, titleFont) { Alignment = Element.ALIGN_CENTER });
            document.Add(new Paragraph("Generated on " + DateTime.Now.ToString("dd-MMM-yyyy HH:mm"), FontFactory.GetFont(FontFactory.HELVETICA, 9)));
            document.Add(new Paragraph(" "));

            var table = new PdfPTable(headers.Count) { WidthPercentage = 100 };
            if (relativeColumnWidths != null && relativeColumnWidths.Length == headers.Count)
                table.SetWidths(relativeColumnWidths);

            var headerBg = new BaseColor(0, 102, 204);
            foreach (var h in headers)
            {
                table.AddCell(new PdfPCell(new Phrase(h, headerFont)) { BackgroundColor = headerBg, HorizontalAlignment = Element.ALIGN_CENTER, Padding = 4 });
            }

            var any = false;
            foreach (var row in rows)
            {
                any = true;
                foreach (var value in row)
                    table.AddCell(new PdfPCell(new Phrase(value ?? string.Empty, cellFont)) { HorizontalAlignment = Element.ALIGN_LEFT, Padding = 4 });
            }
            document.Add(table);
            if (!any)
                document.Add(new Paragraph("No records match the current filter.", cellFont));

            document.Close();
            return ms.ToArray();
        }
    }
}
