-- ============================================================================
-- UpdateReadyStockDaysAndColors_2026-10-01.sql
--
-- Loads ReadyStockDays.xlsx (413 rows: Id, Variety, Sub-Veriety, ReadyStockDays,
-- Color) into dbo.PlantSpecies.ReadyStockDays / dbo.PlantSpecies.Color.
--
-- Source hierarchy: Excel "Variety" = dbo.PlantTypes.Name, Excel "Sub-Veriety" =
-- dbo.PlantSpecies.Name. No schema change: ReadyStockDays and Color already
-- exist as nullable columns on dbo.PlantSpecies (added in earlier phases).
--
-- MATCHING (performed in a separate analysis pass, not in this script):
--   - 396 rows matched exactly one PlantSpecies row by (PlantTypeId, Name)
--     after LTRIM/RTRIM + CR/LF-strip + case-normalisation.
--   - 10 rows (5 distinct names, all in CHRYSANTHEMUM) matched a PRE-EXISTING
--     duplicate pair of PlantSpecies rows sharing the same Name (not caused by
--     this task -- found already in the DB: Ids 24/3136 "Sharvari Purple",
--     26/3142 "Pramila Bronze", 30/3141 "Jayashree Bronze Red", 33/3144
--     "Aishwarya Yellow", 3115/3118 "SH-WR"). 4 of the 5 pairs had identical
--     Days/Color on both Excel occurrences; "Sharvari Purple" had conflicting
--     Days (22 vs 20) between its two Excel rows targeting two different-but-
--     empty DB rows -- user explicitly chose to apply 22 to both.
--   - 2 rows matched only after ignoring punctuation (parentheses): Excel
--     "Samui Pink" -> Id 3155 "Samui (Pink)"; Excel "Marigold Vanilla White"
--     -> Id 3434 "Marigold Vanilla (White)" (already correct, no-op).
--   - 5 rows had no name match at all; these are the exact same name-format
--     drift records already resolved by explicit user approval in a prior
--     session (see UpdateSeasonalVarietyReadyStockDays_2026-09-28.sql /
--     UpdateSeasonalVarietyColors_2026-09-27.sql): Ids 3426, 3667, 3681, 3718,
--     3750. Verified live: all 5 already carry the exact Days/Color this file
--     specifies -- no-op, included only for completeness/idempotency.
--   => All 413 Excel rows resolve to an existing PlantSpecies row. 0 new
--      species created, 0 rows inserted, 0 schema changes.
--
-- COLOR OVERWRITE NOTICE (explicit user decision, 2026-10-01): for 183 of the
-- 413 target rows (all under SEASONAL VARITIES / PlantTypeId=4), this file's
-- Color value conflicts with an already-curated Color set in a prior, separately
-- approved task (UpdateSeasonalVarietyColors_2026-09-27.sql). In nearly every
-- conflicting case this file's Color is simply the last word of the variety
-- name (e.g. "Petunia Red Star" -> old Color "Red", this file's Color "Star").
-- Flagged to the user before running; user explicitly chose to OVERWRITE with
-- this file's values rather than keep the prior curated ones. No silent
-- decision was made here.
--
-- Scope: dbo.PlantSpecies.ReadyStockDays and dbo.PlantSpecies.Color ONLY, for
-- the 413 Ids listed below. Name, ScientificName, PlantTypeId, and every other
-- table (stock, bookings, dispatches, sales, sowings, seed data, UserLoginHistory,
-- DailyReport) are untouched.
--
-- IDEMPOTENT: driven by a fixed (Id, ExpectedName, Days, Color) list; the
-- UPDATE only touches rows whose Days or Color actually differs, and an
-- ExpectedName check aborts rather than guess if a target row's name has
-- since changed. Re-running after a successful run updates 0 rows.
--
-- SAFETY:
--   * Guarded to PlantsIMS2_Test only.
--   * Runs inside BEGIN TRANSACTION.
--   * Ends with ROLLBACK TRANSACTION (dry run). Flip the final statement to
--     COMMIT TRANSACTION only after the user has reviewed this dry run's
--     verification output and explicitly approved committing.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 51001, 'UpdateReadyStockDaysAndColors_2026-10-01.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION UpdateReadyStockDaysAndColors;

BEGIN TRY

    DECLARE @Updates TABLE (Id INT PRIMARY KEY, ExpectedName NVARCHAR(200) NOT NULL, NewDays INT NOT NULL, NewColor NVARCHAR(100) NOT NULL);
    INSERT INTO @Updates (Id, ExpectedName, NewDays, NewColor) VALUES
    (1, N'Ganesha yellow', 22, N'yellow'),
    (2, N'Kalkatta Orange', 22, N'Orange'),
    (3, N'Kalkatta yellow', 22, N'yellow'),
    (4, N'Maha Vishnu yellow', 22, N'yellow'),
    (5, N'Amber yellow', 22, N'yellow'),
    (6, N'Bharani Orange', 22, N'Orange'),
    (7, N'Keshar Orange', 22, N'Orange'),
    (8, N'Laxmi Orange', 22, N'Orange'),
    (9, N'maha Vishnu Orange', 22, N'Orange'),
    (10, N'Mallica Orange', 22, N'Orange'),
    (11, N'Pari Orange', 22, N'Orange'),
    (12, N'Sai Super Orange', 22, N'Orange'),
    (13, N'Surya white', 22, N'white'),
    (14, N'New Kapari', 22, N'Kapari'),
    (15, N'Sai super yellow', 22, N'yellow'),
    (16, N'Vishnu yellow', 20, N'yellow'),
    (17, N'Bhagyshree White', 20, N'White'),
    (18, N'Bhagyshree Plus', 20, N'Plus'),
    (19, N'Bhagyshree Yellow', 20, N'Yellow'),
    (20, N'Bhagyshree Red', 20, N'Red'),
    (21, N'Bhagyshree Pink', 20, N'Pink'),
    (22, N'Pramila Yellow', 20, N'Yellow'),
    (23, N'Mansi Bronze', 20, N'Bronze'),
    (24, N'Sharvari Purple', 22, N'Purple'),
    (25, N'Suvrna Yellow', 22, N'Yellow'),
    (26, N'Pramila Bronze', 20, N'Bronze'),
    (27, N'Madhuri Yellow', 20, N'Yellow'),
    (28, N'Aditi Red', 20, N'Red'),
    (29, N'Ishwari Yellow', 20, N'Yellow'),
    (30, N'Jayashree Bronze red', 20, N'Red'),
    (32, N'Shanka White', 18, N'White'),
    (33, N'Aishwarya Yellow', 20, N'Yellow'),
    (34, N'Suvidha White', 20, N'White'),
    (35, N'Chandni Pink', 20, N'Pink'),
    (36, N'Snehal White', 20, N'White'),
    (37, N'Priya White Pink', 20, N'Pink'),
    (38, N'Shanka Pink', 20, N'Pink'),
    (39, N'Marry Orange', 20, N'Orange'),
    (40, N'Sona Green', 20, N'Green'),
    (41, N'Shanka Purple', 20, N'Purple'),
    (42, N'Sharvari Red', 20, N'Red'),
    (43, N'Aishwarya Pink', 20, N'Pink'),
    (44, N'Meghana Orange', 20, N'Orange'),
    (45, N'Madhuri White', 20, N'White'),
    (46, N'Sent White', 20, N'White'),
    (47, N'Sent Yellow', 20, N'Yellow'),
    (48, N'Zinnia Scarlet', 20, N'Scarlet'),
    (51, N'Jayshree Bronze 2', 20, N'Bronze'),
    (1051, N'Vishnu Yellow Plus', 20, N'yellow'),
    (1052, N'Zinnia Mix', 20, N'Mix'),
    (1053, N'Zinnia Wine', 20, N'Wine'),
    (1054, N'Zinnia Deep Red', 20, N'Red'),
    (1055, N'Zinnia White', 20, N'White'),
    (1056, N'Zinnia Purple', 20, N'Purple'),
    (1057, N'Zinnia Coral', 20, N'Coral'),
    (1058, N'Zinnia Lime', 20, N'Lime'),
    (1059, N'Zinnia Lilac', 20, N'Lilac'),
    (1060, N'Zinnia Orange', 20, N'Orange'),
    (1061, N'Zinnia Golden Yellow', 20, N'Yellow'),
    (1062, N'Zinnia Bright Pink', 20, N'Pink'),
    (1063, N'Zinnia Salmon Rose', 20, N'Rose'),
    (1064, N'Zinna Carmine', 20, N'Carmine'),
    (2052, N'Shanka Plus', 20, N'Plus'),
    (2054, N'Chintamani Orange', 20, N'Orange'),
    (2055, N'Ishwari Purple', 20, N'Purple'),
    (3069, N'Petunia Red Star', 32, N'Star'),
    (3070, N'Petunia Crimson Star', 32, N'Star'),
    (3071, N'Petunia Blue Star', 32, N'Star'),
    (3072, N'Petunia Hulahoop Mix', 32, N'Mix'),
    (3073, N'Petunia Brovo Mix', 32, N'Mix'),
    (3074, N'Petunia Rose Star', 32, N'Star'),
    (3075, N'Celosia Raven Red', 25, N'Red'),
    (3076, N'Celosia Ice cream Pink', 25, N'Pink'),
    (3077, N'Celosia Ice cream Cherry', 25, N'Cherry'),
    (3078, N'Celosia Ice cream Orange', 25, N'Orange'),
    (3079, N'Celosia Cristata Mad. Magenta', 25, N'Magenta'),
    (3080, N'Celosia Ice cream Yellow', 25, N'Yellow'),
    (3081, N'Platycodon Popstar White', 39, N'White'),
    (3082, N'Platycodon Popstar Blue', 39, N'Blue'),
    (3083, N'Platycodon Popstar Pink', 39, N'Pink'),
    (3084, N'Vinca Pacifica Mix', 32, N'Mix'),
    (3085, N'Salvia Mojave Red', 28, N'Red'),
    (3086, N'Mg. Hot Pak mix', 15, N'mix'),
    (3087, N'Cosmos S. Dwarf I. Scarlet', 15, N'Scarlet'),
    (3088, N'Cosmos S. Dwarf I. Yellow', 15, N'Yellow'),
    (3089, N'Cosmos S. Dwarf I. Orange', 15, N'Orange'),
    (3090, N'M.G Marvel Gold', 20, N'Gold'),
    (3091, N'M.G Big Top Yellow', 20, N'Yellow'),
    (3092, N'M.G Big Top Orange', 20, N'Orange'),
    (3093, N'M.G Big Top Gold', 20, N'Gold'),
    (3094, N'Gomphrena Buddy Purple', 25, N'Purple'),
    (3095, N'Celosia Cristata Raven Red', 25, N'Red'),
    (3096, N'Begonia Sprint Plus Mix', 39, N'Mix'),
    (3097, N'Begonia Cocktail Mix', 39, N'Mix'),
    (3098, N'Hypoestes Splash Rose', 25, N'Rose'),
    (3099, N'Cosmos Mix', 15, N'Mix'),
    (3100, N'Hypoestes Splash White', 25, N'White'),
    (3101, N'Hypoestes Splash Red', 25, N'Red'),
    (3102, N'Hypoestes Splash Pink', 25, N'Pink'),
    (3103, N'Zinnia Scarlet Elegans', 22, N'Elegans'),
    (3104, N'Gerbera Meg Revolution Mix', 46, N'Mix'),
    (3105, N'Antirrhinum Tulinny Mix', 35, N'Mix'),
    (3106, N'Antirrhinum Snappy Mix', 32, N'Mix'),
    (3107, N'Celosia Glorious Mix', 25, N'Mix'),
    (3108, N'Verbena Apex Mix', 32, N'Mix'),
    (3109, N'Impatiens Imara Mix', 32, N'Mix'),
    (3110, N'Impatiens Accent Star Mix', 32, N'Mix'),
    (3111, N'Salvia Vista Mix', 28, N'Mix'),
    (3112, N'Torenia Fournieri Mix', 28, N'Mix'),
    (3113, N'Aster Callistephus Chinensis mix', 30, N'mix'),
    (3114, N'SW-R', 20, N'SW-R'),
    (3115, N'SH-WR', 20, N'SH-WR'),
    (3117, N'US-WR', 20, N'US-WR'),
    (3118, N'SH-WR', 20, N'SH-WR'),
    (3123, N'Zinnia Zydeco Mix', 20, N'Mix'),
    (3124, N'Zinnia Zesty Purple Mix', 20, N'Mix'),
    (3125, N'Zinnia Zahara Dbl Mix', 20, N'Mix'),
    (3126, N'Salvia F. White', 28, N'White'),
    (3127, N'Vinca Bold Mix', 32, N'Mix'),
    (3129, N'Zinnia Zesty Mix', 20, N'Mix'),
    (3131, N'Celosia Armor mix', 25, N'mix'),
    (3132, N'Galardia Mix', 35, N'Mix'),
    (3133, N'Salvia Red Hot', 28, N'Hot'),
    (3134, N'M.G Hot pack mix', 15, N'mix'),
    (3135, N'Dahlia Figaro Mix', 25, N'Mix'),
    (3136, N'Sharvari Purple', 22, N'Purple'),
    (3137, N'Bhagyashree Pink', 20, N'Pink'),
    (3138, N'Gompie Pink', 20, N'Pink'),
    (3139, N'Gompie White', 20, N'White'),
    (3140, N'Wendy Bronze', 20, N'Bronze'),
    (3141, N'Jayashree Bronze Red', 20, N'Red'),
    (3142, N'Pramila Bronze', 20, N'Bronze'),
    (3143, N'Gompie Bronze', 20, N'Bronze'),
    (3144, N'Aishwarya Yellow', 20, N'Yellow'),
    (3145, N'Bhagyashree Yellow TC', 20, N'TC'),
    (3146, N'Bhagyashree White TC', 20, N'TC'),
    (3147, N'Meghana Orange TC', 20, N'TC'),
    (3148, N'Jayashree Bronze Red TC', 20, N'TC'),
    (3149, N'Pornima White', 20, N'White'),
    (3150, N'Chilli Sitara', 25, N'Sitara'),
    (3153, N'Vido Red', 20, N'Red'),
    (3154, N'Vido Purple', 20, N'Purple'),
    (3155, N'Samui (Pink)', 20, N'Pink'),
    (3156, N'Smola White', 20, N'White'),
    (3157, N'Vinca Cora XDR Mix', 32, N'Mix'),
    (3158, N'Kalpataru White', 20, N'White'),
    (3159, N'Vinca Pacifica Punch', 32, N'Punch'),
    (3160, N'Petunia Burgundy Star', 32, N'Star'),
    (3161, N'Bijali', 30, N'Bijali'),
    (3162, N'Antirrhinum Snapshot Mix', 35, N'Mix'),
    (3163, N'Dianthus Ideal Mix', 32, N'Mix'),
    (3164, N'Salvia Sahara Red', 28, N'Red'),
    (3165, N'Dianthus F1 Telstar Mix', 32, N'Mix'),
    (3166, N'Cineraria Early Perfection Mix', 30, N'Mix'),
    (3167, N'Verbena Quartz Mix', 32, N'Mix'),
    (3168, N'Gazania Zanny Mix', 32, N'Mix'),
    (3169, N'Gazania New Day Mix', 32, N'Mix'),
    (3170, N'OF.3', 20, N'OF.3'),
    (3171, N'PC11', 20, N'PC11'),
    (3172, N'PC8', 20, N'PC8'),
    (3173, N'PC9', 20, N'PC9'),
    (3174, N'MW-R', 20, N'MW-R'),
    (3175, N'NK-WR', 20, N'NK-WR'),
    (3176, N'Cabbage Scarlet', 20, N'Scarlet'),
    (3177, N'Brocoli Shishir', 20, N'Shishir'),
    (3178, N'Viola Wittrokiaha Mix', 32, N'Mix'),
    (3179, N'Basil', 20, N'Basil'),
    (3180, N'Celosia Glorious Red', 25, N'Red'),
    (3181, N'Celosia Glorious Pink', 25, N'Pink'),
    (3182, N'Celosia Glorious Yellow', 25, N'Yellow'),
    (3183, N'Gompie Yellow', 20, N'Yellow'),
    (3184, N'Gompie Red', 20, N'Red'),
    (3185, N'Beppie Yellow', 20, N'Yellow'),
    (3186, N'Beppie White', 20, N'White'),
    (3187, N'Gompie purple', 20, N'purple'),
    (3188, N'Salvia F. Fairy Queen', 28, N'Queen'),
    (3189, N'Salvia F. Evalution Violet', 28, N'Violet'),
    (3190, N'Salvia F. Victoria Blue', 28, N'Blue'),
    (3191, N'Salvia X Superba Adorba Blue', 28, N'Blue'),
    (3192, N'Salvia Sup. Blue Queen', 28, N'Queen'),
    (3193, N'Celosia Icecream Salmon', 25, N'Salmon'),
    (3194, N'Tulsi', 20, N'Tulsi'),
    (3195, N'Celosia Cristata T. Mix', 25, N'Mix'),
    (3196, N'vijayata Orange', 20, N'Orange'),
    (3197, N'Super Shree Orange', 20, N'Orange'),
    (3198, N'Super Shree Yellow', 20, N'Yellow'),
    (3199, N'Chintamani Yellow', 20, N'Yellow'),
    (3200, N'Lakhani Yellow', 20, N'Yellow'),
    (3201, N'Surya Yellow', 20, N'Yellow'),
    (3202, N'Aditi White', 20, N'White'),
    (3203, N'Aditi Yellow', 20, N'Yellow'),
    (3204, N'Aditi Pink', 20, N'Pink'),
    (3205, N'Aditi Purple', 20, N'Purple'),
    (3253, N'Keshar Orange Plus', 20, N'Plus'),
    (3254, N'Mallica Orange Plus', 20, N'Plus'),
    (3255, N'Kabutar White', 20, N'White'),
    (3256, N'Ambara New', 20, N'New'),
    (3257, N'Ping Pong Red', 20, N'Red'),
    (3258, N'Ping Pong Pink', 20, N'Pink'),
    (3259, N'Ping Pong Green', 20, N'Green'),
    (3260, N'Ping Pong White', 20, N'White'),
    (3261, N'Ping Pong Yellow', 20, N'Yellow'),
    (3262, N'Ping Pong Purple', 20, N'Purple'),
    (3383, N'Alyssum Fields Mix', 30, N'Mix'),
    (3384, N'Antirrhinum Chimes Mix', 35, N'Mix'),
    (3385, N'Aster New Double Mix', 30, N'Mix'),
    (3388, N'Flowering Cabbage Ornamental Kale', 30, N'Kale'),
    (3390, N'Celosia Plumosa Kimono Mix', 25, N'Mix'),
    (3391, N'Celosia Cristata Amigo Mix', 25, N'Mix'),
    (3392, N'Chrysanthemum Deconova Multiflora', 25, N'Multiflora'),
    (3393, N'Chrysanthemum Deconova Grandiflora', 25, N'Grandiflora'),
    (3394, N'Cineraria Silver Dust', 30, N'Dust'),
    (3398, N'Carnation Lilliput Mix', 40, N'Mix'),
    (3399, N'Dahlia Big Flowers Mix', 25, N'Mix'),
    (3400, N'Dahlia Fresco Mix', 25, N'Mix'),
    (3401, N'Dianthus Super Parfait Series', 32, N'Series'),
    (3402, N'Gloxinia Double Brocade Mix', 46, N'Mix'),
    (3404, N'Gerbera Mega Revolution', 46, N'Revolution'),
    (3405, N'Gaillardia Seedling', 32, N'Seedling'),
    (3407, N'Geranium Super', 39, N'Super'),
    (3408, N'Hypoestes Splash Mix', 25, N'Mix'),
    (3409, N'Impatiens walleriana Imara XDR Mix', 32, N'Mix'),
    (3410, N'Impatiens New Guinea', 32, N'Guinea'),
    (3411, N'Kochia Burning Blush', 25, N'Blush'),
    (3412, N'Lobelia Crystal', 32, N'Crystal'),
    (3413, N'Melampodium Lemon Delight', 25, N'Delight'),
    (3414, N'Osteospermum Mix', 32, N'Mix'),
    (3415, N'Ornamental Chilli Dwarf Mix', 32, N'Mix'),
    (3416, N'Petunia Double Double Duo Series', 32, N'Series'),
    (3418, N'Petunia Grandiflora Ultra Star Mix', 32, N'Mix'),
    (3420, N'Petunia Grandiflora Colourwise', 32, N'Colourwise'),
    (3424, N'Platycodon Pop Star Mix', 39, N'Mix'),
    (3425, N'Portulaca Happy Hour', 22, N'Hour'),
    (3426, N'Pansy Super Majestic Giants 2', 32, N'Mix'),
    (3432, N'Sunflower Teddy Bear', 18, N'Bear'),
    (3434, N'Marigold Vanilla (White)', 22, N'White'),
    (3435, N'Marigold French Hot Pack Mix', 22, N'Mix'),
    (3438, N'Vinca Pacifica Red', 32, N'Red'),
    (3440, N'Vinca Valiant Mix', 32, N'Mix'),
    (3442, N'Zinnia Dreamland Mix', 22, N'Mix'),
    (3443, N'Zinnia New Preciosa Mix', 22, N'Mix'),
    (3446, N'Celosia Arrabona', 25, N'Arrabona'),
    (3448, N'Cosmos Casanova', 22, N'Casanova'),
    (3450, N'Gazania Enorma', 32, N'Enorma'),
    (3451, N'Impatiens Balance', 32, N'Balance'),
    (3455, N'Marigold French Chica', 22, N'Chica'),
    (3461, N'Viola Sorbet XP', 32, N'XP'),
    (3462, N'Pentas Lucky Star', 39, N'Star'),
    (3466, N'Torenia Kauai', 28, N'Kauai'),
    (3471, N'Calendula Calypso', 25, N'Calypso'),
    (3475, N'Gerbera Majorette', 46, N'Majorette'),
    (3477, N'Hypoestes Confetti Compact', 25, N'Compact'),
    (3480, N'Petunia Puffin', 32, N'Puffin'),
    (3484, N'Begonia Fiona', 39, N'Fiona'),
    (3485, N'Calendula Zen', 25, N'Zen'),
    (3487, N'Coleus Giant Exhibition', 25, N'Exhibition'),
    (3488, N'Dianthus Telstar', 32, N'Telstar'),
    (3489, N'Gomphrena Audray', 25, N'Audray'),
    (3490, N'Lobelia Aqua', 32, N'Aqua'),
    (3499, N'Viola Penny', 32, N'Penny'),
    (3502, N'Impatiens Imara XDR', 32, N'XDR'),
    (3655, N'Begonia Semperflorens Bada Boom', 40, N'Boom'),
    (3656, N'Begonia Semperflorens Big Mix', 40, N'Mix'),
    (3657, N'Begonia tuberous Nonstop', 45, N'Nonstop'),
    (3658, N'Begonia tuberous Mocca Mix', 45, N'Mix'),
    (3659, N'Coleus Wizard', 25, N'Wizard'),
    (3660, N'Coleus Fareway Mix', 25, N'Mix'),
    (3661, N'Coleus Kong', 25, N'Kong'),
    (3662, N'Coleus Jumbo', 25, N'Jumbo'),
    (3663, N'Cosmos Kosmik', 20, N'Kosmik'),
    (3664, N'Cosmos Ladybird Mix', 20, N'Mix'),
    (3665, N'Gazania Day Break', 32, N'Break'),
    (3666, N'Gazania Kiss Mix', 32, N'Mix'),
    (3667, N'Geranium Ringo 2000', 39, N'Mix'),
    (3668, N'Geranium Bulls Eye Mix', 39, N'Mix'),
    (3669, N'Petunia Grandiflora Bravo', 32, N'Bravo'),
    (3670, N'Petunia Grandiflora Supercascade Formula Mix', 32, N'Mix'),
    (3671, N'Petunia Grandiflora Frost', 32, N'Frost'),
    (3672, N'Petunia Grandiflora Picotee Mix', 32, N'Mix'),
    (3673, N'Petunia Trailing Easy Wave', 32, N'Wave'),
    (3674, N'Petunia Trailing Ramblin', 32, N'Ramblin'),
    (3675, N'Pentas Butterfly', 39, N'Butterfly'),
    (3676, N'Pentas Starla', 39, N'Starla'),
    (3677, N'Phlox Twinkle', 25, N'Twinkle'),
    (3678, N'Phlox Promise Mix', 25, N'Mix'),
    (3679, N'Rudbeckia Rustic', 32, N'Rustic'),
    (3680, N'Rudbeckia Toto', 32, N'Toto'),
    (3681, N'Salvia Flamex 2000', 28, N'Mix'),
    (3682, N'Salvia Vista Red', 28, N'Red'),
    (3683, N'Salvia Salsa', 28, N'Salsa'),
    (3684, N'Strawberry Sweet Charley', 39, N'Charley'),
    (3685, N'Strawberry Winter Down', 39, N'Down'),
    (3686, N'Stock Dwarf Mime', 28, N'Mime'),
    (3687, N'Stock Dwarf Hot Cakes Mix', 28, N'Mix'),
    (3688, N'Torenia Little Kiss Mix', 28, N'Mix'),
    (3689, N'Torenia Kauai Mix', 28, N'Mix'),
    (3690, N'Verbena Obsession', 32, N'Obsession'),
    (3691, N'Vinca Tattoo Series', 32, N'Series'),
    (3692, N'Zinnia Small Zahara', 22, N'Zahara'),
    (3693, N'Zinnia Small Zydeco', 22, N'Zydeco'),
    (3694, N'Zinnia Small Profusion Double', 22, N'Double'),
    (3695, N'Antirrhinum Snappy', 32, N'Snappy'),
    (3696, N'Antirrhinum Twinny', 32, N'Twinny'),
    (3697, N'Antirrhinum DoubleShot', 32, N'DoubleShot'),
    (3698, N'Vinca Solar', 32, N'Solar'),
    (3699, N'Vinca Solar Avalanche', 32, N'Avalanche'),
    (3700, N'Vinca Heatwave', 32, N'Heatwave'),
    (3701, N'Cineraria Quicksilver', 32, N'Quicksilver'),
    (3702, N'Cineraria Silverado', 32, N'Silverado'),
    (3703, N'Dianthus Diana', 32, N'Diana'),
    (3704, N'Dianthus Divinity', 32, N'Divinity'),
    (3705, N'Dianthus Chiba', 32, N'Chiba'),
    (3706, N'Dianthus Supra', 32, N'Supra'),
    (3707, N'Dianthus Elegance', 32, N'Elegance'),
    (3708, N'Geranium Nano', 39, N'Nano'),
    (3709, N'Geranium Apache', 39, N'Apache'),
    (3710, N'Petunia Limbo GP', 32, N'GP'),
    (3711, N'Petunia Mambo GP', 32, N'GP'),
    (3712, N'Petunia Shake', 32, N'Shake'),
    (3713, N'Petunia Tango', 32, N'Tango'),
    (3714, N'Petunia Lambada', 32, N'Lambada'),
    (3715, N'Salvia Zenith', 28, N'Zenith'),
    (3716, N'Salvia Reddy', 28, N'Reddy'),
    (3717, N'Salvia Amore', 28, N'Amore'),
    (3718, N'Salvia Red Alert', 28, N'Red'),
    (3719, N'Salvia Red Hill', 28, N'Hill'),
    (3720, N'Verbena Purple Haze', 32, N'Haze'),
    (3721, N'Verbena Dazzling Nights', 32, N'Nights'),
    (3722, N'Viola Corina', 32, N'Corina'),
    (3723, N'Viola Cello', 32, N'Cello'),
    (3724, N'Viola Xtrada', 32, N'Xtrada'),
    (3725, N'Viola Trumpet', 32, N'Trumpet'),
    (3726, N'Petunia Wave', 32, N'Wave'),
    (3727, N'Petunia Easy Wave', 32, N'Wave'),
    (3728, N'Petunia Shock Wave', 32, N'Wave'),
    (3729, N'Petunia Tidal Wave', 32, N'Wave'),
    (3730, N'Petunia Ez Rider', 32, N'Rider'),
    (3731, N'Petunia Dreams', 32, N'Dreams'),
    (3732, N'Petunia Daddy', 32, N'Daddy'),
    (3733, N'Petunia Double Madness', 32, N'Madness'),
    (3734, N'Vinca Pacifica XP', 32, N'XP'),
    (3735, N'Vinca Tattoo', 32, N'Tattoo'),
    (3736, N'Vinca Titan', 32, N'Titan'),
    (3737, N'Vinca Valiant', 32, N'Valiant'),
    (3738, N'Pansy Cool Wave', 32, N'Wave'),
    (3739, N'Pansy Matrix', 32, N'Matrix'),
    (3740, N'Pansy Panola', 32, N'Panola'),
    (3741, N'Coleus Fairway', 25, N'Fairway'),
    (3742, N'Dianthus Ideal Select', 32, N'Select'),
    (3743, N'Dianthus Super Parfait', 32, N'Parfait'),
    (3744, N'Zinnia Zahara', 22, N'Zahara'),
    (3745, N'Zinnia Double Zahara', 22, N'Zahara'),
    (3746, N'Marigold Inca II', 22, N'II'),
    (3747, N'Marigold Antigua', 22, N'Antigua'),
    (3748, N'Marigold Durango', 22, N'Durango'),
    (3749, N'Salvia Vista', 28, N'Vista'),
    (3750, N'Begonia Semperflorens Ambassador', 39, N'Mix'),
    (3751, N'Begonia Semperflorens Senator IQ', 39, N'IQ'),
    (3752, N'Begonia Semperflorens Emperor', 39, N'Emperor'),
    (3753, N'Begonia Viking', 39, N'Viking'),
    (3754, N'Begonia Viking XL', 39, N'XL'),
    (3755, N'Celosia Kimono', 25, N'Kimono'),
    (3756, N'Celosia Century', 25, N'Century'),
    (3757, N'Celosia Dragon''s Breath', 25, N'Breath'),
    (3758, N'Celosia Flamma', 25, N'Flamma'),
    (3759, N'Coleus Rainbow', 25, N'Rainbow'),
    (3760, N'Dianthus Diamond', 32, N'Diamond'),
    (3761, N'Dianthus Lillipot', 32, N'Lillipot'),
    (3762, N'Gomphrena Gnome', 25, N'Gnome'),
    (3763, N'Gomphrena Pinball', 25, N'Pinball'),
    (3764, N'Gomphrena Ping Pong', 25, N'Pong'),
    (3765, N'Marigold Proud Mari', 22, N'Mari'),
    (3766, N'Marigold Coco', 22, N'Coco'),
    (3767, N'Pansy Crown', 32, N'Crown'),
    (3768, N'Pansy Ultima', 32, N'Ultima'),
    (3769, N'Vinca Victory', 32, N'Victory'),
    (3770, N'Vinca Virtuosa', 32, N'Virtuosa'),
    (3771, N'Zinnia Profusion', 22, N'Profusion'),
    (3772, N'Zinnia Profusion Double', 22, N'Double'),
    (3773, N'Antirrhinum Chantilly', 32, N'Chantilly'),
    (3774, N'Antirrhinum Legend Double', 32, N'Double'),
    (3775, N'Antirrhinum Statement', 32, N'Statement'),
    (3776, N'Celosia Armor', 25, N'Armor'),
    (3777, N'Celosia Castle', 25, N'Castle'),
    (3778, N'Petunia Evening Scentsation', 32, N'Scentsation'),
    (3779, N'Petunia Opera Supreme', 32, N'Supreme'),
    (3780, N'Petunia Trilogy', 32, N'Trilogy'),
    (3781, N'Salvia Hummingbird', 28, N'Hummingbird'),
    (3782, N'Salvia Summer Jewel', 28, N'Jewel'),
    (3783, N'Sunflower Big Smile', 18, N'Smile'),
    (3784, N'Sunflower Smiley', 18, N'Smiley'),
    (3785, N'Sunflower Sunny Smile', 18, N'Smile'),
    (3786, N'Zinnia Dreamland', 22, N'Dreamland'),
    (3787, N'Zinnia Preciosa', 22, N'Preciosa'),
    (3788, N'Zinnia Belize', 22, N'Belize'),
    (3789, N'Vinca Cora XDR', 32, N'XDR'),
    (3790, N'Vinca Blockbuster', 32, N'Blockbuster'),
    (3791, N'Pentas BeeBright', 39, N'BeeBright'),
    (3792, N'Pentas Beehive', 39, N'Beehive'),
    (3793, N'Petunia Ramblin', 32, N'Ramblin'),
    (3794, N'Petunia Picobella', 32, N'Picobella'),
    (3795, N'Petunia Sanguna seed lines', 32, N'lines'),
    (3796, N'Pansy Delta', 32, N'Delta'),
    (3797, N'Pansy Colossus', 32, N'Colossus'),
    (3798, N'Marigold Marvel', 22, N'Marvel'),
    (3799, N'Marigold Bonanza', 22, N'Bonanza'),
    (3800, N'Salvia Sahara', 28, N'Sahara'),
    (3801, N'Salvia Mojave', 28, N'Mojave'),
    (3802, N'Calendula officinalis Calypso Orange', 25, N'Orange'),
    (3803, N'Calendula officinalis Calypso Yellow', 25, N'Yellow'),
    (3804, N'Marigold Inca Orange', 22, N'Orange'),
    (3805, N'Marigold Inca Yellow', 22, N'Yellow'),
    (3806, N'Marigold Inca Gold', 22, N'Gold');

    IF (SELECT COUNT(*) FROM @Updates) <> 413
        THROW 51002, 'Expected exactly 413 (Id, Days, Color) rows -- mismatch, aborting.', 1;

    -- Every target must still exist with the expected (CR/LF- and whitespace-
    -- normalized) name. Abort rather than guess if anything has moved.
    IF (SELECT COUNT(*) FROM @Updates u JOIN dbo.PlantSpecies p ON p.Id = u.Id
        WHERE LOWER(LTRIM(RTRIM(REPLACE(REPLACE(p.Name, CHAR(13), N''), CHAR(10), N'')))) = LOWER(LTRIM(RTRIM(u.ExpectedName)))) <> 413
        THROW 51003, 'A target Id is missing or its Name has changed since this script was generated -- aborting rather than guess.', 1;

    -- Full before-snapshot of the whole table (all types) for the unchanged checks
    SELECT Id, PlantTypeId, Name, ScientificName, ReadyStockDays, Color INTO #BeforeAll FROM dbo.PlantSpecies;

    DECLARE @ExpectedChanges INT = (SELECT COUNT(*) FROM @Updates u JOIN dbo.PlantSpecies p ON p.Id = u.Id
        WHERE ISNULL(p.ReadyStockDays, -1) <> u.NewDays OR ISNULL(p.Color, N'') <> u.NewColor);

    ----------------------------------------------------------------------
    -- THE UPDATE -- ReadyStockDays + Color only, changed rows only
    ----------------------------------------------------------------------
    UPDATE p
        SET p.ReadyStockDays = u.NewDays,
            p.Color = u.NewColor
    FROM dbo.PlantSpecies p
    JOIN @Updates u ON u.Id = p.Id
    WHERE ISNULL(p.ReadyStockDays, -1) <> u.NewDays OR ISNULL(p.Color, N'') <> u.NewColor;

    DECLARE @RowsUpdated INT = @@ROWCOUNT;
    IF @RowsUpdated <> @ExpectedChanges
        THROW 51004, 'UPDATE row count differs from the expected number of changed rows -- aborting.', 1;

    ----------------------------------------------------------------------
    -- VERIFICATION
    ----------------------------------------------------------------------
    PRINT '=== Summary ===';
    SELECT (SELECT COUNT(*) FROM @Updates) AS TargetRows, @RowsUpdated AS RowsUpdated,
           (SELECT COUNT(*) FROM @Updates) - @RowsUpdated AS RowsAlreadyCorrect;

    PRINT '=== Every target row now has exactly the source Days+Color (MismatchCount must be 0) ===';
    SELECT COUNT(*) AS MismatchCount FROM dbo.PlantSpecies p JOIN @Updates u ON u.Id = p.Id
    WHERE ISNULL(p.ReadyStockDays, -1) <> u.NewDays OR ISNULL(p.Color, N'') <> u.NewColor;

    PRINT '=== Name/ScientificName/PlantTypeId unchanged on ALL rows (must be 0) ===';
    SELECT COUNT(*) AS ChangedOtherColumns FROM #BeforeAll b JOIN dbo.PlantSpecies p ON p.Id = b.Id
    WHERE p.Name <> b.Name OR p.PlantTypeId <> b.PlantTypeId
       OR ISNULL(p.ScientificName, N'') <> ISNULL(b.ScientificName, N'');

    PRINT '=== ReadyStockDays/Color changed ONLY on target rows (must be 0) ===';
    SELECT COUNT(*) AS ChangedOutsideTargets FROM #BeforeAll b JOIN dbo.PlantSpecies p ON p.Id = b.Id
    WHERE (ISNULL(p.ReadyStockDays, -1) <> ISNULL(b.ReadyStockDays, -1) OR ISNULL(p.Color, N'') <> ISNULL(b.Color, N''))
      AND b.Id NOT IN (SELECT Id FROM @Updates);

    PRINT '=== Row set unchanged: rows added/removed (must be 0) ===';
    SELECT (SELECT COUNT(*) FROM #BeforeAll b WHERE NOT EXISTS (SELECT 1 FROM dbo.PlantSpecies p WHERE p.Id = b.Id))
         + (SELECT COUNT(*) FROM dbo.PlantSpecies p WHERE NOT EXISTS (SELECT 1 FROM #BeforeAll b WHERE b.Id = p.Id)) AS RowsAddedOrRemoved;

    PRINT '=== PlantType breakdown after (expect 28/78/18/292 for Ids 1/2/3/4) ===';
    SELECT pt.Id, pt.Name, COUNT(*) AS SpeciesCount,
           SUM(CASE WHEN p.ReadyStockDays IS NULL THEN 1 ELSE 0 END) AS StillNullDays,
           SUM(CASE WHEN p.Color IS NULL THEN 1 ELSE 0 END) AS StillNullColor
    FROM dbo.PlantSpecies p JOIN dbo.PlantTypes pt ON pt.Id = p.PlantTypeId
    GROUP BY pt.Id, pt.Name ORDER BY pt.Id;

    PRINT '=== Total PlantSpecies unchanged (expect 416) ===';
    SELECT COUNT(*) AS TotalPlantSpecies FROM dbo.PlantSpecies;

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

    DROP TABLE #BeforeAll;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION UpdateReadyStockDaysAndColors;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-10-01: user reviewed the dry run (413 targets, 307 changed,
-- 106 already correct, 0 mismatches, 0 rows added/removed, 416 total
-- PlantSpecies, 0 FK orphans) twice and explicitly approved committing.
-- Verified backup: PlantsIMS2_Test_PreReadyStockDaysColors_20261001.bak
----------------------------------------------------------------------------
COMMIT TRANSACTION UpdateReadyStockDaysAndColors;
GO
