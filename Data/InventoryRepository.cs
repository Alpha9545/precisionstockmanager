using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using System.Data;
using System.Text;

using static System.Runtime.InteropServices.JavaScript.JSType;


namespace PlantStockManager.Data
{
    public class InventoryRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public InventoryRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }





        public async Task<List<Inventory>> GetAllocatedBookings(
    int? polyhouseId,
    int? plantTypeId,
    int? speciesId,
    DateTime? dateFrom,
    DateTime? dateTo,
    string? searchCustomer)
        {
            var result = new List<Inventory>();

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = new StringBuilder(@"
    SELECT 
        b.Id,
        p.Name,
        pt.Name,
        ps.Name,
        b.CustomerName,
        b.BookingDate,
        b.Quantity,
        it.QuantityUtilized,
        b.ActualDeliveryDate,
        d.DistrictName,
        b.Contact
    FROM InventoryTransactions it
    INNER JOIN Inventory i ON i.Id = it.InventoryId
    INNER JOIN Polyhouses p ON i.PolyhouseId = p.Id
    INNER JOIN PlantTypes pt ON i.PlantId = pt.Id
    INNER JOIN PlantSpecies ps ON i.SpeciesId = ps.Id
    INNER JOIN Bookings b ON b.Id = it.BookingId
    INNER JOIN Districts d ON b.DistrictId = d.DistrictId
    WHERE it.TransactionType = 'Allocation'
    ");

            if (polyhouseId.HasValue)
                sql.Append(" AND i.PolyhouseId = @PolyhouseId");

            if (plantTypeId.HasValue)
                sql.Append(" AND i.PlantId = @PlantTypeId");

            if (speciesId.HasValue)
                sql.Append(" AND i.SpeciesId = @SpeciesId");

            if (dateFrom.HasValue)
                sql.Append(" AND b.ActualDeliveryDate >= @DateFrom");

            if (dateTo.HasValue)
                sql.Append(" AND b.ActualDeliveryDate <= @DateTo");

            if (!string.IsNullOrWhiteSpace(searchCustomer))
            {
                sql.Append(" AND LOWER(b.CustomerName) LIKE '%' + LOWER(@SearchCustomer) + '%'");
            }

            sql.Append(" ORDER BY b.Id DESC");

            using var cmd = new SqlCommand(sql.ToString(), conn);

            if (polyhouseId.HasValue)
                cmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId);

            if (plantTypeId.HasValue)
                cmd.Parameters.AddWithValue("@PlantTypeId", plantTypeId);

            if (speciesId.HasValue)
                cmd.Parameters.AddWithValue("@SpeciesId", speciesId);

            if (dateFrom.HasValue)
                cmd.Parameters.AddWithValue("@DateFrom", dateFrom);

            if (dateTo.HasValue)
                cmd.Parameters.AddWithValue("@DateTo", dateTo);

            if (!string.IsNullOrWhiteSpace(searchCustomer))
                cmd.Parameters.AddWithValue("@SearchCustomer", searchCustomer);

            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add(new Inventory
                {
                    BookingId = reader.GetInt32(0),
                    PolyhouseName = reader.GetString(1),
                    PlantTypeName = reader.GetString(2),
                    SpeciesName = reader.GetString(3),
                    CustomerName = reader.GetString(4),
                    BookingDate = reader.GetDateTime(5),
                    BookingQuantity = reader.GetInt32(6),
                    UtilizedQuantity = reader.GetInt32(7),
                    ActualDeliveryDate = reader.GetDateTime(8),
                    District = reader.GetString(9),
                    Contact = reader.GetString(10)
                });
            }

            return result;
        }









    }
}
