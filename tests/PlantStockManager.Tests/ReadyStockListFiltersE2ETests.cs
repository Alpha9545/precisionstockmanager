using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using LineagePage = PlantStockManager.Pages.Production.ReadyStock.LineageModel;
using ReadyStockIndex = PlantStockManager.Pages.Production.ReadyStock.IndexModel;
using SowingsIndex = PlantStockManager.Pages.Production.SeedSowing.IndexModel;

namespace PlantStockManager.Tests
{
    // 2026-10-27: the Ready Stock list's option-list filters (Species / Variety / Area / Polyhouse / Origin / Status) and the
    // Sowings list's Category / Source filters, through the REAL page models on a SCRATCH COPY (skipped otherwise).
    // Combined with the existing Search / Stock (Overgrowth) / Include-empty filters and the exports; invalid values and an
    // Area outside the user's scope are dropped on the server; filtering never changes a quantity.
    public partial class WorkflowCorrections1019E2ETests
    {
        // A variety under a chosen Plant Type (NewVarietyAsync always uses the lowest one).
        private static async Task<Variety> NewVarietyOfTypeAsync(Env env, int plantTypeId)
        {
            var name = "ZZTEST Lf27 " + Guid.NewGuid().ToString("N")[..10];
            var speciesId = await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays)
VALUES (@T, @N, @D); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@T", plantTypeId), ("@N", name), ("@D", ReadyDays));
            var lot = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 100000, N'lf1027-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", speciesId), ("@A", MainArea), ("@B", "LOT-" + name[^10..]));
            return new Variety(speciesId, lot);
        }

        private static async Task<(int Low, int High, string HighName)> TwoPlantTypesAsync(Env env)
        {
            var low = await env.ScalarAsync<int>("SELECT MIN(Id) FROM dbo.PlantTypes");
            var high = await env.ScalarAsync<int>("SELECT MAX(Id) FROM dbo.PlantTypes");
            Skip.If(low == high, "This copy has only one Plant Type.");
            return (low, high, await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantTypes WHERE Id = @T", ("@T", high)));
        }

        private static async Task<ReadyStockIndex> FilteredReadyStockAsync(Env env, int user, Action<ReadyStockIndex> set)
        {
            var page = env.Page<ReadyStockIndex>(await env.LoginAsync(user));
            set(page);
            await page.OnGetAsync();
            return page;
        }

        [SkippableFact]
        public async Task ReadyStockOptionFilters_CombineWithSearchStockAndExports_QuantitiesUnchanged()
        {
            var env = await OpenOvergrowthAsync();
            var (lowType, highType, highName) = await TwoPlantTypesAsync(env);
            var a = await NewVarietyOfTypeAsync(env, lowType);
            var b = await NewVarietyOfTypeAsync(env, highType);
            var aOg = await ApprovedMainBatchAsync(env, a, plants: 240);
            var aNormal = await ApprovedMainBatchAsync(env, a, plants: 480);
            var bNormal = await ApprovedMainBatchAsync(env, b, plants: 240);
            Assert.True((await env.Get<ReadyStockRepository>().SetOvergrowthAsync(aOg, true, "list filter test", OgActor(SowingSupervisor))).Changed);
            var mine = new[] { aOg, aNormal, bNormal };
            var before = new List<Balances>();
            foreach (var id in mine) before.Add(await BalancesAsync(env, id));
            var nameA = await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantSpecies WHERE Id = @S", ("@S", a.SpeciesId));

            // the options come from the user's own list and include the new batches
            var plain = await FilteredReadyStockAsync(env, SowingSupervisor, _ => { });
            Assert.Contains(plain.Options.Varieties, v => v.Id == a.SpeciesId);
            Assert.Contains(plain.Options.Varieties, v => v.Id == b.SpeciesId);
            Assert.Contains(highName, plain.Options.Species);
            Assert.Contains(plain.Options.Areas, o => o.Id == MainArea);
            Assert.Contains(plain.Options.Polyhouses, o => o.Id == MainPolyhouse);
            Assert.All(plain.Items, i => Assert.Contains(i.AreaId, plain.Options.Areas.Select(o => o.Id)));

            // each filter alone, and combined
            Assert.Equal(Sorted(aOg, aNormal), Ids(await FilteredReadyStockAsync(env, SowingSupervisor, p => p.Variety = a.SpeciesId), mine));
            var bySpecies = await FilteredReadyStockAsync(env, SowingSupervisor, p => p.Species = highName);
            Assert.Equal(new[] { bNormal }, Ids(bySpecies, mine));
            Assert.All(bySpecies.Items, i => Assert.Equal(highName, i.PlantTypeName));
            Assert.Equal(new[] { aOg }, Ids(await FilteredReadyStockAsync(env, SowingSupervisor, p => { p.Variety = a.SpeciesId; p.Stock = ReadyStockIndex.StockOvergrowth; }), mine));
            Assert.Equal(new[] { aNormal }, Ids(await FilteredReadyStockAsync(env, SowingSupervisor, p => { p.Variety = a.SpeciesId; p.Stock = ReadyStockIndex.StockNormal; }), mine));
            Assert.Empty(Ids(await FilteredReadyStockAsync(env, SowingSupervisor, p => { p.Variety = a.SpeciesId; p.Species = highName; }), mine));
            Assert.Equal(Sorted(aOg, aNormal), Ids(await FilteredReadyStockAsync(env, SowingSupervisor,
                p => { p.Variety = a.SpeciesId; p.Area = MainArea; p.Polyhouse = MainPolyhouse; p.Origin = ReadyStock.OriginSowing; p.Status = "Available"; }), mine));
            Assert.Equal(new[] { aNormal }, Ids(await FilteredReadyStockAsync(env, SowingSupervisor, p => { p.Search = nameA; p.Stock = ReadyStockIndex.StockNormal; p.Area = MainArea; }), mine));
            var dropdownAndSearch = await FilteredReadyStockAsync(env, SowingSupervisor, p => { p.Search = nameA; p.Variety = b.SpeciesId; });
            Assert.Empty(Ids(dropdownAndSearch, mine));

            // the Overgrowth totals follow the dropdown filters (before Stock)
            var totals = await FilteredReadyStockAsync(env, SowingSupervisor, p => { p.Variety = a.SpeciesId; p.Stock = ReadyStockIndex.StockOvergrowth; });
            Assert.Equal(1, totals.OvergrowthTotals.Batches);
            Assert.Equal(1, totals.NormalTotals.Batches);
            Assert.Equal(240, totals.OvergrowthTotals.Physical);
            Assert.Equal(480, totals.NormalTotals.Physical);

            // invalid values are dropped on the server (the list is then unfiltered by them), and the page says so
            foreach (var bad in new Action<ReadyStockIndex>[]
            {
                p => p.Variety = -1, p => p.Variety = int.MaxValue, p => p.Area = int.MaxValue, p => p.Polyhouse = int.MaxValue,
                p => p.Species = highName.ToLowerInvariant() + "x", p => p.Origin = "sowing", p => p.Status = "<script>alert(1)</script>",
            })
            {
                var page = await FilteredReadyStockAsync(env, SowingSupervisor, bad);
                Assert.False(page.Filter.IsActive);
                Assert.NotNull(page.FilterNote);
                Assert.Equal(plain.Items.Select(i => i.Id), page.Items.Select(i => i.Id));
            }

            // the route carries the valid filters (to the exports, Trace / Overgrowth and back)
            var routed = await FilteredReadyStockAsync(env, SowingSupervisor, p => { p.Variety = a.SpeciesId; p.Stock = ReadyStockIndex.StockOvergrowth; p.Area = int.MaxValue; });
            Assert.Equal("og", routed.ListRoute["Stock"]);
            Assert.Equal(a.SpeciesId.ToString(), routed.ListRoute["Variety"]);
            Assert.False(routed.ListRoute.ContainsKey("Area"));

            // exports hold exactly the filtered rows
            var export = env.Page<ReadyStockIndex>(await env.LoginAsync(SowingSupervisor));
            export.Variety = a.SpeciesId;
            export.Stock = ReadyStockIndex.StockOvergrowth;
            var file = Assert.IsType<FileContentResult>(await export.OnGetExportExcelAsync());
            using (var book = new XLWorkbook(new MemoryStream(file.FileContents)))
            {
                var codes = book.Worksheets.First().RowsUsed().Skip(1).Select(r => r.Cell(1).GetString()).ToList();
                var aOgCode = await env.ScalarAsync<string>("SELECT SowingCode FROM dbo.ReadyStock rs JOIN dbo.SeedSowings s ON s.Id = rs.SeedSowingId WHERE rs.Id = @B", ("@B", aOg));
                Assert.Equal(aOgCode, Assert.Single(codes));
            }
            Assert.IsType<FileContentResult>(await export.OnGetExportPdfAsync());

            // the Trace page carries the option-list filters back (from the query string)
            var trace = env.Page<LineagePage>(await env.LoginAsync(SowingSupervisor));
            trace.PageContext.HttpContext.Request.QueryString = new QueryString($"?Stock=og&Variety={a.SpeciesId}&Area=abc&Origin=Sowing");
            trace.Stock = ReadyStockIndex.StockOvergrowth;
            Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(await trace.OnGetAsync(aOg));
            Assert.Equal(new Dictionary<string, string?>
                { ["Stock"] = "og", ["Search"] = null, ["IncludeEmpty"] = null, ["Variety"] = a.SpeciesId.ToString(), ["Origin"] = "Sowing" }, trace.ListRoute);

            // filtering changes no quantity
            for (var i = 0; i < mine.Length; i++)
                Assert.Equal(before[i], await BalancesAsync(env, mine[i]));
        }

        [SkippableFact]
        public async Task ReadyStockOptionFilters_AnAreaOutsideTheUsersScope_IsNeitherOfferedNorApplied()
        {
            var env = await OpenOvergrowthAsync();
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var mainBatch = await ApprovedMainBatchAsync(env, v, plants: 240);
            var outsider = await env.LoginAsync(p.OtherAreaSup);
            Skip.If(env.Get<SeedlingAreaScope>().CanAccessArea(outsider, MainArea), "The other-Area supervisor can see the Main Area in this copy.");

            var page = await FilteredReadyStockAsync(env, p.OtherAreaSup, x => { x.Area = MainArea; x.Variety = v.SpeciesId; x.Polyhouse = MainPolyhouse; });
            Assert.DoesNotContain(page.Options.Areas, o => o.Id == MainArea);
            Assert.DoesNotContain(page.Options.Varieties, o => o.Id == v.SpeciesId);
            Assert.DoesNotContain(page.Options.Polyhouses, o => o.Id == MainPolyhouse);
            Assert.False(page.Filter.IsActive);
            Assert.NotNull(page.FilterNote);
            Assert.DoesNotContain(page.Items, i => i.Id == mainBatch || i.AreaId == MainArea);
            Assert.All(page.Items, i => Assert.True(env.Get<SeedlingAreaScope>().CanAccessArea(outsider, i.AreaId)));

            // the export of the same forged query holds no Main Area row either
            var export = env.Page<ReadyStockIndex>(outsider);
            export.Area = MainArea;
            export.IncludeEmpty = true;
            var file = Assert.IsType<FileContentResult>(await export.OnGetExportExcelAsync());
            var mainName = await env.ScalarAsync<string>("SELECT Name FROM dbo.Area WHERE Id = @A", ("@A", MainArea));
            using var book = new XLWorkbook(new MemoryStream(file.FileContents));
            Assert.DoesNotContain(book.Worksheets.First().RowsUsed().Skip(1), r => r.Cell(6).GetString() == mainName);
        }

        [SkippableFact]
        public async Task SowingsCategoryAndSourceFilters_Combine_AreValidated_AndKeepTheAreaScope()
        {
            var env = await OpenAsync();
            var (lowType, highType, _) = await TwoPlantTypesAsync(env);
            var a = await NewVarietyOfTypeAsync(env, lowType);
            var b = await NewVarietyOfTypeAsync(env, highType);
            var aBatch = await ApprovedMainBatchAsync(env, a, plants: 240);
            var bBatch = await ApprovedMainBatchAsync(env, b, plants: 240);
            var aSowing = await env.ScalarAsync<int>("SELECT SeedSowingId FROM dbo.ReadyStock WHERE Id = @B", ("@B", aBatch));
            var bSowing = await env.ScalarAsync<int>("SELECT SeedSowingId FROM dbo.ReadyStock WHERE Id = @B", ("@B", bBatch));
            var mine = new[] { aSowing, bSowing };

            async Task<SowingsIndex> ListAsync(int user, int? category, string? source)
            {
                var page = env.Page<SowingsIndex>(await env.LoginAsync(user));
                page.Category = category;
                page.Source = source;
                await page.OnGetAsync();
                return page;
            }
            int[] Shown(SowingsIndex page) => page.Sowings.Select(s => s.Id).Where(mine.Contains).OrderBy(x => x).ToArray();

            var all = await ListAsync(SowingSupervisor, null, null);
            Assert.Equal(Sorted(aSowing, bSowing), Shown(all));
            Assert.Equal(await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantTypes"), all.Categories.Count);

            var low = await ListAsync(SowingSupervisor, lowType, null);
            Assert.Equal(new[] { aSowing }, Shown(low));
            var lowName = await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantTypes WHERE Id = @T", ("@T", lowType));
            Assert.All(low.Sowings, s => Assert.Equal(lowName, s.PlantTypeName));
            Assert.Equal(new[] { bSowing }, Shown(await ListAsync(SowingSupervisor, highType, SeedSowing.SourceSeed)));
            Assert.Empty(Shown(await ListAsync(SowingSupervisor, highType, SeedSowing.SourceCutting)));
            var cutting = await ListAsync(SowingSupervisor, null, SeedSowing.SourceCutting);
            Assert.All(cutting.Sowings, s => Assert.Equal(SeedSowing.SourceCutting, s.SourceType));

            // invalid values: dropped, noted, and the list is unfiltered by them
            foreach (var (category, source) in new (int?, string?)[] { (int.MaxValue, null), (0, null), (null, "seed"), (null, "Seed' OR '1'='1") })
            {
                var page = await ListAsync(SowingSupervisor, category, source);
                Assert.False(page.Filter.IsActive);
                Assert.NotNull(page.FilterNote);
                Assert.Equal(all.Sowings.Select(s => s.Id), page.Sowings.Select(s => s.Id));
            }

            // the Area scope still applies under a category filter
            var p = await PeopleAsync(env);
            var outsider = await env.LoginAsync(p.OtherAreaSup);
            Skip.If(env.Get<SeedlingAreaScope>().CanAccessArea(outsider, MainArea), "The other-Area supervisor can see the Main Area in this copy.");
            var scoped = await ListAsync(p.OtherAreaSup, lowType, SeedSowing.SourceSeed);
            Assert.Empty(Shown(scoped));
            Assert.All(scoped.Sowings, s => Assert.True(env.Get<SeedlingAreaScope>().CanAccessArea(outsider, s.AreaId)));
        }
    }
}
