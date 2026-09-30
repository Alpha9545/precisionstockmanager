using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // CORRECTION #1 -- Cutting Entry -> destination selection.
    //   Main Office     harvest recorded in the Mother Plant's Area pool + the existing Cutting
    //                   delivery to Main Office created in the SAME transaction (pending Main
    //                   Office confirmation; quantity held In-Transit).
    //   Use for Pot Production   harvest stays in the Mother Plant's own Area pool.
    public class CuttingEntryDestinationTests
    {
        private const int Source = 122, Other = 123, MainOffice = 2, SecondMainOffice = 3;
        private static readonly int[] OneMainOffice = { MainOffice };

        private static string Repo(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        // ---- 1 / 2 / 4. the two destinations --------------------------------------------------------

        [Fact]
        public void MainOffice_Destination_IsTheMainOfficeArea_AndChosenAutomaticallyWhenThereIsOne()
        {
            var (ok, area, error) = CuttingRules.ResolveDestination(CuttingDestination.MainOffice, null, Source, OneMainOffice);
            Assert.True(ok, error);
            Assert.Equal(MainOffice, area);
        }

        [Fact]
        public void PotProduction_Destination_IsTheMotherPlantsOwnArea()
        {
            var (ok, area, error) = CuttingRules.ResolveDestination(CuttingDestination.PotProduction, null, Source, OneMainOffice);
            Assert.True(ok, error);
            Assert.Equal(Source, area);
        }

        [Fact]
        public void PotProduction_IgnoresAnyPostedMainOfficeArea()
        {
            // a tampered form cannot turn a Pot Production entry into a delivery somewhere
            var (ok, area, _) = CuttingRules.ResolveDestination(CuttingDestination.PotProduction, MainOffice, Source, OneMainOffice);
            Assert.True(ok);
            Assert.Equal(Source, area);
        }

        [Fact]
        public void MoreThanOneMainOffice_RequiresAChoice_AndAcceptsAnyOfThem()
        {
            var two = new[] { MainOffice, SecondMainOffice };
            Assert.False(CuttingRules.ResolveDestination(CuttingDestination.MainOffice, null, Source, two).Ok);
            Assert.Equal(SecondMainOffice, CuttingRules.ResolveDestination(CuttingDestination.MainOffice, SecondMainOffice, Source, two).DestinationAreaId);
        }

        [Fact]
        public void DestinationLabels_AreTheBusinessWords_AndHistoricalRowsShowADash()
        {
            Assert.Equal("Main Office", CuttingDestination.Label(CuttingDestination.MainOffice));
            Assert.Equal("Use for Pot Production", CuttingDestination.Label(CuttingDestination.PotProduction));
            Assert.Equal("-", CuttingDestination.Label(null));        // recorded before destinations existed: never guessed
        }

        // ---- validation: destination is mandatory; nothing cross-area ------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Somewhere")]
        [InlineData("mainoffice")]
        public void Destination_IsMandatory_AndMustBeOneOfTheTwo(string? destination)
        {
            var (ok, area, error) = CuttingRules.ResolveDestination(destination, MainOffice, Source, OneMainOffice);
            Assert.False(ok);
            Assert.Null(area);
            Assert.Contains("Main Office or Use for Pot Production", error);
        }

        [Fact]
        public void MainOffice_AnotherProductionArea_IsNotAValidDestination()
        {
            // Area isolation: cuttings can only be given to a real Main Office Area, never to another Area
            var (ok, _, error) = CuttingRules.ResolveDestination(CuttingDestination.MainOffice, Other, Source, OneMainOffice);
            Assert.False(ok);
            Assert.Contains("not an active Main Office Area", error);
        }

        [Fact]
        public void MainOffice_CannotBeTheSourceAreaItself()
        {
            var (ok, _, _) = CuttingRules.ResolveDestination(CuttingDestination.MainOffice, MainOffice, MainOffice, OneMainOffice);
            Assert.False(ok);
        }

        [Fact]
        public void MainOffice_WithNoActiveMainOfficeArea_IsRefused()
            => Assert.False(CuttingRules.ResolveDestination(CuttingDestination.MainOffice, null, Source, Array.Empty<int>()).Ok);

        // ---- 3 / 7. quantity ---------------------------------------------------------------------------

        [Theory]
        [InlineData(1)]
        [InlineData(4000)]
        [InlineData(70000)]
        public void ValidQuantity_IsAccepted(decimal q) => Assert.True(CuttingRules.ValidateProductionQuantity(q).Ok);

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(10.5)]
        public void InvalidQuantity_IsRefused(decimal q) => Assert.False(CuttingRules.ValidateProductionQuantity(q).Ok);

        [Fact]
        public void Repository_ValidatesQuantityAndDestination_BeforeOpeningAnyConnection()
        {
            var src = Repo("Data", "CuttingProductionRepository.cs");
            var insert = src.Substring(src.IndexOf("public async Task<(bool Success, string? Message, int Id)> InsertAsync", StringComparison.Ordinal));
            var firstConnection = insert.IndexOf("_dbHelper.GetConnection()", StringComparison.Ordinal);
            Assert.True(insert.IndexOf("ValidateProductionQuantity", StringComparison.Ordinal) is > 0 and var q && q < firstConnection);
            Assert.True(insert.IndexOf("CuttingDestination.IsValid(entry.DestinationType)", StringComparison.Ordinal) is > 0 and var d && d < firstConnection);
        }

        // ---- 3 / 4 / 1 / 2 / 11. what the repository writes, and that it is ONE transaction ------------------------

        [Fact]
        public void Repository_SavesQuantityAndDestination_AndCreditsTheHarvestOnce()
        {
            var src = Repo("Data", "CuttingProductionRepository.cs");
            Assert.Contains("DestinationType, DestinationAreaId, SubmissionToken", src);
            Assert.Contains("insert.Parameters.AddWithValue(\"@Quantity\", entry.Quantity)", src);
            Assert.Contains("insert.Parameters.AddWithValue(\"@DestinationType\", entry.DestinationType!)", src);
            Assert.Contains("insert.Parameters.AddWithValue(\"@DestinationAreaId\", destinationAreaId!.Value)", src);
            Assert.Single(Regex.Matches(src, "RecordTransactionAsync\\("));                 // exactly one ledger credit ...
            Assert.Contains("\"Harvest\", \"CuttingProduction\", newId", src);              // ... a Harvest of the entered quantity
        }

        [Fact]
        public void MainOffice_CreatesTheExistingPendingDelivery_InsideTheSameTransaction_BeforeCommit()
        {
            var src = Repo("Data", "CuttingProductionRepository.cs");
            var insert = src.Substring(src.IndexOf("public async Task<(bool Success, string? Message, int Id)> InsertAsync", StringComparison.Ordinal));
            var main = insert.IndexOf("if (entry.DestinationType == CuttingDestination.MainOffice)", StringComparison.Ordinal);
            var deliver = insert.IndexOf("_transferRepo.CreateCuttingDeliveryAsync(conn, tx, delivery, userId)", StringComparison.Ordinal);
            var commit = insert.IndexOf("tx.Commit()", StringComparison.Ordinal);
            var harvest = insert.IndexOf("\"Harvest\"", StringComparison.Ordinal);
            Assert.True(harvest > 0 && main > harvest && deliver > main && commit > deliver, "harvest -> delivery -> commit, one transaction");
            Assert.Single(Regex.Matches(insert, "tx\\.Commit\\(\\)"));                     // both legs commit together
            var branch = insert.Substring(main, commit - main);
            Assert.Contains("StockType = \"Cutting\"", branch);
            Assert.Contains("PendingConfirmationAreaId = destinationAreaId", branch);      // pending at the Main Office Area
            Assert.Contains("Quantity = entry.Quantity", branch);
            Assert.Contains("SourceCuttingProductionId = newId", branch);                  // traceable back to this entry
            Assert.Contains("tx.Rollback();", branch);                                     // a failed delivery undoes the harvest
        }

        [Fact]
        public void PotProduction_CreatesNoDelivery_AndNothingIsCreditedToMainOffice()
        {
            var src = Repo("Data", "CuttingProductionRepository.cs");
            // the ONLY place a delivery is created is behind the MainOffice check
            Assert.Single(Regex.Matches(src, "CreateCuttingDeliveryAsync\\("));
            Assert.Equal(1, Regex.Matches(src, "GetOrCreateLockedAsync\\(").Count);       // one pool: the Mother Plant's own Area
            Assert.Contains("GetOrCreateLockedAsync(conn, tx, speciesId, areaId.Value", src);
        }

        [Fact]
        public void DestinationIsRederivedFromTheDatabase_NotTrustedFromTheForm()
        {
            var src = Repo("Data", "CuttingProductionRepository.cs");
            Assert.Contains("SELECT Id FROM dbo.Area WHERE AreaType = N'MainOffice' AND IsActive = 1", src);
            Assert.Contains("CuttingRules.ResolveDestination(", src);
        }

        // ---- 8. the existing delivery flow is the SAME code, unchanged in behaviour -------------------------------

        [Fact]
        public void SendCuttingsToMainOffice_AndCuttingEntry_ShareOneDeliveryImplementation()
        {
            var repo = Repo("Data", "InternalTransferRepository.cs");
            var shared = repo.Substring(repo.IndexOf("public async Task<(bool Success, string? Message, int Id)> CreateCuttingDeliveryAsync", StringComparison.Ordinal));
            shared = shared.Substring(0, shared.IndexOf("private async Task<int> InsertHeaderAsync", StringComparison.Ordinal));
            Assert.Contains("ReserveInTransitAsync(", shared);                                                         // quantity held In-Transit
            Assert.Contains("entry.Status = \"PendingConfirmation\"", shared);                                          // always pending
            Assert.Contains("Source Area and the Main Office Area you're sending to must be different.", shared);       // same rule + message as before
            Assert.Contains("Source Species / Area is required.", shared);
            // the existing page's path (InsertAsync) now calls it and still owns its own transaction
            var insert = repo.Substring(repo.IndexOf("else if (entry.StockType == \"Cutting\")", StringComparison.Ordinal));
            insert = insert.Substring(0, insert.IndexOf("else if (entry.StockType == \"MainOfficeIssue\"", StringComparison.Ordinal));
            Assert.Contains("CreateCuttingDeliveryAsync(conn, tx, entry, userId)", insert);
            Assert.Contains("tx.Rollback()", insert);
            Assert.Contains("tx.Commit()", insert);
            var page = Repo("Pages", "Production", "CuttingStock", "GiveToMainOffice.cshtml.cs");
            Assert.Contains("_internalTransferRepo.InsertAsync(entry, userId)", page);                                  // page untouched
        }

        [Fact]
        public void OrdinaryTransfers_UseExactlyTheColumnsTheyAlwaysDid()
        {
            var repo = Repo("Data", "InternalTransferRepository.cs");
            Assert.Contains("var withProduction = entry.SourceCuttingProductionId.HasValue;", repo);
            Assert.Contains("CreatedBy{(withProduction ? \", SourceCuttingProductionId\" : \"\")}", repo);              // extra column only when linked
            Assert.Contains("if (withProduction)", repo);
        }

        [Fact]
        public void MainOfficeConfirmation_StillMovesTheStock_AndBooksTransitLoss()
        {
            // the confirmation side is untouched: same rules, same ledger movements
            Assert.True(CuttingRules.ConfirmDelivery(4000, 3700, "damaged").Ok);
            Assert.Equal(300, CuttingRules.ConfirmDelivery(4000, 3700, "damaged").TransitLoss);
            Assert.False(CuttingRules.ConfirmDelivery(4000, 3700, null).Ok);
            var repo = Repo("Data", "InternalTransferRepository.cs");
            Assert.Contains("\"TransitLoss\"", repo);
            Assert.Contains("\"Delivered to Main Office\"", repo);
        }

        // ---- 9. Pot Production / tray sowing: same source rules, In-Transit is never usable --------------------------

        [Fact]
        public void PotProductionAndTraySowing_StillDrawFromAvailableStock_WithTheSameSourceRule()
        {
            Assert.True(CuttingRules.CanUseAsSource(canAccessStockArea: true));       // stock of an Area the user may access (the Area's own; Main Office for Main Office users)
            Assert.False(CuttingRules.CanUseAsSource(canAccessStockArea: false));     // another Area's stock -- Main Office stock included (Correction #3: Area isolation)
            var pot = Repo("Pages", "Production", "PotBatch", "Create.cshtml.cs");
            Assert.Contains("s.AvailableQuantity > 0 && CanUseSource(s)", pot);                                 // AvailableQuantity = Physical - InTransit
            var sowing = Repo("Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml.cs");
            Assert.Contains("s.AvailableQuantity > 0 && CanUseSource(s)", sowing);
        }

        // ---- 5 / 6. authorization and Area isolation -----------------------------------------------------------------

        private static ClaimsPrincipal User(IEnumerable<string> permissions, params int[] areas)
        {
            var claims = new List<Claim> { new("UserId", "101") };
            claims.AddRange(permissions.Select(p => new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)));
            claims.AddRange(areas.Select(a => new Claim(AreaAccessService.AreaAccessClaimType, a.ToString())));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        [Fact]
        public void EntryPage_StillRequiresMotherPlantEnter()
        {
            var rule = FeatureAuthorizationConventions.GetRule("/Production/Cutting/Create");
            Assert.Equal("MotherPlant.Enter", rule.Read);
            Assert.True(User(new[] { "MotherPlant.Enter" }, Source).HasPermission(rule.Read));
            Assert.False(User(new[] { "MotherPlant.View", "PotProduction.Enter" }, Source).HasPermission(rule.Read));   // unauthorized user
            Assert.False(User(Array.Empty<string>(), Source).HasPermission(rule.Read));
        }

        [Fact]
        public void MotherPlantOfAnotherArea_CannotBeUsed()
        {
            var access = new AreaAccessService();
            var supervisor = User(new[] { "MotherPlant.Enter" }, Source);
            Assert.True(access.CanAccessArea(supervisor, Source));
            Assert.False(access.CanAccessArea(supervisor, Other));
            var page = Repo("Pages", "Production", "Cutting", "Create.cshtml.cs");
            Assert.Contains(".Where(m => m.AreaId.HasValue && _areaAccess.CanAccessArea(User, m.AreaId))", page);     // list is Area-filtered
            Assert.Contains("You are not authorized to record cuttings for this Mother Plant's Area.", page);           // POST re-checks
        }

        [Fact]
        public void EntryPage_TakesTheDestinationFromThePostedChoice_ValidatedByTheRule()
        {
            var type = typeof(PlantStockManager.Pages.Production.Cutting.CreateModel);
            Assert.NotNull(type.GetProperty("Destination")!.GetCustomAttribute<BindPropertyAttribute>());
            Assert.Equal(typeof(string), type.GetProperty("Destination")!.PropertyType);
            var page = Repo("Pages", "Production", "Cutting", "Create.cshtml.cs");
            Assert.Contains("CuttingRules.ResolveDestination(Destination, MainOfficeAreaId, mp.AreaId.Value, mainOfficeIds)", page);
            Assert.Contains("DestinationType = Destination,", page);
            Assert.Contains("DestinationAreaId = destinationAreaId,", page);
        }

        [Fact]
        public void EntryView_OffersTheTwoDestinations_AsRequiredRadios()
        {
            var html = Repo("Pages", "Production", "Cutting", "Create.cshtml");
            Assert.Contains("Give To / Destination", html);
            Assert.Contains("value=\"@PlantStockManager.Services.CuttingDestination.MainOffice\"", html);
            Assert.Contains("value=\"@PlantStockManager.Services.CuttingDestination.PotProduction\"", html);
            Assert.Contains(">Main Office</label>", html);
            Assert.Contains(">Use for Pot Production</label>", html);
            Assert.Equal(2, Regex.Matches(html, "type=\"radio\" asp-for=\"Destination\"[^>]* required").Count);        // mandatory
            Assert.Contains("@if (Model.MainOfficeAreas.Count > 1)", html);                                             // extra field only when needed
        }

        // ---- 11. no duplicate stock on a repeated submit ---------------------------------------------------------------

        [Fact]
        public void EveryFormLoad_GetsAFreshToken_AndAnEmptyTokenIsRefused()
        {
            var page = Repo("Pages", "Production", "Cutting", "Create.cshtml.cs");
            Assert.Contains("SubmissionToken = Guid.NewGuid();", page);
            Assert.Contains("if (SubmissionToken == Guid.Empty)", page);
            var html = Repo("Pages", "Production", "Cutting", "Create.cshtml");
            Assert.Contains("<input type=\"hidden\" asp-for=\"SubmissionToken\" />", html);
            Assert.Contains("$('#saveBtn').prop('disabled', true)", html);                                             // one click, one save
        }

        [Fact]
        public void RepeatedSubmit_FindsTheSavedEntry_AndCreatesNothing_EvenWhenTwoRaceEachOther()
        {
            var src = Repo("Data", "CuttingProductionRepository.cs");
            var insert = src.Substring(src.IndexOf("public async Task<(bool Success, string? Message, int Id)> InsertAsync", StringComparison.Ordinal));
            var check = insert.IndexOf("FindByTokenAsync(conn, tx, entry.SubmissionToken.Value)", StringComparison.Ordinal);
            var firstWrite = insert.IndexOf("SELECT SpeciesId, AreaId, Status FROM dbo.MotherPlants", StringComparison.Ordinal);
            Assert.True(check > 0 && check < firstWrite, "the token is checked before anything is locked or written");
            Assert.Contains("WITH (UPDLOCK, HOLDLOCK) WHERE SubmissionToken = @Token", src);                            // serialises identical submits
            Assert.Contains("catch (SqlException ex) when ((ex.Number == 2601 || ex.Number == 2627) && entry.SubmissionToken.HasValue)", src);
            var page = Repo("Pages", "Production", "Cutting", "Create.cshtml.cs");
            Assert.Contains("entry.IsDuplicateSubmission", page);
            Assert.Contains("nothing was added twice", page);
        }

        // ---- 12. historical rows and the migration ---------------------------------------------------------------------

        [Fact]
        public void Migration_IsAdditive_Guarded_AndEndsInTheApprovedCommit()
        {
            var sql = Repo("Database", "Migrations", "2026-09-28_CuttingEntryDestination.sql");
            Assert.Contains("IF DB_NAME() <> N'PlantsIMS2_Test'", sql);
            foreach (var forbidden in new[] { "INSERT INTO dbo.", "UPDATE dbo.", "DELETE FROM", "DROP TABLE", "DROP COLUMN", "DROP CONSTRAINT", "TRUNCATE", "sp_rename", "ALTER COLUMN" })
                Assert.DoesNotContain(forbidden, sql);
            Assert.Contains("ADD DestinationType NVARCHAR(30) NULL", sql);                                               // nullable: history stays NULL
            Assert.Contains("ADD DestinationAreaId INT NULL", sql);
            Assert.Contains("ADD SubmissionToken UNIQUEIDENTIFIER NULL", sql);
            Assert.Contains("ADD SourceCuttingProductionId INT NULL", sql);
            Assert.Contains("(DestinationType IS NULL AND DestinationAreaId IS NULL)", sql);                             // the check admits historical rows
            Assert.Contains("WHERE SubmissionToken IS NOT NULL", sql);
            // approved and applied 2026-09-28: it ends in COMMIT; the only ROLLBACK left is the error path in CATCH
            Assert.Matches(@"(?m)^COMMIT TRANSACTION CuttingEntryDestination;", sql);
            Assert.Single(Regex.Matches(sql, @"(?m)^[^-\r\n].*\bROLLBACK TRANSACTION\b"));
            Assert.Contains("IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CuttingEntryDestination;", sql);
        }

        [Fact]
        public void Migration_Trigger_KeepsEveryExistingRuleVerbatim_AndAddsTheDestinationRules()
        {
            var sql = Repo("Database", "Migrations", "2026-09-28_CuttingEntryDestination.sql");
            Assert.Single(Regex.Matches(sql, "CREATE OR ALTER TRIGGER"));
            Assert.Contains("CREATE OR ALTER TRIGGER dbo.TR_CuttingProductions_Rules", sql);
            Assert.Contains("A cutting production record cannot be changed after it is saved.", sql);                   // immutability kept
            Assert.Contains("The supervisor of a cutting production must be an active Mother Plant Supervisor of its Area.", sql);
            Assert.Contains("= N''Mother Plant Supervisor''", sql);
            Assert.Contains("THROW 50352", sql);                                                                         // destination mandatory
            Assert.Contains("THROW 50353", sql);                                                                         // MainOffice must be an active Main Office Area
            Assert.Contains("a.AreaType = N''MainOffice'' AND a.IsActive = 1", sql);
        }

        [Fact]
        public void HistoricalEntry_HasNoDestination_AndCarriesNoInventedTransfer()
        {
            var historical = new CuttingProduction { Quantity = 4000 };
            Assert.Null(historical.DestinationType);
            Assert.Null(historical.DestinationAreaId);
            Assert.Null(historical.TransferId);
            Assert.False(historical.IsDuplicateSubmission);
            Assert.Null(new InternalTransfer().SourceCuttingProductionId);
            var repo = Repo("Data", "CuttingProductionRepository.cs");
            Assert.Contains("DestinationType = r.IsDBNull(r.GetOrdinal(\"DestinationType\")) ? null", repo);            // NULL stays NULL when read
        }

        // ---- 10. the Mother Plant workflow is untouched --------------------------------------------------------------

        [Fact]
        public void MotherPlantChecks_OfTheEntry_AreUnchanged()
        {
            var src = Repo("Data", "CuttingProductionRepository.cs");
            Assert.Contains("cuttings can only be recorded for an Active Mother Plant.", src);
            Assert.Contains("This Mother Plant has no Area. Set its Area (Mother Plants > Edit) before recording cuttings.", src);
            Assert.Contains("The supervisor must be an active Mother Plant Supervisor of this Mother Plant's Area.", src);
            var page = typeof(PlantStockManager.Pages.Production.Cutting.CreateModel);
            Assert.Contains("motherPlantId", page.GetMethod("OnGetSupervisorsAsync")!.GetParameters().Select(p => p.Name));
        }
    }
}
