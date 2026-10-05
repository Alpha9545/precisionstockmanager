using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Tests
{
    // Allocate Batch table + short batch numbers, end-to-end against a SCRATCH
    // COPY of the database. Every test builds its own data through the real
    // code: tray stock -> two Seed Sowings of the same variety on the SAME
    // date (both batch "J-01") into two Polyhouses -> Supervisor Approval ->
    // two Ready Stock rows; plus a Pending booking for that variety. Then it
    // drives SeedlingFulfilmentRepository.SearchBatchOptionsAsync (search,
    // filters, paging, Area scope) and the unchanged AllocateBatchAsync /
    // ReserveAsync / DispatchAsync.
    // Runs only against a database named PlantsIMS2_Scratch_* (refused
    // otherwise, SKIPPED when PSM_SCRATCH_CONNECTION is not set).
    [Collection("ScratchDb")]
    public class BookingBatchAllocationE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        private const int MainOfficeAreaId = 2;
        private const int GreenBlessAreaId = 127, GreenBlessP1 = 14, GreenBlessP2 = 15;
        private const int SupervisorId = 9, Recorder = 21;
        private const int MpA = 114;                         // its variety is the test variety
        private static readonly DateTime SowingDate = new(2026, 10, 1);   // batch "J-01"
        private const string Cavity = "24 Cavity";

        private sealed class StaticMonitor<T> : IOptionsMonitor<T>
        {
            public StaticMonitor(T value) => CurrentValue = value;
            public T CurrentValue { get; }
            public T Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }

        private sealed class Env
        {
            public string ConnectionString { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
            public int Species { get; init; }
            public int PlantType { get; init; }
            public string SpeciesName { get; init; } = "";
            public int SeedStockId { get; init; }
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

            public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
            }

            public Task ExecAsync(string sql, params (string Name, object? Value)[] args) => ScalarAsync<int>(sql + "; SELECT 1", args);

            public Task<decimal> AvailableAsync(int readyStockId)
                => ScalarAsync<decimal>("SELECT Quantity - ReservedQuantity - DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @Id", ("@Id", readyStockId));
        }

        private sealed record Fixture(int BookingId, int ReadyStockP1, int ReadyStockP2, int SowingP1, int SowingP2);

        private static readonly SeedlingFulfilmentRepository.Actor Actor = new("batch-test", Recorder);

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run these tests.");
            string name;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                name = (string)(await cmd.ExecuteScalarAsync())!;
            }
            if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run these tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions()));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddSingleton<SeedlingAreaScope>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var provider = services.BuildServiceProvider();

            var env = new Env { ConnectionString = cs!, Provider = provider };
            var species = await env.ScalarAsync<int>("SELECT SpeciesId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpA));
            await env.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = 60 WHERE Id = @S AND ReadyStockDays IS NULL", ("@S", species));
            var plantType = await env.ScalarAsync<int>("SELECT PlantTypeId FROM dbo.PlantSpecies WHERE Id = @S", ("@S", species));
            var speciesName = (await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantSpecies WHERE Id = @S", ("@S", species))).Trim();
            var seedStockId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", species), ("@A", MainOfficeAreaId));
            if (seedStockId == 0)
            {
                await env.ExecAsync("INSERT INTO dbo.SeedStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'batch-test')",
                    ("@S", species), ("@A", MainOfficeAreaId));
                seedStockId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", species), ("@A", MainOfficeAreaId));
            }
            await env.ExecAsync("UPDATE dbo.SeedStock SET InTransitQuantity = 0, PhysicalQuantity = 100000 WHERE Id = @Id", ("@Id", seedStockId));
            return new Env { ConnectionString = cs!, Provider = provider, Species = species, PlantType = plantType, SpeciesName = speciesName, SeedStockId = seedStockId };
        }

        // Two same-date sowings (P-1: 480 plants, P-2: 240 plants), both approved in full, + a Pending booking.
        private static async Task<Fixture> BuildAsync(Env env, int bookingQuantity = 600)
        {
            var trays = env.Get<TrayStockRepository>();
            foreach (var polyhouse in new[] { GreenBlessP1, GreenBlessP2 })
            {
                var (ok, msg, _, _) = await trays.AddStockAsync(GreenBlessAreaId, polyhouse, Cavity, 100, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue), SupervisorId, "batch-test", "batch test setup", _ => true);
                Assert.True(ok, msg);
            }

            async Task<(int SowingId, int ReadyStockId)> SowAndApproveAsync(int polyhouse, decimal quantity, int traysReady)
            {
                var sowing = new SeedSowing
                {
                    SourceSeedStockId = env.SeedStockId, SeedQuantity = quantity, CavityType = Cavity, SowingDate = SowingDate,
                    AreaId = GreenBlessAreaId, PolyhouseId = polyhouse, SupervisorId = SupervisorId, CreatedBy = "batch-test", CreatedById = Recorder
                };
                var (ok, msg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, Recorder, _ => true);
                Assert.True(ok, msg);
                var (cOk, cMsg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, traysReady, null, null, null, "batch-test", SupervisorId, _ => true);
                Assert.True(cOk, cMsg);
                var readyStockId = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowingId));
                Assert.True(readyStockId > 0);
                return (sowingId, readyStockId);
            }

            var p1 = await SowAndApproveAsync(GreenBlessP1, 480, 20);
            var p2 = await SowAndApproveAsync(GreenBlessP2, 240, 10);
            var bookingId = await env.ScalarAsync<int>(@"
INSERT INTO dbo.Bookings (SpeciesId, Quantity, CustomerName, Status, PlantId, DeliveryDate, AddedBy)
VALUES (@S, @Q, N'ZZTEST Batch Customer', N'Pending', @P, @D, N'batch-test');
SELECT CAST(SCOPE_IDENTITY() AS INT);", ("@S", env.Species), ("@Q", bookingQuantity), ("@P", env.PlantType), ("@D", DateTime.Today.AddDays(30)));
            return new Fixture(bookingId, p1.ReadyStockId, p2.ReadyStockId, p1.SowingId, p2.SowingId);
        }

        private static Task<BatchSearchResult> SearchAsync(Env env, BatchSearch filter, IReadOnlyCollection<int>? allowed = null)
            => env.Get<SeedlingFulfilmentRepository>().SearchBatchOptionsAsync(env.PlantType, env.Species, filter, allowed);

        private static BatchSearch All(BatchSearch f) { f.PageSize = 100; return f; }

        // ---- duplicate batch numbers ---------------------------------------------------------------------

        [SkippableFact]
        public async Task TwoSameDateSowings_BothShowJ01_AndStaySeparateRows()
        {
            var env = await OpenAsync();
            var fx = await BuildAsync(env);

            var result = await SearchAsync(env, All(new BatchSearch { Query = "J-01", AreaId = GreenBlessAreaId }));

            var p1 = Assert.Single(result.Rows, r => r.ReadyStockId == fx.ReadyStockP1);
            var p2 = Assert.Single(result.Rows, r => r.ReadyStockId == fx.ReadyStockP2);
            Assert.Equal("J-01", p1.BatchNo);
            Assert.Equal("J-01", p2.BatchNo);
            Assert.NotEqual(p1.BatchCode, p2.BatchCode);          // the full SowingCodes stay unique
            Assert.Equal(fx.SowingP1, p1.SeedSowingId);
            Assert.Equal(fx.SowingP2, p2.SeedSowingId);
            Assert.Equal("P-1", p1.PolyhouseName);
            Assert.Equal("P-2", p2.PolyhouseName);
            Assert.Equal(480m, p1.AvailableQuantity);
            Assert.Equal(240m, p2.AvailableQuantity);
            Assert.All(result.Rows, r => Assert.Equal("J-01", r.BatchNo));

            // Without any other filter, "J-01" still returns ONLY 1 October batches -- never a
            // text match on a full code such as "2026-10-02-J-012".
            var unfiltered = await SearchAsync(env, All(new BatchSearch { Query = "J-01" }));
            Assert.NotEmpty(unfiltered.Rows);
            Assert.All(unfiltered.Rows, r => Assert.Equal("J-01", r.BatchNo));
        }

        [SkippableFact]
        public async Task SelectingOneJ01_AllocatesFromThatReadyStockRowOnly()
        {
            var env = await OpenAsync();
            var fx = await BuildAsync(env);

            var (ok, msg) = await env.Get<SeedlingFulfilmentRepository>().AllocateBatchAsync(fx.BookingId, fx.ReadyStockP2, 100, null, Actor, _ => true);

            Assert.True(ok, msg);
            Assert.Equal(140m, await env.AvailableAsync(fx.ReadyStockP2));
            Assert.Equal(480m, await env.AvailableAsync(fx.ReadyStockP1));   // the other "J-01" is untouched
            var line = Assert.Single(await env.Get<SeedlingFulfilmentRepository>().GetAllocationsAsync(fx.BookingId));
            Assert.Equal(fx.ReadyStockP2, line.ReadyStockId);
            Assert.Equal(fx.SowingP2, line.SeedSowingId);
            Assert.Equal("J-01", line.BatchNo);
            Assert.Equal(100m, await env.ScalarAsync<decimal>("SELECT ReservedQuantity FROM dbo.Bookings WHERE Id = @B", ("@B", fx.BookingId)));
        }

        // ---- search / filters / paging -------------------------------------------------------------------

        [SkippableFact]
        public async Task SearchByVariety_AndByFullSowingCode()
        {
            var env = await OpenAsync();
            var fx = await BuildAsync(env);

            var byVariety = await SearchAsync(env, All(new BatchSearch { Query = env.SpeciesName[..Math.Min(6, env.SpeciesName.Length)] }));
            Assert.Contains(byVariety.Rows, r => r.ReadyStockId == fx.ReadyStockP1);
            Assert.All(byVariety.Rows, r => Assert.Contains(env.SpeciesName[..Math.Min(6, env.SpeciesName.Length)], r.SpeciesName!, StringComparison.OrdinalIgnoreCase));

            var code = await env.ScalarAsync<string>("SELECT SowingCode FROM dbo.SeedSowings WHERE Id = @S", ("@S", fx.SowingP2));
            var byCode = await SearchAsync(env, All(new BatchSearch { Query = code }));
            Assert.Equal(fx.ReadyStockP2, Assert.Single(byCode.Rows).ReadyStockId);
        }

        [SkippableFact]
        public async Task Filters_Date_Area_Polyhouse_AndAllocationType()
        {
            var env = await OpenAsync();
            var fx = await BuildAsync(env);

            var byDate = await SearchAsync(env, All(new BatchSearch { SowingDate = SowingDate }));
            Assert.Contains(byDate.Rows, r => r.ReadyStockId == fx.ReadyStockP1);
            Assert.All(byDate.Rows, r => Assert.Equal(SowingDate, r.SowingDate.Date));

            var byArea = await SearchAsync(env, All(new BatchSearch { AreaId = GreenBlessAreaId }));
            Assert.All(byArea.Rows, r => Assert.Equal(GreenBlessAreaId, r.AreaId));
            var byOtherArea = await SearchAsync(env, All(new BatchSearch { AreaId = MainOfficeAreaId }));
            Assert.DoesNotContain(byOtherArea.Rows, r => r.ReadyStockId == fx.ReadyStockP1);

            var byPolyhouse = await SearchAsync(env, All(new BatchSearch { AreaId = GreenBlessAreaId, PolyhouseId = GreenBlessP2 }));
            Assert.Contains(byPolyhouse.Rows, r => r.ReadyStockId == fx.ReadyStockP2);
            Assert.DoesNotContain(byPolyhouse.Rows, r => r.ReadyStockId == fx.ReadyStockP1);

            var booked = await SearchAsync(env, All(new BatchSearch { AllocationType = BatchSearch.TypeBooked }));
            Assert.All(booked.Rows, r => Assert.Equal(env.Species, r.SpeciesId));
            Assert.Contains(booked.Rows, r => r.ReadyStockId == fx.ReadyStockP1);
            var substitutes = await SearchAsync(env, All(new BatchSearch { AllocationType = BatchSearch.TypeSubstitute }));
            Assert.All(substitutes.Rows, r => Assert.NotEqual(env.Species, r.SpeciesId));

            var (areas, polyhouses) = await env.Get<SeedlingFulfilmentRepository>().GetBatchFilterOptionsAsync(env.PlantType, null);
            Assert.Contains(areas, a => a.Id == GreenBlessAreaId);
            Assert.Contains(polyhouses, p => p.Id == GreenBlessP2 && p.AreaId == GreenBlessAreaId);
        }

        [SkippableFact]
        public async Task Paging_25PerPageByDefault_AndPagesDoNotOverlap()
        {
            var env = await OpenAsync();
            await BuildAsync(env);

            var all = await SearchAsync(env, new BatchSearch { AreaId = GreenBlessAreaId });
            Assert.Equal(BatchSearch.DefaultPageSize, all.PageSize);
            Assert.True(all.Total >= 2);

            var page1 = await SearchAsync(env, new BatchSearch { AreaId = GreenBlessAreaId, PageSize = 1, Page = 1 });
            var page2 = await SearchAsync(env, new BatchSearch { AreaId = GreenBlessAreaId, PageSize = 1, Page = 2 });
            Assert.Single(page1.Rows);
            Assert.Single(page2.Rows);
            Assert.NotEqual(page1.Rows[0].ReadyStockId, page2.Rows[0].ReadyStockId);
            Assert.Equal(all.Total, page1.Total);
            Assert.Equal(all.Total, page1.PageCount);
        }

        [SkippableFact]
        public async Task AreaScope_OtherAreasBatchesAreNeverListed()
        {
            var env = await OpenAsync();
            var fx = await BuildAsync(env);

            var mainOfficeOnly = await SearchAsync(env, All(new BatchSearch()), new[] { MainOfficeAreaId });
            Assert.DoesNotContain(mainOfficeOnly.Rows, r => r.AreaId == GreenBlessAreaId);
            var noAreas = await SearchAsync(env, All(new BatchSearch()), Array.Empty<int>());
            Assert.Empty(noAreas.Rows);
            var greenBless = await SearchAsync(env, All(new BatchSearch()), new[] { GreenBlessAreaId });
            Assert.Contains(greenBless.Rows, r => r.ReadyStockId == fx.ReadyStockP1);
        }

        // ---- existing allocation rules (unchanged) -------------------------------------------------------

        [SkippableFact]
        public async Task Allocation_AboveAvailable_AboveBookingNeed_OrUnauthorizedArea_IsBlocked()
        {
            var env = await OpenAsync();
            var fx = await BuildAsync(env, bookingQuantity: 200);
            var repo = env.Get<SeedlingFulfilmentRepository>();

            var aboveAvailable = await repo.AllocateBatchAsync(fx.BookingId, fx.ReadyStockP2, 250, null, Actor, _ => true);   // P-2 has 240; booking needs 200
            var aboveBooking = await repo.AllocateBatchAsync(fx.BookingId, fx.ReadyStockP1, 300, null, Actor, _ => true);     // P-1 has 480; booking needs 200
            var unauthorized = await repo.AllocateBatchAsync(fx.BookingId, fx.ReadyStockP1, 50, null, Actor, area => area != GreenBlessAreaId);

            Assert.False(aboveAvailable.Success);
            Assert.False(aboveBooking.Success);
            Assert.Contains("not yet reserved", aboveBooking.Message);
            Assert.False(unauthorized.Success);
            Assert.Contains("not authorized", unauthorized.Message);
            Assert.Equal(480m, await env.AvailableAsync(fx.ReadyStockP1));
            Assert.Equal(240m, await env.AvailableAsync(fx.ReadyStockP2));
            Assert.Empty(await repo.GetAllocationsAsync(fx.BookingId));
        }

        [SkippableFact]
        public async Task ExistingReserveAndDispatch_StillWork()
        {
            var env = await OpenAsync();
            var fx = await BuildAsync(env, bookingQuantity: 300);
            var repo = env.Get<SeedlingFulfilmentRepository>();

            var (reserveOk, reserveMsg) = await repo.ReserveAsync(fx.BookingId, 100, Actor);   // oldest batch first
            Assert.True(reserveOk, reserveMsg);
            var line = (await repo.GetAllocationsAsync(fx.BookingId)).First();
            var (dispatchOk, dispatchMsg) = await repo.DispatchAsync(fx.BookingId, new[] { (line.Id, 40m) }, DateTime.Today, null, "batch test", Actor, _ => true);
            Assert.True(dispatchOk, dispatchMsg);

            var booking = await repo.GetBookingAsync(fx.BookingId);
            Assert.Equal(40m, booking!.DispatchedQuantity);
            Assert.Equal(60m, booking.ReservedQuantity);
            var dispatchLine = Assert.Single((await repo.GetDispatchesAsync(fx.BookingId)).SelectMany(d => d.Lines));
            Assert.Equal(line.BatchNo, dispatchLine.BatchNo);
            Assert.True(dispatchLine.SeedSowingId > 0);

            // The Dispatch Register finds it by the short batch number too.
            var register = await repo.GetDispatchRegisterAsync(DateTime.Today, DateTime.Today, line.BatchNo);
            Assert.Contains(register, l => l.BookingId == fx.BookingId);
        }
    }
}
