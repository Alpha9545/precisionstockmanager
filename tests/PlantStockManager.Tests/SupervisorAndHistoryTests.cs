using PlantStockManager.Services;
using static PlantStockManager.Services.SupervisorRules;

namespace PlantStockManager.Tests
{
    // Supervisor dropdowns show only people with the role (and Area) the
    // field means; Stock History is Opening + Incoming - Outgoing = Balance.
    public class SupervisorAndHistoryTests
    {
        private static readonly RoleAssignment[] Assignments =
        {
            new(6, "Akshay", "Sowing Operator", null, true),
            new(6, "Akshay", "Sowing Supervisor", null, true),
            new(7, "Jalindar", "Sowing Operator", null, true),
            new(21, "Prajwal", "System Administrator", null, true),
            new(22, "Vishal", "Mother Plant Supervisor", null, true),
            new(22, "Vishal", "Mother Plant Supervisor", 1, true),
            new(23, "Kiran MP", "Mother Plant Supervisor", 2, true),
            new(24, "Old MP", "Mother Plant Supervisor", 1, false),
        };

        [Fact]
        public void SowingSupervisorDropdown_OnlySowingSupervisors()
        {
            var list = Eligible(Assignments, SowingSupervisor);
            Assert.Equal(new[] { 6 }, list.Select(u => u.UserId));   // not operators, not administrators
        }

        [Fact]
        public void MotherPlantSupervisorDropdown_OnlyMotherPlantSupervisors_Active()
        {
            var list = Eligible(Assignments, MotherPlantSupervisor).Select(u => u.UserId).ToList();
            Assert.Equal(new[] { 23, 22 }.OrderBy(i => i), list.OrderBy(i => i));
            Assert.DoesNotContain(24, list);   // inactive
        }

        [Fact]
        public void AreaSupervisorDropdown_OnlyThatArea()
        {
            Assert.Equal(new[] { 22 }, Eligible(Assignments, MotherPlantSupervisor, areaId: 1).Select(u => u.UserId));
            Assert.Equal(new[] { 23 }, Eligible(Assignments, MotherPlantSupervisor, areaId: 2).Select(u => u.UserId));
            Assert.Empty(Eligible(Assignments, MotherPlantSupervisor, areaId: 3));
        }

        [Theory]
        [InlineData("Sowing Supervisor", true)]
        [InlineData("Mother Plant Supervisor", true)]
        [InlineData("Sowing Operator", false)]
        [InlineData("System Administrator", false)]
        public void SupervisorRoleNames(string role, bool expected) => Assert.Equal(expected, IsSupervisorRole(role));

        // ---- stock history ----------------------------------------------

        [Fact]
        public void StockHistory_OpeningIncomingOutgoingBalance()
        {
            var d = new DateTime(2026, 9, 10);
            var moves = new[]
            {
                new StockHistoryRules.Movement("S1", d.AddDays(-5), 5000),   // before the period
                new StockHistoryRules.Movement("S1", d.AddDays(-2), -4000),  // before the period
                new StockHistoryRules.Movement("S1", d, 600),                // in the period
                new StockHistoryRules.Movement("S1", d.AddDays(1), -588),    // in the period
                new StockHistoryRules.Movement("S1", d.AddDays(9), -1),      // after the period
            };
            var s = Assert.Single(StockHistoryRules.Summarize(moves, d, d.AddDays(2)));
            Assert.Equal(1000, s.Opening);
            Assert.Equal(600, s.Incoming);
            Assert.Equal(588, s.Outgoing);
            Assert.Equal(1012, s.Balance);
        }

        [Fact]
        public void StockHistory_BalanceEqualsSumOfAllMovementsToDate()
        {
            var d = new DateTime(2026, 9, 1);
            var moves = Enumerable.Range(0, 30).Select(i => new StockHistoryRules.Movement("P", d.AddDays(i), i % 3 == 0 ? -7 : 11)).ToList();
            var s = Assert.Single(StockHistoryRules.Summarize(moves, d.AddDays(10), d.AddDays(20)));
            Assert.Equal(moves.Where(m => m.Date <= d.AddDays(20)).Sum(m => m.Quantity), s.Balance);
        }

        [Theory]
        [InlineData("Reservation", false)]
        [InlineData("ReservationRelease", false)]
        [InlineData("Dispatch", true)]
        [InlineData("Production", true)]
        [InlineData("TransitLoss", true)]
        public void Reservations_AreNotStockMovements(string type, bool moves)
            => Assert.Equal(moves, StockHistoryRules.MovesPhysicalStock(type));
    }
}
