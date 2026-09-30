-- ============================================================================
-- UpdateSeasonalVarietyColors_2026-09-27.sql
--
-- Sets dbo.PlantSpecies.Color for Seasonal Varieties (PlantTypeId = 4) from
-- a user-supplied Id/Variety/Sub-Variety/ReadyStockDays/Color list, matched
-- by Name (whitespace/case-normalized, NOT fuzzy -- see below).
--
-- Of 289 "SEASONAL VARITIES" rows in the source list:
--   - 0 duplicate names within the source list.
--   - 283 matched an existing dbo.PlantSpecies row (PlantTypeId=4) by exact
--     (whitespace/case-normalized) name.
--   - 6 did NOT match by exact name (pure name-format differences: an extra/
--     missing "Mix" suffix, parentheses, or word order) and were resolved by
--     EXPLICIT user-supplied mapping to the existing DB record, verified
--     unique under PlantTypeId=4 immediately below before this script was
--     written -- not guessed automatically:
--       'Pansy Super Majestic Giants Mix'      -> Id 3426 'Pansy Super Majestic Giants 2'
--       'Marigold Vanilla White'                -> Id 3434 'Marigold Vanilla (White)'
--       'Geranium Ringo 2000 Mix'               -> Id 3667 'Geranium Ringo 2000'
--       'Salvia Flamex 2000 Mix'                -> Id 3681 'Salvia Flamex 2000'
--       'Salvia Alert Red'                      -> Id 3718 'Salvia Red Alert'
--       'Begonia Semperflorens Ambassador Mix'  -> Id 3750 'Begonia Semperflorens Ambassador'
--   - All 289 targets get their Color set/updated below. 288 currently have
--     Color = NULL (real change, NULL -> value); 1 (Basil, Id 3179) currently
--     has Color = 'Green' and is being overwritten to 'Mix' per explicit
--     user approval of the source list as final authority (their rule 7).
--
-- Only dbo.PlantSpecies.Color is written. Name, ScientificName,
-- ReadyStockDays, PlantTypeId and every other column/table are untouched.
-- Only PlantTypeId = 4 rows are ever touched (the join is scoped to it).
--
-- IDEMPOTENT: driven entirely by a fixed (Id, Color) list, not by name
-- pattern matching or position. Running this script a second time re-applies
-- the same 289 (Id, Color) pairs -- @@ROWCOUNT will still report matched
-- rows (SQL Server counts a row touched by UPDATE even when the new value
-- equals the old one), but no value actually changes and the verification
-- block below confirms every target Id already holds the expected color
-- either way. Safe to re-run at any time.
--
-- SAFETY:
--   * Guarded to PlantsIMS2_Test only.
--   * Runs inside BEGIN TRANSACTION and ends with ROLLBACK TRANSACTION.
--   * Prints a full verification report before the rollback.
--   * Change ROLLBACK TRANSACTION -> COMMIT TRANSACTION at the bottom only
--     after reviewing that report, and only with explicit approval.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50280, 'UpdateSeasonalVarietyColors_2026-09-27.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION UpdateSeasonalColors;

BEGIN TRY

    DECLARE @ColorUpdates TABLE (Id INT PRIMARY KEY, NewColor NVARCHAR(100));
    INSERT INTO @ColorUpdates (Id, NewColor) VALUES
    (3069, N'Red'),
    (3070, N'Crimson'),
    (3071, N'Blue'),
    (3072, N'Mix'),
    (3073, N'Mix'),
    (3074, N'Rose'),
    (3075, N'Red'),
    (3076, N'Mix'),
    (3077, N'Mix'),
    (3078, N'Mix'),
    (3079, N'Magenta'),
    (3080, N'Mix'),
    (3081, N'White'),
    (3082, N'Blue'),
    (3083, N'Pink'),
    (3084, N'Mix'),
    (3085, N'Red'),
    (3086, N'Mix'),
    (3087, N'Scarlet'),
    (3088, N'Yellow'),
    (3089, N'Orange'),
    (3090, N'Gold'),
    (3091, N'Yellow'),
    (3092, N'Orange'),
    (3093, N'Gold'),
    (3094, N'Purple'),
    (3095, N'Red'),
    (3096, N'Mix'),
    (3097, N'Mix'),
    (3098, N'Rose'),
    (3099, N'Mix'),
    (3100, N'White'),
    (3101, N'Red'),
    (3102, N'Pink'),
    (3103, N'Scarlet'),
    (3104, N'Mix'),
    (3105, N'Mix'),
    (3106, N'Mix'),
    (3107, N'Mix'),
    (3108, N'Mix'),
    (3109, N'Mix'),
    (3110, N'Mix'),
    (3111, N'Mix'),
    (3112, N'Mix'),
    (3113, N'Mix'),
    (3126, N'White'),
    (3127, N'Mix'),
    (3131, N'Mix'),
    (3132, N'Mix'),
    (3133, N'Red'),
    (3134, N'Mix'),
    (3135, N'Mix'),
    (3150, N'Mix'),
    (3157, N'Mix'),
    (3159, N'Mix'),
    (3160, N'Burgundy'),
    (3162, N'Mix'),
    (3163, N'Mix'),
    (3164, N'Red'),
    (3165, N'Mix'),
    (3166, N'Mix'),
    (3167, N'Mix'),
    (3168, N'Mix'),
    (3169, N'Mix'),
    (3176, N'Scarlet'),
    (3177, N'Mix'),
    (3178, N'Mix'),
    (3179, N'Mix'),
    (3180, N'Red'),
    (3181, N'Pink'),
    (3182, N'Yellow'),
    (3188, N'Mix'),
    (3189, N'Violet'),
    (3190, N'Blue'),
    (3191, N'Blue'),
    (3192, N'Blue'),
    (3193, N'Salmon'),
    (3194, N'Mix'),
    (3195, N'Mix'),
    (3383, N'Mix'),
    (3384, N'Mix'),
    (3385, N'Mix'),
    (3388, N'Mix'),
    (3390, N'Mix'),
    (3391, N'Mix'),
    (3392, N'Mix'),
    (3393, N'Mix'),
    (3394, N'Silver'),
    (3398, N'Mix'),
    (3399, N'Mix'),
    (3400, N'Mix'),
    (3401, N'Mix'),
    (3402, N'Mix'),
    (3404, N'Mix'),
    (3405, N'Mix'),
    (3407, N'Mix'),
    (3408, N'Mix'),
    (3409, N'Mix'),
    (3410, N'Mix'),
    (3411, N'Mix'),
    (3412, N'Mix'),
    (3413, N'Lemon'),
    (3414, N'Mix'),
    (3415, N'Mix'),
    (3416, N'Mix'),
    (3418, N'Mix'),
    (3420, N'Mix'),
    (3424, N'Mix'),
    (3425, N'Mix'),
    (3432, N'Mix'),
    (3435, N'Mix'),
    (3438, N'Red'),
    (3440, N'Mix'),
    (3442, N'Mix'),
    (3443, N'Mix'),
    (3446, N'Mix'),
    (3448, N'Mix'),
    (3450, N'Mix'),
    (3451, N'Mix'),
    (3455, N'Mix'),
    (3461, N'Mix'),
    (3462, N'Mix'),
    (3466, N'Mix'),
    (3471, N'Mix'),
    (3475, N'Mix'),
    (3477, N'Mix'),
    (3480, N'Mix'),
    (3484, N'Mix'),
    (3485, N'Mix'),
    (3487, N'Mix'),
    (3488, N'Mix'),
    (3489, N'Mix'),
    (3490, N'Aqua'),
    (3499, N'Mix'),
    (3502, N'Mix'),
    (3655, N'Mix'),
    (3656, N'Mix'),
    (3657, N'Mix'),
    (3658, N'Mix'),
    (3659, N'Mix'),
    (3660, N'Mix'),
    (3661, N'Mix'),
    (3662, N'Mix'),
    (3663, N'Mix'),
    (3664, N'Mix'),
    (3665, N'Mix'),
    (3666, N'Mix'),
    (3668, N'Mix'),
    (3669, N'Mix'),
    (3670, N'Mix'),
    (3671, N'Mix'),
    (3672, N'Mix'),
    (3673, N'Mix'),
    (3674, N'Mix'),
    (3675, N'Mix'),
    (3676, N'Mix'),
    (3677, N'Mix'),
    (3678, N'Mix'),
    (3679, N'Mix'),
    (3680, N'Mix'),
    (3682, N'Red'),
    (3683, N'Mix'),
    (3684, N'Mix'),
    (3685, N'Mix'),
    (3686, N'Mix'),
    (3687, N'Mix'),
    (3688, N'Mix'),
    (3689, N'Mix'),
    (3690, N'Mix'),
    (3691, N'Mix'),
    (3692, N'Mix'),
    (3693, N'Mix'),
    (3694, N'Mix'),
    (3695, N'Mix'),
    (3696, N'Mix'),
    (3697, N'Mix'),
    (3698, N'Mix'),
    (3699, N'Mix'),
    (3700, N'Mix'),
    (3701, N'Mix'),
    (3702, N'Mix'),
    (3703, N'Mix'),
    (3704, N'Mix'),
    (3705, N'Mix'),
    (3706, N'Mix'),
    (3707, N'Mix'),
    (3708, N'Mix'),
    (3709, N'Mix'),
    (3710, N'Mix'),
    (3711, N'Mix'),
    (3712, N'Mix'),
    (3713, N'Mix'),
    (3714, N'Mix'),
    (3715, N'Mix'),
    (3716, N'Mix'),
    (3717, N'Mix'),
    (3719, N'Red'),
    (3720, N'Purple'),
    (3721, N'Mix'),
    (3722, N'Mix'),
    (3723, N'Mix'),
    (3724, N'Mix'),
    (3725, N'Mix'),
    (3726, N'Mix'),
    (3727, N'Mix'),
    (3728, N'Mix'),
    (3729, N'Mix'),
    (3730, N'Mix'),
    (3731, N'Mix'),
    (3732, N'Mix'),
    (3733, N'Mix'),
    (3734, N'Mix'),
    (3735, N'Mix'),
    (3736, N'Mix'),
    (3737, N'Mix'),
    (3738, N'Mix'),
    (3739, N'Mix'),
    (3740, N'Mix'),
    (3741, N'Mix'),
    (3742, N'Mix'),
    (3743, N'Mix'),
    (3744, N'Mix'),
    (3745, N'Mix'),
    (3746, N'Mix'),
    (3747, N'Mix'),
    (3748, N'Mix'),
    (3749, N'Mix'),
    (3751, N'Mix'),
    (3752, N'Mix'),
    (3753, N'Mix'),
    (3754, N'Mix'),
    (3755, N'Mix'),
    (3756, N'Mix'),
    (3757, N'Mix'),
    (3758, N'Mix'),
    (3759, N'Mix'),
    (3760, N'Mix'),
    (3761, N'Mix'),
    (3762, N'Mix'),
    (3763, N'Mix'),
    (3764, N'Mix'),
    (3765, N'Mix'),
    (3766, N'Mix'),
    (3767, N'Mix'),
    (3768, N'Mix'),
    (3769, N'Mix'),
    (3770, N'Mix'),
    (3771, N'Mix'),
    (3772, N'Mix'),
    (3773, N'Mix'),
    (3774, N'Mix'),
    (3775, N'Mix'),
    (3776, N'Mix'),
    (3777, N'Mix'),
    (3778, N'Mix'),
    (3779, N'Mix'),
    (3780, N'Mix'),
    (3781, N'Mix'),
    (3782, N'Mix'),
    (3783, N'Mix'),
    (3784, N'Mix'),
    (3785, N'Mix'),
    (3786, N'Mix'),
    (3787, N'Mix'),
    (3788, N'Mix'),
    (3789, N'Mix'),
    (3790, N'Mix'),
    (3791, N'Mix'),
    (3792, N'Mix'),
    (3793, N'Mix'),
    (3794, N'Mix'),
    (3795, N'Mix'),
    (3796, N'Mix'),
    (3797, N'Mix'),
    (3798, N'Mix'),
    (3799, N'Mix'),
    (3800, N'Mix'),
    (3801, N'Mix'),
    (3802, N'Orange'),
    (3803, N'Yellow'),
    (3804, N'Orange'),
    (3805, N'Yellow'),
    (3806, N'Gold'),
    -- The 6 name-format resolutions, explicitly approved and pre-verified
    -- unique under PlantTypeId=4 (see header comment):
    (3426, N'Mix'),   -- Pansy Super Majestic Giants 2      <- source 'Pansy Super Majestic Giants Mix'
    (3434, N'White'), -- Marigold Vanilla (White)           <- source 'Marigold Vanilla White'
    (3667, N'Mix'),   -- Geranium Ringo 2000                <- source 'Geranium Ringo 2000 Mix'
    (3681, N'Mix'),   -- Salvia Flamex 2000                 <- source 'Salvia Flamex 2000 Mix'
    (3718, N'Red'),   -- Salvia Red Alert                   <- source 'Salvia Alert Red'
    (3750, N'Mix');   -- Begonia Semperflorens Ambassador   <- source 'Begonia Semperflorens Ambassador Mix'

    DECLARE @ExpectedCount INT = (SELECT COUNT(*) FROM @ColorUpdates);
    IF @ExpectedCount <> 289
        THROW 50281, 'Expected exactly 289 (Id, Color) pairs in @ColorUpdates -- mismatch, aborting.', 1;

    DECLARE @IdsFoundUnderType4 INT = (
        SELECT COUNT(*) FROM dbo.PlantSpecies p JOIN @ColorUpdates c ON c.Id = p.Id WHERE p.PlantTypeId = 4);
    IF @IdsFoundUnderType4 <> 289
        THROW 50282, 'One or more target Ids no longer exist under PlantTypeId=4 -- aborting rather than guess.', 1;

    -- Capture full before-state for the verification block (Name/ScientificName/
    -- ReadyStockDays must be provably unchanged after the update)
    SELECT p.Id, p.Name, p.ScientificName, p.ReadyStockDays, p.Color AS ColorBefore
    INTO #Before
    FROM dbo.PlantSpecies p JOIN @ColorUpdates c ON c.Id = p.Id;

    ----------------------------------------------------------------------
    -- THE UPDATE -- scoped to PlantTypeId = 4 explicitly, Color only
    ----------------------------------------------------------------------
    UPDATE p
        SET p.Color = c.NewColor
    FROM dbo.PlantSpecies p
    JOIN @ColorUpdates c ON c.Id = p.Id
    WHERE p.PlantTypeId = 4;

    DECLARE @RowsUpdated INT = @@ROWCOUNT;
    IF @RowsUpdated <> 289
        THROW 50283, 'Expected UPDATE to touch exactly 289 rows -- mismatch, aborting.', 1;

    ----------------------------------------------------------------------
    -- VERIFICATION -- before the ROLLBACK, so it shows what WOULD happen
    ----------------------------------------------------------------------
    PRINT '=== Update summary ===';
    SELECT @ExpectedCount AS TargetRows, @RowsUpdated AS RowsUpdated;

    PRINT '=== Every target row now has the expected Color ===';
    SELECT COUNT(*) AS MismatchCount
    FROM dbo.PlantSpecies p
    JOIN @ColorUpdates c ON c.Id = p.Id
    WHERE p.PlantTypeId = 4 AND ISNULL(p.Color, N'') <> c.NewColor;
    PRINT '(MismatchCount above must be 0)';

    PRINT '=== Name / ScientificName / ReadyStockDays unchanged for every updated row ===';
    SELECT COUNT(*) AS ChangedNonColorFields
    FROM #Before b
    JOIN dbo.PlantSpecies p ON p.Id = b.Id
    WHERE p.Name <> b.Name
       OR ISNULL(p.ScientificName, N'') <> ISNULL(b.ScientificName, N'')
       OR ISNULL(p.ReadyStockDays, -1) <> ISNULL(b.ReadyStockDays, -1);
    PRINT '(ChangedNonColorFields above must be 0)';

    PRINT '=== No duplicate Seasonal Variety names under PlantTypeId=4 ===';
    SELECT Name, COUNT(*) AS Occurrences FROM dbo.PlantSpecies WHERE PlantTypeId = 4 GROUP BY Name HAVING COUNT(*) > 1;
    PRINT '(no rows above = clean)';

    PRINT '=== PlantTypes 1/2/3 unchanged (expect 28/78/18) ===';
    SELECT PlantTypeId, COUNT(*) AS SpeciesCount FROM dbo.PlantSpecies WHERE PlantTypeId IN (1,2,3) GROUP BY PlantTypeId ORDER BY PlantTypeId;

    PRINT '=== Total PlantSpecies count unchanged (expect 413) ===';
    SELECT COUNT(*) AS TotalPlantSpecies FROM dbo.PlantSpecies;

    PRINT '=== Sample of updated rows ===';
    SELECT TOP 10 p.Id, p.Name, p.Color, p.ReadyStockDays FROM dbo.PlantSpecies p
    JOIN @ColorUpdates c ON c.Id = p.Id ORDER BY p.Id;

    DROP TABLE #Before;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION UpdateSeasonalColors;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-09-27: user reviewed the dry-run verification output
-- (including the 6 explicit name-format mappings and the Basil overwrite)
-- and explicitly approved committing this exact script. Verified backup in
-- place: PlantsIMS2_Test_PreSeasonalVarietyColors_20260927_232540.bak.
----------------------------------------------------------------------------
IF XACT_STATE() = 1 COMMIT TRANSACTION UpdateSeasonalColors;
ELSE IF XACT_STATE() <> 0 ROLLBACK TRANSACTION UpdateSeasonalColors;
GO
