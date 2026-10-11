namespace PlantStockManager.Tests
{
    // Which databases the PSM_TEST_CONNECTION tests (DeleteIntegrationTests, PotStockConcurrencyTests,
    // TrayStockConcurrencyTests) may run against. Until 2026-10-10 only the literal PlantsIMS2_Test was
    // accepted, so these tests could never run without touching that database. A restored scratch
    // copy (PlantsIMS2_Scratch_*, the same rule as PSM_SCRATCH_CONNECTION) is now accepted as well, so
    // they can run on a disposable copy. Opt-in stays explicit: nothing runs unless the variable is set,
    // and every other database name is still refused.
    internal static class IntegrationDatabase
    {
        public const string ConnectionVariable = "PSM_TEST_CONNECTION";
        public const string Allowed = "PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy";

        public static bool IsAllowed(string? name)
            => string.Equals(name, "PlantsIMS2_Test", StringComparison.OrdinalIgnoreCase)
               || (name != null && name.StartsWith("PlantsIMS2_Scratch_", StringComparison.Ordinal));
    }
}
