using PlantStockManager.Authorization;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // CORRECTION #8 -- Fertilizer Transaction: Received By and filters. Pure rules and wiring here; the real database behaviour
    // (receiver saved/shown, every filter, "Not recorded", historical rows, stock balances, duplicate / concurrent submission)
    // is FertilizerTransactionE2ETests, which runs against a scratch copy of the database.
    public class FertilizerTransactionTests
    {
        // ---- filter normalisation ----------------------------------------------------------------------------

        [Fact]
        public void AnEmptyFilter_NarrowsNothing()
        {
            var f = new FertilizerTransactionFilter();
            Assert.Empty(f.Normalize());
            Assert.False(f.HasAnyFilter);
            Assert.Equal(FertilizerTransactionFilter.ReceiverMode.Any, f.ReceiverKind);
        }

        [Fact]
        public void BlankAndNonPositiveValues_MeanNoFilter()
        {
            var f = new FertilizerTransactionFilter { FertilizerId = 0, SourceId = -3, Receiver = "  ", EnteredBy = "" };
            Assert.Empty(f.Normalize());
            Assert.False(f.HasAnyFilter);
            Assert.Null(f.FertilizerId); Assert.Null(f.SourceId); Assert.Null(f.Receiver); Assert.Null(f.EnteredBy);
        }

        [Theory]
        [InlineData("From")] [InlineData("To")] [InlineData("Fertilizer")] [InlineData("Source")] [InlineData("Receiver")] [InlineData("EnteredBy")]
        public void EachFilter_OnItsOwn_IsRecognisedAndKept(string which)
        {
            var f = new FertilizerTransactionFilter();
            switch (which)
            {
                case "From": f.From = new DateTime(2026, 9, 1); break;
                case "To": f.To = new DateTime(2026, 9, 30); break;
                case "Fertilizer": f.FertilizerId = 4; break;
                case "Source": f.SourceId = 2; break;
                case "Receiver": f.Receiver = "u:7"; break;
                case "EnteredBy": f.EnteredBy = "11"; break;
            }
            Assert.Empty(f.Normalize());
            Assert.True(f.HasAnyFilter);
        }

        [Fact]
        public void TheReceiverChoice_IsParsedIntoItsThreeKinds()
        {
            var user = new FertilizerTransactionFilter { Receiver = " u:12 " };
            user.Normalize();
            Assert.Equal((FertilizerTransactionFilter.ReceiverMode.User, 12, "u:12"), (user.ReceiverKind, user.ReceiverUserId, user.Receiver));

            var none = new FertilizerTransactionFilter { Receiver = "none" };
            none.Normalize();
            Assert.Equal(FertilizerTransactionFilter.ReceiverMode.NotRecorded, none.ReceiverKind);

            var text = new FertilizerTransactionFilter { Receiver = "t:  Patel Sir " };
            text.Normalize();
            Assert.Equal((FertilizerTransactionFilter.ReceiverMode.Text, "Patel Sir", "t:Patel Sir"), (text.ReceiverKind, text.ReceiverText, text.Receiver));
        }

        [Theory]
        [InlineData("garbage")] [InlineData("u:")] [InlineData("u:abc")] [InlineData("u:0")] [InlineData("u:-4")] [InlineData("t:")] [InlineData("t:   ")]
        [InlineData("5")] [InlineData("'; DROP TABLE FertilizerUsage;--")]
        public void ARubbishReceiver_IsIgnoredWithANotice_NotTurnedIntoAFilter(string value)
        {
            var f = new FertilizerTransactionFilter { Receiver = value };
            var notices = f.Normalize();
            Assert.Single(notices);
            Assert.Null(f.Receiver);
            Assert.Equal(FertilizerTransactionFilter.ReceiverMode.Any, f.ReceiverKind);
            Assert.False(f.HasAnyFilter);
        }

        [Theory]
        [InlineData("abc")] [InlineData("0")] [InlineData("-1")] [InlineData("u:5")] [InlineData("1; DROP TABLE x")]
        public void ARubbishEnteredBy_IsIgnoredWithANotice(string value)
        {
            var f = new FertilizerTransactionFilter { EnteredBy = value };
            Assert.Single(f.Normalize());
            Assert.Null(f.EnteredBy);
            Assert.False(f.HasAnyFilter);
        }

        [Fact]
        public void ANotRecordedEnteredBy_IsKept()
        {
            var f = new FertilizerTransactionFilter { EnteredBy = "none" };
            Assert.Empty(f.Normalize());
            Assert.True(f.EnteredByNotRecorded);
            Assert.Null(f.EnteredByUserId);
        }

        [Fact]
        public void DatesAreWholeDays_AndAFromAfterToIsSwapped()
        {
            var f = new FertilizerTransactionFilter { From = new DateTime(2026, 9, 20, 14, 30, 0), To = new DateTime(2026, 9, 1, 8, 0, 0) };
            var notices = f.Normalize();
            Assert.Single(notices);
            Assert.Equal(new DateTime(2026, 9, 1), f.From);
            Assert.Equal(new DateTime(2026, 9, 20), f.To);
        }

        [Fact]
        public void ValuesThatAreNotInTheDropdowns_AreDroppedWithANotice()
        {
            var f = new FertilizerTransactionFilter { FertilizerId = 99, SourceId = 98, Receiver = "u:97", EnteredBy = "96" };
            f.Normalize();
            var notices = f.RestrictTo(new[] { 1, 2 }, new[] { 1 }, new[] { "u:5", "t:Patel Sir" }, new[] { "11" });
            Assert.Equal(4, notices.Count);
            Assert.False(f.HasAnyFilter);
            Assert.Equal(FertilizerTransactionFilter.ReceiverMode.Any, f.ReceiverKind);
        }

        [Fact]
        public void ValuesThatAreInTheDropdowns_AreKept_AndNotRecordedIsAlwaysAllowed()
        {
            var f = new FertilizerTransactionFilter { FertilizerId = 1, SourceId = 1, Receiver = "t:patel sir", EnteredBy = "none" };
            f.Normalize();
            Assert.Empty(f.RestrictTo(new[] { 1 }, new[] { 1 }, new[] { "t:Patel Sir" }, Array.Empty<string>()));
            Assert.True(f.HasAnyFilter);

            var g = new FertilizerTransactionFilter { Receiver = "none" };
            g.Normalize();
            Assert.Empty(g.RestrictTo(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<string>(), Array.Empty<string>()));
            Assert.Equal(FertilizerTransactionFilter.ReceiverMode.NotRecorded, g.ReceiverKind);
        }

        // ---- how a receiver / entered-by is shown ---------------------------------------------------------------

        [Fact]
        public void TheReceiver_IsTheSelectedUser_ElseTheOlderTypedName_ElseNotRecorded()
        {
            Assert.Equal("Akshay Shinde", FertilizerIssueRules.ReceiverLabel("Akshay Shinde", true, "somebody typed"));
            Assert.Equal("Somnath (inactive)", FertilizerIssueRules.ReceiverLabel("Somnath", false, null));              // an inactive receiver is shown, and marked
            Assert.Equal("Patel Sir", FertilizerIssueRules.ReceiverLabel(null, null, "  Patel Sir "));                    // older typed name: kept, not matched to anyone
            Assert.Equal("Not recorded", FertilizerIssueRules.ReceiverLabel(null, null, null));
            Assert.Equal("Not recorded", FertilizerIssueRules.ReceiverLabel(null, null, "   "));                          // nothing is guessed
            Assert.Equal("Not recorded", FertilizerIssueRules.ReceiverLabel("  ", true, ""));
        }

        [Fact]
        public void TheEnteredBy_IsTheUser_ElseNotRecorded()
        {
            Assert.Equal("Mahadev Pawar", FertilizerIssueRules.EnteredByLabel("Mahadev Pawar", true));
            Assert.Equal("Mahadev Pawar (inactive)", FertilizerIssueRules.EnteredByLabel("Mahadev Pawar", false));
            Assert.Equal("Not recorded", FertilizerIssueRules.EnteredByLabel(null, null));
        }

        // ---- the checks an issue must pass before touching the database ------------------------------------------

        [Theory]
        [InlineData(0, 5)] [InlineData(-1, 5)]
        public void AnIssue_NeedsAPositiveQuantity(double quantity, int receiver)
            => Assert.False(FertilizerIssueRules.ValidateIssue((decimal)quantity, DateTime.Today, receiver).Ok);

        [Fact]
        public void AnIssue_NeedsADate_AndAReceiver()
        {
            Assert.False(FertilizerIssueRules.ValidateIssue(1, default, 5).Ok);
            Assert.False(FertilizerIssueRules.ValidateIssue(1, DateTime.Today, null).Ok);
            Assert.False(FertilizerIssueRules.ValidateIssue(1, DateTime.Today, 0).Ok);
            Assert.True(FertilizerIssueRules.ValidateIssue(0.5m, DateTime.Today, 5).Ok);
        }

        // ---- wiring (source scans) -------------------------------------------------------------------------------

        private static string Repo(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.GetFiles(dir, "*.sln").Any()) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(parts).ToArray()));
        }

        [Fact]
        public void EveryFilter_IsAParameter_AppliedTogether_ByTheDatabase()
        {
            var repo = Repo("Data", "FertilizerTransactionRepository.cs");
            foreach (var predicate in new[]
            {
                "fu.IssueDate >= @From", "fu.IssueDate <= @To", "fs.FertilizerId = @FertilizerId", "fs.SourceId = @SourceId",
                "fu.ReceivedById = @RecvUserId", "fu.ReceivedById IS NULL AND LTRIM(RTRIM(ISNULL(fu.ReceivedBy, N''))) = N''",   // Not recorded
                "LTRIM(RTRIM(fu.ReceivedBy)) = @RecvText", "fu.EnteredById = @EnteredUserId", "fu.EnteredById IS NULL"
            })
                Assert.Contains(predicate, repo);
            Assert.Contains("OPTION (RECOMPILE)", repo);
            Assert.Contains("ORDER BY fu.IssueDate DESC, fu.UsageId DESC", repo);
            // a value is never concatenated into the SQL
            Assert.DoesNotContain("filter.Receiver +", repo);
            Assert.DoesNotContain("$\"WHERE", repo);
        }

        [Fact]
        public void TheHistory_IsReadOnly()
        {
            var repo = Repo("Data", "FertilizerTransactionRepository.cs");
            var search = repo.Substring(repo.IndexOf("SearchAsync", StringComparison.Ordinal), repo.IndexOf("GetFilterOptionsAsync", StringComparison.Ordinal) - repo.IndexOf("SearchAsync", StringComparison.Ordinal));
            foreach (var word in new[] { "INSERT ", "UPDATE ", "DELETE ", "MERGE " })
                Assert.DoesNotContain(word, search);
            var page = Repo("Pages", "Fertilizer", "Transactions.cshtml.cs");
            Assert.DoesNotContain("OnPost", page);
        }

        [Fact]
        public void TheIssue_LocksTheBatch_ChecksTheReceiver_AndKeepsTheStockRule()
        {
            var repo = Repo("Data", "FertilizerTransactionRepository.cs");
            Assert.Contains("FROM dbo.FertilizerStock WITH (UPDLOCK, HOLDLOCK) WHERE StockId = @Id", repo);            // balance read under a lock
            Assert.Contains("SELECT Name FROM dbo.IMSUsers WHERE Id = @Id AND IsActive = 1", repo);                    // the receiver must be an active user
            Assert.Contains("CreatedAt >= DATEADD(SECOND, -@Window, SYSDATETIME())", repo);                            // the double-submit guard
            Assert.Contains("SET LatestAvailableQuantity = LatestAvailableQuantity - @Q,", repo);                      // the stock rule as it was
            Assert.Contains("IsUtilized = CASE WHEN LatestAvailableQuantity - @Q = 0 THEN 1 ELSE 0 END", repo);
            Assert.Contains("quantity > available", repo);
            // the lock is taken before the quantity is compared, and the insert and the balance change commit together
            Assert.True(repo.IndexOf("UPDLOCK, HOLDLOCK", StringComparison.Ordinal) < repo.IndexOf("quantity > available", StringComparison.Ordinal));
            Assert.True(repo.IndexOf("INSERT INTO dbo.FertilizerUsage", StringComparison.Ordinal) < repo.IndexOf("UPDATE dbo.FertilizerStock", StringComparison.Ordinal));
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(repo, "tx\\.Commit\\(\\)").Count);
        }

        [Fact]
        public void TheEntryPage_UsesASelectedReceiver_AndTheLoggedInUserAsEnteredBy()
        {
            var page = Repo("Pages", "Fertilizer", "Usage.cshtml.cs");
            Assert.Contains("_repo.IssueAsync(Usage.StockId, Usage.UsedQuantity, Usage.IssueDate, Usage.ReceivedById,", page);
            Assert.Contains("User.GetUserId()", page);                                          // the entering user is never a posted value
            Assert.DoesNotContain("EnteredById", page.Replace("// the entering user", ""));      // no bound EnteredById property to tamper with

            var view = Repo("Pages", "Fertilizer", "Usage.cshtml");
            Assert.Contains("<select asp-for=\"Usage.ReceivedById\"", view);
            Assert.DoesNotContain("asp-for=\"Usage.ReceivedBy\"", view);                        // no free-text receiver any more
            Assert.Contains("asp-page-handler=\"Save\"", view);                                 // the workflow / handler is unchanged
            Assert.Contains("validateQuantity()", view);
        }

        [Fact]
        public void PagePermissions_AreUnchanged()
        {
            Assert.Equal("Fertilizer.Enter", FeatureAuthorizationConventions.GetRule("/Fertilizer/Usage").Read);
            Assert.Equal("Fertilizer.View", FeatureAuthorizationConventions.GetRule("/Fertilizer/View").Read);
            Assert.Equal("Fertilizer.View", FeatureAuthorizationConventions.GetRule("/Fertilizer/Transactions").Read);   // the new page asks for the permission the report already asks for
            Assert.Equal("Fertilizer.View", FeatureAuthorizationConventions.GetRule("/Fertilizer/Stock").Read);
            Assert.Equal("Fertilizer.Enter", FeatureAuthorizationConventions.GetRule("/Fertilizer/Stock").Write);
        }

        [Fact]
        public void TheHistoryPage_HasTheFilters_TheCountAndTheMessages()
        {
            var view = Repo("Pages", "Fertilizer", "Transactions.cshtml");
            foreach (var name in new[] { "name=\"from\"", "name=\"to\"", "name=\"fertilizerId\"", "name=\"sourceId\"", "name=\"receiver\"", "name=\"enteredBy\"" })
                Assert.Contains(name, view);
            Assert.Contains("Clear filters", view);
            Assert.Contains("asp-page=\"/Fertilizer/Transactions\"", view);                    // Clear = plain link, no query string
            Assert.Contains("record(s)", view);
            Assert.Contains("No fertilizer transactions match the selected filters.", view);
            foreach (var column in new[] { "Date", "Fertilizer", "Type", "Quantity", "Unit", "Source", "Received by", "Entered by", "Remark" })
                Assert.Contains($"<th>{column}</th>", view.Replace("<th class=\"text-end\">", "<th>"));
            // no Area / Destination filter is invented: that data is not stored
            Assert.DoesNotContain("name=\"areaId\"", view);
            Assert.DoesNotContain("name=\"destination\"", view);
        }

        [Fact]
        public void TheMigration_IsGuarded_Nullable_AndKeepsItsErrorPathRollback()
        {
            var sql = Repo("Database", "Migrations", "2026-09-28_FertilizerUsageReceiverAndEnteredBy.sql");
            Assert.Contains("IF DB_NAME() <> N'PlantsIMS2_Test'", sql);
            Assert.Contains("ADD ReceivedById INT NULL", sql);
            Assert.Contains("ADD EnteredById INT NULL", sql);
            Assert.Contains("REFERENCES dbo.IMSUsers (Id)", sql);
            Assert.Contains("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", sql);      // the error path still rolls back
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(sql, "COMMIT TRANSACTION;").Count);   // approved 2026-09-28: the one successful-path COMMIT
            // additive only: no update / delete / drop of anything that exists
            foreach (var word in new[] { "UPDATE dbo.", "DELETE ", "DROP ", "TRUNCATE ", "ALTER COLUMN", "CREATE TRIGGER", "CREATE INDEX" })
                Assert.DoesNotContain(word, sql);
        }
    }
}
