using System.Text.RegularExpressions;
using PlantStockManager.Authorization;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // CORRECTION #5 -- filters of the Cutting Production register. Pure rules and wiring here; the real database behaviour
    // (every filter alone, together, no match, Area isolation, historical rows, destinations, delivery status) is
    // CuttingProductionFilterE2ETests, which runs against a scratch copy of the database.
    public class CuttingProductionFilterTests
    {
        // ---- normalisation ---------------------------------------------------------------------------------

        [Fact]
        public void AnEmptyFilter_NarrowsNothing_AndNeedsNoNotice()
        {
            var f = new CuttingProductionFilter();
            Assert.Empty(f.Normalize());
            Assert.False(f.HasNarrowingFilter);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-4)]
        public void ZeroAndNegativeIds_MeanNoFilter(int id)
        {
            var f = new CuttingProductionFilter { AreaId = id, MotherPlantId = id, SpeciesId = id, PolyhouseId = id, SupervisorId = id, DestinationAreaId = id };
            f.Normalize();
            Assert.Null(f.AreaId); Assert.Null(f.MotherPlantId); Assert.Null(f.SpeciesId);
            Assert.Null(f.PolyhouseId); Assert.Null(f.SupervisorId); Assert.Null(f.DestinationAreaId);
            Assert.False(f.HasNarrowingFilter);
        }

        [Theory]
        [InlineData("AreaId")]
        [InlineData("MotherPlantId")]
        [InlineData("SpeciesId")]
        [InlineData("PolyhouseId")]
        [InlineData("SupervisorId")]
        [InlineData("DestinationAreaId")]
        [InlineData("Destination")]
        [InlineData("DeliveryStatus")]
        public void EachFilter_AloneCountsAsNarrowing(string which)
        {
            var f = new CuttingProductionFilter();
            switch (which)
            {
                case "AreaId": f.AreaId = 1; break;
                case "MotherPlantId": f.MotherPlantId = 1; break;
                case "SpeciesId": f.SpeciesId = 1; break;
                case "PolyhouseId": f.PolyhouseId = 1; break;
                case "SupervisorId": f.SupervisorId = 1; break;
                case "DestinationAreaId": f.DestinationAreaId = 1; break;
                case "Destination": f.Destination = CuttingDestination.MainOffice; break;
                case "DeliveryStatus": f.DeliveryStatus = "Completed"; break;
            }
            Assert.Empty(f.Normalize());
            Assert.True(f.HasNarrowingFilter);
        }

        [Fact]
        public void TheDateRangeAlone_IsNotANarrowingFilter_ItIsTheDefaultView()
        {
            var f = new CuttingProductionFilter { From = new DateTime(2026, 9, 1, 13, 30, 0), To = new DateTime(2026, 9, 30) };
            f.Normalize();
            Assert.Equal(new DateTime(2026, 9, 1), f.From);          // whole days
            Assert.False(f.HasNarrowingFilter);
        }

        [Fact]
        public void AFromAfterTo_IsPutInOrder_WithANotice()
        {
            var f = new CuttingProductionFilter { From = new DateTime(2026, 9, 30), To = new DateTime(2026, 9, 1) };
            var notices = f.Normalize();
            Assert.Equal(new DateTime(2026, 9, 1), f.From);
            Assert.Equal(new DateTime(2026, 9, 30), f.To);
            Assert.Single(notices);
        }

        [Theory]
        [InlineData("Everything")]
        [InlineData("mainoffice")]         // values are exact, like the stored ones
        [InlineData("1; DROP TABLE x")]
        public void UnknownDestinationAndDeliveryValues_AreIgnored_NeverPassedOn(string value)
        {
            var f = new CuttingProductionFilter { Destination = value, DeliveryStatus = value };
            var notices = f.Normalize();
            Assert.Null(f.Destination);
            Assert.Null(f.DeliveryStatus);
            Assert.Equal(2, notices.Count);
        }

        [Fact]
        public void BlankValues_AreNoFilter()
        {
            var f = new CuttingProductionFilter { Destination = "  ", DeliveryStatus = "" };
            Assert.Empty(f.Normalize());
            Assert.Null(f.Destination);
            Assert.Null(f.DeliveryStatus);
        }

        // ---- vocabulary: exactly the values that are stored -------------------------------------------------

        [Fact]
        public void DestinationOptions_AreTheStoredDestinationsPlusNotRecorded()
        {
            Assert.Equal(new[] { CuttingDestination.MainOffice, CuttingDestination.PotProduction, CuttingProductionFilter.DestinationNotRecorded },
                CuttingProductionFilter.DestinationOptions.Select(o => o.Value).ToArray());
            Assert.Equal("Main Office", CuttingProductionFilter.DestinationLabel(CuttingDestination.MainOffice));
            Assert.Equal("Use for Pot Production", CuttingProductionFilter.DestinationLabel(CuttingDestination.PotProduction));
        }

        [Fact]
        public void DeliveryStatusOptions_AreTheStatusesACuttingDeliveryCanHave_PlusNoDelivery()
        {
            var values = CuttingProductionFilter.DeliveryStatusOptions.Select(o => o.Value).ToArray();
            Assert.Equal(new[] { "PendingConfirmation", "Completed", "Rejected", CuttingProductionFilter.DeliveryNone }, values);
            Assert.Equal("Awaiting Main Office", CuttingProductionFilter.DeliveryStatusLabel("PendingConfirmation"));   // same wording as the register's badge
        }

        // ---- wiring (source scans) --------------------------------------------------------------------------

        private static string Repo(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.GetFiles(dir, "*.sln").Any()) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(parts).ToArray()));
        }

        private static string Search()
        {
            var repo = Repo("Data", "CuttingProductionRepository.cs");
            var start = repo.IndexOf("public async Task<List<CuttingProduction>> SearchAsync", StringComparison.Ordinal);
            return repo.Substring(start, repo.IndexOf("public sealed record FilterOption", start, StringComparison.Ordinal) - start);
        }

        [Fact]
        public void TheDatabaseAppliesEveryFilter_TogetherWithAnd_AsParameters()
        {
            var sql = Search();
            foreach (var condition in new[]
            {
                "cp.CuttingDate >= @From", "cp.CuttingDate <= @To", "cp.AreaId = @AreaId", "cp.MotherPlantId = @MotherPlantId",
                "cp.SpeciesId = @SpeciesId", "mp.PolyhouseId = @PolyhouseId", "cp.SupervisorId = @SupervisorId",
                "cp.DestinationType = @Destination", "cp.DestinationAreaId = @DestinationAreaId", "tr.Status = @Delivery"
            })
                Assert.Contains(condition, sql);
            Assert.Contains("@Destination = N'NotRecorded' AND cp.DestinationType IS NULL", sql);   // older entries
            Assert.Contains("@Delivery = N'None' AND tr.Id IS NULL", sql);                            // no delivery
            Assert.Equal(11, Regex.Matches(sql, @"\n\s+AND \(|WHERE \(").Count);                     // ten filters + the Area security, all ANDed
            Assert.Contains("OPTION (RECOMPILE)", sql);
            // user values are parameters, never concatenated into the SQL text
            Assert.DoesNotContain("+ filter.", sql);
            Assert.DoesNotContain("$@\"", sql);
            Assert.Contains("cmd.Parameters.AddWithValue(\"@Delivery\", (object?)filter.DeliveryStatus", sql);
        }

        [Fact]
        public void AreaSecurity_IsPartOfTheQuery_AndAnEmptyAreaListReturnsNothing()
        {
            var sql = Search();
            Assert.Contains("@Allowed IS NULL OR cp.AreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ','))", sql);
            Assert.Contains("allowedAreaIds != null && allowedAreaIds.Count == 0", sql);
            Assert.Contains("return list;", sql.Substring(0, sql.IndexOf("using var conn", StringComparison.Ordinal)));   // before any query is made
            var repo = Repo("Data", "CuttingProductionRepository.cs");
            var options = repo.Substring(repo.IndexOf("GetFilterOptionsAsync", StringComparison.Ordinal));
            options = options.Substring(0, options.IndexOf("public async Task<(bool Success, string? Message, int Id)> InsertAsync", StringComparison.Ordinal));
            Assert.Equal(6, Regex.Matches(options, @"WHERE .*\{scope\}").Count);                      // every dropdown is scoped to the user's Areas
            Assert.Contains("(@Allowed IS NULL OR cp.AreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ',')))", options);
        }

        [Fact]
        public void ThePage_PassesTheUsersAreasToTheDatabase_KeepsTheOldGuard_AndClearFiltersIsAPlainLink()
        {
            var page = Repo("Pages", "Production", "Cutting", "Index.cshtml.cs");
            Assert.Contains("_areaAccess.HasFullAreaAccess(User) ? null : _areaAccess.GetAccessibleAreaIds(User)", page);
            Assert.Contains("_repo.SearchAsync(filter, allowedAreas)", page);
            Assert.Contains("_repo.GetFilterOptionsAsync(allowedAreas)", page);
            Assert.Contains("_areaAccess.FilterByArea(User,", page);
            Assert.Equal(8, Regex.Matches(page, @"\[BindProperty\(SupportsGet = true\)\]").Count);   // GET filters: the view can be refreshed / bookmarked
            var view = Repo("Pages", "Production", "Cutting", "Index.cshtml");
            Assert.Contains("<form method=\"get\"", view);
            Assert.DoesNotContain("<form method=\"post\"", view);
            Assert.Contains("<a asp-page=\"/Production/Cutting/Index\" class=\"btn btn-outline-secondary btn-sm\">Clear filters</a>", view);
        }

        [Fact]
        public void TheChosenValuesStayVisibleAfterSearching()
        {
            var view = Repo("Pages", "Production", "Cutting", "Index.cshtml");
            foreach (var name in new[] { "areaId", "polyhouseId", "motherPlantId", "speciesId", "supervisorId", "destination", "destinationAreaId", "deliveryStatus" })
                Assert.Contains($"name=\"{name}\"", view);
            Assert.Equal(8, Regex.Matches(view, @"selected=""@\(Model\.\w+ == o\.(Id|Value)\)""").Count);
            Assert.Contains("value=\"@Model.From.ToString(\"yyyy-MM-dd\")\"", view);
            Assert.Contains("value=\"@Model.To.ToString(\"yyyy-MM-dd\")\"", view);
        }

        [Fact]
        public void EntryDestinationsStockAndDeliveries_AreNotTouched()
        {
            var repo = Repo("Data", "CuttingProductionRepository.cs");
            var insert = repo.Substring(repo.IndexOf("public async Task<(bool Success, string? Message, int Id)> InsertAsync", StringComparison.Ordinal));
            Assert.Contains("_transferRepo.CreateCuttingDeliveryAsync(conn, tx, delivery, userId)", insert);
            Assert.Contains("RecordTransactionAsync", insert);
            Assert.Contains("\"Harvest\"", insert);
            Assert.DoesNotContain("CuttingProductionFilter", insert);
            var create = Repo("Pages", "Production", "Cutting", "Create.cshtml.cs");
            Assert.DoesNotContain("CuttingProductionFilter", create);
            // the original register query is still there for other callers
            Assert.Contains("public async Task<List<CuttingProduction>> GetAllAsync(DateTime? from = null", repo);
        }

        [Fact]
        public void PermissionsAreUnchanged()
        {
            Assert.Equal("MotherPlant.View|MainOffice.View|PotProduction.View", FeatureAuthorizationConventions.GetRule("/Production/Cutting/Index").Read);
            Assert.Equal("MotherPlant.Enter", FeatureAuthorizationConventions.GetRule("/Production/Cutting/Create").Read);
        }
    }
}
