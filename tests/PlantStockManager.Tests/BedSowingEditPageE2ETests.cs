using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using BedDetails = PlantStockManager.Pages.Production.BedSowing.DetailsModel;
using BedEdit = PlantStockManager.Pages.Production.BedSowing.EditModel;

namespace PlantStockManager.Tests
{
    // 2026-10-22: the Bed Sowing Edit PAGE with a real login (the System Administrator of the scratch copy, principal from
    // dbo.UserRoles via the app's claims factory): GET fills the form, POST saves and returns to Details with a success
    // message, a failed POST shows the reason and changes nothing, and a bed that is not untouched is sent back to Details.
    public partial class WorkflowCorrections1019E2ETests
    {
        [SkippableFact]
        public async Task BedSowingEditPage_PrefillsSavesAndRedirects_RefusesTouchedOrStaleBeds()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.BedSowingEditProbeSql);
            var p = await PeopleAsync(env);
            var admin = await env.LoginAsync(p.Admin);
            var location = await env.ScalarAsync<int>("SELECT TOP 1 PolyhouseId FROM dbo.BedSowingLocations WHERE IsActive = 1 ORDER BY PolyhouseId");
            Skip.If(location == 0, "No Bed Sowing location in this copy.");
            var v = await NewVarietyAsync(env);
            var bed = new BedSowing { SpeciesId = v.SpeciesId, SeedStockId = v.SeedStockId, PolyhouseId = location, Quantity = 800,
                SowingDate = AppClock.Today.AddDays(-6), Remarks = "page test", SubmissionToken = Guid.NewGuid() };
            var (ok, msg, bedId) = await env.Get<BedSowingRepository>().CreateAsync(bed, new BedSowingRepository.Actor(p.Admin, "page-test", true), Any);
            Assert.True(ok, msg);

            // Details offers Edit for this untouched bed
            var details = env.Page<BedDetails>(admin);
            Assert.IsType<PageResult>(await details.OnGetAsync(bedId));
            Assert.True(details.CanEdit);

            // GET: the form holds the bed's values
            var get = env.Page<BedEdit>(admin);
            Assert.IsType<PageResult>(await get.OnGetAsync(bedId));
            Assert.Equal((AppClock.Today.AddDays(-6), v.SpeciesId, v.SeedStockId, 800m, "page test"),
                (get.SowingDate, get.SpeciesId, get.SeedStockId, get.Quantity, get.Remarks));
            Assert.Equal((get.SowingDate, get.SpeciesId, get.SeedStockId, get.Quantity), (get.OriginalSowingDate, get.OriginalSpeciesId, get.OriginalSeedStockId, get.OriginalQuantity));
            Assert.True(get.EditSchemaReady);

            BedEdit Post(decimal qty, string? reason)
            {
                var page = env.Page<BedEdit>(admin);
                (page.SowingDate, page.PlantTypeId, page.SpeciesId, page.SeedStockId, page.Quantity, page.Remarks, page.Reason)
                    = (get.SowingDate, get.PlantTypeId, get.SpeciesId, get.SeedStockId, qty, "page test edited", reason);
                (page.OriginalSowingDate, page.OriginalSpeciesId, page.OriginalSeedStockId, page.OriginalQuantity, page.OriginalRemarks)
                    = (get.OriginalSowingDate, get.OriginalSpeciesId, get.OriginalSeedStockId, get.OriginalQuantity, get.OriginalRemarks);
                return page;
            }

            // POST without the required reason: the form again, with the reason; nothing changed
            var noReason = Post(750, null);
            Assert.IsType<PageResult>(await noReason.OnPostAsync(bedId));
            Assert.Contains(noReason.ModelState.Values.SelectMany(x => x.Errors), e => e.ErrorMessage.Contains("reason is required"));
            Assert.Equal(800m, await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.BedSowings WHERE Id = @B", ("@B", bedId)));

            // POST: saved, back to Details with a success message naming the change
            var save = Post(750, "page test recount");
            var redirect = Assert.IsType<RedirectToPageResult>(await save.OnPostAsync(bedId));
            Assert.Equal("/Production/BedSowing/Details", redirect.PageName);
            Assert.Contains("Quantity 800 -> 750", save.TempData["Success"] as string);
            Assert.Equal((750m, "page test edited"), (await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.BedSowings WHERE Id = @B", ("@B", bedId)),
                await env.ScalarAsync<string>("SELECT Remarks FROM dbo.BedSowings WHERE Id = @B", ("@B", bedId))));

            // the same form posted again: stale, refused, nothing applied twice
            var again = Post(750, "page test recount");
            Assert.IsType<PageResult>(await again.OnPostAsync(bedId));
            Assert.Contains(again.ModelState.Values.SelectMany(x => x.Errors), e => e.ErrorMessage == BedSowingRules.EditStaleMessage);
            Assert.Equal(-750m, await env.ScalarAsync<decimal>(
                "SELECT SUM(Quantity) FROM dbo.SeedStockTransactions WHERE ReferenceType = N'BedSowing' AND ReferenceId = @B", ("@B", bedId)));

            // a cancelled bed: Edit sends the user back to Details with the reason, and Details no longer offers Edit
            Assert.True((await env.Get<BedSowingRepository>().CancelAsync(bedId, "page test", new BedSowingRepository.Actor(p.Admin, "page-test", true), Any)).Success);
            var refused = env.Page<BedEdit>(admin);
            Assert.IsType<RedirectToPageResult>(await refused.OnGetAsync(bedId));
            Assert.Equal(BedSowingRules.EditCancelledMessage, refused.TempData["Error"]);
            var after = env.Page<BedDetails>(admin);
            await after.OnGetAsync(bedId);
            Assert.False(after.CanEdit);
        }
    }
}
