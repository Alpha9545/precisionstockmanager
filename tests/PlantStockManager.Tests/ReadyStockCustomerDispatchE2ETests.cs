using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using MovePage = PlantStockManager.Pages.Production.ReadyStock.MoveModel;

namespace PlantStockManager.Tests
{
    // 2026-10-19: Seed Production > Ready Stock > Send > Customer -- Ready Seedling Stock dispatched straight to a customer
    // (the existing direct tray sale, SeedlingFulfilmentRepository.DirectTraySaleAsync, now also from a Main Office batch when
    // started from ReadyStock/Move). SCRATCH COPY only, with REAL users (principals from dbo.UserRoles via the claims factory).
    // The Office Coordinator's ReadyStock.DispatchToCustomer comes from 2026-10-23_ReadyStockDispatchToCustomer.sql: on a copy
    // without it the Office Coordinator must NOT reach the dispatch (asserted), and the page flow is driven by the System
    // Administrator (full access) instead.
    public partial class WorkflowCorrections1019E2ETests
    {
        private const string Customer = "ZZTEST Direct Customer";
        private const string CustomerPhone = "9000000019";

        private static SeedlingFulfilmentRepository.DirectTraySaleRequest Dispatch(int rs, int trays, Guid? token = null, string? name = Customer,
            string? contact = CustomerPhone, bool fromReadyStock = true)
            => new(rs, trays, name, contact, "wf1019 customer address", "wf1019 customer dispatch", token ?? Guid.NewGuid(), fromReadyStock);

        private static Task<decimal> DispatchedAsync(Env env, int rs) => env.ScalarAsync<decimal>("SELECT DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs));
        private static Task<int> DispatchCountAsync(Env env, int rs) => env.ScalarAsync<int>(
            "SELECT COUNT(DISTINCT SeedlingDispatchId) FROM dbo.SeedlingDispatchLines WHERE ReadyStockId = @R", ("@R", rs));

        private static async Task<int> OfficeCoordinatorAsync(Env env)
        {
            var oc = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Office Coordinator' AND ur.AreaId = @A
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x JOIN dbo.Roles xr ON xr.Id = x.RoleId WHERE x.UserId = ur.UserId AND xr.Name = N'System Administrator')
ORDER BY ur.UserId", ("@A", MainArea));
            Skip.If(oc == 0, "No active Office Coordinator in the Main Office Area of this copy.");
            return oc;
        }

        private static Task<bool> OfficeCoordinatorHoldsDispatchAsync(Env env) => env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.RolePermissions rp
JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Office Coordinator' AND p.Code = @C", ("@C", MovePage.CustomerDispatchPolicy))
            .ContinueWith(t => t.Result > 0);

        [SkippableFact]
        public async Task CustomerDispatch_MainOfficeBatch_DeductsOnce_ReplayOnce_ConcurrentNeverOverdraws_RefusalsChangeNothing()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.SeedlingSaleTokenProbeSql);
            var v = await NewVarietyAsync(env);
            var rs = await ApprovedMainBatchAsync(env, v, 480);                                     // 20 trays of 24, Main Office
            var sales = env.Get<SeedlingFulfilmentRepository>();
            var actor = new SeedlingFulfilmentRepository.Actor("wf1019-dispatch", Recorder);

            async Task Refused(SeedlingFulfilmentRepository.DirectTraySaleRequest r, string expect, Func<int, bool>? access = null)
            {
                var (ok, msg, _) = await sales.DirectTraySaleAsync(r, actor, access ?? Any);
                Assert.False(ok);
                Assert.Contains(expect, msg);
                Assert.Equal(0m, await DispatchedAsync(env, rs));
            }

            // the Mother Plant direct sale is unchanged: a Main Office batch is still refused there
            await Refused(Dispatch(rs, 1, fromReadyStock: false), "active Mother Plant Area");
            // out of the user's Areas, no customer, no contact, nothing / too much
            await Refused(Dispatch(rs, 1), "not authorized", _ => false);
            await Refused(Dispatch(rs, 1, name: "  "), "Customer name is required");
            await Refused(Dispatch(rs, 1, contact: null), "contact number is required");
            await Refused(Dispatch(rs, 0), "greater than zero");
            await Refused(Dispatch(rs, 21), "Only 480 plants (20 complete 24 Cavity trays)");
            await Refused(Dispatch(rs, 1, token: Guid.Empty), "expired");

            // the same form three times at once: one dispatch, the plants leave the batch once
            var token = Guid.NewGuid();
            var same = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => sales.DirectTraySaleAsync(Dispatch(rs, 3, token), actor, a => a == MainArea)));
            Assert.All(same, r => Assert.True(r.Success, r.Message));
            Assert.Single(same.Select(r => r.DispatchCode).Distinct());
            Assert.Equal(2, same.Count(r => r.Message == SeedlingFulfilmentRepository.AlreadySoldMessage));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedlingDispatches WHERE SubmissionToken = @T", ("@T", token)));
            Assert.Equal((72m, 0m), (await DispatchedAsync(env, rs),
                await env.ScalarAsync<decimal>("SELECT ReservedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs))));

            // the dispatch is a customer dispatch, linked to its customer, and told apart from the Mother Plant direct sale
            var code = same[0].DispatchCode!;
            Assert.Equal((Customer, 72m, "Completed"), (
                await env.ScalarAsync<string>("SELECT CustomerName FROM dbo.SeedlingDispatches WHERE DispatchCode = @C", ("@C", code)),
                await env.ScalarAsync<decimal>("SELECT TotalQuantity FROM dbo.SeedlingDispatches WHERE DispatchCode = @C", ("@C", code)),
                await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedlingDispatches WHERE DispatchCode = @C", ("@C", code))));
            Assert.StartsWith(SeedlingFulfilmentRepository.DirectCustomerDispatchLabel + ": 3 x 24 Cavity trays of batch",
                await env.ScalarAsync<string>("SELECT Remarks FROM dbo.SeedlingDispatches WHERE DispatchCode = @C", ("@C", code)));
            Assert.Equal((Customer, CustomerPhone, "Completed", 72), (
                await env.ScalarAsync<string>("SELECT b.CustomerName FROM dbo.Bookings b JOIN dbo.SeedlingDispatches d ON d.BookingId = b.Id WHERE d.DispatchCode = @C", ("@C", code)),
                await env.ScalarAsync<string>("SELECT b.Contact FROM dbo.Bookings b JOIN dbo.SeedlingDispatches d ON d.BookingId = b.Id WHERE d.DispatchCode = @C", ("@C", code)),
                await env.ScalarAsync<string>("SELECT b.Status FROM dbo.Bookings b JOIN dbo.SeedlingDispatches d ON d.BookingId = b.Id WHERE d.DispatchCode = @C", ("@C", code)),
                await env.ScalarAsync<int>("SELECT b.Quantity FROM dbo.Bookings b JOIN dbo.SeedlingDispatches d ON d.BookingId = b.Id WHERE d.DispatchCode = @C", ("@C", code))));
            // exactly one 'Dispatch' ledger row for it, of -72
            Assert.Equal((1, -72m), (
                await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.ReadyStockTransactions rt JOIN dbo.SeedlingDispatches d ON d.Id = rt.ReferenceId
WHERE rt.ReferenceType = N'SeedlingDispatch' AND rt.TransactionType = N'Dispatch' AND d.DispatchCode = @C", ("@C", code)),
                await env.ScalarAsync<decimal>(@"SELECT SUM(rt.Quantity) FROM dbo.ReadyStockTransactions rt JOIN dbo.SeedlingDispatches d ON d.Id = rt.ReferenceId
WHERE rt.ReferenceType = N'SeedlingDispatch' AND rt.TransactionType = N'Dispatch' AND d.DispatchCode = @C", ("@C", code))));
            // the dispatch register (history) shows the customer and the quantity
            var line = Assert.Single(await sales.GetDispatchRegisterAsync(AppClock.Today, AppClock.Today, code), l => l.DispatchCode == code);
            Assert.Equal((Customer, 72m, rs), (line.CustomerName, line.Quantity, line.ReadyStockId));

            // three different forms at once for 6 trays each with 17 left: two succeed, never more than the batch
            var race = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => sales.DirectTraySaleAsync(Dispatch(rs, 6), actor, Any)));
            Assert.Equal(2, race.Count(r => r.Success));
            Assert.Equal(72m + 288m, await DispatchedAsync(env, rs));
            Assert.Equal(3, await DispatchCountAsync(env, rs));
            // the batch ledger explains Quantity - Dispatched; no reservation is left behind
            Assert.Equal(480m - 360m, await env.ScalarAsync<decimal>(@"SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R
AND TransactionType NOT IN ('Reservation', 'ReservationRelease', 'TransferHold', 'TransferHoldRelease')", ("@R", rs)));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT ReservedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)));
        }

        [SkippableFact]
        public async Task CustomerDispatch_Page_OfficeCoordinatorOnlyWithTheGrant_ViewerRefused_FullFlowOnce_OutletUnchanged()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.SeedlingSaleTokenProbeSql);
            var p = await PeopleAsync(env);
            var oc = await OfficeCoordinatorAsync(env);
            var granted = await OfficeCoordinatorHoldsDispatchAsync(env);
            var feature = env.Get<FeatureAccessService>();
            var v = await NewVarietyAsync(env);
            var rs = await ApprovedMainBatchAsync(env, v, 240);                                     // 10 trays of 24, Main Office

            async Task<(IActionResult Result, MovePage Page)> PostCustomerAsync(int user, int trays, Guid token, string? name = Customer)
            {
                var page = env.Page<MovePage>(await env.LoginAsync(user));
                page.Destination = MovePage.ToCustomer;
                (page.Trays, page.CustomerName, page.CustomerContact, page.CustomerAddress, page.Remarks, page.SubmissionToken)
                    = (trays, name, CustomerPhone, "wf1019 address", "wf1019 page", token);
                return (await page.OnPostAsync(rs), page);
            }
            static IEnumerable<string> Errors(PageModel page) => page.ModelState.Values.SelectMany(x => x.Errors).Select(e => e.ErrorMessage);

            // Office Coordinator: reaches the dispatch exactly when the role holds ReadyStock.DispatchToCustomer -- and never the
            // Main Office / Outlet transfer (no InternalTransfer.Enter / MainOffice.Confirm is granted)
            var ocPrincipal = await env.LoginAsync(oc);
            Assert.Equal(granted, await feature.CanAccessPageAsync(ocPrincipal, "/Production/ReadyStock/Move"));
            var ocGet = env.Page<MovePage>(ocPrincipal);
            Assert.IsType<PageResult>(await ocGet.OnGetAsync(rs));
            Assert.False(ocGet.CanTransfer);
            Assert.Equal(granted, ocGet.CanDispatchToCustomer);
            if (granted)
            {
                Assert.Equal(MovePage.ToCustomer, ocGet.Destination);                               // preselected
                var ocOutlet = env.Page<MovePage>(ocPrincipal);
                (ocOutlet.Destination, ocOutlet.DestinationAreaId, ocOutlet.Quantity, ocOutlet.SubmissionToken) = (MovePage.ToOutlet, OutletArea, 240, Guid.NewGuid());
                Assert.IsType<PageResult>(await ocOutlet.OnPostAsync(rs));
                Assert.Contains("You are not allowed to transfer stock.", Errors(ocOutlet));
                // a batch of a Mother Plant Area (not the Office Coordinator's Area): not offered, and the POST is refused
                var mpBatch = await ApprovedAreaBatchAsync(env, p, v, 48);
                var ocMp = env.Page<MovePage>(ocPrincipal);
                ocMp.Destination = MovePage.ToCustomer;
                (ocMp.Trays, ocMp.CustomerName, ocMp.CustomerContact, ocMp.SubmissionToken) = (1, Customer, CustomerPhone, Guid.NewGuid());
                var mpResult = await ocMp.OnPostAsync(mpBatch);
                Assert.True(mpResult is RedirectToPageResult || Errors(ocMp).Any());
                Assert.Equal(0m, await DispatchedAsync(env, mpBatch));
                // the Office Coordinator dispatches one tray of the Main Office batch
                var (ocSent, ocPage) = await PostCustomerAsync(oc, 1, Guid.NewGuid());
                Assert.IsType<RedirectToPageResult>(ocSent);
                Assert.Equal(24m, await DispatchedAsync(env, rs));
            }
            else
            {
                var (ocTried, ocPage) = await PostCustomerAsync(oc, 1, Guid.NewGuid());
                Assert.IsType<PageResult>(ocTried);
                Assert.Contains("You are not allowed to dispatch this stock to a customer.", Errors(ocPage));
                Assert.Equal(0m, await DispatchedAsync(env, rs));
            }
            var before = await DispatchedAsync(env, rs);

            // a Main Office user with ReadyStock.View only: neither the page nor a forged Customer POST
            Assert.False(await feature.CanAccessPageAsync(await env.LoginAsync(p.Viewer), "/Production/ReadyStock/Move"));
            var (viewerTried, viewerPage) = await PostCustomerAsync(p.Viewer, 1, Guid.NewGuid());
            Assert.IsType<PageResult>(viewerTried);
            Assert.Contains("You are not allowed to dispatch this stock to a customer.", Errors(viewerPage));
            Assert.Equal(before, await DispatchedAsync(env, rs));

            // the full flow (System Administrator): GET offers Customer and the transfer; validation refusals change nothing
            var get = env.Page<MovePage>(await env.LoginAsync(p.Admin));
            Assert.IsType<PageResult>(await get.OnGetAsync(rs));
            Assert.True(get.CanDispatchToCustomer && get.CanTransfer);
            Assert.True(get.CustomerEligibility.Available, get.CustomerEligibility.Reason);
            var (noName, noNamePage) = await PostCustomerAsync(p.Admin, 1, Guid.NewGuid(), name: null);
            Assert.IsType<PageResult>(noName);
            Assert.Contains("Customer name is required.", Errors(noNamePage));
            var left = (int)((240m - before) / 24m);
            var (tooMany, tooManyPage) = await PostCustomerAsync(p.Admin, left + 1, Guid.NewGuid());
            Assert.IsType<PageResult>(tooMany);
            Assert.Contains(Errors(tooManyPage), e => e.Contains($"{left} complete 24 Cavity trays"));
            Assert.Equal(before, await DispatchedAsync(env, rs));

            // confirmed: one dispatch, the confirmation names the customer, the variety / batch, the quantity and the reference;
            // the same form again records nothing more
            var token = Guid.NewGuid();
            var (sent, sentPage) = await PostCustomerAsync(p.Admin, 2, token);
            Assert.Equal("/Production/ReadyStock/Index", Assert.IsType<RedirectToPageResult>(sent).PageName);
            var code = await env.ScalarAsync<string>("SELECT DispatchCode FROM dbo.SeedlingDispatches WHERE SubmissionToken = @T", ("@T", token));
            var confirmation = Assert.IsType<string>(sentPage.TempData["Success"]);
            foreach (var part in new[] { Customer, CustomerPhone, "2 x 24 Cavity trays", "48 plants", get.Batch.BatchNo, get.Batch.SpeciesName!.Trim(), $"Dispatch reference {code}" })
                Assert.Contains(part, confirmation);
            var (again, againPage) = await PostCustomerAsync(p.Admin, 2, token);
            Assert.IsType<RedirectToPageResult>(again);
            Assert.Contains(SeedlingFulfilmentRepository.AlreadySoldMessage, againPage.TempData["Success"] as string);
            Assert.Equal(before + 48m, await DispatchedAsync(env, rs));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedlingDispatches WHERE SubmissionToken = @T", ("@T", token)));

            // a partly dispatched batch: the whole-batch transfer is explained as unavailable, the customer dispatch still offered
            var after = env.Page<MovePage>(await env.LoginAsync(p.Admin));
            Assert.IsType<PageResult>(await after.OnGetAsync(rs));
            Assert.False(after.TransferEligibility.Available);
            Assert.True(after.CustomerEligibility.Available);

            // Outlet dispatch is unchanged: a whole untouched batch moves to the Outlet once
            var whole = await ApprovedMainBatchAsync(env, v, 96);
            var outletToken = Guid.NewGuid();
            async Task<(IActionResult Result, MovePage Page)> SendAsync()
            {
                var send = env.Page<MovePage>(await env.LoginAsync(p.Admin));
                (send.Destination, send.DestinationAreaId, send.Quantity, send.SubmissionToken) = (MovePage.ToOutlet, OutletArea, 96, outletToken);
                return (await send.OnPostAsync(whole), send);
            }
            var (sendResult, sendPage) = await SendAsync();
            Assert.True(sendResult is RedirectToPageResult, string.Join("; ", Errors(sendPage)));
            Assert.Equal("/Production/ReadyStock/Index", ((RedirectToPageResult)sendResult).PageName);
            Assert.StartsWith($"Sent batch {await env.ScalarAsync<string>("SELECT BatchNo FROM dbo.ReadyStock WHERE Id = @R", ("@R", whole))}", sendPage.TempData["Success"] as string);
            // the same form again (the batch is now the Outlet's, so the Outlet is no longer a destination for it): nothing moves twice
            await SendAsync();
            Assert.Equal((OutletArea, 1, 0m), (await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", whole)),
                await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE SubmissionToken = @T", ("@T", outletToken)),
                await DispatchedAsync(env, whole)));
        }
    }
}
