using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using HistoryPage = PlantStockManager.Pages.Production.CuttingStock.MyTransactionsModel;

namespace PlantStockManager.Tests
{
    // CORRECTION #7 -- end-to-end: the REAL Cutting Delivery History (page model + repository) against a SCRATCH COPY of the test
    // database (same safety rules as the other E2E classes: PSM_SCRATCH_CONNECTION, database name must start with PlantsIMS2_Scratch_).
    //
    // Entries and deliveries are created through the real Cutting Entry, Confirm Receipt and Reject code:
    //   A1 Area 1   Mother Plant 114  supervisor 115  Main Office  100  entered by Kiran    delivery Awaiting Main Office
    //   A2 Area 1   Mother Plant 114  supervisor 115  Pot Prod.     200  entered by Kiran    (no delivery)
    //   A3 Area 1   Mother Plant 114  supervisor 115  Main Office   70  entered by Kiran    Awaiting Main Office
    //   B1 Area 122 Mother Plant 106  supervisor 101  Main Office   50  entered by Prajwal  Completed, received 50 by Mahadev(11)
    //   B2 Area 122 Mother Plant 106  supervisor 101  Main Office   60  entered by Prajwal  Rejected by Reshma ("Damaged in transit")
    //   B3 Area 122 Mother Plant 106  supervisor 101  Pot Prod.     30  entered by Prajwal  (no delivery)
    //   B5 Area 122 Mother Plant 106  supervisor 101  Main Office  100  entered by Prajwal  Completed, received 90 by Maya(9): shortfall 10
    // plus the REAL older data: 3 deliveries not linked to any entry (ids 247, 256, 257) and 6 entries with no destination recorded.
    // Other E2E classes share the copy, so checks look at THIS data (or at Area membership), never at totals.
    [Collection("ScratchDb")]
    public class CuttingDeliveryHistoryE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MpA = 114, SupA = 115, AreaA = 1, MpB = 106, SupB = 101, AreaB = 122, MainOfficeArea = 2, Mahadev = 11, Maya = 9;
        private static readonly int[] OlderDeliveries = { 247, 256, 257 };
        private static readonly int[] HistoricalEntries = { 167, 171, 172, 173, 174, 175 };

        private sealed class NullTempData : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
            public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
        }

        private sealed class StaticMonitor<T> : IOptionsMonitor<T>
        {
            public StaticMonitor(T value) => CurrentValue = value;
            public T CurrentValue { get; }
            public T Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }

        private sealed class Data
        {
            public int A1, A2, A3, B1, B2, B3, B5;
            public int T_A1, T_A3, T_B1, T_B2, T_B5;      // the delivery (transfer) ids
            public int[] Mine => new[] { A1, A2, A3, B1, B2, B3, B5 };
            public int SpeciesA, SpeciesB; public int? PolyhouseA, PolyhouseB;
        }

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

            public async Task<ClaimsPrincipal> LoginAsync(int userId)
            {
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                return await factory.CreatePrincipalAsync(user!);
            }

            public HistoryPage Page(ClaimsPrincipal principal)
            {
                var http = new DefaultHttpContext { User = principal };
                var page = ActivatorUtilities.CreateInstance<HistoryPage>(Provider);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()));
                page.TempData = new TempDataDictionary(http, new NullTempData());
                return page;
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

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddSingleton<SeedlingAreaScope>();
            services.AddScoped<UserClaimsFactory>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            return new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider() };
        }

        private static async Task<(int Entry, int Transfer)> EnterAsync(Env env, int motherPlant, int supervisor, decimal quantity, string destination, int daysAgo, string enteredBy)
        {
            var entry = new CuttingProduction
            {
                MotherPlantId = motherPlant, SupervisorId = supervisor, Quantity = quantity, CuttingDate = DateTime.Today.AddDays(-daysAgo),
                DestinationType = destination, SubmissionToken = Guid.NewGuid(), CreatedBy = enteredBy, Remarks = $"e2e {enteredBy}"
            };
            var (ok, message, id) = await env.Get<CuttingProductionRepository>().InsertAsync(entry, supervisor);
            Assert.True(ok, message);
            return (id, entry.TransferId ?? 0);
        }

        private static async Task<Data> SeedAsync(Env env)
        {
            var transfers = env.Get<InternalTransferRepository>();
            var d = new Data();
            (d.A1, d.T_A1) = await EnterAsync(env, MpA, SupA, 100, CuttingDestination.MainOffice, 0, "Kiran");
            (d.A2, _) = await EnterAsync(env, MpA, SupA, 200, CuttingDestination.PotProduction, 1, "Kiran");
            (d.A3, d.T_A3) = await EnterAsync(env, MpA, SupA, 70, CuttingDestination.MainOffice, 4, "Kiran");
            (d.B1, d.T_B1) = await EnterAsync(env, MpB, SupB, 50, CuttingDestination.MainOffice, 2, "Prajwal");
            (d.B2, d.T_B2) = await EnterAsync(env, MpB, SupB, 60, CuttingDestination.MainOffice, 3, "Prajwal");
            (d.B3, _) = await EnterAsync(env, MpB, SupB, 30, CuttingDestination.PotProduction, 5, "Prajwal");
            (d.B5, d.T_B5) = await EnterAsync(env, MpB, SupB, 100, CuttingDestination.MainOffice, 6, "Prajwal");
            Assert.True((await transfers.ConfirmReceiptAsync(d.T_B1, 50, null, Mahadev, "Mahadev")).Success);
            Assert.True((await transfers.RejectAsync(d.T_B2, "Damaged in transit", "Reshma")).Success);
            Assert.True((await transfers.ConfirmReceiptAsync(d.T_B5, 90, "10 dried up", Maya, "Maya")).Success);
            d.SpeciesA = await env.ScalarAsync<int>("SELECT SpeciesId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpA));
            d.SpeciesB = await env.ScalarAsync<int>("SELECT SpeciesId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpB));
            d.PolyhouseA = await env.ScalarAsync<int?>("SELECT PolyhouseId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpA));
            d.PolyhouseB = await env.ScalarAsync<int?>("SELECT PolyhouseId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpB));
            return d;
        }

        private static int[] Set(params int[] ids) => ids.OrderBy(i => i).ToArray();

        // Rows of THIS test data (entries), found by their Cutting Entry id
        private static CuttingDeliveryHistoryRow Row(IEnumerable<CuttingDeliveryHistoryRow> rows, int entryId) => rows.Single(r => r.ProductionId == entryId);
        private static int[] Mine(Data d, IEnumerable<CuttingDeliveryHistoryRow> rows) => rows.Where(r => r.ProductionId.HasValue && d.Mine.Contains(r.ProductionId.Value)).Select(r => r.ProductionId!.Value).OrderBy(i => i).ToArray();

        private static async Task<HistoryPage> LoadAsync(Env env, ClaimsPrincipal user, Action<HistoryPage>? set = null)
        {
            var page = env.Page(user);
            set?.Invoke(page);
            await page.OnGetAsync(null);
            return page;
        }

        private static async Task<CuttingDeliveryHistoryRow[]> AllAsync(Env env)
            => (await env.Get<CuttingDeliveryHistoryRepository>().SearchAsync(new CuttingDeliveryHistoryFilter(), null)).ToArray();

        // ---- every value is the one its underlying relationship holds ----

        [SkippableFact]
        public async Task EveryColumn_MatchesTheDatabaseRelationships()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var rows = await AllAsync(env);

            async Task<string> Str(string sql, params (string, object?)[] a) => (await env.ScalarAsync<string>(sql, a))?.Trim() ?? "";
            foreach (var (entry, transfer, motherPlant, supervisor, area, enteredBy) in new[]
            {
                (d.A1, d.T_A1, MpA, SupA, AreaA, "Kiran"), (d.B1, d.T_B1, MpB, SupB, AreaB, "Prajwal"), (d.B2, d.T_B2, MpB, SupB, AreaB, "Prajwal"), (d.B5, d.T_B5, MpB, SupB, AreaB, "Prajwal")
            })
            {
                var r = Row(rows, entry);
                Assert.Equal(entry, r.ProductionId);
                Assert.Equal(await Str("SELECT ProductionCode FROM dbo.CuttingProductions WHERE Id = @I", ("@I", entry)), r.ProductionCode);
                Assert.Equal(DateTime.Today.AddDays(-(await env.ScalarAsync<int>("SELECT DATEDIFF(DAY, CuttingDate, CAST(GETDATE() AS DATE)) FROM dbo.CuttingProductions WHERE Id = @I", ("@I", entry)))), r.ProductionDate);
                Assert.Equal(transfer, r.TransferId);
                Assert.Equal(await Str("SELECT TransferCode FROM dbo.InternalTransfers WHERE Id = @I", ("@I", transfer)), r.TransferCode);
                Assert.Equal(await Str("SELECT Status FROM dbo.InternalTransfers WHERE Id = @I", ("@I", transfer)), r.TransferStatus);
                Assert.Equal(await Str("SELECT MotherPlantCode FROM dbo.MotherPlants WHERE Id = @M", ("@M", motherPlant)), r.MotherPlantCode);                       // Mother Plant
                Assert.Equal(await Str("SELECT ph.Name FROM dbo.MotherPlants mp JOIN dbo.Polyhouses ph ON ph.Id = mp.PolyhouseId WHERE mp.Id = @M", ("@M", motherPlant)), r.SourcePolyhouseName);   // source Polyhouse = the Mother Plant's
                Assert.Equal(await Str("SELECT Name FROM dbo.IMSUsers WHERE Id = @U", ("@U", supervisor)), r.SupervisorName);                                              // Cutting Supervisor
                Assert.Equal(supervisor, r.SupervisorId);
                Assert.Equal(await Str("SELECT Name FROM dbo.Area WHERE Id = @A", ("@A", area)), r.SourceAreaName);                                                       // source Area
                Assert.Equal(await Str("SELECT Name FROM dbo.Area WHERE Id = @A", ("@A", MainOfficeArea)), r.DestinationAreaName);                                         // destination Area = Main Office
                Assert.Equal(CuttingDestination.MainOffice, r.DestinationType);
                Assert.Equal(enteredBy, r.EnteredBy);                                                                                                                       // entered by = the username stored
                Assert.Equal(await Str("SELECT Name FROM dbo.IMSUsers WHERE Username = @N", ("@N", enteredBy)), r.EnteredByName);
                Assert.Equal(await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.InternalTransfers WHERE Id = @I", ("@I", transfer)), r.Quantity);
                Assert.Equal(await env.ScalarAsync<DateTime>("SELECT CreatedDate FROM dbo.InternalTransfers WHERE Id = @I", ("@I", transfer)), r.DeliveryDate);
            }

            // the three people are three different things
            var confirmed = Row(rows, d.B1);
            Assert.Equal((SupB, "Prajwal", Mahadev), (confirmed.SupervisorId, confirmed.EnteredBy, confirmed.ConfirmedById));
            Assert.Equal(await Str("SELECT Name FROM dbo.IMSUsers WHERE Id = @U", ("@U", Mahadev)), confirmed.ConfirmedByName);
            Assert.Equal(await env.ScalarAsync<DateTime>("SELECT ConfirmedDate FROM dbo.InternalTransfers WHERE Id = @I", ("@I", d.T_B1)), confirmed.ConfirmedDate);
            Assert.NotEqual(confirmed.SupervisorName, confirmed.ConfirmedByName);
            Assert.NotEqual(confirmed.SupervisorName, confirmed.EnteredByName);

            // remarks: the entry's own remarks and the delivery's (auto-worded with the entry code)
            Assert.Equal("e2e Kiran", Row(rows, d.A1).EntryRemarks);
            Assert.StartsWith("Cutting entry " + Row(rows, d.A1).ProductionCode, Row(rows, d.A1).DeliveryRemarks);
        }

        [SkippableFact]
        public async Task EveryDeliveryStatus_IsShownFromTheRealTransferState()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var rows = await AllAsync(env);

            var pending = Row(rows, d.A1);
            Assert.Equal(("PendingConfirmation", "PendingConfirmation"), (pending.TransferStatus, pending.StatusKey));
            Assert.Null(pending.ConfirmedById); Assert.Null(pending.ConfirmedQuantity); Assert.Null(pending.TransitLoss); Assert.Null(pending.RejectedBy);
            Assert.Equal(MainOfficeArea, pending.PendingAreaId);

            var completed = Row(rows, d.B1);
            Assert.Equal(("Completed", "Completed"), (completed.TransferStatus, completed.StatusKey));
            Assert.Equal((50m, 50m, 0m), (completed.Quantity, completed.ConfirmedQuantity, completed.TransitLoss));

            var shortfall = Row(rows, d.B5);
            Assert.Equal(("Completed", "CompletedShortfall"), (shortfall.TransferStatus, shortfall.StatusKey));
            Assert.Equal((100m, 90m, 10m), (shortfall.Quantity, shortfall.ConfirmedQuantity, shortfall.TransitLoss));
            Assert.Equal("10 dried up", shortfall.Reason);
            Assert.Equal(await env.ScalarAsync<decimal>("SELECT -SUM(Quantity) FROM dbo.CuttingStockTransactions WHERE TransactionType = N'TransitLoss' AND ReferenceType = N'InternalTransfer' AND ReferenceId = @T", ("@T", d.T_B5)), shortfall.TransitLoss);   // = the ledger's loss

            var rejected = Row(rows, d.B2);
            Assert.Equal(("Rejected", "Rejected"), (rejected.TransferStatus, rejected.StatusKey));
            Assert.Equal("Reshma", rejected.RejectedBy);
            Assert.NotNull(rejected.RejectedDate);
            Assert.Equal("Damaged in transit", rejected.Reason);
            Assert.Null(rejected.ConfirmedById); Assert.Null(rejected.TransitLoss);
        }

        [SkippableFact]
        public async Task ThingsThatWereNotDelivered_AreNeverShownAsDeliveries()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var rows = await AllAsync(env);

            foreach (var id in new[] { d.A2, d.B3 })                                       // "Use for Pot Production"
            {
                var r = Row(rows, id);
                Assert.Equal(CuttingDestination.PotProduction, r.DestinationType);
                Assert.False(r.HasDelivery);
                Assert.Equal("None", r.StatusKey);
                Assert.Null(r.TransferId); Assert.Null(r.PendingAreaId);
                Assert.Equal(r.SourceAreaId, r.DestinationAreaId);                         // it stays in the Area
            }
            foreach (var id in HistoricalEntries)                                          // destination never recorded
            {
                var r = Row(rows, id);
                Assert.Equal(CuttingProductionFilter.DestinationNotRecorded, r.DestinationType);
                Assert.False(r.HasDelivery);
                Assert.Equal("None", r.StatusKey);
                Assert.Null(r.DestinationAreaId);                                          // not guessed
            }
        }

        [SkippableFact]
        public async Task OlderDeliveries_StayVisible_WithNothingGuessed()
        {
            var env = await OpenAsync();
            var fingerprint = await env.ScalarAsync<string>("SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(*))) FROM dbo.InternalTransfers WHERE Id IN (247, 256, 257)");
            await SeedAsync(env);
            var rows = await AllAsync(env);
            var older = rows.Where(r => r.Kind == "D" && r.TransferId.HasValue && OlderDeliveries.Contains(r.TransferId.Value)).OrderBy(r => r.TransferId).ToList();
            Assert.Equal(OlderDeliveries, older.Select(r => r.TransferId!.Value).ToArray());
            Assert.All(older, r =>
            {
                Assert.False(r.HasEntry);
                Assert.Null(r.ProductionId); Assert.Null(r.MotherPlantId); Assert.Null(r.SupervisorId); Assert.Null(r.SourcePolyhouseId);   // never guessed
                Assert.Equal(CuttingDestination.MainOffice, r.DestinationType);
                Assert.Equal(MainOfficeArea, r.DestinationAreaId);                                                                                 // a fact of the delivery itself
                Assert.NotNull(r.EnteredBy);
                Assert.NotNull(r.ConfirmedByName);
            });
            var (t247, t256, t257) = (older[0], older[1], older[2]);
            Assert.Equal(("Completed", 4000m, 4000m), (t247.StatusKey, t247.Quantity, t247.ConfirmedQuantity));
            Assert.Equal(("CompletedShortfall", 24500m, 24000m, 500m), (t256.StatusKey, t256.Quantity, t256.ConfirmedQuantity, t256.TransitLoss));
            Assert.Equal(("CompletedShortfall", 4000m, 3700m, 300m), (t257.StatusKey, t257.Quantity, t257.ConfirmedQuantity, t257.TransitLoss));
            Assert.Equal(("Prajwal", "Kiran", "Kiran"), (t247.EnteredBy, t256.EnteredBy, t257.EnteredBy));
            Assert.Equal("missing", t256.Reason);
            Assert.Equal(fingerprint, await env.ScalarAsync<string>("SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(*))) FROM dbo.InternalTransfers WHERE Id IN (247, 256, 257)"));   // untouched
        }

        // ---- each filter alone ----

        [SkippableFact]
        public async Task EveryFilter_OnItsOwn_ReturnsExactlyTheMatchingRecords()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var repo = env.Get<CuttingDeliveryHistoryRepository>();
            async Task<int[]> With(Action<CuttingDeliveryHistoryFilter> set)
            {
                var f = new CuttingDeliveryHistoryFilter { From = DateTime.Today.AddDays(-30), To = DateTime.Today };
                set(f); f.Normalize();
                return Mine(d, await repo.SearchAsync(f, null));
            }

            Assert.Equal(Set(d.A1, d.A2, d.B1), await With(f => { f.From = DateTime.Today.AddDays(-2); f.To = DateTime.Today; }));                       // date range
            Assert.Equal(Set(d.A1, d.A2, d.A3), await With(f => f.SourceAreaId = AreaA));                                                                 // source Area
            Assert.Equal(Set(d.B1, d.B2, d.B3, d.B5), await With(f => f.SourceAreaId = AreaB));
            if (d.PolyhouseA.HasValue && d.PolyhouseA != d.PolyhouseB)                                                                                    // source Polyhouse
                Assert.Equal(Set(d.A1, d.A2, d.A3), await With(f => f.SourcePolyhouseId = d.PolyhouseA));
            if (d.PolyhouseB.HasValue && d.PolyhouseA != d.PolyhouseB)
                Assert.Equal(Set(d.B1, d.B2, d.B3, d.B5), await With(f => f.SourcePolyhouseId = d.PolyhouseB));
            Assert.Equal(Set(d.A1, d.A2, d.A3), await With(f => f.MotherPlantId = MpA));                                                                  // Mother Plant
            Assert.Equal(Set(d.B1, d.B2, d.B3, d.B5), await With(f => f.MotherPlantId = MpB));
            Assert.Equal(Set(d.A1, d.A2, d.A3), await With(f => f.SupervisorId = SupA));                                                                  // Cutting Supervisor
            Assert.Equal(Set(d.B1, d.B2, d.B3, d.B5), await With(f => f.SupervisorId = SupB));
            Assert.Equal(Set(d.A1, d.A3, d.B1, d.B2, d.B5), await With(f => f.Destination = CuttingDestination.MainOffice));                             // destination
            Assert.Equal(Set(d.A2, d.B3), await With(f => f.Destination = CuttingDestination.PotProduction));
            Assert.Empty(await With(f => f.Destination = CuttingProductionFilter.DestinationNotRecorded));                                                // (none of the new ones)
            Assert.Equal(Set(d.A1, d.A3, d.B1, d.B2, d.B5), await With(f => f.DestinationAreaId = MainOfficeArea).ContinueWith(t => t.Result.Where(i => i != d.A2 && i != d.B3).ToArray()));   // destination Area (Pot Production's = its own Area)
            Assert.Equal(Set(d.A1, d.A3), await With(f => f.DeliveryStatus = "PendingConfirmation"));                                                     // statuses
            Assert.Equal(Set(d.B1), await With(f => f.DeliveryStatus = "Completed"));
            Assert.Equal(Set(d.B5), await With(f => f.DeliveryStatus = "CompletedShortfall"));
            Assert.Equal(Set(d.B2), await With(f => f.DeliveryStatus = "Rejected"));
            Assert.Equal(Set(d.A2, d.B3), await With(f => f.DeliveryStatus = "None"));
            Assert.Equal(Set(d.A1, d.A2, d.A3), await With(f => f.EnteredBy = "Kiran"));                                                                  // entered by
            Assert.Equal(Set(d.B1, d.B2, d.B3, d.B5), await With(f => f.EnteredBy = "Prajwal"));
            Assert.Equal(Set(d.B1), await With(f => f.ReceivedById = Mahadev));                                                                           // received by
            Assert.Equal(Set(d.B5), await With(f => f.ReceivedById = Maya));
        }

        [SkippableFact]
        public async Task DestinationArea_OfPotProduction_IsTheAreaItself()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var repo = env.Get<CuttingDeliveryHistoryRepository>();
            var rows = await repo.SearchAsync(new CuttingDeliveryHistoryFilter { From = DateTime.Today.AddDays(-30), To = DateTime.Today, DestinationAreaId = AreaA }, null);
            Assert.Equal(Set(d.A2), Mine(d, rows));                                                       // Pot Production stays in Area 1
        }

        [SkippableFact]
        public async Task Filters_CombineWithAnd()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var repo = env.Get<CuttingDeliveryHistoryRepository>();
            async Task<int[]> With(Action<CuttingDeliveryHistoryFilter> set)
            {
                var f = new CuttingDeliveryHistoryFilter { From = DateTime.Today.AddDays(-30), To = DateTime.Today };
                set(f); f.Normalize();
                return Mine(d, await repo.SearchAsync(f, null));
            }
            Assert.Equal(Set(d.A1, d.A2), await With(f => { f.From = DateTime.Today.AddDays(-3); f.SourceAreaId = AreaA; f.SupervisorId = SupA; }));       // date + Area + supervisor
            Assert.Equal(Set(d.A1, d.A3), await With(f => { f.Destination = CuttingDestination.MainOffice; f.DeliveryStatus = "PendingConfirmation"; f.SourceAreaId = AreaA; }));
            Assert.Equal(Set(d.B5), await With(f => { f.SourceAreaId = AreaB; f.MotherPlantId = MpB; f.DeliveryStatus = "CompletedShortfall"; f.ReceivedById = Maya; f.EnteredBy = "Prajwal"; f.DestinationAreaId = MainOfficeArea; }));
            Assert.Equal(Set(d.B1), await With(f => { f.To = DateTime.Today.AddDays(-2); f.From = DateTime.Today.AddDays(-2); f.Destination = CuttingDestination.MainOffice; f.SourceAreaId = AreaB; }));
            Assert.Empty(await With(f => { f.SourceAreaId = AreaA; f.SupervisorId = SupB; }));                                                            // cannot all be true
            Assert.Empty(await With(f => { f.Destination = CuttingDestination.PotProduction; f.DeliveryStatus = "PendingConfirmation"; }));
            Assert.Empty(await With(f => { f.EnteredBy = "Kiran"; f.ReceivedById = Mahadev; }));
        }

        // ---- no match, Clear filters, kept values, invalid values ----

        [SkippableFact]
        public async Task NoMatch_ClearFilters_AndTheChosenValuesStayOnScreen()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var admin = await env.LoginAsync(21);

            var none = await LoadAsync(env, admin, p => { p.SupervisorId = 999999; });
            Assert.Empty(none.Rows);
            Assert.True(none.FilterActive);
            Assert.Equal(999999, none.SupervisorId);

            var filtered = await LoadAsync(env, admin, p =>
            {
                p.From = DateTime.Today.AddDays(-10); p.To = DateTime.Today; p.SourceAreaId = AreaB; p.MotherPlantId = MpB; p.SupervisorId = SupB;
                p.Destination = CuttingDestination.MainOffice; p.DestinationAreaId = MainOfficeArea; p.DeliveryStatus = "CompletedShortfall"; p.EnteredBy = "Prajwal"; p.ReceivedById = Maya;
            });
            Assert.Equal(Set(d.B5), Mine(d, filtered.Rows));
            Assert.Equal((AreaB, MpB, SupB, CuttingDestination.MainOffice, MainOfficeArea, "CompletedShortfall", "Prajwal", Maya),
                         (filtered.SourceAreaId!.Value, filtered.MotherPlantId!.Value, filtered.SupervisorId!.Value, filtered.Destination!, filtered.DestinationAreaId!.Value, filtered.DeliveryStatus!, filtered.EnteredBy!, filtered.ReceivedById!.Value));
            Assert.Contains(filtered.Options.EnteredBy, o => o.Value == "Prajwal");                      // the chosen value is an option, so it stays selected
            Assert.Contains(filtered.Options.ReceivedBy, o => o.Id == Maya);

            var cleared = await LoadAsync(env, admin);                                                    // "Clear filters" = the page with no query string
            Assert.False(cleared.FilterActive);
            Assert.Null(cleared.SourceAreaId); Assert.Null(cleared.Destination); Assert.Null(cleared.DeliveryStatus); Assert.Null(cleared.EnteredBy); Assert.Null(cleared.From);
            Assert.Equal(d.Mine.Length, Mine(d, cleared.Rows).Length);                                    // everything again
            Assert.True(cleared.Rows.Count(r => r.Kind == "D") >= 3);                                     // old records are not hidden by any default

            Assert.Equal(cleared.Rows.Sum(r => r.Quantity), cleared.TotalQuantity);                       // the total is of the filtered records
            Assert.Equal(filtered.Rows.Sum(r => r.Quantity), filtered.TotalQuantity);
            // every matching row is a 100-sent / 90-received / 10-lost delivery (earlier tests of the run seeded identical ones)
            Assert.All(filtered.Rows, r => Assert.Equal((100m, 90m, 10m), (r.Quantity, r.ConfirmedQuantity, r.TransitLoss)));
            Assert.Equal((100m * filtered.Rows.Count, 90m * filtered.Rows.Count, 10m * filtered.Rows.Count), (filtered.TotalQuantity, filtered.TotalReceived, filtered.TotalTransitLoss));
        }

        [SkippableTheory]
        [InlineData("Destination", "everything")]
        [InlineData("DeliveryStatus", "bogus")]
        [InlineData("DeliveryStatus", "1; DROP TABLE dbo.InternalTransfers; --")]
        public async Task InvalidValues_AreIgnoredWithANotice(string field, string value)
        {
            var env = await OpenAsync();
            await SeedAsync(env);
            var admin = await env.LoginAsync(21);
            var page = await LoadAsync(env, admin, p => { if (field == "Destination") p.Destination = value; else p.DeliveryStatus = value; });
            Assert.NotEmpty(page.Notices);
            Assert.False(page.FilterActive);
            Assert.Equal((await LoadAsync(env, admin)).Rows.Count, page.Rows.Count);                      // the same as no filter
        }

        // ---- Area security ----

        [SkippableFact]
        public async Task AreaSecurity_EachUserSeesOnlyWhatTheirAreasHave()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);

            var kiran = await LoadAsync(env, await env.LoginAsync(SupA));                                  // Area 1
            Assert.Equal(Set(d.A1, d.A2, d.A3), Mine(d, kiran.Rows));
            Assert.Contains(kiran.Rows, r => r.TransferId == 256); Assert.Contains(kiran.Rows, r => r.TransferId == 257);
            Assert.DoesNotContain(kiran.Rows, r => r.TransferId == 247);                                   // Area 122's older delivery
            Assert.All(kiran.Rows, r => Assert.True(r.SourceAreaId == AreaA || r.DestinationAreaId == AreaA));

            var achyut = await LoadAsync(env, await env.LoginAsync(SupB));                                 // Area 122
            Assert.Equal(Set(d.B1, d.B2, d.B3, d.B5), Mine(d, achyut.Rows));
            Assert.Contains(achyut.Rows, r => r.TransferId == 247);
            Assert.DoesNotContain(achyut.Rows, r => r.TransferId == 256);

            // Main Office: the deliveries addressed to it (even before it has confirmed them), never other Areas' Pot Production or unrecorded entries
            var mainOffice = await LoadAsync(env, await env.LoginAsync(Mahadev));
            Assert.Equal(Set(d.A1, d.A3, d.B1, d.B2, d.B5), Mine(d, mainOffice.Rows));
            Assert.All(new[] { 247, 256, 257 }, id => Assert.Contains(mainOffice.Rows, r => r.TransferId == id));
            Assert.DoesNotContain(mainOffice.Rows, r => r.Kind == "E" && !r.HasDelivery);
            Assert.All(mainOffice.Rows, r => Assert.True(r.DestinationAreaId == MainOfficeArea || r.PendingAreaId == MainOfficeArea || r.SourceAreaId == MainOfficeArea));

            // Green Bless Nursery (Area 127): none of the others' deliveries, only its own entry
            var greenBless = await LoadAsync(env, await env.LoginAsync(110));
            Assert.Empty(Mine(d, greenBless.Rows));
            Assert.DoesNotContain(greenBless.Rows, r => r.HasDelivery);
            Assert.All(greenBless.Rows, r => Assert.Equal(127, r.SourceAreaId));

            // a user with no Area, and an empty Area list
            var noAreas = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(AreaAccessService.RoleNameClaimType, "Mother Plant Supervisor") }, "test"));
            var nobody = await LoadAsync(env, noAreas);
            Assert.Empty(nobody.Rows);
            Assert.Empty(nobody.Options.SourceAreas);
            Assert.Empty(await env.Get<CuttingDeliveryHistoryRepository>().SearchAsync(new CuttingDeliveryHistoryFilter(), new int[0]));

            // full access sees everything
            var admin = await LoadAsync(env, await env.LoginAsync(21));
            Assert.Equal(d.Mine.Length, Mine(d, admin.Rows).Length);
            Assert.All(OlderDeliveries, id => Assert.Contains(admin.Rows, r => r.TransferId == id));
            Assert.All(HistoricalEntries, id => Assert.Contains(admin.Rows, r => r.ProductionId == id));
        }

        [SkippableFact]
        public async Task ChoosingAnotherAreaInAFilter_NeverRevealsAnything_AndTheDropdownsDoNotLeak()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var kiranUser = await env.LoginAsync(SupA);
            foreach (var attack in new (string Name, Action<HistoryPage> Set)[]
            {
                ("Source Area", p => p.SourceAreaId = AreaB), ("Mother Plant", p => p.MotherPlantId = MpB), ("Supervisor", p => p.SupervisorId = SupB),
                ("Received By", p => p.ReceivedById = Mahadev), ("Entered By", p => p.EnteredBy = "Prajwal"), ("Status", p => { p.SourceAreaId = AreaB; p.DeliveryStatus = "Rejected"; })
            })
            {
                var page = await LoadAsync(env, kiranUser, attack.Set);
                Assert.Empty(Mine(d, page.Rows).Intersect(Set(d.B1, d.B2, d.B3, d.B5)));
                Assert.All(page.Rows, r => Assert.True(r.SourceAreaId == AreaA || r.DestinationAreaId == AreaA, attack.Name));
            }
            // filtering by the Main Office Area (a legitimate destination for her deliveries) still shows only her own
            var byDestination = await LoadAsync(env, kiranUser, p => p.DestinationAreaId = MainOfficeArea);
            Assert.Empty(Mine(d, byDestination.Rows).Intersect(Set(d.B1, d.B2, d.B3, d.B5)));
            Assert.Equal(Set(d.A1, d.A3), Mine(d, byDestination.Rows));

            var options = (await LoadAsync(env, kiranUser)).Options;                                       // nothing from Area 122 in her dropdowns
            Assert.Equal(new[] { AreaA }, options.SourceAreas.Select(o => o.Id).ToArray());
            Assert.DoesNotContain(options.MotherPlants, o => o.Id == MpB);
            Assert.DoesNotContain(options.Supervisors, o => o.Id == SupB);
            Assert.DoesNotContain(options.ReceivedBy, o => o.Id == Mahadev || o.Id == Maya);
            Assert.Equal(new[] { "Kiran" }, options.EnteredBy.Select(o => o.Value).ToArray());
            Assert.DoesNotContain(options.SourcePolyhouses, o => d.PolyhouseB.HasValue && d.PolyhouseB != d.PolyhouseA && o.Id == d.PolyhouseB);
        }

        [SkippableFact]
        public async Task WhatTheOldPageShowed_IsStillShown()
        {
            var env = await OpenAsync();
            await SeedAsync(env);
            var transfers = env.Get<InternalTransferRepository>();
            var access = env.Get<AreaAccessService>();
            foreach (var userId in new[] { 21, SupA, SupB, Mahadev, 110, 111 })
            {
                var user = await env.LoginAsync(userId);
                var page = await LoadAsync(env, user);
                var seen = page.Rows.Where(r => r.TransferId.HasValue).Select(r => r.TransferId!.Value).ToHashSet();
                foreach (var area in access.FilterByArea(user, await env.Get<AreaRepository>().GetAllAreas(), a => (int?)a.Id))
                    foreach (var t in (await transfers.GetByAreaAsync(area.Id)).Where(t => t.StockType == "Cutting"))       // exactly the old page's query
                        Assert.True(seen.Contains(t.Id), $"user {userId} used to see transfer {t.Id} (Area {area.Name})");
            }
        }

        [SkippableFact]
        public async Task ViewingTheHistory_ChangesNothing()
        {
            var env = await OpenAsync();
            await SeedAsync(env);
            const string state = @"SELECT CONCAT((SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(*))) FROM dbo.InternalTransfers), '|',
                (SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(*))) FROM dbo.CuttingProductions), '|',
                (SELECT CONCAT(COUNT(*), '/', SUM(PhysicalQuantity), '/', SUM(InTransitQuantity)) FROM dbo.CuttingStock), '|', (SELECT COUNT(*) FROM dbo.CuttingStockTransactions))";
            var before = await env.ScalarAsync<string>(state);
            await LoadAsync(env, await env.LoginAsync(21), p => { p.SourceAreaId = AreaA; p.DeliveryStatus = "PendingConfirmation"; });
            await LoadAsync(env, await env.LoginAsync(Mahadev));
            Assert.Equal(before, await env.ScalarAsync<string>(state));
        }
    }
}
