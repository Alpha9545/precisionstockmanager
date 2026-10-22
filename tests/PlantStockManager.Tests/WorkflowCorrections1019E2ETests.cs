using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
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
using ApprovalsIndex = PlantStockManager.Pages.Production.ReadyConfirmation.IndexModel;
using ConfirmPage = PlantStockManager.Pages.Production.ReadyConfirmation.ConfirmModel;
using MpApprovals = PlantStockManager.Pages.Production.MotherPlant.SowingApprovalsModel;
using MpReadyStock = PlantStockManager.Pages.Production.MotherPlant.AreaReadyStockModel;
using MovePage = PlantStockManager.Pages.Production.ReadyStock.MoveModel;
using SellPage = PlantStockManager.Pages.Production.MotherPlant.SellTraysModel;

namespace PlantStockManager.Tests
{
    // 2026-10-19 workflow corrections, end-to-end against a SCRATCH COPY only (PSM_SCRATCH_CONNECTION, database name must start
    // with PlantsIMS2_Scratch_; SKIPPED otherwise, and per test without the migrations it needs). REAL users: their principals
    // are built from dbo.UserRoles by the real claims factory (as at login) and drive the REAL page models and repositories.
    //   R1  Mother Plant & Cutting > Sowing Approvals lists the Area's transplanted tray batches; approve = the one approval.
    //   R3  every authorised user sees every pending batch in scope; seeing never grants approving; Area scope on GET and POST.
    //   R2  Mother Plant Area Ready Stock: Send to Outlet (whole batch) and Sell to customer, or why not; ledgers reconcile.
    //   R4  70 Cavity end to end (only on a copy with 2026-10-19_Cavity70.sql; the not-applied state is tested otherwise).
    [Collection("ScratchDb")]
    public partial class WorkflowCorrections1019E2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MainArea = 2, MainPolyhouse = 4, OutletArea = 124, SowingSupervisor = 9, Recorder = 21, ReadyDays = 30;
        private static readonly MotherPlantTransferRepository.Actor Actor = new(Recorder, "wf1019-test");
        private static bool Any(int _) => true;

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

        private sealed class Env
        {
            public string Cs = "";
            public ServiceProvider Provider = null!;
            public int Cocopeat;
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

            public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(Cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
            }

            public async Task<List<int>> IdsAsync(string sql, params (string Name, object? Value)[] args)
            {
                var list = new List<int>();
                using var conn = new SqlConnection(Cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) list.Add(r.GetInt32(0));
                return list;
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
                var http = new DefaultHttpContext { User = principal, RequestServices = Provider };
                var page = ActivatorUtilities.CreateInstance<T>(Provider);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()));
                page.TempData = new TempDataDictionary(http, new NullTempData());
                return page;
            }

            public Task<int> ReadyStockOfAsync(int sowing) => ScalarAsync<int>("SELECT ISNULL(MAX(Id), 0) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowing));
            public Task<int> ApprovalsOfAsync(int sowing) => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", sowing));
        }

        private static async Task<Env> OpenAsync(params string[] probes)
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run these tests.");
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                var name = (string)(await cmd.ExecuteScalarAsync())!;
                if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Refusing to run these tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");
                foreach (var probe in new[] { BedSowingSchema.TraysProbeSql, MotherPlantAreaSchema.ProbeSql, WorkflowCorrectionsSchema.TransplantWasteProbeSql }.Concat(probes))
                {
                    using var p = new SqlCommand(probe, conn);
                    Skip.If((int)(await p.ExecuteScalarAsync())! != 1, $"A migration this test needs is not applied to this scratch database ({probe}).");
                }
            }
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddLogging();
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));   // Area scope ENFORCED (production)
            services.AddSingleton<SeedlingAreaScope>();
            services.AddScoped<UserClaimsFactory>();
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            services.AddScoped<IAuthorizationHandler, MinimumAuthorizationLevelHandler>();
            services.AddAuthorization();
            services.AddScoped<FeatureAccessService>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var env = new Env { Cs = cs!, Provider = services.BuildServiceProvider() };
            env.Cocopeat = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'");
            return env;
        }

        // ---- fixtures -------------------------------------------------------------------------------------------------

        private sealed record Variety(int SpeciesId, int SeedStockId);

        private static async Task<Variety> NewVarietyAsync(Env env, int area = MainArea)
        {
            var name = "ZZTEST Wf19 " + Guid.NewGuid().ToString("N")[..10];
            var speciesId = await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays)
VALUES ((SELECT MIN(Id) FROM dbo.PlantTypes), @N, @D); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", name), ("@D", ReadyDays));
            var lot = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 100000, N'wf1019-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", speciesId), ("@A", area), ("@B", "LOT-" + name[^10..]));
            return new Variety(speciesId, lot);
        }

        private static async Task<int> ApprovedMainBatchAsync(Env env, Variety v, int plants = 480)
        {
            Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, "24 Cavity", plants / 24 + 5, DateTime.UtcNow.AddDays(-45),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "wf1019-test", "wf1019 setup", Any)).Success);
            Assert.True((await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 500m, "wf1019 setup", Guid.NewGuid(), Recorder, Any,
                LedgerDates.Instant(AppClock.Today.AddDays(-45)).TransactionUtc)).Success);
            var sowing = new SeedSowing
            {
                SourceSeedStockId = v.SeedStockId, SeedQuantity = plants, CavityType = "24 Cavity", SowingDate = AppClock.Today.AddDays(-40),
                PolyhouseId = MainPolyhouse, SupervisorId = SowingSupervisor, CreatedBy = "wf1019-test", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            };
            var (ok, msg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, Recorder, Any);
            Assert.True(ok, msg);
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, plants / 24, null, null, null, "wf1019-test", SowingSupervisor, Any)).Success);
            return await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S AND OriginType = N'Sowing'", ("@S", sowingId));
        }

        private static async Task<int> ReceivedAsync(Env env, Variety v, int mpArea, string purpose, decimal plants, int daysAgo)
        {
            var main = await ApprovedMainBatchAsync(env, v);
            var transfers = env.Get<MotherPlantTransferRepository>();
            var (ok, msg, transferId, _) = await transfers.CreateAsync(new MotherPlantTransferRepository.CreateRequest(main, plants, mpArea, null,
                AppClock.Today.AddDays(-daysAgo), "wf1019", Guid.NewGuid(), purpose), Actor, Any);
            Assert.True(ok, msg);
            var (rOk, rMsg, batch) = await transfers.ReceiveAsync(transferId, plants, null, Actor, Any,
                daysAgo > 0 ? AppClock.Today.AddDays(-daysAgo) : null, daysAgo > 0 ? "wf1019 back-dated receipt" : null);
            Assert.True(rOk, rMsg);
            return batch!.Value;
        }

        // A tray batch transplanted in a Mother Plant Area `daysAgo` days ago (Ready Date = that + 30) -> its sowing id.
        // received: plants received in the Area (whole 24-trays from the Main Office batch); process: of those, the seedlings
        // processed into `plants` (complete trays) + waste. Both default to `plants`.
        private static async Task<int> TransplantedTrayBatchAsync(Env env, Variety v, int mpArea, decimal plants, int daysAgo, string cavity = "24 Cavity",
            decimal? received = null, string? wasteReason = null, decimal? process = null)
        {
            var size = DirectSowingRules.CavityCount(cavity)!.Value;
            var source = await ReceivedAsync(env, v, mpArea, MotherPlantTransferRules.PurposeTrayTransplant, received ?? plants, daysAgo);
            Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(mpArea, null, cavity, plants / size + 1, DateTime.UtcNow.AddDays(-daysAgo - 1),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "wf1019-test", "wf1019 trays", Any)).Success);
            var selected = process ?? received ?? plants;
            var (ok, msg, id, _) = await env.Get<TransplantRepository>().CreateAsync(new TransplantRepository.CreateRequest(source, mpArea, null, cavity,
                selected, plants, selected - plants, AppClock.Today.AddDays(-daysAgo), "wf1019 transplant", Guid.NewGuid(), wasteReason), Actor, Any);
            Assert.True(ok, msg);
            return await env.ScalarAsync<int>("SELECT OutputSeedSowingId FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", id));
        }

        private const string ConfirmersOfArea = @"SELECT DISTINCT ur.UserId FROM dbo.UserRoles ur JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId AND p.Code = N'ReadyStock.Confirm'
WHERE ur.AreaId = @A AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x JOIN dbo.Roles r ON r.Id = x.RoleId WHERE x.UserId = ur.UserId AND r.Name = N'System Administrator')";

        // A Mother Plant Area with TWO Area Supervisors, a supervisor of ANOTHER Mother Plant Area only, a Main Office supervisor
        // (not of any Mother Plant Area), a System Administrator, and a Main Office user who may VIEW Sowing Approvals but not approve.
        private sealed record People(int Area, int Sup1, int Sup2, int OtherAreaSup, int MainSup, int Admin, int Viewer);

        private static async Task<People> PeopleAsync(Env env)
        {
            var area = await env.ScalarAsync<int>(@"SELECT TOP 1 a.Id FROM dbo.Area a WHERE a.AreaType = N'MotherPlant' AND a.IsActive = 1
AND (SELECT COUNT(*) FROM (" + ConfirmersOfArea.Replace("@A", "a.Id") + ") s) >= 2 ORDER BY a.Id");
            Skip.If(area == 0, "No Mother Plant Area with two Area Supervisors in this copy.");
            var sups = await env.IdsAsync(ConfirmersOfArea + " ORDER BY ur.UserId", ("@A", area));
            var other = (await env.IdsAsync(@"SELECT DISTINCT ur.UserId FROM dbo.UserRoles ur JOIN dbo.Area a ON a.Id = ur.AreaId AND a.AreaType = N'MotherPlant'
JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId AND p.Code = N'ReadyStock.Confirm'
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserRoles y WHERE y.UserId = ur.UserId AND (y.AreaId = @A OR y.AreaId IS NULL)) ORDER BY ur.UserId", ("@A", area))).FirstOrDefault();
            var main = (await env.IdsAsync(ConfirmersOfArea + " AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles z JOIN dbo.Area za ON za.Id = z.AreaId WHERE z.UserId = ur.UserId AND za.AreaType = N'MotherPlant') ORDER BY ur.UserId",
                ("@A", MainArea))).FirstOrDefault();
            var admin = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE r.Name = N'System Administrator' ORDER BY ur.UserId");
            var viewer = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE ur.AreaId = @M
  AND EXISTS (SELECT 1 FROM dbo.UserRoles a JOIN dbo.RolePermissions rp ON rp.RoleId = a.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE a.UserId = ur.UserId AND p.Code = N'ReadyStock.View')
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles a JOIN dbo.RolePermissions rp ON rp.RoleId = a.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE a.UserId = ur.UserId AND p.Code = N'ReadyStock.Confirm')
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x JOIN dbo.Roles r ON r.Id = x.RoleId WHERE x.UserId = ur.UserId AND r.Name = N'System Administrator')
ORDER BY ur.UserId", ("@M", MainArea));
            Skip.If(other == 0 || main == 0 || admin == 0 || viewer == 0, "The copy lacks one of the user kinds these tests need.");
            return new People(area, sups[0], sups[1], other, main, admin, viewer);
        }

        private static async Task<List<SowingApprovalQueueRepository.Item>> MpPageAsync(Env env, int user)
        {
            var page = env.Page<MpApprovals>(await env.LoginAsync(user));
            await page.OnGetAsync();
            return page.Items;
        }

        private static async Task<List<SowingApprovalQueueRepository.Item>> MainPageAsync(Env env, int user, string? category = null)
        {
            var page = env.Page<ApprovalsIndex>(await env.LoginAsync(user));
            page.Category = category;
            await page.OnGetAsync();
            return page.Items;
        }

        // =====================================================================================================
        // R1 + R3: visibility and approval authority
        // =====================================================================================================

        [SkippableFact]
        public async Task MotherPlantApprovals_EveryAreaSupervisorSeesEveryPendingBatchOfTheArea_OtherAreasNever_SeeingIsNotApproving()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var due = await TransplantedTrayBatchAsync(env, v, p.Area, 96, ReadyDays);           // Ready Date = today
            var growing = await TransplantedTrayBatchAsync(env, v, p.Area, 48, 5);               // Ready Date in 25 days

            // two different Area Supervisors of the Area: BOTH see BOTH batches (not only "their own"), both may approve the due one
            foreach (var sup in new[] { p.Sup1, p.Sup2 })
            {
                var items = await MpPageAsync(env, sup);
                var d = Assert.Single(items, i => i.Sowing.Id == due);
                var g = Assert.Single(items, i => i.Sowing.Id == growing);
                Assert.True(d.Eligibility.CanApprove, d.Eligibility.Reason);
                if (await env.ScalarAsync<int>(WorkflowCorrectionsSchema.TransplantAnyDateProbeSql) == 1)
                    Assert.True(g.Eligibility.CanApprove, g.Eligibility.Reason);                    // 2026-10-20: any date
                else
                {
                    Assert.True(g.Eligibility.WaitingForReadyDate);
                    Assert.Equal(AppClock.Today.AddDays(25), g.Eligibility.ApproveFrom);
                }
                Assert.Equal(SowingApprovalRules.CategoryMotherPlantTransplant, d.Category);
                Assert.NotNull(d.SourceBatchNo);                                                      // source batch lineage shown
                Assert.All(items, i => Assert.True(SowingApprovalRules.IsMotherPlantCategory(i.Category), i.Category));   // only Mother Plant Area batches (2026-10-21: transplant + Direct Sowing)
                // the same batches under Seed Production > Sowing Approvals, in their own category
                Assert.Contains(await MainPageAsync(env, sup), i => i.Sowing.Id == due && i.Category == SowingApprovalRules.CategoryMotherPlantTransplant);
            }

            // another Mother Plant Area's supervisor and a Main Office supervisor: the batches are not exposed at all
            foreach (var outsider in new[] { p.OtherAreaSup, p.MainSup })
            {
                Assert.DoesNotContain(await MpPageAsync(env, outsider), i => i.Sowing.Id == due || i.Sowing.Id == growing);
                Assert.DoesNotContain(await MainPageAsync(env, outsider), i => i.Sowing.Id == due || i.Sowing.Id == growing);
            }
            // a System Administrator sees and may approve
            Assert.True(Assert.Single(await MpPageAsync(env, p.Admin), i => i.Sowing.Id == due).Eligibility.CanApprove);

            // a Main Office user who may only VIEW Sowing Approvals: sees the Main Office batches, may approve none of them
            var mainItems = await MainPageAsync(env, p.Viewer);
            Assert.All(mainItems, i => Assert.False(i.Eligibility.CanApprove));
            Assert.All(mainItems, i => Assert.Equal(SowingApprovalRules.NoApprovePermissionMessage, i.Eligibility.Reason));
            Assert.DoesNotContain(mainItems, i => i.Sowing.Id == due);
        }

        [SkippableFact]
        public async Task ApprovePost_UnauthorisedOrBeforeReadyDate_Refused_AuthorisedPostsReadyStockExactlyOnce_ReturnsToMotherPlantList()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var due = await TransplantedTrayBatchAsync(env, v, p.Area, 96, ReadyDays);
            var growing = await TransplantedTrayBatchAsync(env, v, p.Area, 48, 5);

            async Task<(IActionResult Result, ConfirmPage Page)> PostAsync(int user, int sowing, decimal trays)
            {
                var page = env.Page<ConfirmPage>(await env.LoginAsync(user));
                page.SeedSowingId = sowing;
                page.ActualReadyTrays = trays;
                page.From = ConfirmPage.FromMotherPlant;
                return (await page.OnPostAsync(), page);
            }

            // the page permission: viewing Sowing Approvals does not open the approval form
            var feature = env.Get<FeatureAccessService>();
            Assert.True(await feature.CanAccessPageAsync(await env.LoginAsync(p.Viewer), "/Production/ReadyConfirmation/Index"));
            Assert.False(await feature.CanAccessPageAsync(await env.LoginAsync(p.Viewer), "/Production/ReadyConfirmation/Confirm"));
            Assert.True(await feature.CanAccessPageAsync(await env.LoginAsync(p.Sup1), "/Production/MotherPlant/SowingApprovals"));

            // a POST by another Area's supervisor (a forged batch id for them) and by a Main Office supervisor: refused, nothing posted
            foreach (var outsider in new[] { p.OtherAreaSup, p.MainSup })
            {
                var (result, page) = await PostAsync(outsider, due, 4);
                Assert.Equal("/Production/MotherPlant/SowingApprovals", Assert.IsType<RedirectToPageResult>(result).PageName);
                Assert.NotNull(page.TempData["Error"]);
            }
            Assert.Equal((0, 0), (await env.ApprovalsOfAsync(due), await env.ReadyStockOfAsync(due)));

            // the Area Supervisor before the Ready Date: refused by the server, nothing posted
            // (2026-10-20: allowed once 2026-10-20_TransplantApprovalAnyDate.sql is applied -- WorkflowCorrections1020E2ETests)
            if (!(await env.ScalarAsync<int>(WorkflowCorrectionsSchema.TransplantAnyDateProbeSql) == 1))
            {
                var (early, earlyPage) = await PostAsync(p.Sup1, growing, 2);
                Assert.IsType<PageResult>(early);
                Assert.Contains(earlyPage.ModelState.Values.SelectMany(x => x.Errors), e => e.ErrorMessage.Contains("Ready Date") || e.ErrorMessage.Contains("ready"));
                Assert.Equal((0, 0), (await env.ApprovalsOfAsync(growing), await env.ReadyStockOfAsync(growing)));
            }

            // the Area Supervisor on the Ready Date: approved once, back to the Mother Plant list; Ready Stock posted once in the Area
            var (ok, okPage) = await PostAsync(p.Sup1, due, 4);
            Assert.Equal("/Production/MotherPlant/SowingApprovals", Assert.IsType<RedirectToPageResult>(ok).PageName);
            Assert.NotNull(okPage.TempData["Success"]);
            // the second supervisor submitting the same batch afterwards (a stale page): refused, still one approval
            var (late, latePage) = await PostAsync(p.Sup2, due, 4);
            Assert.NotNull(latePage.TempData["Error"] ?? (latePage.ModelState.IsValid ? null : "invalid"));
            Assert.Equal(1, await env.ApprovalsOfAsync(due));
            var rs = await env.ReadyStockOfAsync(due);
            Assert.Equal((p.Area, 96m, ReadyStock.OriginSowing), (
                await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.ScalarAsync<string>("SELECT OriginType FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs))));
            Assert.Equal(96m, await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'Confirmed'", ("@R", rs)));
            // gone from both approval lists, now on the Area's Ready Stock page
            Assert.DoesNotContain(await MpPageAsync(env, p.Sup2), i => i.Sowing.Id == due);
            var stock = env.Page<MpReadyStock>(await env.LoginAsync(p.Sup2));
            await stock.OnGetAsync();
            Assert.Contains(stock.Trays, t => t.Id == rs);
        }

        [SkippableFact]
        public async Task BedSowingBatch_VisibleToEverySowingApprover_ApprovableOnlyByTheDesignatedApprover()
        {
            var env = await OpenAsync(BedSowingSchema.ProbeSql, WorkflowCorrectionsSchema.BedApproverProbeSql);
            var settings = await env.Get<ReadyConfirmationRepository>().GetApprovalSettingsAsync();
            Skip.If(settings.Bed.UserId is null, "No active Bed Sowing approver in this copy.");
            var maya = settings.Bed.UserId!.Value;
            var bedPolyhouse = await env.ScalarAsync<int>("SELECT TOP 1 PolyhouseId FROM dbo.BedSowingLocations WHERE IsActive = 1 ORDER BY PolyhouseId");
            var bedArea = await env.ScalarAsync<int>("SELECT AreaId FROM dbo.BedSowingLocations WHERE PolyhouseId = @P", ("@P", bedPolyhouse));
            var other = (await env.IdsAsync(ConfirmersOfArea + " AND ur.UserId <> @Maya ORDER BY ur.UserId", ("@A", bedArea), ("@Maya", maya))).FirstOrDefault();
            Skip.If(bedPolyhouse == 0 || other == 0, "No bed location or second sowing approver in this copy.");

            var v = await NewVarietyAsync(env, bedArea);
            Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(bedArea, bedPolyhouse, "24 Cavity", 20, DateTime.UtcNow.AddDays(-10),
                Random.Shared.Next(1, int.MaxValue), other, "wf1019-test", "wf1019 bed trays", Any)).Success);
            Assert.True((await env.Get<MaterialStockRepository>().AdjustAsync(bedArea, bedPolyhouse, env.Cocopeat, 1, 100m, "wf1019 bed", Guid.NewGuid(), other, Any,
                LedgerDates.Instant(AppClock.Today.AddDays(-10)).TransactionUtc)).Success);
            var beds = env.Get<BedSowingRepository>();
            var bed = new BedSowing { SpeciesId = v.SpeciesId, SeedStockId = v.SeedStockId, PolyhouseId = bedPolyhouse, Quantity = 240,
                SowingDate = AppClock.Today.AddDays(-5), Remarks = "wf1019 bed", SubmissionToken = Guid.NewGuid() };
            Assert.True((await beds.CreateAsync(bed, new BedSowingRepository.Actor(other, "wf1019-sower", false), Any)).Success);
            var (tOk, tMsg, _, batch, _) = await beds.TransplantAsync(new BedTransplantRequest
            {
                BedSowingId = bed.Id, TargetPolyhouseId = bedPolyhouse, CavityType = "24 Cavity", Quantity = 240, TransplantDate = AppClock.Today,
                GrowingMaterialTypeId = env.Cocopeat, Remarks = "wf1019", SubmissionToken = Guid.NewGuid()
            }, new BedSowingRepository.Actor(maya, "wf1019-maya", false));
            Assert.True(tOk, tMsg);

            // 2026-10-20 visibility policy: a non-admin who may not approve it does not see it at all; the rule itself is unchanged
            Assert.DoesNotContain(await MainPageAsync(env, other, SowingApprovalRules.CategoryBedSowing), i => i.Sowing.Id == batch);
            var forOther = SowingApprovalRules.Evaluate((await env.Get<SeedSowingRepository>().GetByIdAsync(batch))!, other, "other", true, false,
                new SowingApprovalRules.Settings(settings.Bed, settings.BedAnyDate, settings.TransplantAreaScope, settings.TransplantAnyDate), AppClock.Today);
            Assert.False(forOther.CanApprove);
            Assert.Equal(DirectSowingRules.NotBedApproverMessage(settings.Bed.Name), forOther.Reason);
            var forMaya = Assert.Single(await MainPageAsync(env, maya, SowingApprovalRules.CategoryBedSowing), i => i.Sowing.Id == batch);
            Assert.Equal(settings.BedAnyDate, forMaya.Eligibility.CanApprove);                        // any date once 2026-10-17 BedAnyDate is applied
            Assert.DoesNotContain(await MainPageAsync(env, maya, SowingApprovalRules.CategorySowing), i => i.Sowing.Id == batch);   // categories separate
            // the server agrees: the other supervisor is refused, nothing posted
            Assert.False((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(batch, 10, null, null, null, "wf1019", other, Any)).Success);
            Assert.Equal(0, await env.ApprovalsOfAsync(batch));
        }

        [SkippableFact]
        public async Task HomePage_YourApprovals_CountsEveryBatchTheUserMayApprove_NotOnlyAssignedSowings()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            await TransplantedTrayBatchAsync(env, v, p.Area, 48, ReadyDays);                     // no assigned supervisor -- formerly never counted
            var principal = await env.LoginAsync(p.Sup1);
            var home = env.Page<PlantStockManager.Pages.IndexModel>(principal);
            await home.OnGetAsync();
            var expected = (await env.Get<SowingApprovalQueueRepository>().GetPendingAsync(ApprovalsIndex.Viewer(principal, true,
                env.Get<SeedlingAreaScope>(), env.Get<AreaAccessService>()))).Items;
            Assert.True(expected.Count(i => i.Eligibility.CanApprove) >= 1);
            Assert.Equal(expected.Count(i => i.Eligibility.CanApprove), home.Items.Single(i => i.Text == "Sowings waiting for your approval").Count);
            Assert.Equal(expected.Count, home.Items.Single(i => i.Text == "Sowings pending approval in your Areas").Count);
        }

        // =====================================================================================================
        // R2: Mother Plant Area Ready Stock -- Send to Outlet / Sell to customer
        // =====================================================================================================

        private static async Task<int> ApprovedAreaBatchAsync(Env env, People p, Variety v, decimal plants = 240)
        {
            var sowing = await TransplantedTrayBatchAsync(env, v, p.Area, plants, ReadyDays);
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowing, plants / 24, null, null, null, "wf1019", p.Sup1, Any)).Success);
            return await env.ReadyStockOfAsync(sowing);
        }

        private static async Task<MpReadyStock> StockPageAsync(Env env, int user)
        {
            var page = env.Page<MpReadyStock>(await env.LoginAsync(user));
            await page.OnGetAsync();
            return page;
        }

        [SkippableFact]
        public async Task ReadyStockPage_EligibleBatch_OffersSendAndSell_AfterASale_SendExplainsTheWholeBatchRule_PotStockNeither()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql, WorkflowCorrectionsSchema.SeedlingSaleTokenProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var rs = await ApprovedAreaBatchAsync(env, p, v);

            var page = await StockPageAsync(env, p.Sup1);
            var t = Assert.Single(page.Trays, x => x.Id == rs);
            Assert.True(page.SendAction(t).Available, page.SendAction(t).Reason);
            Assert.True(page.SellAction(t).Available, page.SellAction(t).Reason);
            // the batch is never shown to another Area's supervisor
            Assert.DoesNotContain((await StockPageAsync(env, p.OtherAreaSup)).Trays, x => x.Id == rs);

            // after selling one tray, the batch is no longer whole: Send is unavailable WITH the reason; Sell still offered
            var (sold, msg, _) = await env.Get<SeedlingFulfilmentRepository>().DirectTraySaleAsync(
                new SeedlingFulfilmentRepository.DirectTraySaleRequest(rs, 1, "ZZTEST Wf19 Customer", "9000000001", null, null, Guid.NewGuid()),
                new SeedlingFulfilmentRepository.Actor("wf1019", p.Sup1), a => a == p.Area);
            Assert.True(sold, msg);
            page = await StockPageAsync(env, p.Sup1);
            t = Assert.Single(page.Trays, x => x.Id == rs);
            Assert.False(page.SendAction(t).Available);
            Assert.Contains("only as a whole", page.SendAction(t).Reason);
            Assert.True(page.SellAction(t).Available);
            // ... and the server keeps the whole-batch rule (unchanged): a transfer of the rest is refused, nothing moves
            var (moved, why, _) = await env.Get<InternalTransferRepository>().InsertAsync(new InternalTransfer
            {
                StockType = "ReadyStock", SourceReadyStockId = rs, DestinationAreaId = OutletArea, Quantity = 216, CreatedBy = "wf1019", SubmissionToken = Guid.NewGuid()
            }, p.Sup1);
            Assert.False(moved);
            Assert.Contains("dispatched", why);
            Assert.Equal(p.Area, await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)));

            // stock received for Pot Production: neither action (and the server refuses the sale too)
            var potBatch = await ReceivedAsync(env, v, p.Area, MotherPlantTransferRules.PurposePotProduction, 48, 0);
            page = await StockPageAsync(env, p.Sup1);
            var pots = Assert.Single(page.Trays, x => x.Id == potBatch);
            Assert.Equal(AreaReadyStockActionRules.PotProductionMessage, page.SendAction(pots).Reason);
            Assert.Equal(AreaReadyStockActionRules.PotProductionMessage, page.SellAction(pots).Reason);
        }

        [SkippableFact]
        public async Task SendToOutlet_FromTheMotherPlantPage_WholeBatchOnce_SourceAndDestinationReconcile_ForgedAndReplayedPostsRefused()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var rs = await ApprovedAreaBatchAsync(env, p, v, 96);
            var transfers0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE SourceReadyStockId = @R", ("@R", rs));

            async Task<(IActionResult Result, MovePage Page)> PostAsync(int user, int? destination, Guid token)
            {
                var page = env.Page<MovePage>(await env.LoginAsync(user));
                page.From = "mp";
                page.Destination = MovePage.ToOutlet;
                page.DestinationAreaId = destination;
                page.Quantity = 96;
                page.SubmissionToken = token;
                return (await page.OnPostAsync(rs), page);
            }

            // GET from the Mother Plant page: Outlet preselected, the whole batch
            var get = env.Page<MovePage>(await env.LoginAsync(p.Sup1));
            get.From = "mp";
            Assert.IsType<PageResult>(await get.OnGetAsync(rs));
            Assert.Equal((MovePage.ToOutlet, 96m), (get.Destination, get.Quantity));
            Assert.All(get.OutletAreas, a => Assert.Equal("Outlet", a.AreaType));

            // another Area's supervisor (forged id): refused before anything; a destination that is not an Outlet: refused
            var (forged, _) = await PostAsync(p.OtherAreaSup, OutletArea, Guid.NewGuid());
            Assert.Equal("/Production/MotherPlant/ReadyStock", Assert.IsType<RedirectToPageResult>(forged).PageName);
            var (wrongDest, wrongPage) = await PostAsync(p.Sup1, p.Area, Guid.NewGuid());
            Assert.IsType<PageResult>(wrongDest);
            Assert.False(wrongPage.ModelState.IsValid);
            Assert.Equal(transfers0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE SourceReadyStockId = @R", ("@R", rs)));

            // the Area Supervisor sends it -- the same form twice: one transfer
            var token = Guid.NewGuid();
            var (sent, _) = await PostAsync(p.Sup1, OutletArea, token);
            Assert.Equal("/Production/MotherPlant/ReadyStock", Assert.IsType<RedirectToPageResult>(sent).PageName);
            var (again, _) = await PostAsync(p.Sup1, OutletArea, token);
            Assert.IsType<RedirectToPageResult>(again);
            Assert.Equal(transfers0 + 1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE SourceReadyStockId = @R", ("@R", rs)));

            // reconciliation: the SAME batch row (lineage) now at the Outlet; the header records source + destination; the ledger
            // explains the move; plants neither created nor lost
            Assert.Equal((OutletArea, 96m, 0m, 0m, 0m), (
                await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.ScalarAsync<decimal>("SELECT ReservedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.ScalarAsync<decimal>("SELECT DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.ScalarAsync<decimal>("SELECT InTransitQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs))));
            Assert.Equal((p.Area, OutletArea, 96m, "ReadyStock"), (
                await env.ScalarAsync<int>("SELECT SourceAreaId FROM dbo.InternalTransfers WHERE SubmissionToken = @T", ("@T", token)),
                await env.ScalarAsync<int>("SELECT DestinationAreaId FROM dbo.InternalTransfers WHERE SubmissionToken = @T", ("@T", token)),
                await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.InternalTransfers WHERE SubmissionToken = @T", ("@T", token)),
                await env.ScalarAsync<string>("SELECT StockType FROM dbo.InternalTransfers WHERE SubmissionToken = @T", ("@T", token))));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.ReadyStockTransactions rt JOIN dbo.InternalTransfers it ON it.Id = rt.ReferenceId
WHERE rt.ReadyStockId = @R AND rt.TransactionType = N'Transfer' AND rt.ReferenceType = N'InternalTransfer' AND it.SubmissionToken = @T", ("@R", rs), ("@T", token)));
            Assert.Equal(96m, await env.ScalarAsync<decimal>(@"SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R
AND TransactionType NOT IN ('Reservation', 'ReservationRelease', 'TransferHold', 'TransferHoldRelease')", ("@R", rs)));
            Assert.DoesNotContain((await StockPageAsync(env, p.Sup1)).Trays, x => x.Id == rs);           // no longer the Area's stock
            // a new form now: the Mother Plant supervisor can no longer reach it (it is the Outlet's), nothing moves again
            var (stale, _) = await PostAsync(p.Sup1, OutletArea, Guid.NewGuid());
            Assert.IsType<RedirectToPageResult>(stale);
            Assert.Equal(transfers0 + 1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE SourceReadyStockId = @R", ("@R", rs)));
        }

        [SkippableFact]
        public async Task SellToCustomer_ThroughThePage_OnceForAReplayedForm_ConcurrentFormsNeverOverdraw_LedgerReconciles()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql, WorkflowCorrectionsSchema.SeedlingSaleTokenProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var rs = await ApprovedAreaBatchAsync(env, p, v, 120);                                  // 5 trays of 24

            async Task<(IActionResult Result, SellPage Page)> PostAsync(int user, int trays, Guid token)
            {
                var page = env.Page<SellPage>(await env.LoginAsync(user));
                page.Trays = trays;
                page.CustomerName = "ZZTEST Wf19 Buyer";
                page.Contact = "9000000002";
                page.Address = "wf1019 address";
                page.Remarks = "wf1019";
                page.SubmissionToken = token;
                return (await page.OnPostAsync(rs), page);
            }

            // another Area's supervisor: refused, nothing sold
            var (forged, _) = await PostAsync(p.OtherAreaSup, 1, Guid.NewGuid());
            Assert.Equal("/Production/MotherPlant/ReadyStock", Assert.IsType<RedirectToPageResult>(forged).PageName);
            // more trays than available: refused with the limit
            var (tooMany, tooManyPage) = await PostAsync(p.Sup1, 6, Guid.NewGuid());
            Assert.IsType<PageResult>(tooMany);
            Assert.Contains(tooManyPage.ModelState.Values.SelectMany(x => x.Errors), e => e.ErrorMessage.Contains("5 complete 24 Cavity trays"));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)));

            // the same form submitted twice: one sale
            var token = Guid.NewGuid();
            Assert.IsType<RedirectToPageResult>((await PostAsync(p.Sup1, 2, token)).Result);
            Assert.IsType<RedirectToPageResult>((await PostAsync(p.Sup1, 2, token)).Result);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedlingDispatches WHERE SubmissionToken = @T", ("@T", token)));
            Assert.Equal(48m, await env.ScalarAsync<decimal>("SELECT DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)));

            // two different forms at once for 2 trays each with 3 left: exactly one succeeds
            var race = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => PostAsync(p.Sup1, 2, Guid.NewGuid())));
            Assert.Equal(1, race.Count(r => r.Result is RedirectToPageResult));
            Assert.Equal((96m, 0m), (await env.ScalarAsync<decimal>("SELECT DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.ScalarAsync<decimal>("SELECT ReservedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs))));
            // the batch ledger explains Quantity - Dispatched; every sale = one Completed booking, closed allocation, dispatch line
            Assert.Equal(120m - 96m, await env.ScalarAsync<decimal>(@"SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R
AND TransactionType NOT IN ('Reservation', 'ReservationRelease', 'TransferHold', 'TransferHoldRelease')", ("@R", rs)));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType IN (N'Reservation', N'ReservationRelease')", ("@R", rs)));
            Assert.Equal(96m, await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.SeedlingDispatchLines WHERE ReadyStockId = @R", ("@R", rs)));
            Assert.Equal(0, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.BookingBatchAllocations WHERE ReadyStockId = @R AND (Status <> N'Closed' OR DispatchedQuantity <> Quantity)", ("@R", rs)));
            Assert.Equal(96m, await env.ScalarAsync<decimal>(@"SELECT SUM(b.DispatchedQuantity) FROM dbo.Bookings b
WHERE b.Status = N'Completed' AND b.Id IN (SELECT BookingId FROM dbo.BookingBatchAllocations WHERE ReadyStockId = @R)", ("@R", rs)));
        }

        // =====================================================================================================
        // R4: 70 Cavity
        // =====================================================================================================

        [SkippableFact]
        public async Task Cavity70_WithoutTheMigration_IsNeitherOfferedNorStocked()
        {
            var env = await OpenAsync();
            Skip.If(await env.ScalarAsync<int>(WorkflowCorrectionsSchema.Cavity70ProbeSql) == 1, "2026-10-19_Cavity70.sql is applied to this copy.");
            Assert.DoesNotContain(DirectSowingRules.Cavity70, await WorkflowCorrectionsSchema.SelectableCavitiesAsync(env.Get<DatabaseHelper>()));
            Assert.Equal(5, (await WorkflowCorrectionsSchema.SelectableCavitiesAsync(env.Get<DatabaseHelper>())).Count);
            var before = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStockTransactions");
            var (ok, msg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, "70 Cavity", 5, DateTime.UtcNow,
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "wf1019", "wf1019", Any);
            Assert.Equal((false, WorkflowCorrectionsSchema.Cavity70MigrationMessage), (ok, msg));
            var (aOk, aMsg, _, _) = await env.Get<TrayStockRepository>().AdjustAsync(MainArea, MainPolyhouse, "70 Cavity", 5, AppClock.Today,
                Random.Shared.Next(1, int.MaxValue), "wf1019", SowingSupervisor, "wf1019", Any);
            Assert.Equal((false, WorkflowCorrectionsSchema.Cavity70MigrationMessage), (aOk, aMsg));
            Assert.Equal(before, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStockTransactions"));
            // the other five are offered exactly as before
            Assert.Equal(DirectSowingRules.CavityTypes.Where(c => c != "70 Cavity"), await WorkflowCorrectionsSchema.SelectableCavitiesAsync(env.Get<DatabaseHelper>()));
        }

        [SkippableFact]
        public async Task Cavity70_DirectSowing_150Seeds_TwoTrays140_TraysAndMaterialFromTheMainPolyhouse_ApprovalPosts140()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.Cavity70ProbeSql);
            Assert.Contains(DirectSowingRules.Cavity70, await WorkflowCorrectionsSchema.SelectableCavitiesAsync(env.Get<DatabaseHelper>()));
            var v = await NewVarietyAsync(env);
            var trays = env.Get<TrayStockRepository>();
            async Task<decimal> Pool() => (await trays.GetCavityAvailabilityAsync("70 Cavity", new[] { new TrayLocation(MainArea, MainPolyhouse) })).Single().Available;
            Assert.True((await trays.AddStockAsync(MainArea, MainPolyhouse, "70 Cavity", 3, DateTime.UtcNow.AddDays(-2), Random.Shared.Next(1, int.MaxValue),
                SowingSupervisor, "wf1019", "wf1019 70 trays", Any)).Success);
            Assert.True((await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 50m, "wf1019", Guid.NewGuid(), Recorder, Any,
                LedgerDates.Instant(AppClock.Today.AddDays(-2)).TransactionUtc)).Success);
            var pool0 = await Pool();
            async Task<decimal> Material() => await env.ScalarAsync<decimal>("SELECT CurrentQuantity FROM dbo.MaterialStock WHERE AreaId = @A AND PolyhouseId = @P AND MaterialTypeId = @M",
                ("@A", MainArea), ("@P", MainPolyhouse), ("@M", env.Cocopeat));
            var material0 = await Material();
            var lot0 = await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @L", ("@L", v.SeedStockId));

            var (ok, msg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(new SeedSowing
            {
                SourceSeedStockId = v.SeedStockId, SeedQuantity = 150, CavityType = "70 Cavity", SowingDate = AppClock.Today.AddDays(-1),
                PolyhouseId = MainPolyhouse, SupervisorId = SowingSupervisor, CreatedBy = "wf1019", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            }, Recorder, Any);
            Assert.True(ok, msg);
            Assert.Equal((2, 140m), (await env.ScalarAsync<int>("SELECT NumberOfTrays FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowingId)),
                await env.ScalarAsync<decimal>("SELECT QuantitySown FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowingId))));
            // the existing remainder rule: 140 sown (2 complete trays), the 10 left over wasted automatically at sowing
            Assert.Equal(lot0 - 150, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @L", ("@L", v.SeedStockId)));
            Assert.Equal((-140m, -10m), (
                await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.SeedStockTransactions WHERE ReferenceType = N'SeedSowing' AND ReferenceId = @S AND TransactionType = N'Sown'", ("@S", sowingId)),
                await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.SeedStockTransactions WHERE ReferenceType = N'SeedSowing' AND ReferenceId = @S AND TransactionType = N'Wastage'", ("@S", sowingId))));
            // the existing Direct Sowing rule: PHYSICAL trays = CEILING(seed quantity / cavity) -- the 10 left-over seeds still
            // start a third tray (DirectSowingRules.PhysicalTraysRequired), from THIS polyhouse; material = physical trays x the
            // CONFIGURED kg per 70-cavity tray (Material Stock > Settings; editable, so read it rather than assume 1.5 kg)
            var kgPerTray = await env.ScalarAsync<decimal>("SELECT QuantityPerTray FROM dbo.MaterialConsumptionRules WHERE MaterialTypeId = @M AND CavityType = N'70 Cavity' AND IsActive = 1",
                ("@M", env.Cocopeat));
            Assert.True(kgPerTray > 0, "No active Cocopeat consumption rule for 70 Cavity in this copy.");
            Assert.Equal(pool0 - 3, await Pool());
            Assert.Equal(material0 - 3 * kgPerTray, await Material());

            // more than the polyhouse holds: refused, nothing taken
            var (more, moreMsg, _) = await env.Get<SeedSowingRepository>().InsertAsync(new SeedSowing
            {
                SourceSeedStockId = v.SeedStockId, SeedQuantity = 70 * (await Pool() + 1), CavityType = "70 Cavity", SowingDate = AppClock.Today,
                PolyhouseId = MainPolyhouse, SupervisorId = SowingSupervisor, CreatedBy = "wf1019", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            }, Recorder, Any);
            Assert.False(more, moreMsg);
            Assert.Equal(pool0 - 3, await Pool());

            var (aOk, aMsg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, 2, null, null, null, "wf1019", SowingSupervisor, Any);
            Assert.True(aOk, aMsg);
            Assert.Equal((140m, "70 Cavity"), (await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowingId)),
                await env.ScalarAsync<string>("SELECT CavityType FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowingId))));
            var rb = (await env.Get<ReadyStockRepository>().GetByIdAsync(await env.ReadyStockOfAsync(sowingId)))!;
            Assert.Equal((70, 2m), (rb.CavitySize, rb.PhysicalTrays));
        }

        [SkippableFact]
        public async Task Cavity70_MotherPlantTransplant_150Seedlings_TwoTrays140_TenWaste_TraysFromTheAreaPool_ThenApprovedAndSoldInWholeTrays()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.Cavity70ProbeSql, WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var trays = env.Get<TrayStockRepository>();
            async Task<decimal> AreaPool() => (await trays.GetCavityAvailabilityAsync("70 Cavity", new[] { new TrayLocation(p.Area, null) })).Single().Available;
            var before = await AreaPool();
            // received 150 seedlings, processed into 70-cavity trays: 2 full trays (140), 10 waste with the reason
            // received 168 (7 whole 24-trays from the Main Office); 150 of them processed: 2 x 70 = 140 transplanted, 10 waste, 18 stay
            var sowing = await TransplantedTrayBatchAsync(env, v, p.Area, 140, ReadyDays, "70 Cavity", received: 168, wasteReason: "Remainder after complete trays", process: 150);
            var rt = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStockTransplants WHERE OutputSeedSowingId = @S", ("@S", sowing));
            Assert.Equal((2, 140m, 10m), (await env.ScalarAsync<int>("SELECT OutputTrays FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", rt)),
                await env.ScalarAsync<decimal>("SELECT TransplantedQuantity FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", rt)),
                await env.ScalarAsync<decimal>("SELECT LostQuantity FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", rt))));
            var source = await env.ScalarAsync<int>("SELECT SourceReadyStockId FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", rt));
            Assert.Equal(18m, (await env.Get<ReadyStockRepository>().GetByIdAsync(source))!.AvailableQuantity);   // unprocessed seedlings stay, not waste
            Assert.Equal(before + (140 / 70 + 1) - 2, await AreaPool());                              // fixture stocked 3, the transplant used 2
            Assert.Equal((2, 140m, "70 Cavity"), (await env.ScalarAsync<int>("SELECT NumberOfTrays FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)),
                await env.ScalarAsync<decimal>("SELECT QuantitySown FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)),
                await env.ScalarAsync<string>("SELECT CavityType FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing))));

            // listed in Mother Plant Sowing Approvals with its trays and plants; approved -> 140 Ready Stock, sold in whole 70-trays
            var item = Assert.Single(await MpPageAsync(env, p.Sup1), i => i.Sowing.Id == sowing);
            Assert.Equal((2m, "70 Cavity"), (item.Trays, item.Sowing.CavityType));
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowing, 2, null, null, null, "wf1019", p.Sup1, Any)).Success);
            var rs = await env.ReadyStockOfAsync(sowing);
            var page = await StockPageAsync(env, p.Sup1);
            var t = Assert.Single(page.Trays, x => x.Id == rs);
            Assert.Equal(2, AreaReadyStockActionRules.AvailableCompleteTrays(t));
            if (await env.ScalarAsync<int>(WorkflowCorrectionsSchema.SeedlingSaleTokenProbeSql) == 1)
            {
                var sales = env.Get<SeedlingFulfilmentRepository>();
                var actor = new SeedlingFulfilmentRepository.Actor("wf1019", p.Sup1);
                var (s3, m3, _) = await sales.DirectTraySaleAsync(new(rs, 3, "ZZTEST Wf19 70", "9000000003", null, null, Guid.NewGuid()), actor, a => a == p.Area);
                Assert.False(s3);
                Assert.Contains("2 complete 70 Cavity trays", m3);
                var (s1, m1, _) = await sales.DirectTraySaleAsync(new(rs, 1, "ZZTEST Wf19 70", "9000000003", null, null, Guid.NewGuid()), actor, a => a == p.Area);
                Assert.True(s1, m1);
                Assert.Equal(70m, await env.ScalarAsync<decimal>("SELECT DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)));
            }
        }

        [SkippableFact]
        public async Task Cavity70_TrayAvailability_PerMainPolyhouseAndPerMotherPlantArea_AndTransfersBetweenThem()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.Cavity70ProbeSql);
            var p = await PeopleAsync(env);
            var trays = env.Get<TrayStockRepository>();
            var where = new[] { new TrayLocation(MainArea, MainPolyhouse), new TrayLocation(p.Area, null) };
            var before = (await trays.GetCavityAvailabilityAsync("70 Cavity", where)).ToDictionary(r => r.AreaId, r => r.Available);
            Assert.True((await trays.AddStockAsync(MainArea, MainPolyhouse, "70 Cavity", 4, DateTime.UtcNow.AddDays(-1), Random.Shared.Next(1, int.MaxValue),
                SowingSupervisor, "wf1019", "wf1019", Any)).Success);
            Assert.True((await trays.AddStockAsync(p.Area, null, "70 Cavity", 6, DateTime.UtcNow.AddDays(-1), Random.Shared.Next(1, int.MaxValue),
                SowingSupervisor, "wf1019", "wf1019", Any)).Success);
            var after = (await trays.GetCavityAvailabilityAsync("70 Cavity", where)).ToDictionary(r => r.AreaId, r => r.Available);
            Assert.Equal((before[MainArea] + 4, before[p.Area] + 6), (after[MainArea], after[p.Area]));
            // other cavities are separate pools (adding 70 never changes them)
            var other24 = (await trays.GetCavityAvailabilityAsync("24 Cavity", where)).ToDictionary(r => r.AreaId, r => r.Available);
            Assert.True((await trays.AddStockAsync(MainArea, MainPolyhouse, "70 Cavity", 1, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue),
                SowingSupervisor, "wf1019", "wf1019", Any)).Success);
            Assert.Equal(other24, (await trays.GetCavityAvailabilityAsync("24 Cavity", where)).ToDictionary(r => r.AreaId, r => r.Available));
        }
    }
}
