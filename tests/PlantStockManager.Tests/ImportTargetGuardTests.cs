using MainOfficeSowingImport;

namespace PlantStockManager.Tests
{
    // Where the Main Office sowing import may write (tools/MainOfficeSowingImport/ImportTargetGuard.cs): decided from the
    // connected server's own identity, never the database name alone. Live on the VPS is also named PlantsIMS2_Test.
    // No database needed.
    public class ImportTargetGuardTests
    {
        private const string Local = ImportTargetGuard.LocalDevelopmentServer;
        private const string Vps = @"VPS-SERVER\MSSQLSERVER02";          // stands for any server that is not the laptop

        private static (bool Allowed, bool IsLive, string Reason) Eval(string? server, string? db, string? flag = null, string? property = "same")
            => ImportTargetGuard.Evaluate(new ImportTarget(server, property == "same" ? server : property, db), flag);

        [Theory]
        [InlineData("PlantsIMS2_Test")]
        [InlineData("PlantsIMS2_Scratch_MoSowing_20261014")]
        public void LocalServer_TestOrScratch_Allowed_NotLive(string db)
        {
            var r = Eval(Local, db);
            Assert.True(r.Allowed, r.Reason);
            Assert.False(r.IsLive);
        }

        [Fact]
        public void LocalServer_NameComparedIgnoringCase()
            => Assert.True(Eval(Local.ToLowerInvariant(), "PlantsIMS2_Test").Allowed);

        [Theory]
        [InlineData("PlantsIMS2")]
        [InlineData("PlantsIMS")]
        [InlineData("master")]
        [InlineData("plantsims2_scratch_x")]                                  // prefix is exact
        public void LocalServer_OtherDatabases_Refused(string db)
            => Assert.False(Eval(Local, db).Allowed);

        [Fact]
        public void LocalServer_WithLiveFlag_Refused()
            => Assert.False(Eval(Local, "PlantsIMS2_Test", Local + "/PlantsIMS2_Test").Allowed);

        [Theory]
        [InlineData("PlantsIMS2_Test")]                                      // the live database's real name
        [InlineData("PlantsIMS2_Scratch_Copy")]                              // scratch names grant nothing off the laptop
        [InlineData("PlantsIMS2")]
        public void VpsOrUnknownServer_WithoutFlag_Refused(string db)
        {
            var r = Eval(Vps, db);
            Assert.False(r.Allowed);
            Assert.Contains(ImportTargetGuard.LiveFlag, r.Reason);
        }

        [Fact]
        public void VpsServer_WithFlagNamingExactlyIt_Allowed_AsLive()
        {
            var r = Eval(Vps, "PlantsIMS2_Test", Vps + "/PlantsIMS2_Test");
            Assert.True(r.Allowed, r.Reason);
            Assert.True(r.IsLive);
        }

        [Theory]
        [InlineData(@"OTHER\MSSQLSERVER02/PlantsIMS2_Test")]                // wrong server
        [InlineData(@"VPS-SERVER\MSSQLSERVER02/PlantsIMS2")]                // wrong database
        [InlineData(@"VPS-SERVER\MSSQLSERVER02/plantsims2_test")]           // database must match exactly
        [InlineData("PlantsIMS2_Test")]                                      // no server part
        [InlineData("")]                                                     // flag without a value
        [InlineData("   ")]
        public void VpsServer_WithFlagNotMatchingTarget_Refused(string flag)
            => Assert.False(Eval(Vps, "PlantsIMS2_Test", flag).Allowed);

        [Theory]
        [InlineData(null, "PlantsIMS2_Test")]
        [InlineData("", "PlantsIMS2_Test")]
        [InlineData(Local, null)]
        [InlineData(Local, " ")]
        public void IdentityNotVerifiable_Refused_EvenWithFlag(string? server, string? db)
        {
            Assert.False(Eval(server, db).Allowed);
            Assert.False(Eval(server, db, $"{server}/{db}").Allowed);
        }

        [Fact]
        public void ServerNamesDisagree_Refused()
        {
            // @@SERVERNAME claims the laptop but the instance reports another name (renamed / cloned server): fail closed
            var r = Eval(Local, "PlantsIMS2_Test", null, Vps);
            Assert.False(r.Allowed);
            Assert.Contains("cannot be verified", r.Reason);
            Assert.False(Eval(Local, "PlantsIMS2_Test", null, null).Allowed);
        }
    }
}
