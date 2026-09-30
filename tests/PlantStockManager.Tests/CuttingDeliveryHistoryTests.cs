using System.Text.RegularExpressions;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // CORRECTION #7 -- Cutting Delivery History. Pure rules and wiring here; the real database behaviour (every column against its
    // underlying relationship, every status, every filter, Area security, historical rows) is CuttingDeliveryHistoryE2ETests,
    // which runs against a scratch copy of the database.
    public class CuttingDeliveryHistoryTests
    {
        // ---- normalisation ----------------------------------------------------------------------------------

        [Fact]
        public void AnEmptyFilter_NarrowsNothing()
        {
            var f = new CuttingDeliveryHistoryFilter();
            Assert.Empty(f.Normalize());
            Assert.False(f.HasAnyFilter);
        }

        [Fact]
        public void ZeroAndNegativeIds_MeanNoFilter()
        {
            var f = new CuttingDeliveryHistoryFilter { SourceAreaId = 0, SourcePolyhouseId = -1, MotherPlantId = 0, SupervisorId = -5, DestinationAreaId = 0, ReceivedById = -2 };
            f.Normalize();
            Assert.False(f.HasAnyFilter);
            Assert.Null(f.SourceAreaId); Assert.Null(f.SourcePolyhouseId); Assert.Null(f.MotherPlantId);
            Assert.Null(f.SupervisorId); Assert.Null(f.DestinationAreaId); Assert.Null(f.ReceivedById);
        }

        [Theory]
        [InlineData("From")] [InlineData("To")] [InlineData("SourceAreaId")] [InlineData("SourcePolyhouseId")] [InlineData("MotherPlantId")]
        [InlineData("SupervisorId")] [InlineData("Destination")] [InlineData("DestinationAreaId")] [InlineData("DeliveryStatus")]
        [InlineData("EnteredBy")] [InlineData("ReceivedById")]
        public void EachFilter_OnItsOwn_IsRecognisedAndKept(string which)
        {
            var f = new CuttingDeliveryHistoryFilter();
            switch (which)
            {
                case "From": f.From = new DateTime(2026, 9, 1); break;
                case "To": f.To = new DateTime(2026, 9, 30); break;
                case "SourceAreaId": f.SourceAreaId = 1; break;
                case "SourcePolyhouseId": f.SourcePolyhouseId = 1; break;
                case "MotherPlantId": f.MotherPlantId = 1; break;
                case "SupervisorId": f.SupervisorId = 1; break;
                case "Destination": f.Destination = CuttingDestination.MainOffice; break;
                case "DestinationAreaId": f.DestinationAreaId = 1; break;
                case "DeliveryStatus": f.DeliveryStatus = CuttingDeliveryHistoryFilter.StatusShortfall; break;
                case "EnteredBy": f.EnteredBy = "Kiran"; break;
                case "ReceivedById": f.ReceivedById = 11; break;
            }
            Assert.Empty(f.Normalize());
            Assert.True(f.HasAnyFilter);
        }

        [Fact]
        public void AFromAfterTo_IsPutInOrder_WithANotice_AndDatesAreWholeDays()
        {
            var f = new CuttingDeliveryHistoryFilter { From = new DateTime(2026, 9, 30, 15, 0, 0), To = new DateTime(2026, 9, 1, 9, 0, 0) };
            var notices = f.Normalize();
            Assert.Equal(new DateTime(2026, 9, 1), f.From);
            Assert.Equal(new DateTime(2026, 9, 30), f.To);
            Assert.Single(notices);
        }

        [Theory]
        [InlineData("Everything")] [InlineData("mainoffice")] [InlineData("1; DROP TABLE x")]
        public void UnknownDestinationAndStatus_AreIgnored_WithNotices(string value)
        {
            var f = new CuttingDeliveryHistoryFilter { Destination = value, DeliveryStatus = value };
            var notices = f.Normalize();
            Assert.Null(f.Destination);
            Assert.Null(f.DeliveryStatus);
            Assert.Equal(2, notices.Count);
        }

        [Fact]
        public void ABlankOrTooLongEnteredBy_IsIgnored()
        {
            var blank = new CuttingDeliveryHistoryFilter { EnteredBy = "   " };
            Assert.Empty(blank.Normalize());
            Assert.Null(blank.EnteredBy);
            var tooLong = new CuttingDeliveryHistoryFilter { EnteredBy = new string('x', CuttingDeliveryHistoryFilter.MaxNameLength + 1) };
            Assert.Single(tooLong.Normalize());
            Assert.Null(tooLong.EnteredBy);
            var fine = new CuttingDeliveryHistoryFilter { EnteredBy = "  Kiran " };
            fine.Normalize();
            Assert.Equal("Kiran", fine.EnteredBy);
        }

        [Fact]
        public void TheVocabulary_IsTheStatusesThatExist_AndTheThreeDestinations()
        {
            Assert.Equal(new[] { "PendingConfirmation", "Completed", "CompletedShortfall", "Rejected", "None" }, CuttingDeliveryHistoryFilter.StatusOptions.Select(o => o.Value).ToArray());
            Assert.Equal(new[] { "MainOffice", "PotProduction", "NotRecorded" }, CuttingDeliveryHistoryFilter.DestinationOptions.Select(o => o.Value).ToArray());
            Assert.Equal("Awaiting Main Office", CuttingDeliveryHistoryFilter.StatusLabel("PendingConfirmation"));
        }

        // ---- the row: transit loss comes from the existing figures, and only for a completed delivery ----------

        [Theory]
        [InlineData("Completed", 100, 90, 10)]
        [InlineData("Completed", 100, 100, 0)]
        public void TransitLoss_IsSentMinusReceived_ForACompletedDelivery(string status, double sent, double received, double loss)
        {
            var row = new CuttingDeliveryHistoryRow { TransferId = 1, TransferStatus = status, Quantity = (decimal)sent, ConfirmedQuantity = (decimal)received };
            Assert.Equal((decimal)loss, row.TransitLoss);
        }

        [Theory]
        [InlineData("PendingConfirmation")]
        [InlineData("Rejected")]
        public void NoTransitLoss_IsShownForAnUnconfirmedOrRejectedDelivery(string status)
            => Assert.Null(new CuttingDeliveryHistoryRow { TransferId = 1, TransferStatus = status, Quantity = 100, ConfirmedQuantity = null }.TransitLoss);

        [Fact]
        public void AnEntryWithoutADelivery_IsNotADelivery()
        {
            var row = new CuttingDeliveryHistoryRow { Kind = "E", TransferId = null, StatusKey = "None" };
            Assert.True(row.HasEntry);
            Assert.False(row.HasDelivery);
            Assert.False(new CuttingDeliveryHistoryRow { Kind = "D", TransferId = 5 }.HasEntry);
        }

        // ---- wiring (source scans) --------------------------------------------------------------------------

        private static string Repo(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.GetFiles(dir, "*.sln").Any()) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(parts).ToArray()));
        }

        [Fact]
        public void TheRepository_IsReadOnly()
        {
            var repo = Repo("Data", "CuttingDeliveryHistoryRepository.cs");
            foreach (var write in new[] { "INSERT INTO", "UPDATE dbo", "DELETE FROM", "ALTER TABLE", "CREATE TABLE", "DROP ", "TRUNCATE", "BeginTransaction", "ExecuteNonQuery" })
                Assert.DoesNotContain(write, repo);
        }

        [Fact]
        public void TheQuery_UsesOnlyExistingRelationships_ForEveryColumn()
        {
            var repo = Repo("Data", "CuttingDeliveryHistoryRepository.cs");
            Assert.Contains("LEFT JOIN dbo.InternalTransfers t ON t.SourceCuttingProductionId = cp.Id", repo);      // the entry -> delivery link
            Assert.Contains("mp.PolyhouseId AS SourcePolyhouseId, ph.Name AS SourcePolyhouseName", repo);              // source Polyhouse = the Mother Plant's
            Assert.Contains("cp.SupervisorId, sup.Name AS SupervisorName", repo);                                       // Cutting Supervisor = the entry's
            Assert.Contains("COALESCE(t.CreatedBy, cp.CreatedBy) AS EnteredBy", repo);                                  // entered by = the username stored
            Assert.Contains("t.ConfirmedBy AS ConfirmedById, cb.Name AS ConfirmedByName, t.ConfirmedDate", repo);       // received by
            Assert.Contains("CASE WHEN t.Status = N'Rejected' THEN t.ModifiedBy END AS RejectedBy", repo);              // rejected by (username only)
            Assert.Contains("t.DiscrepancyReason AS Reason", repo);                                                      // rejection / shortfall reason
            Assert.Contains("WHERE t.StockType = N'Cutting' AND t.SourceCuttingProductionId IS NULL", repo);           // older deliveries
            // nothing is invented for the older ones: their source details are NULL, and no destination Polyhouse exists anywhere
            var older = repo.Substring(repo.IndexOf("UNION ALL", StringComparison.Ordinal));
            Assert.Contains("CAST(NULL AS INT), CAST(NULL AS NVARCHAR(50)), CAST(NULL AS INT)", older);
            Assert.DoesNotContain("DestinationPolyhouse", repo);
            Assert.DoesNotContain("Area.PolyhouseId", repo);
            Assert.DoesNotContain("a.PolyhouseId", repo);
        }

        [Fact]
        public void FiltersAndAreaSecurity_AreInTheSql_AsParameters()
        {
            var repo = Repo("Data", "CuttingDeliveryHistoryRepository.cs");
            var search = repo.Substring(repo.IndexOf("public async Task<List<CuttingDeliveryHistoryRow>> SearchAsync", StringComparison.Ordinal));
            search = search.Substring(0, search.IndexOf("public sealed record IdOption", StringComparison.Ordinal));
            foreach (var condition in new[]
            {
                "h.RecordDate >= @From", "h.RecordDate <= @To", "h.SourceAreaId = @SourceAreaId", "h.SourcePolyhouseId = @SourcePolyhouseId",
                "h.MotherPlantId = @MotherPlantId", "h.SupervisorId = @SupervisorId", "h.DestinationType = @Destination",
                "h.DestinationAreaId = @DestinationAreaId", "h.StatusKey = @Status", "h.EnteredBy = @EnteredBy", "h.ConfirmedById = @ReceivedById"
            })
                Assert.Contains(condition, search);
            Assert.Contains("WHERE (@From IS NULL OR h.RecordDate >= @From)", search);                                 // the first of ten filters
            Assert.Equal(10, Regex.Matches(search, @"\n\s*AND \(@").Count);                                             // the other ten, each ANDed
            Assert.Contains("AND \" + Scope +", search);                                                               // and the Area scope, ANDed last
            Assert.Contains("OPTION (RECOMPILE)", search);
            Assert.DoesNotContain("+ filter.", search);
            Assert.DoesNotContain(".Where(", search);                                                                  // no filtering in C#
            // security: source, destination or the Main Office Area an unconfirmed delivery waits at; NULL never grants access
            Assert.Contains("h.SourceAreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ','))", repo);
            Assert.Contains("h.DestinationAreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ','))", repo);
            Assert.Contains("h.PendingAreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ','))", repo);
            Assert.Contains("allowedAreaIds != null && allowedAreaIds.Count == 0", repo);
            // the dropdowns are scoped exactly like the list
            Assert.Contains("\" AND \" + Scope + \" OPTION (RECOMPILE)\"", repo);
        }

        [Fact]
        public void ThePage_PassesTheUsersAreas_KeepsASecondGuard_AndClearFiltersIsAPlainLink()
        {
            var page = Repo("Pages", "Production", "CuttingStock", "MyTransactions.cshtml.cs");
            Assert.Contains("_areaAccessService.HasFullAreaAccess(User) ? null : _areaAccessService.GetAccessibleAreaIds(User)", page);
            Assert.Contains("_historyRepo.SearchAsync(filter, allowedAreas)", page);
            Assert.Contains("_historyRepo.GetFilterOptionsAsync(allowedAreas)", page);
            Assert.Contains("_areaAccessService.CanAccessAnyArea(User, r.SourceAreaId, r.DestinationAreaId, r.PendingAreaId)", page);
            Assert.Equal(11, Regex.Matches(page, @"\[BindProperty\(SupportsGet = true\)\]").Count);
            var view = Repo("Pages", "Production", "CuttingStock", "MyTransactions.cshtml");
            Assert.Contains("<form method=\"get\"", view);
            Assert.DoesNotContain("<form method=\"post\"", view);
            Assert.Contains("<a asp-page=\"/Production/CuttingStock/MyTransactions\" class=\"btn btn-outline-secondary btn-sm\">Clear filters</a>", view);
        }

        [Fact]
        public void TheView_ShowsEveryRequiredColumn_KeepsThePeopleSeparate_AndSaysNotRecorded()
        {
            var view = Repo("Pages", "Production", "CuttingStock", "MyTransactions.cshtml");
            foreach (var header in new[]
            {
                "Cutting Production", "Mother Plant", "Cutting Supervisor", "Source Area", "Source Polyhouse", "Variety", "Quantity", "Destination",
                "Destination Area", "Destination Polyhouse", "Delivery Status", "Entered By", "Received / Rejected By", "Delivery Date"
            })
                Assert.Contains($"<th>{header}</th>", view.Replace("<th class=\"text-end\">Quantity</th>", "<th>Quantity</th>"));
            // details: transfer id, production id, sent / received / transit loss, reasons, remarks, and the three people apart
            foreach (var label in new[] { "Delivery / Transfer:", "Cutting Production:", "Sent:", "Received (confirmed):", "Shortfall / transit loss:", "Cutting Supervisor:", "Delivery entered by:", "Rejected by", "Received / confirmed by", "Rejection reason", "Shortfall reason", "Entry remarks:", "Delivery remarks:" })
                Assert.Contains(label, view);
            Assert.Contains("Not recorded", view);
            Assert.Contains("A cutting delivery does not record a Polyhouse at the destination", view);   // never invented
            Assert.Contains("record@(Model.Rows.Count == 1 ? \"\" : \"s\") found", view);
            Assert.Contains("total <strong>@Model.TotalQuantity.ToString(\"N0\")</strong> cuttings", view);
            Assert.Contains("No records match the selected filters.", view);
            foreach (var name in new[] { "From", "To", "SourceAreaId", "SourcePolyhouseId", "MotherPlantId", "SupervisorId", "Destination", "DestinationAreaId", "DeliveryStatus", "EnteredBy", "ReceivedById" })
                Assert.Contains($"name=\"{name}\"", view);
            Assert.Equal(9, Regex.Matches(view, @"selected=""@\(Model\.\w+ == o\.(Id|Value)\)""").Count);
        }

        [Fact]
        public void TheDeliveryWorkflow_StockAndPermissions_AreNotTouched()
        {
            foreach (var file in new[]
            {
                "Data/InternalTransferRepository.cs", "Data/CuttingProductionRepository.cs", "Data/CuttingStockRepository.cs",
                "Pages/Production/CuttingStock/ConfirmReceipt.cshtml.cs", "Pages/Production/CuttingStock/PendingConfirmations.cshtml.cs",
                "Pages/Production/CuttingStock/GiveToMainOffice.cshtml.cs", "Pages/Production/Cutting/Create.cshtml.cs"
            })
                Assert.DoesNotContain("CuttingDeliveryHistory", Repo(file.Split('/')));
            Assert.Equal("MotherPlant.View|Kunjir.View|Kiran.View|MainOffice.View", FeatureAuthorizationConventions.GetRule("/Production/CuttingStock/MyTransactions").Read);
            Assert.Equal("MainOffice.Confirm", FeatureAuthorizationConventions.GetRule("/Production/CuttingStock/ConfirmReceipt").Read);
        }
    }
}
