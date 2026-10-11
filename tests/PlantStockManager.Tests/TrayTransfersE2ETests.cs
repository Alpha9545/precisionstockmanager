using System.Security.Claims;
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
    // Tray adjustment and tray transfer (2026-10-10) end-to-end against a SCRATCH COPY
    // (PlantsIMS2_Scratch_*, refused otherwise; SKIPPED without PSM_SCRATCH_CONNECTION or when
    // 2026-10-10_TrayTransfers.sql is not applied). Every check is a DELTA on pools the test
    // itself stocks, so data already in the copy does not matter.
    [Collection("ScratchDb")]
    public class TrayTransfersE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MainArea = 2, MainPolyhouse = 4;   // Main-Office-Areas / Facility-4
        private const int MpArea = 127, MpP1 = 14;            // Green Bless Nursery (Mother Plant) / P-1
        private const int UserId = 21;
        private const string Cavity = "102 Cavity";
        private static bool Any(int _) => true;

        private sealed class Env
        {
            public string ConnectionString { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
            public TrayStockRepository Trays => Provider.GetRequiredService<TrayStockRepository>();
            public TrayTransferRepository Transfers => Provider.GetRequiredService<TrayTransferRepository>();

            public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, typeof(T));
            }

            public Task<decimal> BalanceAsync(int area, int polyhouse, string cavity = Cavity)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId = @P AND TraySize = @C",
                    ("@A", area), ("@P", polyhouse), ("@C", cavity));

            // The tray ledger explains every pool the test touched.
            public async Task AssertLedgerExplainsAsync(int area, int polyhouse, string cavity = Cavity)
            {
                var net = await ScalarAsync<decimal>(@"SELECT ISNULL(SUM(x.Quantity), 0) FROM dbo.TrayStockTransactions x
JOIN dbo.TrayStock t ON t.Id = x.TrayStockId WHERE t.AreaId = @A AND t.PolyhouseId = @P AND t.TraySize = @C", ("@A", area), ("@P", polyhouse), ("@C", cavity));
                Assert.Equal(await BalanceAsync(area, polyhouse, cavity), net);
            }
        }

        private static async Task<Env> OpenAsync()
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
                using var mig = new SqlCommand("SELECT OBJECT_ID(N'dbo.TrayTransfers', N'U')", conn);
                Skip.If(await mig.ExecuteScalarAsync() is DBNull or null, "2026-10-10_TrayTransfers.sql is not applied to this scratch database.");
            }
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddScoped<TrayStockRepository>();
            services.AddScoped<BatchNumberRepository>();
            services.AddScoped<TrayTransferRepository>();
            services.AddScoped<AreaRepository>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions()));
            return new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider() };
        }

        private static int Token() => Random.Shared.Next(1, int.MaxValue);

        private static async Task StockAsync(Env env, int area, int polyhouse, decimal trays)
        {
            // dated a week back (2026-10-10): tests also send trays dated yesterday, which may only use trays already there
            var (ok, msg, _, _) = await env.Trays.AdjustAsync(area, polyhouse, Cavity, trays, AppClock.Today.AddDays(-7), Token(), "test setup: physical count", UserId, "tray-test", Any);
            Assert.True(ok, msg);
        }

        private static TrayTransferRequest Send(decimal qty, DateTime? date = null, Guid? token = null) => new()
        {
            SourceAreaId = MainArea, SourcePolyhouseId = MainPolyhouse, DestinationAreaId = MpArea, DestinationPolyhouseId = MpP1,
            TraySize = Cavity, Quantity = qty, TransferDate = date ?? AppClock.Today, Remarks = "tray transfer test", SubmissionToken = token ?? Guid.NewGuid(),
        };

        private static async Task<(string Type, DateTime BusinessDay, DateTime RecordedDay, decimal Qty, string? Remarks)> LastRowAsync(Env env, int area, int polyhouse)
        {
            using var conn = new SqlConnection(env.ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"SELECT TOP 1 x.TransactionType, x.TransactionDate, x.CreatedAt, x.Quantity, x.Remarks
FROM dbo.TrayStockTransactions x JOIN dbo.TrayStock t ON t.Id = x.TrayStockId
WHERE t.AreaId = @A AND t.PolyhouseId = @P AND t.TraySize = @C ORDER BY x.Id DESC", conn);
            cmd.Parameters.AddWithValue("@A", area);
            cmd.Parameters.AddWithValue("@P", polyhouse);
            cmd.Parameters.AddWithValue("@C", Cavity);
            using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            return (r.GetString(0), AppClock.ToIndiaTime(r.GetDateTime(1)).Date, AppClock.ToIndiaTime(r.GetDateTime(2)).Date, r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetString(4));
        }

        // ===== who sees which Areas' trays (TrayAreaScope, 2026-10-11) ===================================

        private static ClaimsPrincipal User(int[] areas, params string[] permissions)
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, "tray-scope-test") };
            claims.AddRange(permissions.Select(p => new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)));
            claims.AddRange(areas.Select(a => new Claim(AreaAccessService.AreaAccessClaimType, a.ToString())));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        [SkippableFact]
        public async Task TrayView_MotherPlantSupervisor_SeesOnlyItsOwnMotherPlantAreas_ReadOnly()
        {
            var env = await OpenAsync();
            var scope = new TrayAreaScope(env.Provider.GetRequiredService<AreaRepository>(), env.Provider.GetRequiredService<AreaAccessService>());
            var mpAreas = (await env.Provider.GetRequiredService<AreaRepository>().GetAllAreas())
                .Where(a => a.IsActive && a.AreaType == TrayAreaScope.MotherPlantAreaType).Select(a => a.Id).ToHashSet();
            Assert.Contains(MpArea, mpAreas);

            // Mother Plant Supervisor of Area 127 -- also (hypothetically) assigned to the Main Office Area 2:
            // the read-only permission still shows only its OWN Mother Plant Area.
            var mp = User(new[] { MpArea, MainArea }, MaterialStockAccess.TrayViewMotherPlant, TrayTransferRules.ReceivePermission);
            Assert.Equal(new[] { MpArea }, (await scope.ViewScopeAsync(mp))!.OrderBy(x => x).ToArray());
            var canSee = await scope.CanViewPredicateAsync(mp);
            Assert.True(canSee(MpArea));
            Assert.False(canSee(MainArea));
            Assert.All(mpAreas.Where(a => a != MpArea), a => Assert.False(canSee(a)));       // not the other Mother Plant Areas
            Assert.True(mp.CanViewTrays());
            Assert.False(mp.CanAddTrays());
            Assert.False(mp.CanAddMaterials());
            Assert.False(mp.CanViewMaterials());
            Assert.False(mp.CanAdjustTrays());
            Assert.False(mp.CanSendTrayTransfers());
            Assert.Empty(await scope.AddableAreasAsync(mp));                                   // adds trays nowhere

            // receiving alone is not viewing
            Assert.Empty((await scope.ViewScopeAsync(User(new[] { MpArea }, TrayTransferRules.ReceivePermission)))!);
            // unchanged: Booking Executive / Office Coordinator (EnterMotherPlant, Main Office) -> own Area + every Mother Plant Area
            var be = User(new[] { MainArea }, MaterialStockAccess.TrayAddMotherPlant);
            Assert.Equal(mpAreas.Append(MainArea).OrderBy(x => x).ToArray(), (await scope.ViewScopeAsync(be))!.OrderBy(x => x).ToArray());
            // unchanged: Sowing Supervisor -> own Areas
            Assert.Equal(new[] { MainArea }, (await scope.ViewScopeAsync(User(new[] { MainArea }, "TrayStock.Enter")))!.ToArray());
        }

        // ===== destination rule (2026-10-11) =============================================================

        // A scratch-only, ACTIVE Mother Plant Area + Polyhouse nobody is assigned to (no receiver). Made
        // inactive again at the end of the test, so no other test ever sees it as active.
        private static async Task<(int Area, int Polyhouse)> NoReceiverLocationAsync(Env env)
        {
            const string name = "ZZTEST-TT No Receiver";
            var area = await env.ScalarAsync<int>("SELECT ISNULL((SELECT TOP 1 Id FROM dbo.Area WHERE Name = @N), 0)", ("@N", name));
            if (area == 0)
                area = await env.ScalarAsync<int>("INSERT INTO dbo.Area (Name, IsActive, AreaType, Remarks) VALUES (@N, 1, N'MotherPlant', N'scratch test fixture'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", name));
            else
                await env.ScalarAsync<int>("UPDATE dbo.Area SET IsActive = 1 WHERE Id = @A; SELECT 1", ("@A", area));
            var polyhouse = await env.ScalarAsync<int>("SELECT ISNULL((SELECT TOP 1 Id FROM dbo.Polyhouses WHERE AreaId = @A), 0)", ("@A", area));
            if (polyhouse == 0)
                polyhouse = await env.ScalarAsync<int>("INSERT INTO dbo.Polyhouses (Name, AreaId) VALUES (N'ZZTEST-TT P-1', @A); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@A", area));
            return (area, polyhouse);
        }

        [SkippableFact]
        public async Task Transfer_OnlyToAreasWithALocationAwareReceiver_AdminMayHandleExceptions_AndCanCloseThem()
        {
            var env = await OpenAsync();
            var (noReceiverArea, noReceiverPolyhouse) = await NoReceiverLocationAsync(env);
            try
            {
                var receivable = await env.Transfers.GetReceivableAreaIdsAsync();
                Assert.Contains(MpArea, receivable);                                           // Green Bless: its Mother Plant Supervisor
                Assert.DoesNotContain(noReceiverArea, receivable);
                await StockAsync(env, MainArea, MainPolyhouse, 8);
                var src0 = await env.BalanceAsync(MainArea, MainPolyhouse);

                // 1. no receiver at the destination: refused for a non-administrator, nothing leaves the source
                var toNobody = Send(2); toNobody.DestinationAreaId = noReceiverArea; toNobody.DestinationPolyhouseId = noReceiverPolyhouse;
                var (ok, msg, _, _, _) = await env.Transfers.CreateAsync(toNobody, UserId, "tray-test", Any);
                Assert.False(ok);
                Assert.Equal(TrayTransferRepository.NoReceiverMessage, msg);
                Assert.Equal(src0, await env.BalanceAsync(MainArea, MainPolyhouse));

                // 2. the administrator's exceptional transfer there, received by the administrator: never stuck in transit
                toNobody.SubmissionToken = Guid.NewGuid();
                var (aOk, aMsg, id, _, _) = await env.Transfers.CreateAsync(toNobody, UserId, "tray-admin", Any, anyValidDestination: true);
                Assert.True(aOk, aMsg);
                Assert.True((await env.Transfers.ReceiveAsync(id, AppClock.Today, UserId, "tray-admin", Any)).Success);
                Assert.Equal(2m, await env.BalanceAsync(noReceiverArea, noReceiverPolyhouse));

                // 3. Main Office destinations: receivable exactly when a Main Area tray role (TrayStock.ReceiveTransferMainArea,
                //    2026-10-11_TrayAccessByLocation.sql) is assigned there -- the Sowing Supervisors of the Main Office
                const int facility3 = 3;
                var mainReceiver = await env.ScalarAsync<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'TrayStock.ReceiveTransferMainArea') THEN 1 ELSE 0 END") == 1;
                Assert.Equal(mainReceiver, receivable.Contains(MainArea));
                var toMain = Send(2); toMain.DestinationAreaId = MainArea; toMain.DestinationPolyhouseId = facility3;
                var (mOk, mMsg, mId, _, _) = await env.Transfers.CreateAsync(toMain, UserId, "tray-test", Any);
                Assert.Equal(mainReceiver, mOk);
                if (mOk)
                {
                    var d0 = await env.BalanceAsync(MainArea, facility3);
                    Assert.True((await env.Transfers.ReceiveAsync(mId, AppClock.Today, UserId, "sowing-supervisor", Any)).Success);
                    Assert.Equal(d0 + 2, await env.BalanceAsync(MainArea, facility3));
                }
                else Assert.Equal(TrayTransferRepository.NoReceiverMessage, mMsg);

                // 4. even the administrator never sends to an Outlet
                var outletPolyhouse = await env.ScalarAsync<int>("SELECT TOP 1 p.Id FROM dbo.Polyhouses p JOIN dbo.Area a ON a.Id = p.AreaId WHERE a.AreaType = N'Outlet' AND a.IsActive = 1");
                if (outletPolyhouse > 0)
                {
                    var outletArea = await env.ScalarAsync<int>("SELECT AreaId FROM dbo.Polyhouses WHERE Id = @P", ("@P", outletPolyhouse));
                    var toOutlet = Send(1); toOutlet.DestinationAreaId = outletArea; toOutlet.DestinationPolyhouseId = outletPolyhouse;
                    var (oOk, oMsg, _, _, _) = await env.Transfers.CreateAsync(toOutlet, UserId, "tray-admin", Any, anyValidDestination: true);
                    Assert.False(oOk);
                    Assert.Contains("non-Outlet", oMsg);
                }
                await env.AssertLedgerExplainsAsync(MainArea, MainPolyhouse);
                await env.AssertLedgerExplainsAsync(MainArea, facility3);
            }
            finally
            {
                await env.ScalarAsync<int>("UPDATE dbo.Area SET IsActive = 0 WHERE Id = @A; SELECT 1", ("@A", noReceiverArea));
            }
        }

        // ===== receiving: location-aware, nobody else (2026-10-10) =======================================
        // The same rule the Tray Transfers page passes to the repository (TrayAreaScope.IsReceivableBy).
        private static async Task<Func<int, bool>> ReceiverRuleAsync(Env env, ClaimsPrincipal user)
        {
            var areas = (await env.Provider.GetRequiredService<AreaRepository>().GetAllAreas()).ToDictionary(a => a.Id);
            var access = env.Provider.GetRequiredService<AreaAccessService>();
            return id => access.HasFullAreaAccess(user)
                || (areas.TryGetValue(id, out var a) && TrayAreaScope.IsReceivableBy(user, a, x => access.CanAccessRequiredArea(user, x), false));
        }

        [SkippableFact]
        public async Task Receive_OnlyTheReceiverOfThatLocation_OthersAreRefused_AndNothingMoves()
        {
            var env = await OpenAsync();
            await StockAsync(env, MainArea, MainPolyhouse, 6);
            const int facility3 = 3;
            var toMain = Send(1); toMain.DestinationAreaId = MainArea; toMain.DestinationPolyhouseId = facility3;
            var (mOk, mMsg, toMainId, _, _) = await env.Transfers.CreateAsync(toMain, UserId, "tray-admin", Any, anyValidDestination: true);
            Assert.True(mOk, mMsg);
            var (pOk, pMsg, toMpId, _, _) = await env.Transfers.CreateAsync(Send(1), UserId, "tray-test", Any);
            Assert.True(pOk, pMsg);
            var main0 = await env.BalanceAsync(MainArea, facility3);
            var mp0 = await env.BalanceAsync(MpArea, MpP1);

            var mpSupervisor = User(new[] { MpArea }, TrayTransferRules.ReceivePermission, MaterialStockAccess.TrayViewMotherPlant);
            var sowingSupervisor = User(new[] { MainArea }, "TrayStock.Enter", TrayTransferRules.ReceiveMainAreaPermission);
            var bookingExecutive = User(new[] { MainArea }, "Booking.View", MaterialStockAccess.TrayAddMotherPlant);
            async Task<(bool Success, string? Message)> Receive(int id, ClaimsPrincipal who)
                => await env.Transfers.ReceiveAsync(id, AppClock.Today, UserId, "tray-test", await ReceiverRuleAsync(env, who));
            async Task<(bool Success, string? Message)> Reject(int id, ClaimsPrincipal who)
                => await env.Transfers.ReturnAsync(id, true, "wrong place", UserId, "tray-test", await ReceiverRuleAsync(env, who));

            // wrong location type, or no receive permission at all: refused, receive and reject alike
            Assert.Contains("not authorized", (await Receive(toMainId, mpSupervisor)).Message);
            Assert.Contains("not authorized", (await Receive(toMpId, sowingSupervisor)).Message);
            Assert.Contains("not authorized", (await Receive(toMainId, bookingExecutive)).Message);
            Assert.Contains("not authorized", (await Receive(toMpId, bookingExecutive)).Message);
            Assert.False((await Reject(toMainId, mpSupervisor)).Success);
            Assert.False((await Reject(toMpId, sowingSupervisor)).Success);
            Assert.Equal((main0, mp0), (await env.BalanceAsync(MainArea, facility3), await env.BalanceAsync(MpArea, MpP1)));
            Assert.All(new[] { toMainId, toMpId }, id => Assert.Equal(TrayTransferRules.StatusPending, env.Transfers.GetByIdAsync(id).Result!.Status));

            // the receiver of each location
            Assert.True((await Receive(toMainId, sowingSupervisor)).Success);
            Assert.True((await Receive(toMpId, mpSupervisor)).Success);
            Assert.Equal((main0 + 1, mp0 + 1), (await env.BalanceAsync(MainArea, facility3), await env.BalanceAsync(MpArea, MpP1)));
        }

        // ===== adjustment =================================================================================

        [SkippableFact]
        public async Task Adjust_AddsAndRemoves_OneLedgerRowEach_BusinessDated_NeverBelowZero()
        {
            var env = await OpenAsync();
            var before = await env.BalanceAsync(MainArea, MainPolyhouse);
            var countDay = AppClock.Today.AddDays(-1);

            var (ok, msg, dup, balance) = await env.Trays.AdjustAsync(MainArea, MainPolyhouse, Cavity, 7, countDay, Token(), "Found 7 trays in store", UserId, "tray-test", Any);
            Assert.True(ok, msg);
            Assert.False(dup);
            Assert.Equal(before + 7, balance);
            var row = await LastRowAsync(env, MainArea, MainPolyhouse);
            Assert.Equal(("Adjustment", countDay, AppClock.Today, 7m), (row.Type, row.BusinessDay, row.RecordedDay, row.Qty));
            Assert.Contains("Found 7 trays in store", row.Remarks);

            var (rOk, rMsg, _, after) = await env.Trays.AdjustAsync(MainArea, MainPolyhouse, Cavity, -(before + 7), AppClock.Today, Token(), "All broken", UserId, "tray-test", Any);
            Assert.True(rOk, rMsg);
            Assert.Equal(0m, after);

            var (nOk, nMsg, _, _) = await env.Trays.AdjustAsync(MainArea, MainPolyhouse, Cavity, -1, AppClock.Today, Token(), "One more", UserId, "tray-test", Any);
            Assert.False(nOk);
            Assert.Contains("below zero", nMsg);
            Assert.Equal(0m, await env.BalanceAsync(MainArea, MainPolyhouse));
            await env.AssertLedgerExplainsAsync(MainArea, MainPolyhouse);
        }

        [SkippableFact]
        public async Task Adjust_IsValidatedOnTheServer_AndARepeatedSubmitChangesNothingTwice()
        {
            var env = await OpenAsync();
            var before = await env.BalanceAsync(MainArea, MainPolyhouse);
            async Task<string?> Refused(int area, int polyhouse, string cavity, decimal delta, DateTime date, string? reason, Func<int, bool>? access = null)
            {
                var (ok, msg, _, _) = await env.Trays.AdjustAsync(area, polyhouse, cavity, delta, date, Token(), reason, UserId, "tray-test", access ?? Any);
                Assert.False(ok);
                return msg;
            }
            Assert.Contains("reason", await Refused(MainArea, MainPolyhouse, Cavity, 3, AppClock.Today, " "));
            Assert.Contains("whole number", await Refused(MainArea, MainPolyhouse, Cavity, 1.5m, AppClock.Today, "x"));
            Assert.Contains("Cavity", await Refused(MainArea, MainPolyhouse, "7 Cavity", 1, AppClock.Today, "x"));
            Assert.Contains("future", await Refused(MainArea, MainPolyhouse, Cavity, 1, AppClock.Today.AddDays(1), "x"));
            Assert.Contains("not authorized", await Refused(MainArea, MainPolyhouse, Cavity, 1, AppClock.Today, "x", _ => false));
            Assert.Contains("does not belong", await Refused(MpArea, MainPolyhouse, Cavity, 1, AppClock.Today, "x"));   // forged pair
            Assert.Equal(before, await env.BalanceAsync(MainArea, MainPolyhouse));

            var token = Token();
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                env.Trays.AdjustAsync(MainArea, MainPolyhouse, Cavity, 5, AppClock.Today, token, "Recount", UserId, "tray-test", Any)));
            Assert.All(results, r => Assert.True(r.Success, r.Message));
            Assert.Equal(3, results.Count(r => r.Duplicate));
            Assert.Equal(before + 5, await env.BalanceAsync(MainArea, MainPolyhouse));
            await env.AssertLedgerExplainsAsync(MainArea, MainPolyhouse);
        }

        // ===== transfer ===================================================================================

        [SkippableFact]
        public async Task Transfer_SentYesterday_InTransitInNeitherPool_ReceivedToday_AtTheDestination()
        {
            var env = await OpenAsync();
            await StockAsync(env, MainArea, MainPolyhouse, 30);
            var src0 = await env.BalanceAsync(MainArea, MainPolyhouse);
            var dst0 = await env.BalanceAsync(MpArea, MpP1);
            var transit0 = (await env.Transfers.GetInTransitAsync(Any)).Trays;
            var yesterday = AppClock.Today.AddDays(-1);

            var (ok, msg, id, code, dup) = await env.Transfers.CreateAsync(Send(12, yesterday), UserId, "tray-test", Any);
            Assert.True(ok, msg);
            Assert.False(dup);
            Assert.StartsWith("TT-", code);
            Assert.Equal(src0 - 12, await env.BalanceAsync(MainArea, MainPolyhouse));   // left the source at sending
            Assert.Equal(dst0, await env.BalanceAsync(MpArea, MpP1));                   // not at the destination yet
            Assert.Equal(transit0 + 12, (await env.Transfers.GetInTransitAsync(Any)).Trays);
            var sent = await LastRowAsync(env, MainArea, MainPolyhouse);
            Assert.Equal(("TransferOut", yesterday, AppClock.Today, -12m), (sent.Type, sent.BusinessDay, sent.RecordedDay, sent.Qty));

            Assert.Contains("before the transfer was sent", (await env.Transfers.ReceiveAsync(id, yesterday.AddDays(-1), UserId, "tray-test", Any)).Message);
            Assert.Contains("not authorized", (await env.Transfers.ReceiveAsync(id, AppClock.Today, UserId, "tray-test", _ => false)).Message);
            var (rOk, rMsg) = await env.Transfers.ReceiveAsync(id, AppClock.Today, UserId, "tray-test", Any);
            Assert.True(rOk, rMsg);
            Assert.Equal(dst0 + 12, await env.BalanceAsync(MpArea, MpP1));
            Assert.Equal(src0 - 12, await env.BalanceAsync(MainArea, MainPolyhouse));
            Assert.Equal(transit0, (await env.Transfers.GetInTransitAsync(Any)).Trays);
            var got = await LastRowAsync(env, MpArea, MpP1);
            Assert.Equal(("TransferIn", AppClock.Today, 12m), (got.Type, got.BusinessDay, got.Qty));
            var t = (await env.Transfers.GetByIdAsync(id))!;
            Assert.Equal(TrayTransferRules.StatusReceived, t.Status);
            Assert.NotNull(t.DestinationTrayStockId);

            // Closed: cannot be received, rejected or cancelled again.
            Assert.False((await env.Transfers.ReceiveAsync(id, AppClock.Today, UserId, "tray-test", Any)).Success);
            Assert.False((await env.Transfers.ReturnAsync(id, true, "late", UserId, "tray-test", Any)).Success);
            Assert.False((await env.Transfers.ReturnAsync(id, false, "late", UserId, "tray-test", Any)).Success);
            Assert.Equal(dst0 + 12, await env.BalanceAsync(MpArea, MpP1));
            await env.AssertLedgerExplainsAsync(MainArea, MainPolyhouse);
            await env.AssertLedgerExplainsAsync(MpArea, MpP1);
        }

        [SkippableFact]
        public async Task Transfer_RejectAndCancel_ReturnTheTraysToTheSource_WithAReason()
        {
            var env = await OpenAsync();
            await StockAsync(env, MainArea, MainPolyhouse, 20);
            var src0 = await env.BalanceAsync(MainArea, MainPolyhouse);
            var dst0 = await env.BalanceAsync(MpArea, MpP1);
            var (_, _, t1, _, _) = await env.Transfers.CreateAsync(Send(5), UserId, "tray-test", Any);
            var (_, _, t2, _, _) = await env.Transfers.CreateAsync(Send(3), UserId, "tray-test", Any);
            Assert.Equal(src0 - 8, await env.BalanceAsync(MainArea, MainPolyhouse));

            Assert.False((await env.Transfers.ReturnAsync(t1, true, " ", UserId, "tray-test", Any)).Success);                 // reason required
            Assert.False((await env.Transfers.ReturnAsync(t1, true, "wrong cavity", UserId, "tray-test", _ => false)).Success); // destination access
            Assert.True((await env.Transfers.ReturnAsync(t1, true, "wrong cavity", UserId, "tray-test", Any)).Success);
            Assert.False((await env.Transfers.ReturnAsync(t2, false, "sent by mistake", UserId, "tray-test", _ => false)).Success); // source access
            Assert.True((await env.Transfers.ReturnAsync(t2, false, "sent by mistake", UserId, "tray-test", Any)).Success);
            Assert.False((await env.Transfers.ReturnAsync(t2, false, "again", UserId, "tray-test", Any)).Success);           // replayed
            Assert.False((await env.Transfers.ReceiveAsync(t1, AppClock.Today, UserId, "tray-test", Any)).Success);          // rejected cannot be received

            Assert.Equal(src0, await env.BalanceAsync(MainArea, MainPolyhouse));
            Assert.Equal(dst0, await env.BalanceAsync(MpArea, MpP1));
            var r1 = (await env.Transfers.GetByIdAsync(t1))!;
            Assert.Equal((TrayTransferRules.StatusRejected, "wrong cavity"), (r1.Status, r1.CloseReason));
            Assert.Equal(TrayTransferRules.StatusCancelled, (await env.Transfers.GetByIdAsync(t2))!.Status);
            var back = await LastRowAsync(env, MainArea, MainPolyhouse);
            Assert.Equal(("TransferReturn", 3m), (back.Type, back.Qty));
            await env.AssertLedgerExplainsAsync(MainArea, MainPolyhouse);
        }

        [SkippableFact]
        public async Task Transfer_IsValidatedOnTheServer_AndChangesNothingWhenRefused()
        {
            var env = await OpenAsync();
            await StockAsync(env, MainArea, MainPolyhouse, 4);
            var src0 = await env.BalanceAsync(MainArea, MainPolyhouse);
            async Task<string?> Refused(TrayTransferRequest req, Func<int, bool>? access = null)
            {
                var (ok, msg, _, _, _) = await env.Transfers.CreateAsync(req, UserId, "tray-test", access ?? Any);
                Assert.False(ok);
                return msg;
            }
            var tooMany = Send(src0 + 1);
            Assert.Contains($"Only {QuantityFormat.Qty(src0)}", await Refused(tooMany));
            Assert.Contains("whole number", await Refused(Send(1.5m)));
            Assert.Contains("future", await Refused(Send(1, AppClock.Today.AddDays(1))));
            Assert.Contains("not authorized", await Refused(Send(1), _ => false));
            var samePolyhouse = Send(1); samePolyhouse.DestinationAreaId = MainArea; samePolyhouse.DestinationPolyhouseId = MainPolyhouse;
            Assert.Contains("different Polyhouses", await Refused(samePolyhouse));
            var forged = Send(1); forged.DestinationAreaId = MainArea;                     // Polyhouse 14 is not in Area 2
            Assert.Contains("does not belong", await Refused(forged));
            var noToken = Send(1); noToken.SubmissionToken = Guid.Empty;
            Assert.Contains("expired", await Refused(noToken));
            Assert.Equal(src0, await env.BalanceAsync(MainArea, MainPolyhouse));
        }

        [SkippableFact]
        public async Task Transfer_RepeatedSubmit_AndConcurrentSendsAndReceives_NeverDoubleCount()
        {
            var env = await OpenAsync();
            await StockAsync(env, MainArea, MainPolyhouse, 10);
            var src0 = await env.BalanceAsync(MainArea, MainPolyhouse);
            var dst0 = await env.BalanceAsync(MpArea, MpP1);

            var token = Guid.NewGuid();
            var same = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => env.Transfers.CreateAsync(Send(2, token: token), UserId, "tray-test", Any)));
            Assert.All(same, r => Assert.True(r.Success, r.Message));
            Assert.Single(same.Select(r => r.TransferId).Distinct());
            Assert.Equal(src0 - 2, await env.BalanceAsync(MainArea, MainPolyhouse));

            var left = src0 - 2;
            var per = Math.Floor(left / 2) > 0 ? Math.Floor(left / 2) : 1;
            var racing = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => env.Transfers.CreateAsync(Send(per), UserId, "tray-test", Any)));
            Assert.Equal(Math.Min(5, (int)Math.Floor(left / per)), racing.Count(r => r.Success));
            Assert.True(await env.BalanceAsync(MainArea, MainPolyhouse) >= 0);

            var receive = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => env.Transfers.ReceiveAsync(same[0].TransferId, AppClock.Today, UserId, "tray-test", Any)));
            Assert.Equal(1, receive.Count(r => r.Success));                                  // atomic: received once
            Assert.Equal(dst0 + 2, await env.BalanceAsync(MpArea, MpP1));
            await env.AssertLedgerExplainsAsync(MainArea, MainPolyhouse);
            await env.AssertLedgerExplainsAsync(MpArea, MpP1);
        }
    }
}
