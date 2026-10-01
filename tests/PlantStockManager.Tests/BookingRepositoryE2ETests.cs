using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PlantStockManager.Data;

namespace PlantStockManager.Tests
{
    // Regression for the InvalidCastException in
    // BookingRepository.GetBookingCountByBookingDateAsync: dbo.Bookings.Quantity
    // is an INT column, so SUM(Quantity) returns an INT, which
    // SqlDataReader.GetDecimal(1) cannot read directly. The query now casts the
    // aggregate to DECIMAL(18,2) before it reaches the reader. Runs against a
    // REAL scratch copy of the test database (same safety rules as every other
    // E2E class in this project: PSM_SCRATCH_CONNECTION, database name must
    // start with PlantsIMS2_Scratch_).
    [Collection("ScratchDb")]
    public class BookingRepositoryE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private static readonly DateTime TestDay = new(2020, 1, 20);

        private sealed class Env
        {
            public required string ConnectionString { get; init; }
            public required BookingRepository Repo { get; init; }

            public async Task InsertBookingAsync(DateTime bookingDate, int quantity, int plantId, int speciesId)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(@"
INSERT INTO dbo.Bookings (SpeciesId, Quantity, PlantId, BookingDate, AddedBy, Status)
VALUES (@SpeciesId, @Quantity, @PlantId, @BookingDate, N'E2E_TEST', N'Pending');", conn);
                cmd.Parameters.AddWithValue("@SpeciesId", speciesId);
                cmd.Parameters.AddWithValue("@Quantity", quantity);
                cmd.Parameters.AddWithValue("@PlantId", plantId);
                cmd.Parameters.AddWithValue("@BookingDate", bookingDate);
                await cmd.ExecuteNonQueryAsync();
            }

            public async Task DeleteBookingsForDayAsync(DateTime day)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(
                    "DELETE FROM dbo.Bookings WHERE AddedBy = N'E2E_TEST' AND BookingDate >= @From AND BookingDate < @To;", conn);
                cmd.Parameters.AddWithValue("@From", day.Date);
                cmd.Parameters.AddWithValue("@To", day.Date.AddDays(1));
                await cmd.ExecuteNonQueryAsync();
            }

            public async Task<(int PlantId, int SpeciesId)> FindValidPlantSpeciesAsync()
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT TOP 1 Id, PlantTypeId FROM dbo.PlantSpecies", conn);
                using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                return (r.GetInt32(1), r.GetInt32(0)); // (PlantId, SpeciesId)
            }
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the end-to-end tests.");
            string name;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                name = (string)(await cmd.ExecuteScalarAsync())!;
            }
            if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run end-to-end tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build();
            var dbHelper = new DatabaseHelper(config);
            var fulfilmentRepo = new SeedlingFulfilmentRepository(dbHelper, new BatchNumberRepository(dbHelper), new ReadyStockRepository(dbHelper));
            var env = new Env { ConnectionString = cs!, Repo = new BookingRepository(dbHelper, fulfilmentRepo) };

            await env.DeleteBookingsForDayAsync(TestDay); // clean slate for this fixed test date
            return env;
        }

        [SkippableFact]
        public async Task IntegerValuedQuantitySum_DoesNotThrow_AndIsReadAsDecimal()
        {
            var env = await OpenAsync();
            var (plantId, speciesId) = await env.FindValidPlantSpeciesAsync();
            // Every row's Quantity is a whole number here, which is the exact
            // shape that previously made SUM(Quantity) come back as an int and
            // blow up SqlDataReader.GetDecimal.
            await env.InsertBookingAsync(TestDay, 5, plantId, speciesId);
            await env.InsertBookingAsync(TestDay, 7, plantId, speciesId);

            var (count, quantity) = await env.Repo.GetBookingCountByBookingDateAsync(TestDay, TestDay.AddDays(1));

            Assert.Equal(2, count);
            Assert.Equal(12m, quantity);
        }

        [SkippableFact]
        public async Task NoBookingsForTheDay_ReturnsZeroCountAndZeroQuantity()
        {
            var env = await OpenAsync();

            var (count, quantity) = await env.Repo.GetBookingCountByBookingDateAsync(TestDay, TestDay.AddDays(1));

            Assert.Equal(0, count);
            Assert.Equal(0m, quantity);
        }
    }
}
