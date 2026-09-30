-- ============================================================================
-- UpdateSeasonalVarietyReadyStockDays_2026-09-28.sql
--
-- Sets dbo.PlantSpecies.ReadyStockDays ("Days to Ready") for Seasonal Varieties
-- (PlantTypeId = 4) from a user-supplied Id/Variety/Sub-Variety/Days/Color list.
--
-- Source list: 289 "SEASONAL VARITIES" rows, 0 duplicate names, no blank days.
-- Matching: exact Name after LTRIM/RTRIM + case-normalisation. NOT fuzzy.
--   - 283 source rows matched exactly one PlantTypeId=4 row by name.
--   - 6 source rows did not match by exact name (name-format drift from the
--     earlier insert tasks) and were mapped to the existing DB record by EXPLICIT
--     user approval, each verified unique under PlantTypeId=4 (not guessed):
--       'Pansy Super Majestic Giants Mix'      (32 d) -> Id 3426 'Pansy Super Majestic Giants 2'
--       'Marigold Vanilla White'               (22 d) -> Id 3434 'Marigold Vanilla (White)'
--       'Geranium Ringo 2000 Mix'              (39 d) -> Id 3667 'Geranium Ringo 2000'
--       'Salvia Flamex 2000 Mix'               (28 d) -> Id 3681 'Salvia Flamex 2000'
--       'Salvia Alert Red'                     (28 d) -> Id 3718 'Salvia Red Alert'
--       'Begonia Semperflorens Ambassador Mix' (39 d) -> Id 3750 'Begonia Semperflorens Ambassador'
--   => all 289 Seasonal rows are targets. The user approved overwriting existing
--      values (211 of them) with the source list, which is the master data.
---- Only dbo.PlantSpecies.ReadyStockDays is written, only for PlantTypeId = 4,
-- only for rows whose current value differs from the source value.
-- Name, ScientificName, Color, PlantTypeId and all other tables are untouched.
--
-- IDEMPOTENT: driven by a fixed (Id, ExpectedName, NewDays) list and only
-- touches rows whose value differs, so a re-run updates 0 rows.
--
-- SAFETY:
--   * Guarded to PlantsIMS2_Test only.
--   * Runs inside BEGIN TRANSACTION and ends with COMMIT TRANSACTION (approved 2026-09-28).
--   * Was a ROLLBACK dry run twice before being switched to COMMIT with explicit approval.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50290, 'UpdateSeasonalVarietyReadyStockDays_2026-09-28.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION UpdateSeasonalDays;

BEGIN TRY

    DECLARE @Updates TABLE (Id INT PRIMARY KEY, ExpectedName NVARCHAR(200) NOT NULL, NewDays INT NOT NULL);
    INSERT INTO @Updates (Id, ExpectedName, NewDays) VALUES
    (3069, N'Petunia Red Star', 32),
    (3070, N'Petunia Crimson Star', 32),
    (3071, N'Petunia Blue Star', 32),
    (3072, N'Petunia Hulahoop Mix', 32),
    (3073, N'Petunia Brovo Mix', 32),
    (3074, N'Petunia Rose Star', 32),
    (3075, N'Celosia Raven Red', 25),
    (3076, N'Celosia Ice cream Pink', 25),
    (3077, N'Celosia Ice cream Cherry', 25),
    (3078, N'Celosia Ice cream Orange', 25),
    (3079, N'Celosia Cristata Mad. Magenta', 25),
    (3080, N'Celosia Ice cream Yellow', 25),
    (3081, N'Platycodon Popstar White', 39),
    (3082, N'Platycodon Popstar Blue', 39),
    (3083, N'Platycodon Popstar Pink', 39),
    (3084, N'Vinca Pacifica Mix', 32),
    (3085, N'Salvia Mojave Red', 28),
    (3086, N'Mg. Hot Pak mix', 15),
    (3087, N'Cosmos S. Dwarf I. Scarlet', 15),
    (3088, N'Cosmos S. Dwarf I. Yellow', 15),
    (3089, N'Cosmos S. Dwarf I. Orange', 15),
    (3090, N'M.G Marvel Gold', 20),
    (3091, N'M.G Big Top Yellow', 20),
    (3092, N'M.G Big Top Orange', 20),
    (3093, N'M.G Big Top Gold', 20),
    (3094, N'Gomphrena Buddy Purple', 25),
    (3095, N'Celosia Cristata Raven Red', 25),
    (3096, N'Begonia Sprint Plus Mix', 39),
    (3097, N'Begonia Cocktail Mix', 39),
    (3098, N'Hypoestes Splash Rose', 25),
    (3099, N'Cosmos Mix', 15),
    (3100, N'Hypoestes Splash White', 25),
    (3101, N'Hypoestes Splash Red', 25),
    (3102, N'Hypoestes Splash Pink', 25),
    (3103, N'Zinnia Scarlet Elegans', 22),
    (3104, N'Gerbera Meg Revolution Mix', 46),
    (3105, N'Antirrhinum Tulinny Mix', 35),
    (3106, N'Antirrhinum Snappy Mix', 32),
    (3107, N'Celosia Glorious Mix', 25),
    (3108, N'Verbena Apex Mix', 32),
    (3109, N'Impatiens Imara Mix', 32),
    (3110, N'Impatiens Accent Star Mix', 32),
    (3111, N'Salvia Vista Mix', 28),
    (3112, N'Torenia Fournieri Mix', 28),
    (3113, N'Aster Callistephus Chinensis mix', 30),
    (3126, N'Salvia F. White', 28),
    (3127, N'Vinca Bold Mix', 32),
    (3131, N'Celosia Armor mix', 25),
    (3132, N'Galardia Mix', 35),
    (3133, N'Salvia Red Hot', 28),
    (3134, N'M.G Hot pack mix', 15),
    (3135, N'Dahlia Figaro Mix', 25),
    (3150, N'Chilli Sitara', 25),
    (3157, N'Vinca Cora XDR Mix', 32),
    (3159, N'Vinca Pacifica Punch', 32),
    (3160, N'Petunia Burgundy Star', 32),
    (3162, N'Antirrhinum Snapshot Mix', 35),
    (3163, N'Dianthus Ideal Mix', 32),
    (3164, N'Salvia Sahara Red', 28),
    (3165, N'Dianthus F1 Telstar Mix', 32),
    (3166, N'Cineraria Early Perfection Mix', 30),
    (3167, N'Verbena Quartz Mix', 32),
    (3168, N'Gazania Zanny Mix', 32),
    (3169, N'Gazania New Day Mix', 32),
    (3176, N'Cabbage Scarlet', 20),
    (3177, N'Brocoli Shishir', 20),
    (3178, N'Viola Wittrokiaha Mix', 32),
    (3179, N'Basil', 20),
    (3180, N'Celosia Glorious Red', 25),
    (3181, N'Celosia Glorious Pink', 25),
    (3182, N'Celosia Glorious Yellow', 25),
    (3188, N'Salvia F. Fairy Queen', 28),
    (3189, N'Salvia F. Evalution Violet', 28),
    (3190, N'Salvia F. Victoria Blue', 28),
    (3191, N'Salvia X Superba Adorba Blue', 28),
    (3192, N'Salvia Sup. Blue Queen', 28),
    (3193, N'Celosia Icecream Salmon', 25),
    (3194, N'Tulsi', 20),
    (3195, N'Celosia Cristata T. Mix', 25),
    (3383, N'Alyssum Fields Mix', 30),
    (3384, N'Antirrhinum Chimes Mix', 35),
    (3385, N'Aster New Double Mix', 30),
    (3388, N'Flowering Cabbage Ornamental Kale', 30),
    (3390, N'Celosia Plumosa Kimono Mix', 25),
    (3391, N'Celosia Cristata Amigo Mix', 25),
    (3392, N'Chrysanthemum Deconova Multiflora', 25),
    (3393, N'Chrysanthemum Deconova Grandiflora', 25),
    (3394, N'Cineraria Silver Dust', 30),
    (3398, N'Carnation Lilliput Mix', 40),
    (3399, N'Dahlia Big Flowers Mix', 25),
    (3400, N'Dahlia Fresco Mix', 25),
    (3401, N'Dianthus Super Parfait Series', 32),
    (3402, N'Gloxinia Double Brocade Mix', 46),
    (3404, N'Gerbera Mega Revolution', 46),
    (3405, N'Gaillardia Seedling', 32),
    (3407, N'Geranium Super', 39),
    (3408, N'Hypoestes Splash Mix', 25),
    (3409, N'Impatiens walleriana Imara XDR Mix', 32),
    (3410, N'Impatiens New Guinea', 32),
    (3411, N'Kochia Burning Blush', 25),
    (3412, N'Lobelia Crystal', 32),
    (3413, N'Melampodium Lemon Delight', 25),
    (3414, N'Osteospermum Mix', 32),
    (3415, N'Ornamental Chilli Dwarf Mix', 32),
    (3416, N'Petunia Double Double Duo Series', 32),
    (3418, N'Petunia Grandiflora Ultra Star Mix', 32),
    (3420, N'Petunia Grandiflora Colourwise', 32),
    (3424, N'Platycodon Pop Star Mix', 39),
    (3425, N'Portulaca Happy Hour', 22),
    (3432, N'Sunflower Teddy Bear', 18),
    (3435, N'Marigold French Hot Pack Mix', 22),
    (3438, N'Vinca Pacifica Red', 32),
    (3440, N'Vinca Valiant Mix', 32),
    (3442, N'Zinnia Dreamland Mix', 22),
    (3443, N'Zinnia New Preciosa Mix', 22),
    (3446, N'Celosia Arrabona', 25),
    (3448, N'Cosmos Casanova', 22),
    (3450, N'Gazania Enorma', 32),
    (3451, N'Impatiens Balance', 32),
    (3455, N'Marigold French Chica', 22),
    (3461, N'Viola Sorbet XP', 32),
    (3462, N'Pentas Lucky Star', 39),
    (3466, N'Torenia Kauai', 28),
    (3471, N'Calendula Calypso', 25),
    (3475, N'Gerbera Majorette', 46),
    (3477, N'Hypoestes Confetti Compact', 25),
    (3480, N'Petunia Puffin', 32),
    (3484, N'Begonia Fiona', 39),
    (3485, N'Calendula Zen', 25),
    (3487, N'Coleus Giant Exhibition', 25),
    (3488, N'Dianthus Telstar', 32),
    (3489, N'Gomphrena Audray', 25),
    (3490, N'Lobelia Aqua', 32),
    (3499, N'Viola Penny', 32),
    (3502, N'Impatiens Imara XDR', 32),
    (3655, N'Begonia Semperflorens Bada Boom', 40),
    (3656, N'Begonia Semperflorens Big Mix', 40),
    (3657, N'Begonia tuberous Nonstop', 45),
    (3658, N'Begonia tuberous Mocca Mix', 45),
    (3659, N'Coleus Wizard', 25),
    (3660, N'Coleus Fareway Mix', 25),
    (3661, N'Coleus Kong', 25),
    (3662, N'Coleus Jumbo', 25),
    (3663, N'Cosmos Kosmik', 20),
    (3664, N'Cosmos Ladybird Mix', 20),
    (3665, N'Gazania Day Break', 32),
    (3666, N'Gazania Kiss Mix', 32),
    (3668, N'Geranium Bulls Eye Mix', 39),
    (3669, N'Petunia Grandiflora Bravo', 32),
    (3670, N'Petunia Grandiflora Supercascade Formula Mix', 32),
    (3671, N'Petunia Grandiflora Frost', 32),
    (3672, N'Petunia Grandiflora Picotee Mix', 32),
    (3673, N'Petunia Trailing Easy Wave', 32),
    (3674, N'Petunia Trailing Ramblin', 32),
    (3675, N'Pentas Butterfly', 39),
    (3676, N'Pentas Starla', 39),
    (3677, N'Phlox Twinkle', 25),
    (3678, N'Phlox Promise Mix', 25),
    (3679, N'Rudbeckia Rustic', 32),
    (3680, N'Rudbeckia Toto', 32),
    (3682, N'Salvia Vista Red', 28),
    (3683, N'Salvia Salsa', 28),
    (3684, N'Strawberry Sweet Charley', 39),
    (3685, N'Strawberry Winter Down', 39),
    (3686, N'Stock Dwarf Mime', 28),
    (3687, N'Stock Dwarf Hot Cakes Mix', 28),
    (3688, N'Torenia Little Kiss Mix', 28),
    (3689, N'Torenia Kauai Mix', 28),
    (3690, N'Verbena Obsession', 32),
    (3691, N'Vinca Tattoo Series', 32),
    (3692, N'Zinnia Small Zahara', 22),
    (3693, N'Zinnia Small Zydeco', 22),
    (3694, N'Zinnia Small Profusion Double', 22),
    (3695, N'Antirrhinum Snappy', 32),
    (3696, N'Antirrhinum Twinny', 32),
    (3697, N'Antirrhinum DoubleShot', 32),
    (3698, N'Vinca Solar', 32),
    (3699, N'Vinca Solar Avalanche', 32),
    (3700, N'Vinca Heatwave', 32),
    (3701, N'Cineraria Quicksilver', 32),
    (3702, N'Cineraria Silverado', 32),
    (3703, N'Dianthus Diana', 32),
    (3704, N'Dianthus Divinity', 32),
    (3705, N'Dianthus Chiba', 32),
    (3706, N'Dianthus Supra', 32),
    (3707, N'Dianthus Elegance', 32),
    (3708, N'Geranium Nano', 39),
    (3709, N'Geranium Apache', 39),
    (3710, N'Petunia Limbo GP', 32),
    (3711, N'Petunia Mambo GP', 32),
    (3712, N'Petunia Shake', 32),
    (3713, N'Petunia Tango', 32),
    (3714, N'Petunia Lambada', 32),
    (3715, N'Salvia Zenith', 28),
    (3716, N'Salvia Reddy', 28),
    (3717, N'Salvia Amore', 28),
    (3719, N'Salvia Red Hill', 28),
    (3720, N'Verbena Purple Haze', 32),
    (3721, N'Verbena Dazzling Nights', 32),
    (3722, N'Viola Corina', 32),
    (3723, N'Viola Cello', 32),
    (3724, N'Viola Xtrada', 32),
    (3725, N'Viola Trumpet', 32),
    (3726, N'Petunia Wave', 32),
    (3727, N'Petunia Easy Wave', 32),
    (3728, N'Petunia Shock Wave', 32),
    (3729, N'Petunia Tidal Wave', 32),
    (3730, N'Petunia Ez Rider', 32),
    (3731, N'Petunia Dreams', 32),
    (3732, N'Petunia Daddy', 32),
    (3733, N'Petunia Double Madness', 32),
    (3734, N'Vinca Pacifica XP', 32),
    (3735, N'Vinca Tattoo', 32),
    (3736, N'Vinca Titan', 32),
    (3737, N'Vinca Valiant', 32),
    (3738, N'Pansy Cool Wave', 32),
    (3739, N'Pansy Matrix', 32),
    (3740, N'Pansy Panola', 32),
    (3741, N'Coleus Fairway', 25),
    (3742, N'Dianthus Ideal Select', 32),
    (3743, N'Dianthus Super Parfait', 32),
    (3744, N'Zinnia Zahara', 22),
    (3745, N'Zinnia Double Zahara', 22),
    (3746, N'Marigold Inca II', 22),
    (3747, N'Marigold Antigua', 22),
    (3748, N'Marigold Durango', 22),
    (3749, N'Salvia Vista', 28),
    (3751, N'Begonia Semperflorens Senator IQ', 39),
    (3752, N'Begonia Semperflorens Emperor', 39),
    (3753, N'Begonia Viking', 39),
    (3754, N'Begonia Viking XL', 39),
    (3755, N'Celosia Kimono', 25),
    (3756, N'Celosia Century', 25),
    (3757, N'Celosia Dragon''s Breath', 25),
    (3758, N'Celosia Flamma', 25),
    (3759, N'Coleus Rainbow', 25),
    (3760, N'Dianthus Diamond', 32),
    (3761, N'Dianthus Lillipot', 32),
    (3762, N'Gomphrena Gnome', 25),
    (3763, N'Gomphrena Pinball', 25),
    (3764, N'Gomphrena Ping Pong', 25),
    (3765, N'Marigold Proud Mari', 22),
    (3766, N'Marigold Coco', 22),
    (3767, N'Pansy Crown', 32),
    (3768, N'Pansy Ultima', 32),
    (3769, N'Vinca Victory', 32),
    (3770, N'Vinca Virtuosa', 32),
    (3771, N'Zinnia Profusion', 22),
    (3772, N'Zinnia Profusion Double', 22),
    (3773, N'Antirrhinum Chantilly', 32),
    (3774, N'Antirrhinum Legend Double', 32),
    (3775, N'Antirrhinum Statement', 32),
    (3776, N'Celosia Armor', 25),
    (3777, N'Celosia Castle', 25),
    (3778, N'Petunia Evening Scentsation', 32),
    (3779, N'Petunia Opera Supreme', 32),
    (3780, N'Petunia Trilogy', 32),
    (3781, N'Salvia Hummingbird', 28),
    (3782, N'Salvia Summer Jewel', 28),
    (3783, N'Sunflower Big Smile', 18),
    (3784, N'Sunflower Smiley', 18),
    (3785, N'Sunflower Sunny Smile', 18),
    (3786, N'Zinnia Dreamland', 22),
    (3787, N'Zinnia Preciosa', 22),
    (3788, N'Zinnia Belize', 22),
    (3789, N'Vinca Cora XDR', 32),
    (3790, N'Vinca Blockbuster', 32),
    (3791, N'Pentas BeeBright', 39),
    (3792, N'Pentas Beehive', 39),
    (3793, N'Petunia Ramblin', 32),
    (3794, N'Petunia Picobella', 32),
    (3795, N'Petunia Sanguna seed lines', 32),
    (3796, N'Pansy Delta', 32),
    (3797, N'Pansy Colossus', 32),
    (3798, N'Marigold Marvel', 22),
    (3799, N'Marigold Bonanza', 22),
    (3800, N'Salvia Sahara', 28),
    (3801, N'Salvia Mojave', 28),
    (3802, N'Calendula officinalis Calypso Orange', 25),
    (3803, N'Calendula officinalis Calypso Yellow', 25),
    (3804, N'Marigold Inca Orange', 22),
    (3805, N'Marigold Inca Yellow', 22),
    (3806, N'Marigold Inca Gold', 22),
    -- The 6 name-format resolutions, explicitly approved by the user (same mapping
    -- approach as UpdateSeasonalVarietyColors_2026-09-27.sql); ExpectedName is the DB name:
    (3426, N'Pansy Super Majestic Giants 2', 32),            -- source 'Pansy Super Majestic Giants Mix'
    (3434, N'Marigold Vanilla (White)', 22),                 -- source 'Marigold Vanilla White'
    (3667, N'Geranium Ringo 2000', 39),                      -- source 'Geranium Ringo 2000 Mix'
    (3681, N'Salvia Flamex 2000', 28),                       -- source 'Salvia Flamex 2000 Mix'
    (3718, N'Salvia Red Alert', 28),                         -- source 'Salvia Alert Red'
    (3750, N'Begonia Semperflorens Ambassador', 39)          -- source 'Begonia Semperflorens Ambassador Mix';

    IF (SELECT COUNT(*) FROM @Updates) <> 289
        THROW 50291, 'Expected exactly 289 (Id, Days) pairs -- mismatch, aborting.', 1;

    -- Every target must still exist under PlantTypeId=4 with the expected name
    IF (SELECT COUNT(*) FROM @Updates u JOIN dbo.PlantSpecies p ON p.Id = u.Id
        WHERE p.PlantTypeId = 4 AND LOWER(LTRIM(RTRIM(p.Name))) = LOWER(LTRIM(RTRIM(u.ExpectedName)))) <> 289
        THROW 50292, 'A target Id is missing, has changed type, or has changed name -- aborting rather than guess.', 1;

    -- Full before-snapshot of the whole table (all types) for the unchanged checks
    SELECT Id, PlantTypeId, Name, ScientificName, ReadyStockDays, Color INTO #BeforeAll FROM dbo.PlantSpecies;

    DECLARE @ExpectedChanges INT = (SELECT COUNT(*) FROM @Updates u JOIN dbo.PlantSpecies p ON p.Id = u.Id
                                    WHERE p.PlantTypeId = 4 AND ISNULL(p.ReadyStockDays, -1) <> u.NewDays);

    ----------------------------------------------------------------------
    -- THE UPDATE -- PlantTypeId = 4 only, ReadyStockDays only, changed rows only
    ----------------------------------------------------------------------
    UPDATE p
        SET p.ReadyStockDays = u.NewDays
    FROM dbo.PlantSpecies p
    JOIN @Updates u ON u.Id = p.Id
    WHERE p.PlantTypeId = 4
      AND ISNULL(p.ReadyStockDays, -1) <> u.NewDays;

    DECLARE @RowsUpdated INT = @@ROWCOUNT;
    IF @RowsUpdated <> @ExpectedChanges
        THROW 50293, 'UPDATE row count differs from the expected number of changed rows -- aborting.', 1;

    ----------------------------------------------------------------------
    -- VERIFICATION
    ----------------------------------------------------------------------
    PRINT '=== Summary ===';
    SELECT (SELECT COUNT(*) FROM @Updates) AS TargetRows, @RowsUpdated AS RowsUpdated,
           (SELECT COUNT(*) FROM @Updates) - @RowsUpdated AS RowsAlreadyCorrect;

    PRINT '=== Every target row has exactly the source Days (MismatchCount must be 0) ===';
    SELECT COUNT(*) AS MismatchCount FROM dbo.PlantSpecies p JOIN @Updates u ON u.Id = p.Id
    WHERE ISNULL(p.ReadyStockDays, -1) <> u.NewDays;

    PRINT '=== Coverage: PlantTypeId=4 rows that are NOT targets (must be 0) ===';
    SELECT COUNT(*) AS SeasonalNotTargeted FROM dbo.PlantSpecies p WHERE p.PlantTypeId = 4 AND p.Id NOT IN (SELECT Id FROM @Updates);
    PRINT '=== Name/ScientificName/Color/PlantTypeId unchanged on ALL rows (must be 0) ===';
    SELECT COUNT(*) AS ChangedOtherColumns FROM #BeforeAll b JOIN dbo.PlantSpecies p ON p.Id = b.Id
    WHERE p.Name <> b.Name OR p.PlantTypeId <> b.PlantTypeId
       OR ISNULL(p.ScientificName, N'') <> ISNULL(b.ScientificName, N'')
       OR ISNULL(p.Color, N'') <> ISNULL(b.Color, N'');

    PRINT '=== ReadyStockDays changed ONLY on target rows (must be 0) ===';
    SELECT COUNT(*) AS ChangedOutsideTargets FROM #BeforeAll b JOIN dbo.PlantSpecies p ON p.Id = b.Id
    WHERE ISNULL(p.ReadyStockDays, -1) <> ISNULL(b.ReadyStockDays, -1)
      AND b.Id NOT IN (SELECT Id FROM @Updates);

    PRINT '=== Row set unchanged: rows added/removed (must be 0) ===';
    SELECT (SELECT COUNT(*) FROM #BeforeAll b WHERE NOT EXISTS (SELECT 1 FROM dbo.PlantSpecies p WHERE p.Id = b.Id))
         + (SELECT COUNT(*) FROM dbo.PlantSpecies p WHERE NOT EXISTS (SELECT 1 FROM #BeforeAll b WHERE b.Id = p.Id)) AS RowsAddedOrRemoved;

    PRINT '=== No duplicate names under PlantTypeId=4 (no rows = clean) ===';
    SELECT Name, COUNT(*) AS Occurrences FROM dbo.PlantSpecies WHERE PlantTypeId = 4 GROUP BY Name HAVING COUNT(*) > 1;

    PRINT '=== PlantTypes 1/2/3: counts (expect 28/78/18) and ReadyStockDays checksum vs before (must match) ===';
    SELECT a.PlantTypeId, a.SpeciesCount, a.DaysChecksumAfter, b.DaysChecksumBefore
    FROM (SELECT PlantTypeId, COUNT(*) AS SpeciesCount, CHECKSUM_AGG(CHECKSUM(ISNULL(ReadyStockDays, -1))) AS DaysChecksumAfter
          FROM dbo.PlantSpecies WHERE PlantTypeId IN (1,2,3) GROUP BY PlantTypeId) a
    JOIN (SELECT PlantTypeId, CHECKSUM_AGG(CHECKSUM(ISNULL(ReadyStockDays, -1))) AS DaysChecksumBefore
          FROM #BeforeAll WHERE PlantTypeId IN (1,2,3) GROUP BY PlantTypeId) b ON b.PlantTypeId = a.PlantTypeId
    ORDER BY a.PlantTypeId;

    PRINT '=== Total PlantSpecies (expect 413) and PlantTypeId=4 count (expect 289) ===';
    SELECT COUNT(*) AS TotalPlantSpecies, SUM(CASE WHEN PlantTypeId = 4 THEN 1 ELSE 0 END) AS SeasonalCount FROM dbo.PlantSpecies;

    PRINT '=== FK orphan check: rows in tables referencing dbo.PlantSpecies with no parent (must be 0) ===';
    DECLARE @orphans INT = 0, @sql NVARCHAR(MAX), @n INT;
    DECLARE fkc CURSOR LOCAL FAST_FORWARD FOR
        SELECT N'SELECT @n = COUNT(*) FROM ' + QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id))
             + N' c WHERE c.' + QUOTENAME(pc.name) + N' IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.PlantSpecies p WHERE p.' + QUOTENAME(rc.name) + N' = c.' + QUOTENAME(pc.name) + N')'
        FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns fkcol ON fkcol.constraint_object_id = fk.object_id
        JOIN sys.columns pc ON pc.object_id = fkcol.parent_object_id AND pc.column_id = fkcol.parent_column_id
        JOIN sys.columns rc ON rc.object_id = fkcol.referenced_object_id AND rc.column_id = fkcol.referenced_column_id
        WHERE fk.referenced_object_id = OBJECT_ID('dbo.PlantSpecies');
    OPEN fkc; FETCH NEXT FROM fkc INTO @sql;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC sp_executesql @sql, N'@n INT OUTPUT', @n = @n OUTPUT;
        SET @orphans += @n;
        FETCH NEXT FROM fkc INTO @sql;
    END
    CLOSE fkc; DEALLOCATE fkc;
    SELECT @orphans AS OrphanRows;

    PRINT '=== BEFORE / AFTER (Variety Name | PlantSpecies ID | Old | New | Source Days) ===';
    SELECT p.Name AS [Variety Name], p.Id AS [PlantSpecies ID],
           b.ReadyStockDays AS [Old ReadyStockDays], p.ReadyStockDays AS [New ReadyStockDays], u.NewDays AS [Source Days]
    FROM @Updates u
    JOIN dbo.PlantSpecies p ON p.Id = u.Id
    JOIN #BeforeAll b ON b.Id = u.Id
    ORDER BY p.Id;

    DROP TABLE #BeforeAll;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION UpdateSeasonalDays;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-09-28: user reviewed both dry runs (283 exact + 6 mapped = 289)
-- and explicitly approved committing this exact script. Verified backup:
-- PlantsIMS2_Test_PreSeasonalReadyStockDaysCommit_20260928_110157.bak
----------------------------------------------------------------------------
COMMIT TRANSACTION UpdateSeasonalDays;
GO


