using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // 2026-10-19 (owner requirements 2026-10-12): who SEES a batch waiting for Supervisor Approval, and who may APPROVE it,
    // are two separate questions.
    //   SEE     every pending batch in the user's Area / workflow scope -- never only the batches personally assigned to
    //           them. Seed / Cutting / Bed Sowing batches: the seedling Area scope (SeedlingAreaScope); Mother Plant Area
    //           transplant batches: the regular Area rule, always enforced (AreaAccessService), like all Mother Plant work.
    //   APPROVE exactly what the approval itself enforces (ReadyConfirmation/Confirm + ReadyConfirmationRepository.ConfirmAsync
    //           + TR_ReadyConfirmations_AssignedSupervisor): the ReadyStock.Confirm permission, DirectSowingRules.CanApprove
    //           (assigned supervisor / designated Bed Sowing approver / never one's own seed sowing), a Mother Plant transplant
    //           batch only by an Area Supervisor of its own Area, and the Ready Date. Seeing a batch never grants approving it.
    // The list pages use Evaluate only to show or hide the Approve button and to say why; the approval re-checks everything
    // on the server, under the batch's lock, and the database trigger checks it again.
    public static class SowingApprovalRules
    {
        // Workflow categories, kept separate on the pages.
        public const string CategorySowing = "Sowing";                         // Direct Seed Sowing and Cutting Sowing
        public const string CategoryBedSowing = "BedSowing";                   // tray batches from a Main Office Bed Sowing
        public const string CategoryMotherPlantTransplant = "MotherPlantTransplant";   // tray batches transplanted in a Mother Plant Area
        // 2026-10-21: Direct (seed) Sowings recorded in a Mother Plant Area (Mother Plant & Cutting > Direct Sowing).
        public const string CategoryMotherPlantDirectSowing = "MotherPlantDirectSowing";
        public static readonly IReadOnlyList<string> Categories =
            new[] { CategorySowing, CategoryBedSowing, CategoryMotherPlantTransplant, CategoryMotherPlantDirectSowing };
        // The workflows listed under Mother Plant & Cutting > Sowing Approvals (both approved by an Area Supervisor of the Area).
        public static readonly IReadOnlyList<string> MotherPlantCategories = new[] { CategoryMotherPlantDirectSowing, CategoryMotherPlantTransplant };
        public static bool IsMotherPlantCategory(string category) => MotherPlantCategories.Contains(category);

        // motherPlantDirect: SeedSowings.IsMotherPlantDirectSowing (a 'Seed' sowing, told apart by that flag, never by its Area).
        public static string CategoryOf(string? sourceType, bool motherPlantDirect = false) => sourceType switch
        {
            SeedSowing.SourceBedSowing => CategoryBedSowing,
            SeedSowing.SourceTransplant => CategoryMotherPlantTransplant,
            _ when motherPlantDirect => CategoryMotherPlantDirectSowing,
            _ => CategorySowing,
        };

        public static string CategoryOfBatch(SeedSowing s) => CategoryOf(s.SourceType, s.IsMotherPlantDirectSowing);

        public static string CategoryLabel(string category) => category switch
        {
            CategoryBedSowing => "Bed Sowing tray batches",
            CategoryMotherPlantTransplant => "Mother Plant Area transplant batches",
            CategoryMotherPlantDirectSowing => "Mother Plant Area Direct Sowings",
            _ => "Direct Seed and Cutting sowings",
        };

        public const string NoApprovePermissionMessage =
            "You can see this batch, but approving sowings (Supervisor Approval) is not part of your role.";

        // Area scope ONLY -- deliberately no user / supervisor / creator filter.
        public static bool IsVisible(string category, int areaId, Func<int, bool> seedlingScope, Func<int, bool> strictAreaScope)
            => IsMotherPlantCategory(category) ? strictAreaScope(areaId) : seedlingScope(areaId);

        public sealed record Settings(DirectSowingRules.BedApprover BedApprover, bool BedAnyDate, bool TransplantAreaScope, bool TransplantAnyDate = false);

        public sealed record Eligibility(bool CanApprove, string? Reason, string Approver, DateTime? ApproveFrom)
        {
            // Authorised to approve, only the Ready Date has not come yet.
            public bool WaitingForReadyDate => !CanApprove && ApproveFrom.HasValue;
            // 2026-10-20: this user passed EVERY authority check for this batch (permission, assigned / Bed Sowing approver,
            // Area Supervisor of its Area) -- ApproveFrom is only set after all of them -- so the batch is theirs to approve now
            // or from its Ready Date. Used to keep a "my approvals" queue to the user's own batches.
            public bool Authorized => CanApprove || WaitingForReadyDate;
        }

        // Who approves this batch -- display only.
        public static string ApproverLabel(SeedSowing s, Settings settings)
        {
            if (s.SourceType == SeedSowing.SourceBedSowing && settings.BedApprover.RuleActive)
                return settings.BedApprover.Name is { } name ? $"{name} (Bed Sowing approver)" : "No Bed Sowing approver designated";
            if (s.SourceType == SeedSowing.SourceTransplant)
                return settings.TransplantAreaScope ? $"Area Supervisor of {s.AreaName}" : "Any approver";
            if (s.SourceType == SeedSowing.SourceBedSowing)
                return "Any approver (not the transplanter)";
            if (s.IsMotherPlantDirectSowing)
                return $"Area Supervisor of {s.AreaName}";
            return s.SupervisorName is { } sup ? $"{sup} (assigned supervisor)" : "No supervisor assigned";
        }

        // isAreaApprover: ReadyConfirmationRepository.IsAreaApproverAsync for this user and the batch's Area (only consulted
        // for a Mother Plant transplant batch under TransplantAreaScope, and -- always -- for a Mother Plant Area Direct Sowing).
        public static Eligibility Evaluate(SeedSowing s, int? userId, string? userName, bool hasConfirmPermission, bool isAreaApprover,
            Settings settings, DateTime todayIndia)
        {
            var approver = ApproverLabel(s, settings);
            if (!hasConfirmPermission)
                return new Eligibility(false, NoApprovePermissionMessage, approver, null);
            var (mayApprove, error) = DirectSowingRules.CanApprove(s.SupervisorId, s.CreatedById, s.CreatedBy, userId, userName, s.SourceType,
                settings.BedApprover, s.IsMotherPlantDirectSowing, isAreaApprover, s.AreaName);
            if (!mayApprove)
                return new Eligibility(false, error, approver, null);
            if (s.SourceType == SeedSowing.SourceTransplant && settings.TransplantAreaScope && !isAreaApprover)
                return new Eligibility(false, string.Format(NotAreaSupervisorMessageFormat, s.AreaName ?? "this Area"), approver, null);
            var (readyOk, readyError) = BedSowingRules.ValidateApprovalReadyDate(s.SourceType, s.ExpectedReadyDate, todayIndia, settings.BedAnyDate,
                settings.TransplantAnyDate);
            if (!readyOk)
                return new Eligibility(false, readyError, approver, s.ExpectedReadyDate);
            return new Eligibility(true, null, approver, null);
        }

        // Same wording as ReadyConfirmationRepository.NotAreaSupervisorMessageFormat (the server's refusal).
        public const string NotAreaSupervisorMessageFormat =
            "A tray batch transplanted in {0} is approved by an Area Supervisor of {0} (a user who may approve sowings and is assigned to that Area) or a System Administrator.";
    }
}
