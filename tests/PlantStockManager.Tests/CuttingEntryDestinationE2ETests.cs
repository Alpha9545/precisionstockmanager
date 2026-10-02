using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // CORRECTION #1 -- end-to-end test of the REAL Cutting Entry code (page model + repositories)
    // against a SCRATCH COPY of the test database.
    //
    // The repositories commit their own transactions, so unlike DeleteIntegrationTests these tests
    // cannot run inside one rolled-back transaction. They therefore run ONLY when
    // PSM_SCRATCH_CONNECTION points at a database whose name starts with "PlantsIMS2_Scratch_"
    // (a disposable restore of PlantsIMS2_Test that already has the migration applied). Any other
    // database -- including PlantsIMS2_Test, PlantsIMS2 and PlantsIMS -- is REFUSED. Without the
    // variable every test here is SKIPPED, never silently passed.
    //
    //   PowerShell:  $env:PSM_SCRATCH_CONNECTION = "Server=...;Database=PlantsIMS2_Scratch_E2E;Trusted_Connection=True;TrustServerCertificate=True;"
    //                dotnet test --filter FullyQualifiedName~CuttingEntryDestinationE2ETests
    [Collection("ScratchDb")]   // shares one scratch database with CuttingSowingOverageE2ETests: never run at the same time
    public class CuttingEntryDestinationE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        // real rows of the copy (looked up by species + Area, so an entry may create its pool):
        //   Area 1   : Mother Plant 114 (species 3144, supervisor 115)   <- the main test Area
        //   Area 122 : Mother Plant 106 (species 19,   supervisor 101)   <- "another Area"
        //   Main Office = Area 2. (Mother Plants 91 / 108 are 'Removed' in the data: correctly refused.)
        private const int MpA = 114, SupA = 115, AreaA = 1;
        private const int MpB = 106, SupB = 101, AreaB = 122;
        private const int MainOfficeArea = 2;

        private sealed class Env
        {
            public string ConnectionString { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
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

            public Task<int> SpeciesOfAsync(int motherPlantId) => ScalarAsync<int>("SELECT SpeciesId FROM dbo.MotherPlants WHERE Id = @M", ("@M", motherPlantId));

            public Task<int?> PoolIdAsync(int species, int area) => ScalarAsync<int?>("SELECT Id FROM dbo.CuttingStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", species), ("@A", area));

            // (physical, in transit, available) of the (species, Area) pool; zeros when it does not exist yet
            public async Task<(decimal Physical, decimal InTransit, decimal Available)> PoolAsync(int species, int area)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT PhysicalQuantity, InTransitQuantity, AvailableQuantity FROM dbo.CuttingStock WHERE SpeciesId = @S AND AreaId = @A", conn);
                cmd.Parameters.AddWithValue("@S", species);
                cmd.Parameters.AddWithValue("@A", area);
                using var r = await cmd.ExecuteReaderAsync();
                return await r.ReadAsync() ? (r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2)) : (0m, 0m, 0m);
            }

            // every pool that is NOT (species in the given Area) and NOT (species at Main Office), as one comparable string
            public Task<string> OtherPoolsAsync(int species, int area)
                => ScalarAsync<string>("SELECT CONCAT(ISNULL(SUM(PhysicalQuantity), 0), '/', ISNULL(SUM(InTransitQuantity), 0), '/', COUNT(*)) FROM dbo.CuttingStock WHERE NOT (SpeciesId = @S AND AreaId IN (@A, @MO))",
                    ("@S", species), ("@A", area), ("@MO", MainOfficeArea));
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

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions()));
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            return new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider() };
        }

        private static CuttingProduction Entry(int motherPlantId, int supervisorId, decimal quantity, string? destination, Guid? token = null, int? mainOfficeAreaId = null)
            => new()
            {
                MotherPlantId = motherPlantId, SupervisorId = supervisorId, Quantity = quantity, CuttingDate = DateTime.Today,
                DestinationType = destination, DestinationAreaId = mainOfficeAreaId, SubmissionToken = token ?? Guid.NewGuid(), CreatedBy = "e2e-test"
            };

        // ---- A. Main Office: entry -> Area stock -> pending delivery -> Main Office confirmation -> Main Office stock ----

        [SkippableFact]
        public async Task MainOffice_EntryCreatesAPendingDelivery_AndMainOfficeStockOnlyMovesOnConfirmation()
        {
            var env = await OpenAsync();
            var repo = env.Get<CuttingProductionRepository>();
            var species = await env.SpeciesOfAsync(MpA);
            var (p0, t0, a0) = await env.PoolAsync(species, AreaA);
            var (mp0, mt0, _) = await env.PoolAsync(species, MainOfficeArea);
            var others0 = await env.OtherPoolsAsync(species, AreaA);

            var entry = Entry(MpA, SupA, 250, CuttingDestination.MainOffice);
            var (ok, message, id) = await repo.InsertAsync(entry, SupA);
            Assert.True(ok, message);
            Assert.NotNull(entry.TransferId);
            var pool = await env.PoolIdAsync(species, AreaA);

            // saved correctly: quantity, destination, area, entered-by, supervisor, traceability
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions WHERE Id = @Id AND Quantity = 250 AND DestinationType = N'MainOffice' AND DestinationAreaId = @D AND AreaId = @A AND CreatedById = @U AND SupervisorId = @U AND MotherPlantId = @M AND CuttingStockId = @P",
                ("@Id", id), ("@D", MainOfficeArea), ("@A", AreaA), ("@U", SupA), ("@M", MpA), ("@P", pool)));
            // harvested into the Mother Plant's OWN Area pool, held In-Transit -> not available, not Main Office stock yet
            Assert.Equal((p0 + 250, t0 + 250, a0), await env.PoolAsync(species, AreaA));
            var mo1 = await env.PoolAsync(species, MainOfficeArea);
            Assert.Equal((mp0, mt0), (mo1.Physical, mo1.InTransit));                          // Main Office pool untouched
            Assert.Equal(others0, await env.OtherPoolsAsync(species, AreaA));                  // no other Area touched
            // the delivery: existing shape -- pending at the Main Office Area, linked back to the entry
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE Id = @T AND StockType = N'Cutting' AND Status = N'PendingConfirmation' AND PendingConfirmationAreaId = @MO AND SourceAreaId = @A AND Quantity = 250 AND SourceCuttingProductionId = @Id AND SourceCuttingStockId = @P",
                ("@T", entry.TransferId), ("@MO", MainOfficeArea), ("@A", AreaA), ("@Id", id), ("@P", pool)));

            // Main Office confirms what arrived (existing ConfirmReceipt flow): 240 of 250
            var transfers = env.Get<InternalTransferRepository>();
            var (confirmed, confirmMessage, _) = await transfers.ConfirmReceiptAsync(entry.TransferId!.Value, 240, "10 damaged in transit", 1, "e2e-test");
            Assert.True(confirmed, confirmMessage);
            Assert.Equal((p0, t0, a0), await env.PoolAsync(species, AreaA));                   // +250 harvest -240 delivered -10 transit loss; hold released
            Assert.Equal(mp0 + 240, (await env.PoolAsync(species, MainOfficeArea)).Physical);   // NOW it is Main Office stock (what arrived)
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE Id = @T AND Status = N'Completed' AND ConfirmedQuantity = 240 AND DestinationAreaId = @MO", ("@T", entry.TransferId), ("@MO", MainOfficeArea)));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P AND ReferenceType = N'InternalTransfer' AND ReferenceId = @T AND TransactionType = N'TransitLoss' AND Quantity = -10", ("@P", pool), ("@T", entry.TransferId)));
        }

        // ---- B. Use for Pot Production: entry -> the Area's AVAILABLE stock, no transfer ----

        [SkippableFact]
        public async Task PotProduction_EntryAddsAvailableStockToTheSourceArea_WithNoTransfer()
        {
            var env = await OpenAsync();
            var repo = env.Get<CuttingProductionRepository>();
            var species = await env.SpeciesOfAsync(MpA);
            var (p0, t0, a0) = await env.PoolAsync(species, AreaA);
            var mo0 = await env.PoolAsync(species, MainOfficeArea);
            var others0 = await env.OtherPoolsAsync(species, AreaA);
            var transfers0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers");

            var entry = Entry(MpA, SupA, 300, CuttingDestination.PotProduction);
            var (ok, message, id) = await repo.InsertAsync(entry, SupA);
            Assert.True(ok, message);
            Assert.Null(entry.TransferId);
            var pool = await env.PoolIdAsync(species, AreaA);

            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions WHERE Id = @Id AND Quantity = 300 AND DestinationType = N'PotProduction' AND DestinationAreaId = @A AND AreaId = @A", ("@Id", id), ("@A", AreaA)));
            Assert.Equal((p0 + 300, t0, a0 + 300), await env.PoolAsync(species, AreaA));      // available +300, nothing in transit
            Assert.Equal(mo0, await env.PoolAsync(species, MainOfficeArea));                   // NOT Main Office stock
            Assert.Equal(others0, await env.OtherPoolsAsync(species, AreaA));
            Assert.Equal(transfers0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers"));   // no transfer at all
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P AND ReferenceType = N'CuttingProduction' AND ReferenceId = @Id AND TransactionType = N'Harvest' AND Quantity = 300", ("@P", pool), ("@Id", id)));
        }

        // ---- validation ----

        [SkippableTheory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Somewhere")]
        public async Task MissingOrUnknownDestination_IsRejected_AndNothingIsWritten(string? destination)
        {
            var env = await OpenAsync();
            var species = await env.SpeciesOfAsync(MpA);
            var before = (await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions"), await env.PoolAsync(species, AreaA));
            var (ok, message, _) = await env.Get<CuttingProductionRepository>().InsertAsync(Entry(MpA, SupA, 100, destination), SupA);
            Assert.False(ok);
            Assert.Contains("Main Office or Use for Pot Production", message);
            Assert.Equal(before, (await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions"), await env.PoolAsync(species, AreaA)));
        }

        [SkippableTheory]
        [InlineData(0)]
        [InlineData(-3)]
        [InlineData(12.5)]
        public async Task InvalidQuantity_IsRejected(double quantity)
        {
            var env = await OpenAsync();
            var species = await env.SpeciesOfAsync(MpA);
            var before = await env.PoolAsync(species, AreaA);
            var (ok, _, _) = await env.Get<CuttingProductionRepository>().InsertAsync(Entry(MpA, SupA, (decimal)quantity, CuttingDestination.PotProduction), SupA);
            Assert.False(ok);
            Assert.Equal(before, await env.PoolAsync(species, AreaA));
        }

        [SkippableFact]
        public async Task ARemovedMotherPlant_IsStillRefused()
        {
            var env = await OpenAsync();
            var (ok, message, _) = await env.Get<CuttingProductionRepository>().InsertAsync(Entry(91, 101, 10, CuttingDestination.PotProduction), 101);   // Mother Plant 91 is 'Removed'
            Assert.False(ok);
            Assert.Contains("Active Mother Plant", message);
        }

        // ---- duplicate submission protection ----

        [SkippableFact]
        public async Task RepeatedSubmit_OfTheSameForm_SavesOnce_AndCreditsTheStockOnce()
        {
            var env = await OpenAsync();
            var repo = env.Get<CuttingProductionRepository>();
            var token = Guid.NewGuid();
            var species = await env.SpeciesOfAsync(MpA);
            var (p0, _, a0) = await env.PoolAsync(species, AreaA);
            var rows0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions");

            var first = Entry(MpA, SupA, 40, CuttingDestination.PotProduction, token);
            var second = Entry(MpA, SupA, 40, CuttingDestination.PotProduction, token);
            var (ok1, m1, id1) = await repo.InsertAsync(first, SupA);
            var (ok2, m2, id2) = await repo.InsertAsync(second, SupA);

            Assert.True(ok1, m1);
            Assert.True(ok2, m2);
            Assert.False(first.IsDuplicateSubmission);
            Assert.True(second.IsDuplicateSubmission);
            Assert.Equal(id1, id2);
            Assert.Equal(rows0 + 1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions"));
            var (p1, _, a1) = await env.PoolAsync(species, AreaA);
            Assert.Equal((p0 + 40, a0 + 40), (p1, a1));                                        // credited ONCE
        }

        [SkippableFact]
        public async Task ManySimultaneousSubmits_OfTheSameMainOfficeForm_CreateExactlyOneEntryAndOneDelivery()
        {
            var env = await OpenAsync();
            var repo = env.Get<CuttingProductionRepository>();
            var token = Guid.NewGuid();
            var species = await env.SpeciesOfAsync(MpA);
            var (p0, t0, _) = await env.PoolAsync(species, AreaA);
            var rows0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions");
            var transfers0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers");

            var entries = Enumerable.Range(0, 6).Select(_ => Entry(MpA, SupA, 20, CuttingDestination.MainOffice, token)).ToList();
            var results = await Task.WhenAll(entries.Select(e => Task.Run(() => repo.InsertAsync(e, SupA))));

            Assert.All(results, r => Assert.True(r.Success, r.Message));
            Assert.Equal(1, results.Select(r => r.Id).Distinct().Count());
            Assert.Equal(1, entries.Count(e => !e.IsDuplicateSubmission));
            Assert.Equal(rows0 + 1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions"));
            Assert.Equal(transfers0 + 1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers"));
            var (p1, t1, _) = await env.PoolAsync(species, AreaA);
            Assert.Equal((p0 + 20, t0 + 20), (p1, t1));                                        // one credit, one in-transit hold
        }

        // ---- Area isolation ----

        [SkippableFact]
        public async Task AnEntryInOneArea_NeverTouchesAnotherAreasStock()
        {
            var env = await OpenAsync();
            var speciesA = await env.SpeciesOfAsync(MpA);
            var speciesB = await env.SpeciesOfAsync(MpB);
            var a0 = await env.PoolAsync(speciesA, AreaA);
            var mo0 = await env.PoolAsync(speciesA, MainOfficeArea);
            var b0 = await env.PoolAsync(speciesB, AreaB);
            var everythingElse0 = await env.OtherPoolsAsync(speciesB, AreaB);

            var (ok, message, _) = await env.Get<CuttingProductionRepository>().InsertAsync(Entry(MpB, SupB, 70, CuttingDestination.PotProduction), SupB);
            Assert.True(ok, message);

            Assert.Equal((b0.Physical + 70, b0.InTransit, b0.Available + 70), await env.PoolAsync(speciesB, AreaB));     // only Area 122's own pool
            Assert.Equal(a0, await env.PoolAsync(speciesA, AreaA));                            // Area 1 untouched
            Assert.Equal(mo0, await env.PoolAsync(speciesA, MainOfficeArea));
            Assert.Equal(everythingElse0, await env.OtherPoolsAsync(speciesB, AreaB));
        }

        [SkippableFact]
        public async Task CrossAreaSupervisor_AndNonMainOfficeDestination_AreRejected()
        {
            var env = await OpenAsync();
            var repo = env.Get<CuttingProductionRepository>();
            var species = await env.SpeciesOfAsync(MpA);
            var rows0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions");
            var pool0 = await env.PoolAsync(species, AreaA);

            // supervisor 101 belongs to Area 122, not to Mother Plant 114's Area 1
            Assert.False((await repo.InsertAsync(Entry(MpA, SupB, 10, CuttingDestination.PotProduction), SupB)).Success);
            // a Main Office delivery can only go to a real Main Office Area, never to another production Area
            var (ok, message, _) = await repo.InsertAsync(Entry(MpA, SupA, 10, CuttingDestination.MainOffice, mainOfficeAreaId: AreaB), SupA);
            Assert.False(ok);
            Assert.Contains("Main Office Area", message);
            Assert.Equal(rows0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions"));
            Assert.Equal(pool0, await env.PoolAsync(species, AreaA));
        }

        // ---- the existing Cutting Delivery workflow (Send Cuttings to Main Office) is unchanged ----

        [SkippableFact]
        public async Task ExistingSendToMainOffice_StillReservesConfirmsAndRejects()
        {
            var env = await OpenAsync();
            var entries = env.Get<CuttingProductionRepository>();
            var transfers = env.Get<InternalTransferRepository>();
            var species = await env.SpeciesOfAsync(MpA);
            // make sure there is something to send (a Pot Production entry adds available stock)
            Assert.True((await entries.InsertAsync(Entry(MpA, SupA, 400, CuttingDestination.PotProduction), SupA)).Success);
            var pool = (await env.PoolIdAsync(species, AreaA))!.Value;
            var (p0, t0, a0) = await env.PoolAsync(species, AreaA);
            var mp0 = (await env.PoolAsync(species, MainOfficeArea)).Physical;

            // send 100 (no cutting-entry link)
            var send = new InternalTransfer { StockType = "Cutting", SourceCuttingStockId = pool, PendingConfirmationAreaId = MainOfficeArea, Quantity = 100, CreatedBy = "e2e-test" };
            var (ok, message, id) = await transfers.InsertAsync(send, SupA);
            Assert.True(ok, message);
            Assert.Equal((p0, t0 + 100, a0 - 100), await env.PoolAsync(species, AreaA));       // held In-Transit, not available
            Assert.Null(await env.ScalarAsync<int?>("SELECT SourceCuttingProductionId FROM dbo.InternalTransfers WHERE Id = @Id", ("@Id", id)));

            // Main Office confirms all 100
            var (confirmed, cm, _) = await transfers.ConfirmReceiptAsync(id, 100, null, 1, "e2e-test");
            Assert.True(confirmed, cm);
            Assert.Equal((p0 - 100, t0, a0 - 100), await env.PoolAsync(species, AreaA));
            Assert.Equal(mp0 + 100, (await env.PoolAsync(species, MainOfficeArea)).Physical);

            // a second delivery that Main Office REJECTS releases the hold
            var (p1, t1, a1) = await env.PoolAsync(species, AreaA);
            var send2 = new InternalTransfer { StockType = "Cutting", SourceCuttingStockId = pool, PendingConfirmationAreaId = MainOfficeArea, Quantity = 30, CreatedBy = "e2e-test" };
            var (ok2, m2, id2) = await transfers.InsertAsync(send2, SupA);
            Assert.True(ok2, m2);
            Assert.Equal(t1 + 30, (await env.PoolAsync(species, AreaA)).InTransit);
            var (rejected, rm) = await transfers.RejectAsync(id2, "e2e reject", "e2e-test");
            Assert.True(rejected, rm);
            Assert.Equal((p1, t1, a1), await env.PoolAsync(species, AreaA));
        }

        // ---- Pot Production and Cutting Tray Sowing still use AVAILABLE cutting stock ----

        [SkippableFact]
        public async Task PotProduction_AndTraySowing_UseAvailableStock_InTransitCuttingsAreNotUsable_NewPotStockIs()
        {
            var env = await OpenAsync();
            var entries = env.Get<CuttingProductionRepository>();
            var species = await env.SpeciesOfAsync(MpA);
            // test setup on the scratch copy only: this species has no growing days configured in the data
            await env.ScalarAsync<int>("UPDATE dbo.PlantSpecies SET ReadyStockDays = 60 WHERE Id = @S AND ReadyStockDays IS NULL; SELECT 1", ("@S", species));

            // 1000 for Pot Production (available) + 500 for Main Office (physically there, but In-Transit -> NOT available)
            Assert.True((await entries.InsertAsync(Entry(MpA, SupA, 1000, CuttingDestination.PotProduction), SupA)).Success);
            Assert.True((await entries.InsertAsync(Entry(MpA, SupA, 500, CuttingDestination.MainOffice), SupA)).Success);
            var pool = (await env.PoolIdAsync(species, AreaA))!.Value;
            var (physical, inTransit, available) = await env.PoolAsync(species, AreaA);
            Assert.True(inTransit >= 500);
            Assert.Equal(physical - inTransit, available);
            Assert.True(available >= 1000);

            // Pot Production: more than AVAILABLE is refused although physical stock would cover it
            var pots = env.Get<PotBatchRepository>();
            PotProductionBatch Batch(decimal allocated) => new()
            {
                SourceCuttingStockId = pool, CuttingAllocated = allocated, AreaId = AreaA, PotSize = "4 inch", ProductionStartDate = DateTime.Today,
                ExpectedReadyDate = DateTime.Today.AddDays(30), SupervisorId = SupA, CreatedBy = "e2e-test"
            };
            Assert.True(physical > available);
            var (tooMany, tooManyMessage, _) = await pots.CreateAsync(Batch(available + 1), 21);
            Assert.False(tooMany);
            Assert.Contains("available", tooManyMessage, StringComparison.OrdinalIgnoreCase);
            var (batchOk, batchMessage, _) = await pots.CreateAsync(Batch(50), 21);
            Assert.True(batchOk, batchMessage);
            var afterPot = await env.PoolAsync(species, AreaA);
            Assert.Equal(physical - 50, afterPot.Physical);

            // Cutting Tray Sowing: cuttings that are In-Transit cannot be sown either
            var sowings = env.Get<SeedSowingRepository>();
            SeedSowing Sowing(decimal q) => new()
            {
                SourceCuttingStockId = pool, SeedQuantity = q, CavityType = "24 Cavity", SowingDate = DateTime.Today, AreaId = AreaA,
                SupervisorId = SupA, CreatedBy = "e2e-test", CreatedById = 21
            };
            var beyondAvailable = (Math.Floor(afterPot.Available / 24) + 1) * 24;              // more than available, less than physical
            Assert.True(beyondAvailable <= afterPot.Physical);
            var (sowTooMany, sowMessage, _) = await sowings.InsertFromCuttingAsync(Sowing(beyondAvailable), 21, _ => true, _ => true);
            Assert.False(sowTooMany);
            Assert.NotNull(sowMessage);
            var (sowOk, sowOkMessage, _) = await sowings.InsertFromCuttingAsync(Sowing(240), 21, _ => true, _ => true);
            Assert.True(sowOk, sowOkMessage);
            Assert.Equal(afterPot.Physical - 240, (await env.PoolAsync(species, AreaA)).Physical);

            // Pot Production destination: the new quantity IS available (what the pages read: AvailableQuantity)
            var (p3, _, a3) = await env.PoolAsync(species, AreaA);
            Assert.True((await entries.InsertAsync(Entry(MpA, SupA, 700, CuttingDestination.PotProduction), SupA)).Success);
            var (p4, _, a4) = await env.PoolAsync(species, AreaA);
            Assert.Equal((p3 + 700, a3 + 700), (p4, a4));
        }

        // ---- the PAGES: Cutting Entry form and the Cutting Production list ----

        private static T Page<T>(Env env, int userId, params int[] areas) where T : PageModel
        {
            var claims = new List<Claim> { new("UserId", userId.ToString()), new(MinimumAuthorizationLevelHandler.PermissionClaimType, "MotherPlant.Enter"), new(MinimumAuthorizationLevelHandler.PermissionClaimType, "MotherPlant.View") };
            claims.AddRange(areas.Select(a => new Claim(AreaAccessService.AreaAccessClaimType, a.ToString())));
            var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role)) };
            var page = ActivatorUtilities.CreateInstance<T>(env.Provider);
            page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.RazorPages.CompiledPageActionDescriptor()));
            page.TempData = new TempDataDictionary(http, new NullTempData());
            return page;
        }

        private sealed class NullTempData : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
            public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
        }

        private static void Fill(PlantStockManager.Pages.Production.Cutting.CreateModel m, Guid token, int quantity, string? destination)
        {
            m.SubmissionToken = token; m.MotherPlantId = MpA; m.SupervisorId = SupA; m.Quantity = quantity; m.CuttingDate = DateTime.Today; m.Destination = destination;
        }

        [SkippableFact]
        public async Task EntryPage_EndToEnd_MainOffice_PotProduction_MissingDestination_AndTheRegister()
        {
            var env = await OpenAsync();
            var Form = () => Page<PlantStockManager.Pages.Production.Cutting.CreateModel>(env, SupA, AreaA);

            // GET: a fresh token, only the user's own Area's Mother Plants, the single Main Office Area
            var form = Form();
            await form.OnGetAsync(null);
            Assert.NotEqual(Guid.Empty, form.SubmissionToken);
            Assert.Contains(form.MotherPlants, m => m.Id == MpA);
            Assert.DoesNotContain(form.MotherPlants, m => m.AreaId == AreaB);                  // Area isolation on the form
            Assert.Single(form.MainOfficeAreas);

            // missing destination -> validation error, nothing saved
            var rows0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions");
            var missing = Form(); Fill(missing, Guid.NewGuid(), 90, null);
            Assert.IsType<PageResult>(await missing.OnPostAsync());
            Assert.Contains(missing.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.Contains("Choose where the cuttings go"));
            Assert.Equal(rows0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions"));

            // Main Office
            var main = Form(); Fill(main, Guid.NewGuid(), 120, CuttingDestination.MainOffice);
            Assert.IsType<RedirectToPageResult>(await main.OnPostAsync());
            Assert.Contains("sent to Main Office", (string)main.TempData["Success"]!);

            // Pot Production
            var pot = Form(); Fill(pot, Guid.NewGuid(), 130, CuttingDestination.PotProduction);
            Assert.IsType<RedirectToPageResult>(await pot.OnPostAsync());
            Assert.Contains("for Pot Production", (string)pot.TempData["Success"]!);

            // a repeated submit of the same form
            var repeat = Form(); Fill(repeat, pot.SubmissionToken, 130, CuttingDestination.PotProduction);
            Assert.IsType<RedirectToPageResult>(await repeat.OnPostAsync());
            Assert.Contains("nothing was added twice", (string)repeat.TempData["Success"]!);
            Assert.Equal(rows0 + 2, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingProductions"));   // Main Office + Pot only

            // an expired form (no token) and a user of another Area
            var noToken = Form(); Fill(noToken, Guid.Empty, 5, CuttingDestination.PotProduction);
            Assert.IsType<PageResult>(await noToken.OnPostAsync());
            var foreign = Page<PlantStockManager.Pages.Production.Cutting.CreateModel>(env, SupB, AreaB);
            Fill(foreign, Guid.NewGuid(), 5, CuttingDestination.PotProduction);
            Assert.IsType<PageResult>(await foreign.OnPostAsync());
            Assert.Contains(foreign.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.Contains("not authorized"));

            // the Cutting Production list shows the destination + the delivery, only for the user's Areas
            var register = Page<PlantStockManager.Pages.Production.Cutting.IndexModel>(env, SupA, AreaA);
            await register.OnGetAsync(DateTime.Today, DateTime.Today);
            var viaMainOffice = register.Items.Single(i => i.DestinationType == CuttingDestination.MainOffice && i.Quantity == 120);
            Assert.Equal("Main Office", CuttingDestination.Label(viaMainOffice.DestinationType));
            Assert.Equal(MainOfficeArea, viaMainOffice.DestinationAreaId);
            Assert.NotNull(viaMainOffice.TransferCode);
            Assert.Equal("PendingConfirmation", viaMainOffice.TransferStatus);
            var viaPot = register.Items.Single(i => i.DestinationType == CuttingDestination.PotProduction && i.Quantity == 130);
            Assert.Null(viaPot.TransferCode);
            Assert.DoesNotContain(register.Items, i => i.AreaId == AreaB);                     // Area 122 entries are not visible to an Area 1 user
        }

        // ---- historical entries ----

        [SkippableFact]
        public async Task HistoricalEntries_StillListWithNoDestination()
        {
            var env = await OpenAsync();
            var register = Page<PlantStockManager.Pages.Production.Cutting.IndexModel>(env, SupB, AreaB);
            await register.OnGetAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 27));
            var historical = register.Items.Where(i => i.ProductionCode is "CP-2026-00001" or "CP-2026-00005").ToList();
            Assert.Equal(2, historical.Count);
            Assert.All(historical, i => { Assert.Null(i.DestinationType); Assert.Null(i.TransferCode); Assert.Equal("-", CuttingDestination.Label(i.DestinationType)); });
        }
    }
}
