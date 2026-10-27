using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Data;
using PlantStockManager.Services;
using OvergrowthPage = PlantStockManager.Pages.Production.ReadyStock.OvergrowthModel;
using ReadyStockIndex = PlantStockManager.Pages.Production.ReadyStock.IndexModel;

namespace PlantStockManager.Tests
{
    // 2026-10-23: the Ready Stock "Overgrowth" filter (All / Overgrowth only / Normal only) -- on its own and combined with
    // Search and "Include batches with no plants left", in the list, the totals and the exports, and carried through the
    // Overgrowth / Trace pages back to the list. SCRATCH COPY with 2026-10-24_ReadyStockOvergrowth.sql (skipped otherwise).
    // Filtering never changes a quantity: every batch's Quantity / Reserved / Dispatched / In Transit is read before and after.
    public partial class WorkflowCorrections1019E2ETests
    {
        private static async Task<ReadyStockIndex> ReadyStockListAsync(Env env, string? search, string? stock, bool includeEmpty = false)
        {
            var page = env.Page<ReadyStockIndex>(await env.LoginAsync(SowingSupervisor));
            page.Search = search;
            page.Stock = stock;
            page.IncludeEmpty = includeEmpty;
            await page.OnGetAsync();
            return page;
        }

        private static int[] Ids(ReadyStockIndex page, params int[] mine) => page.Items.Select(i => i.Id).Where(mine.Contains).OrderBy(x => x).ToArray();
        private static int[] Sorted(params int[] ids) => ids.OrderBy(x => x).ToArray();

        [SkippableFact]
        public async Task OvergrowthFilter_CombinesWithSearchAndIncludeEmpty_InTheListTotalsAndExports_QuantitiesUnchanged()
        {
            var env = await OpenOvergrowthAsync(WorkflowCorrectionsSchema.SeedlingSaleTokenProbeSql);
            var a = await NewVarietyAsync(env);
            var b = await NewVarietyAsync(env);
            var aOg = await ApprovedMainBatchAsync(env, a, plants: 240);
            var aNormal = await ApprovedMainBatchAsync(env, a, plants: 480);
            var aOgEmpty = await ApprovedMainBatchAsync(env, a, plants: 48);                  // 2 x 24, sold out below
            var bOg = await ApprovedMainBatchAsync(env, b, plants: 240);
            var repo = env.Get<ReadyStockRepository>();
            foreach (var batch in new[] { aOg, aOgEmpty, bOg })
                Assert.True((await repo.SetOvergrowthAsync(batch, true, "filter test", OgActor(SowingSupervisor))).Changed);
            var (sold, soldMsg, _) = await env.Get<SeedlingFulfilmentRepository>().DirectTraySaleAsync(
                new SeedlingFulfilmentRepository.DirectTraySaleRequest(aOgEmpty, 2, "OG filter customer", "9999999999", null, "og filter", Guid.NewGuid(), FromReadySeedlingStock: true),
                new SeedlingFulfilmentRepository.Actor("og-test", SowingSupervisor), Any);
            Assert.True(sold, soldMsg);
            var mine = new[] { aOg, aNormal, aOgEmpty, bOg };
            var before = new List<Balances>();
            foreach (var id in mine) before.Add(await BalancesAsync(env, id));

            var nameA = await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantSpecies WHERE Id = @S", ("@S", a.SpeciesId));
            var nameB = await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantSpecies WHERE Id = @S", ("@S", b.SpeciesId));

            // Search alone, and combined with each Stock value (an unknown value means All)
            Assert.Equal(Sorted(aOg, aNormal), Ids(await ReadyStockListAsync(env, nameA, null), mine));
            Assert.Equal(Sorted(aOg, aNormal), Ids(await ReadyStockListAsync(env, nameA, "bogus"), mine));
            Assert.Equal(new[] { aOg }, Ids(await ReadyStockListAsync(env, nameA, ReadyStockIndex.StockOvergrowth), mine));
            Assert.Equal(new[] { aNormal }, Ids(await ReadyStockListAsync(env, nameA, ReadyStockIndex.StockNormal), mine));
            Assert.Equal(new[] { bOg }, Ids(await ReadyStockListAsync(env, nameB, ReadyStockIndex.StockOvergrowth), mine));
            Assert.Empty(Ids(await ReadyStockListAsync(env, nameB, ReadyStockIndex.StockNormal), mine));
            // without Search: the Overgrowth filter alone
            var ogOnly = await ReadyStockListAsync(env, null, ReadyStockIndex.StockOvergrowth);
            Assert.Equal(Sorted(aOg, bOg), Ids(ogOnly, mine));
            Assert.All(ogOnly.Items, i => Assert.True(i.IsOvergrowth));
            Assert.All((await ReadyStockListAsync(env, null, ReadyStockIndex.StockNormal)).Items, i => Assert.False(i.IsOvergrowth));
            // + Include batches with no plants left
            Assert.Equal(Sorted(aOg, aOgEmpty), Ids(await ReadyStockListAsync(env, nameA, ReadyStockIndex.StockOvergrowth, includeEmpty: true), mine));
            Assert.Equal(new[] { aNormal }, Ids(await ReadyStockListAsync(env, nameA, ReadyStockIndex.StockNormal, includeEmpty: true), mine));

            // totals: the two kinds add up to the unfiltered list, whatever the Stock filter shows
            var all = await ReadyStockListAsync(env, nameA, null);
            var og = await ReadyStockListAsync(env, nameA, ReadyStockIndex.StockOvergrowth);
            Assert.Equal(all.TotalQuantity, og.OvergrowthTotals.Physical + og.NormalTotals.Physical);
            Assert.Equal(og.TotalQuantity, og.OvergrowthTotals.Physical);
            Assert.Equal(all.Items.Sum(i => i.AvailableQuantity), all.OvergrowthTotals.Available + all.NormalTotals.Available);

            // the exports follow the same filters
            var export = env.Page<ReadyStockIndex>(await env.LoginAsync(SowingSupervisor));
            export.Search = nameA;
            export.Stock = ReadyStockIndex.StockOvergrowth;
            var file = Assert.IsType<FileContentResult>(await export.OnGetExportExcelAsync());
            using (var book = new XLWorkbook(new MemoryStream(file.FileContents)))
            {
                var rows = book.Worksheets.First().RowsUsed().Skip(1).Select(r => (Code: r.Cell(1).GetString(), Og: r.Cell(2).GetString())).ToList();
                var aOgCode = await env.ScalarAsync<string>("SELECT SowingCode FROM dbo.ReadyStock rs JOIN dbo.SeedSowings s ON s.Id = rs.SeedSowingId WHERE rs.Id = @B", ("@B", aOg));
                Assert.Equal(aOgCode, Assert.Single(rows).Code);
                Assert.Equal(OvergrowthRules.ShortLabel, rows[0].Og);
            }

            // filtering changes no quantity (reserved / dispatched / in transit untouched)
            for (var i = 0; i < mine.Length; i++)
                Assert.Equal(before[i], await BalancesAsync(env, mine[i]));
        }

        [SkippableFact]
        public async Task OvergrowthFilter_IsCarriedThroughTheOvergrowthAndTracePages_BackToTheSameList()
        {
            var env = await OpenOvergrowthAsync();
            var v = await NewVarietyAsync(env);
            var batch = await ApprovedMainBatchAsync(env, v, plants: 240);
            var name = await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantSpecies WHERE Id = @S", ("@S", v.SpeciesId));

            // the list's own route values: only valid values travel (IndexModel re-validates on return anyway)
            Assert.Equal(new Dictionary<string, string?> { ["Stock"] = "og", ["Search"] = name, ["IncludeEmpty"] = "true" },
                ReadyStockIndex.ListRouteFor("og", name, true));
            Assert.Equal(new Dictionary<string, string?> { ["Stock"] = null, ["Search"] = null, ["IncludeEmpty"] = null },
                ReadyStockIndex.ListRouteFor("<script>", "  ", false));
            var list = await ReadyStockListAsync(env, name, ReadyStockIndex.StockNormal, includeEmpty: true);
            Assert.Equal(new Dictionary<string, string?> { ["Stock"] = "normal", ["Search"] = name, ["IncludeEmpty"] = "true" }, list.ListRoute);

            // marking from the Overgrowth page keeps the list's filters on the redirect (and the page's back link has them)
            var page = env.Page<OvergrowthPage>(await env.LoginAsync(SowingSupervisor));
            page.Stock = ReadyStockIndex.StockNormal;
            page.Search = name;
            page.IncludeEmpty = true;
            page.Reason = "carried filters";
            var redirect = Assert.IsType<RedirectToPageResult>(await page.OnPostMarkAsync(batch));
            Assert.Equal(batch, redirect.RouteValues!["id"]);
            Assert.Equal("normal", redirect.RouteValues["Stock"]);
            Assert.Equal(name, redirect.RouteValues["Search"]);
            Assert.Equal("true", redirect.RouteValues["IncludeEmpty"]);
            Assert.True((await env.Get<ReadyStockRepository>().GetByIdAsync(batch))!.IsOvergrowth);

            // after marking, the same filtered list moves the batch from Normal to Overgrowth
            Assert.Empty(Ids(await ReadyStockListAsync(env, name, ReadyStockIndex.StockNormal), batch));
            Assert.Equal(new[] { batch }, Ids(await ReadyStockListAsync(env, name, ReadyStockIndex.StockOvergrowth), batch));

            // the Trace page carries them as well
            var trace = env.Page<PlantStockManager.Pages.Production.ReadyStock.LineageModel>(await env.LoginAsync(SowingSupervisor));
            trace.Stock = ReadyStockIndex.StockOvergrowth;
            trace.Search = name;
            Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(await trace.OnGetAsync(batch));
            Assert.Equal(new Dictionary<string, string?> { ["Stock"] = "og", ["Search"] = name, ["IncludeEmpty"] = null }, trace.ListRoute);
        }
    }
}
