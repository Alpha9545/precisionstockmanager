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
using SeedlingRecords = PlantStockManager.Pages.Data.BookingsRecordModel;
using SeedlingEditRecords = PlantStockManager.Pages.Bookings.BookingRecordsModel;
using PottedList = PlantStockManager.Pages.Production.PottedPlantBooking.IndexModel;

namespace PlantStockManager.Tests
{
    // CORRECTION #6 -- end-to-end: the REAL booking record pages and repositories against a SCRATCH COPY of the test database
    // (same safety rules as the other E2E classes: PSM_SCRATCH_CONNECTION, database name must start with PlantsIMS2_Scratch_).
    //
    // Seedling bookings (dbo.Bookings; created through BookingRepository.InsertBookingAsync, then -- scratch copy only -- the
    // BookedByOther / Status variants the application does not itself write for seedling bookings), delivery Nov / Dec 2026:
    //   S1 Sarika(3) Nov  species A   S2 Sarika(3) Nov species B   S3 Sarika(3) Dec species A   S4 Rahul(15) Nov
    //   S5 Somnath(5, inactive) Nov   S6 user 10 (no longer exists) Nov   S7 no user + BookedByOther "Walk-in Agent" Nov
    //   S8 nothing recorded Nov       S9 Sarika(3) + BookedByOther "Ramesh" Nov   S10 Sarika(3) Nov, Completed
    // Potted Plant bookings (dbo.PottedPlantBookings, Outlet Area 124; through PottedPlantBookingRepository.InsertAsync):
    //   P1 Sandeep(112)   P2 Prajwal(21)   P3 "Walk-in Agent" (other)   P4 nothing recorded   P5 Sandeep(112) + other "Ramesh"
    // Other E2E classes share the copy, so every check compares with an independent SQL oracle or looks at THIS data only.
    [Collection("ScratchDb")]
    public class BookedByFilterE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int Year = 2026, Nov = 11, Dec = 12;
        private const int Sarika = 3, Rahul = 15, Somnath = 5, Orphan = 10, Sandeep = 112, Prajwal = 21;

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

        private sealed class Seed
        {
            public int S1, S2, S3, S4, S5, S6, S7, S8, S9, S10;
            public int P1, P2, P3, P4, P5;
            public int PlantA = 4, SpeciesA = 3179, PlantB = 1, SpeciesB = 3256;
            public int[] Seedling => new[] { S1, S2, S3, S4, S5, S6, S7, S8, S9, S10 };
            public int[] Potted => new[] { P1, P2, P3, P4, P5 };
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

            public Task ExecAsync(string sql, params (string Name, object? Value)[] args) => ScalarAsync<int>(sql + "; SELECT 1", args);

            public async Task<int[]> IdsAsync(string sql, params (string Name, object? Value)[] args)
            {
                var list = new List<int>();
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) list.Add(r.GetInt32(0));
                return list.OrderBy(i => i).ToArray();
            }

            public async Task<ClaimsPrincipal> LoginAsync(int userId)
            {
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                return await factory.CreatePrincipalAsync(user!);
            }

            public T Page<T>(ClaimsPrincipal principal) where T : PageModel
            {
                var http = new DefaultHttpContext { User = principal };
                var page = ActivatorUtilities.CreateInstance<T>(Provider);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()))
                {
                    ViewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary())
                };
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

        private static async Task<int> SeedlingAsync(Env env, Seed d, int? bookedBy, int plant, int species, int month, string tag)
        {
            var (state, district) = (await env.ScalarAsync<int>("SELECT StateId FROM dbo.Bookings WHERE Id = 5044"), await env.ScalarAsync<int>("SELECT DistrictId FROM dbo.Bookings WHERE Id = 5044"));
            return await env.Get<BookingRepository>().InsertBookingAsync(new Booking
            {
                PlantId = plant, SpeciesId = species, CustomerName = "E2E-BY-" + tag, DeliveryDate = new DateTime(Year, month, 15), Quantity = 10, AddedBy = "e2e",
                Address = "e2e", Contact = "9999999999", AdvanceTaken = false, StateId = state, DistrictId = district, BookedById = bookedBy
            });
        }

        private static async Task<int> PottedAsync(Env env, int stock, int? bookedBy, string? other, string tag)
        {
            var (ok, message, id) = await env.Get<PottedPlantBookingRepository>().InsertAsync(new PottedPlantBooking
            {
                PottedPlantStockId = stock, Quantity = 1, CustomerName = "E2E-BY-" + tag, Contact = "9999999999", BookingDate = DateTime.Today,
                DeliveryDate = DateTime.Today, BookedById = bookedBy, BookedByOther = other, CreatedBy = "e2e"
            }, Prajwal);
            Assert.True(ok, message);
            return id;
        }

        private static async Task<Seed> SeedAsync(Env env)
        {
            var d = new Seed();
            d.S1 = await SeedlingAsync(env, d, Sarika, d.PlantA, d.SpeciesA, Nov, "S1");
            d.S2 = await SeedlingAsync(env, d, Sarika, d.PlantB, d.SpeciesB, Nov, "S2");
            d.S3 = await SeedlingAsync(env, d, Sarika, d.PlantA, d.SpeciesA, Dec, "S3");
            d.S4 = await SeedlingAsync(env, d, Rahul, d.PlantA, d.SpeciesA, Nov, "S4");
            d.S5 = await SeedlingAsync(env, d, Somnath, d.PlantA, d.SpeciesA, Nov, "S5");
            d.S6 = await SeedlingAsync(env, d, Orphan, d.PlantA, d.SpeciesA, Nov, "S6");
            d.S7 = await SeedlingAsync(env, d, null, d.PlantA, d.SpeciesA, Nov, "S7");
            d.S8 = await SeedlingAsync(env, d, null, d.PlantA, d.SpeciesA, Nov, "S8");
            d.S9 = await SeedlingAsync(env, d, Sarika, d.PlantA, d.SpeciesA, Nov, "S9");
            d.S10 = await SeedlingAsync(env, d, Sarika, d.PlantA, d.SpeciesA, Nov, "S10");
            await env.ExecAsync("UPDATE dbo.Bookings SET BookedByOther = 'Walk-in Agent' WHERE Id = @I", ("@I", d.S7));
            await env.ExecAsync("UPDATE dbo.Bookings SET BookedByOther = 'Ramesh' WHERE Id = @I", ("@I", d.S9));
            await env.ExecAsync("UPDATE dbo.Bookings SET Status = 'Completed' WHERE Id = @I", ("@I", d.S10));

            d.P1 = await PottedAsync(env, 193, Sandeep, null, "P1");
            d.P2 = await PottedAsync(env, 193, Prajwal, null, "P2");
            d.P3 = await PottedAsync(env, 195, null, "Walk-in Agent", "P3");
            d.P4 = await PottedAsync(env, 195, null, null, "P4");
            d.P5 = await PottedAsync(env, 195, Sandeep, "Ramesh", "P5");
            return d;
        }

        private static int[] Ids(IEnumerable<Booking> l) => l.Select(b => b.Id).OrderBy(i => i).ToArray();
        private static int[] Ids(IEnumerable<PottedPlantBooking> l) => l.Select(b => b.Id).OrderBy(i => i).ToArray();
        private static int[] Set(params int[] ids) => ids.OrderBy(i => i).ToArray();

        // an independent statement of the old seedling query (no Booking By): the oracle for "exactly the same records as before"
        private const string SeedlingOracle = @"SELECT b.Id FROM dbo.Bookings b INNER JOIN dbo.PlantTypes pt ON b.PlantId = pt.Id INNER JOIN dbo.PlantSpecies ps ON b.SpeciesId = ps.Id
            WHERE YEAR(b.DeliveryDate) = @Y AND b.Status = @S AND (@M = 0 OR MONTH(b.DeliveryDate) = @M) AND (@P IS NULL OR b.PlantId = @P) AND (@Sp IS NULL OR b.SpeciesId = @Sp)";

        private async Task<int[]> Records(Env env, Seed d, string? by, int month = Nov, string status = "Pending", int? plant = null, int? species = null, ClaimsPrincipal? user = null)
        {
            var page = env.Page<SeedlingRecords>(user ?? await env.LoginAsync(Prajwal));
            page.SelectedBookedBy = by; page.SelectedMonth = month; page.SelectedYear = Year; page.SelectedStatus = status;
            page.SelectedPlantType = plant; page.SelectedSpecies = species;
            await page.OnGetAsync();
            return Ids(page.Bookings).Intersect(d.Seedling).ToArray();
        }

        // ---- the options ----

        [SkippableFact]
        public async Task TheDropdown_ListsThePeopleAndNamesThatOccurOnBookings_IncludingInactiveMissingAndOther()
        {
            var env = await OpenAsync();
            await SeedAsync(env);
            var page = env.Page<SeedlingRecords>(await env.LoginAsync(Prajwal));
            await page.OnGetAsync();
            var labels = page.BookedByOptions.ToDictionary(o => o.Value, o => o.Label);
            Assert.Equal("Sarika Kolhe", labels["U:3"]);
            Assert.Equal("Rahul Thorve", labels["U:15"]);
            Assert.EndsWith("(inactive)", labels["U:5"]);                                                  // a historical user who is no longer active stays selectable
            Assert.Equal("User #10 (no longer in the system)", labels["U:10"]);                            // a user who no longer exists too
            Assert.Equal("Walk-in Agent (other)", labels["O:Walk-in Agent"]);
            Assert.Equal("Ramesh (other)", labels["O:Ramesh"]);
            Assert.Equal("Not recorded", labels["X:none"]);
            Assert.Equal(page.BookedByOptions.Count, page.BookedByOptions.Select(o => o.Value).Distinct().Count());   // nobody twice
            // only people who occur on bookings: the option list equals the distinct BookedById values (plus names / not recorded)
            var oracle = await env.IdsAsync("SELECT DISTINCT BookedById FROM dbo.Bookings WHERE BookedById > 0");
            Assert.Equal(oracle, page.BookedByOptions.Where(o => o.Value.StartsWith("U:")).Select(o => int.Parse(o.Value[2..])).OrderBy(i => i).ToArray());
        }

        // ---- every valid user, and the two other kinds ----

        [SkippableFact]
        public async Task EveryValidPerson_ReturnsExactlyTheirBookings()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            Assert.Equal(Set(d.S1, d.S2, d.S9), await Records(env, d, "U:3"));            // S3 is December, S10 is Completed
            Assert.Equal(Set(d.S3), await Records(env, d, "U:3", month: Dec));
            Assert.Equal(Set(d.S10), await Records(env, d, "U:3", status: "Completed"));
            Assert.Equal(Set(d.S4), await Records(env, d, "U:15"));
            Assert.Equal(Set(d.S5), await Records(env, d, "U:5"));                        // inactive
            Assert.Equal(Set(d.S6), await Records(env, d, "U:10"));                       // no longer exists

            // and for EVERY person of the dropdown, the whole register equals an independent SQL statement
            var page = env.Page<SeedlingRecords>(await env.LoginAsync(Prajwal));
            await page.OnGetAsync();
            foreach (var option in page.BookedByOptions.Where(o => o.Value.StartsWith("U:")))
            {
                var expected = await env.IdsAsync(SeedlingOracle + " AND b.BookedById = @U", ("@Y", Year), ("@S", "Pending"), ("@M", Nov), ("@P", null), ("@Sp", null), ("@U", int.Parse(option.Value[2..])));
                var p = env.Page<SeedlingRecords>(await env.LoginAsync(Prajwal));
                p.SelectedBookedBy = option.Value; p.SelectedMonth = Nov; p.SelectedYear = Year; p.SelectedStatus = "Pending";
                await p.OnGetAsync();
                Assert.Equal(expected, Ids(p.Bookings));
                Assert.All(p.Bookings, b => Assert.Equal(int.Parse(option.Value[2..]), b.BookedById));
            }
        }

        [SkippableFact]
        public async Task BookedByOther_IsNotIgnored_AndNotRecordedIsItsOwnChoice()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            Assert.Equal(Set(d.S7), await Records(env, d, "O:Walk-in Agent"));
            Assert.Equal(Set(d.S7), await Records(env, d, "O:walk-in agent"));            // case-insensitive, like the rest of the data
            Assert.Equal(Set(d.S9), await Records(env, d, "O:Ramesh"));                   // recorded next to a user: still findable by the name
            Assert.Equal(Set(d.S1, d.S2, d.S9), await Records(env, d, "U:3"));            // ... and by the user
            Assert.Equal(Set(d.S8), await Records(env, d, "X:none"));                     // neither a user nor a name
            var page = env.Page<SeedlingRecords>(await env.LoginAsync(Prajwal));
            page.SelectedBookedBy = "O:Walk-in Agent"; page.SelectedMonth = Nov; page.SelectedYear = Year;
            await page.OnGetAsync();
            Assert.Equal("Walk-in Agent", PlantStockManager.Services.BookedByFilter.Display(page.Bookings.Single(b => b.Id == d.S7).BookedById, "", page.Bookings.Single(b => b.Id == d.S7).BookedByOther));
        }

        // ---- together with the existing filters (AND) ----

        [SkippableFact]
        public async Task BookingBy_CombinesWithEveryExistingFilter_WithAnd()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            Assert.Equal(Set(d.S1, d.S9), await Records(env, d, "U:3", plant: d.PlantA));                                   // + plant type
            Assert.Equal(Set(d.S2), await Records(env, d, "U:3", plant: d.PlantB, species: d.SpeciesB));                  // + plant type + species
            Assert.Equal(Set(d.S3), await Records(env, d, "U:3", month: Dec));                                            // + month
            Assert.Empty(await Records(env, d, "U:15", month: Dec));                                                       // person has none that month
            Assert.Equal(Set(d.S10), await Records(env, d, "U:3", status: "Completed"));                                   // + status
            Assert.Empty(await Records(env, d, "U:15", plant: d.PlantB));                                                  // person has none of that plant type
            // the oracle for a full combination
            var expected = await env.IdsAsync(SeedlingOracle + " AND b.BookedById = 3", ("@Y", Year), ("@S", "Pending"), ("@M", Nov), ("@P", d.PlantA), ("@Sp", d.SpeciesA));
            var page = env.Page<SeedlingRecords>(await env.LoginAsync(Prajwal));
            page.SelectedBookedBy = "U:3"; page.SelectedMonth = Nov; page.SelectedYear = Year; page.SelectedStatus = "Pending"; page.SelectedPlantType = d.PlantA; page.SelectedSpecies = d.SpeciesA;
            await page.OnGetAsync();
            Assert.Equal(expected, Ids(page.Bookings));
        }

        // ---- no match, clear, retention, invalid values ----

        [SkippableFact]
        public async Task NoMatch_ReturnsNothing_TheValueStaysSelected_AndClearingShowsEveryone()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var page = env.Page<SeedlingRecords>(await env.LoginAsync(Prajwal));
            page.SelectedBookedBy = "U:987654"; page.SelectedMonth = Nov; page.SelectedYear = Year;      // well-formed, nobody has it
            await page.OnGetAsync();
            Assert.Empty(page.Bookings);
            Assert.True(page.BookedByActive);
            Assert.Equal("U:987654", page.SelectedBookedBy);                                            // still what the user chose

            // a person who exists but has nothing in the chosen month
            Assert.Empty(await Records(env, d, "U:15", month: 1));

            // the selected option stays selected on the page (retention) ...
            var chosen = env.Page<SeedlingRecords>(await env.LoginAsync(Prajwal));
            chosen.SelectedBookedBy = "U:3"; chosen.SelectedMonth = Nov; chosen.SelectedYear = Year;
            await chosen.OnGetAsync();
            Assert.Equal("U:3", chosen.SelectedBookedBy);
            Assert.Contains(chosen.BookedByOptions, o => o.Value == chosen.SelectedBookedBy);
            // ... and "Clear filters" (the page with no query string) shows everyone again
            var cleared = env.Page<SeedlingRecords>(await env.LoginAsync(Prajwal));
            await cleared.OnGetAsync();
            Assert.Equal("", cleared.SelectedBookedBy);
            Assert.False(cleared.BookedByActive);
            Assert.Null(cleared.Notice);
        }

        [SkippableTheory]
        [InlineData("bogus")]
        [InlineData("U:abc")]
        [InlineData("U:-1")]
        [InlineData("U:0")]
        [InlineData("O:")]
        [InlineData("U:3; DROP TABLE dbo.Bookings; --")]
        [InlineData("' OR 1=1 --")]
        public async Task InvalidValues_AreIgnoredWithANotice_AndAreNeverTreatedAsAPerson(string raw)
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var user = await env.LoginAsync(Prajwal);
            var page = env.Page<SeedlingRecords>(user);
            page.SelectedBookedBy = raw; page.SelectedMonth = Nov; page.SelectedYear = Year;
            await page.OnGetAsync();
            Assert.NotNull(page.Notice);
            Assert.False(page.BookedByActive);
            Assert.Equal("", page.SelectedBookedBy);
            var unfiltered = env.Page<SeedlingRecords>(user);
            unfiltered.SelectedMonth = Nov; unfiltered.SelectedYear = Year;
            await unfiltered.OnGetAsync();
            Assert.Equal(Ids(unfiltered.Bookings), Ids(page.Bookings));                                   // the same as no filter
            Assert.Equal(d.Seedling.Length - 2, Ids(page.Bookings).Intersect(d.Seedling).Count());          // 8 of the 10 are November + Pending (S3 is December, S10 is Completed)
            Assert.True(await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Bookings WHERE CustomerName LIKE N'E2E-BY-%'") >= 10);   // and nothing was dropped
        }

        // ---- no filter = exactly what it was ----

        [SkippableFact]
        public async Task WithNoFilter_TheRecordsAreExactlyTheSameAsBefore_ForEveryYearStatusAndMonth()
        {
            var env = await OpenAsync();
            await SeedAsync(env);
            var repo = env.Get<BookingRepository>();
            foreach (var status in new[] { "Pending", "Completed", "Cancelled" })
                foreach (var (year, month) in new[] { (2026, 0), (2026, 9), (2026, 11), (2025, 0), (2025, 6), (2024, 0) })
                {
                    var expected = await env.IdsAsync(SeedlingOracle, ("@Y", year), ("@S", status), ("@M", month), ("@P", null), ("@Sp", null));
                    var withParameterOmitted = await repo.GetBookingRecords(null, null, month, year, status, null);
                    var withAll = await repo.GetBookingRecords(null, null, month, year, status, null, BookedByFilter.All);
                    Assert.Equal(expected, Ids(withParameterOmitted));
                    Assert.Equal(expected, Ids(withAll));
                    Assert.Equal(withParameterOmitted.Select(b => b.Id), withAll.Select(b => b.Id));            // same order too
                }
        }

        // ---- historical bookings (real data, made long before this filter) ----

        [SkippableFact]
        public async Task HistoricalBookings_ByInactiveAndMissingUsers_AreFilterable_AndNeverChanged()
        {
            var env = await OpenAsync();
            var fingerprint = await env.ScalarAsync<string>("SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(*))) FROM dbo.Bookings WHERE Id < 5043");
            await SeedAsync(env);
            var repo = env.Get<BookingRepository>();
            var combos = new List<(int Year, int Month, string Status, int User)>();
            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DISTINCT YEAR(DeliveryDate), MONTH(DeliveryDate), Status, BookedById FROM dbo.Bookings WHERE BookedById IN (10, 5, 7) AND Id < 5043", conn);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) combos.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3)));
            }
            Assert.NotEmpty(combos);
            foreach (var (year, month, status, user) in combos.Take(40))
            {
                var expected = await env.IdsAsync(SeedlingOracle + " AND b.BookedById = @U AND b.Id < 5043", ("@Y", year), ("@S", status), ("@M", month), ("@P", null), ("@Sp", null), ("@U", user));
                var actual = (await repo.GetBookingRecords(null, null, month, year, status, null, BookedByFilter.Parse(BookedByFilter.UserValue(user)).Filter)).Where(b => b.Id < 5043);
                Assert.Equal(expected, Ids(actual));
                Assert.NotEmpty(expected);
                Assert.All(actual, b => Assert.Equal(user, b.BookedById));
            }
            Assert.Equal(fingerprint, await env.ScalarAsync<string>("SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(*))) FROM dbo.Bookings WHERE Id < 5043"));
        }

        // ---- the Edit Seedling Booking page: same records, same filter ----

        [SkippableFact]
        public async Task TheEditPage_FiltersIdenticallyToTheRecordsPage()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var user = await env.LoginAsync(Prajwal);
            foreach (var by in new[] { "", "U:3", "U:15", "U:5", "U:10", "O:Walk-in Agent", "O:Ramesh", "X:none", "U:987654", "bogus" })
            {
                var records = env.Page<SeedlingRecords>(user);
                records.SelectedBookedBy = by; records.SelectedMonth = Nov; records.SelectedYear = Year;
                await records.OnGetAsync();
                var edit = env.Page<SeedlingEditRecords>(user);
                edit.SelectedBookedBy = by; edit.SelectedMonth = Nov; edit.SelectedYear = Year;
                await edit.OnGetAsync();
                Assert.Equal(Ids(records.Bookings), Ids(edit.Bookings));
                Assert.Equal(records.SelectedBookedBy, edit.SelectedBookedBy);
                Assert.Equal(records.BookedByOptions.Select(o => o.Value), edit.BookedByOptions.Select(o => o.Value));
                Assert.Equal(records.Notice == null, edit.Notice == null);
            }
            var one = env.Page<SeedlingEditRecords>(user);
            one.SelectedBookedBy = "U:15"; one.SelectedMonth = Nov; one.SelectedYear = Year;
            await one.OnGetAsync();
            Assert.Equal(Set(d.S4), Ids(one.Bookings).Intersect(d.Seedling).ToArray());
        }

        // ---- security (seedling): the page's permissions decide who lists bookings; the filter only narrows ----

        [SkippableFact]
        public async Task SeedlingBookings_HaveNoAreaScope_SoAnyUserSeesTheSameRecords_AndTheFilterOnlyNarrows()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var noAreas = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(AreaAccessService.RoleNameClaimType, "Booking Executive") }, "test"));
            var kiran = await env.LoginAsync(115);                                                        // Area 1 only
            var admin = await env.LoginAsync(Prajwal);
            foreach (var by in new[] { "", "U:3", "U:15", "X:none" })
            {
                var forAdmin = await Records(env, d, by, user: admin);
                Assert.Equal(forAdmin, await Records(env, d, by, user: kiran));                           // as before: not Area-scoped (permission-gated pages)
                Assert.Equal(forAdmin, await Records(env, d, by, user: noAreas));
            }
            // choosing somebody else's name never adds records beyond the unfiltered list
            var unfiltered = await Records(env, d, "");
            foreach (var by in new[] { "U:3", "U:15", "U:5", "U:10", "O:Walk-in Agent", "X:none" })
                Assert.Empty((await Records(env, d, by)).Except(unfiltered));
        }

        // ---- Potted Plant bookings (Area-scoped) ----

        [SkippableFact]
        public async Task PottedBookings_EveryPersonAndName_ReturnsExactlyTheirBookings_AndKeepsOtherAndNotRecorded()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var sandeep = await env.LoginAsync(Sandeep);                                                   // Outlet Area 124
            async Task<int[]> With(string by, ClaimsPrincipal? user = null)
            {
                var page = env.Page<PottedList>(user ?? sandeep);
                page.SelectedBookedBy = by;
                await page.OnGetAsync();
                return Ids(page.Bookings).Intersect(d.Potted).ToArray();
            }
            Assert.Equal(Set(d.P1, d.P5), await With("U:112"));
            Assert.Equal(Set(d.P2), await With("U:21"));
            Assert.Equal(Set(d.P3), await With("O:Walk-in Agent"));
            Assert.Equal(Set(d.P5), await With("O:Ramesh"));
            Assert.Equal(Set(d.P4), await With("X:none"));
            Assert.Equal(d.Potted.Length, (await With("")).Length);
            Assert.Empty(await With("U:987654"));

            // every option of the dropdown equals an independent SQL statement
            var page = env.Page<PottedList>(sandeep);
            await page.OnGetAsync();
            foreach (var option in page.BookedByOptions.Where(o => o.Value.StartsWith("U:")))
            {
                var expected = await env.IdsAsync("SELECT Id FROM dbo.PottedPlantBookings WHERE BookedById = @U", ("@U", int.Parse(option.Value[2..])));
                var p = env.Page<PottedList>(sandeep);
                p.SelectedBookedBy = option.Value;
                await p.OnGetAsync();
                Assert.Equal(expected, Ids(p.Bookings));
            }
            Assert.Contains(page.BookedByOptions, o => o.Label == "Walk-in Agent (other)");
            Assert.Contains(page.BookedByOptions, o => o.Value == "X:none");
        }

        [SkippableFact]
        public async Task PottedBookings_AreaSecurity_ANameNeverOpensAnotherAreasRecords_OrLeaksItsPeople()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var kiran = await env.LoginAsync(115);                                                         // Area 1: no Outlet bookings
            var noAreas = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(AreaAccessService.RoleNameClaimType, "Outlet Sales") }, "test"));
            foreach (var user in new[] { kiran, noAreas })
                foreach (var by in new[] { "", "U:112", "U:21", "O:Walk-in Agent", "X:none" })
                {
                    var page = env.Page<PottedList>(user);
                    page.SelectedBookedBy = by;
                    await page.OnGetAsync();
                    Assert.Empty(Ids(page.Bookings).Intersect(d.Potted));                                 // nothing of the Outlet's, whoever is asked for
                    Assert.All(page.Bookings, b => Assert.Null(b.AreaId));                                // (only Area-less bookings are ever visible to them, as before)
                }
            var kiranPage = env.Page<PottedList>(kiran);
            await kiranPage.OnGetAsync();
            Assert.Empty(kiranPage.BookedByOptions);                                                       // no names from the Outlet's bookings
            var admin = env.Page<PottedList>(await env.LoginAsync(Prajwal));
            admin.SelectedBookedBy = "U:112";
            await admin.OnGetAsync();
            Assert.Equal(Set(d.P1, d.P5), Ids(admin.Bookings).Intersect(d.Potted).ToArray());            // full access sees them
            Assert.Contains(admin.BookedByOptions, o => o.Value == "U:112");
        }

        [SkippableFact]
        public async Task PottedBookings_WithNoFilter_AreExactlyTheSameRecordsAsBefore_ForEveryUser()
        {
            var env = await OpenAsync();
            await SeedAsync(env);
            var repo = env.Get<PottedPlantBookingRepository>();
            var access = env.Get<AreaAccessService>();
            foreach (var userId in new[] { Prajwal, Sandeep, 115, 101, 111, 114 })
            {
                var user = await env.LoginAsync(userId);
                var all = await repo.GetAllAsync();                                                        // the previous page logic
                var before = access.HasFullAreaAccess(user) ? all : all.Where(b => access.CanAccessArea(user, b.AreaId)).ToList();
                var page = env.Page<PottedList>(user);
                await page.OnGetAsync();
                Assert.Equal(before.Select(b => b.Id).ToArray(), page.Bookings.Select(b => b.Id).ToArray());   // same records, same order
            }
        }

        [SkippableFact]
        public async Task HistoricalPottedBookings_AreUntouched_AndFilterable()
        {
            var env = await OpenAsync();
            var fingerprint = await env.ScalarAsync<string>("SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(*))) FROM dbo.PottedPlantBookings WHERE Id IN (79, 80)");
            var d = await SeedAsync(env);
            var page = env.Page<PottedList>(await env.LoginAsync(Prajwal));
            page.SelectedBookedBy = "U:21";
            await page.OnGetAsync();
            Assert.Subset(page.Bookings.Select(b => b.Id).ToHashSet(), new HashSet<int> { 79, 80, d.P2 });    // the two historical ones (by Prajwal) and the new one
            Assert.Equal(fingerprint, await env.ScalarAsync<string>("SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(*))) FROM dbo.PottedPlantBookings WHERE Id IN (79, 80)"));
        }

        [SkippableFact]
        public async Task FilteringNeverChangesAnyData()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            const string state = @"SELECT CONCAT((SELECT COUNT(*) FROM dbo.Bookings), '|', (SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.Bookings), '|', (SELECT COUNT(*) FROM dbo.PottedPlantBookings), '|',
                (SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.PottedPlantBookings), '|', (SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.PottedPlantStock), '|', (SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.ReadyStock))";
            var before = await env.ScalarAsync<string>(state);
            await Records(env, d, "U:3");
            var potted = env.Page<PottedList>(await env.LoginAsync(Prajwal));
            potted.SelectedBookedBy = "U:112";
            await potted.OnGetAsync();
            Assert.Equal(before, await env.ScalarAsync<string>(state));
        }
    }
}
