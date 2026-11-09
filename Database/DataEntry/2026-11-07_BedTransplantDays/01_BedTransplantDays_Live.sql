-- ============================================================================
-- 01_BedTransplantDays_Live.sql   (2026-11-09, for LIVE srv1202245)
--
-- Standalone copy of 01_BedTransplantDays.sql (same workbook rows, same name rule, same plan) with the full
-- TARGET_* guards. Derived from 01_BedTransplantDays.sql SHA-256 292932BE49B53AF6B7F9007C6B0D273709F1A8F7DD1B98C79C26D3E991DEE5B1.
-- Source workbook : BedSowing_ReadyDate.xlsx  SHA-256 5F3251AD6F2A715CA24D3A47347D69BA7A4C1AD014D930445FA48500870AAF8F
-- Varieties       : 355 workbook rows with days.
--
-- Sets dbo.PlantSpecies.BedTransplantDays (Bed Sowing Alerts: Transplant Due Date = bed Sowing Date + these days).
-- Matched by EXACT NAME with Add Variety's rule (capitals ignored, extra spaces collapsed; spelling and punctuation
-- are NOT ignored -- nothing is guessed):
--   SET              the variety has no days yet
--   CHANGE           the variety has different days (the workbook wins)
--   SAME             already this value -- untouched
--   NOT IN DATABASE  no active variety with that name -- skipped and listed
--   INACTIVE ONLY    only a retired (IsActive = 0) variety has that name -- skipped and listed
--   ERROR            the name matches 2+ active varieties -- the whole run stops, nothing written
-- Varieties NOT in the workbook keep whatever they have (never cleared). No stock, bed or ledger row is touched.
--
-- MODE=PREVIEW  prints the plan and the counts, performs the update inside a transaction, verifies it and ROLLS BACK.
-- MODE=COMMIT   the same, then COMMITs. One transaction; any error rolls everything back.
--               Only with the owner's explicit approval for that database.
--
-- TARGET SAFEGUARDS -- the first batch refuses to continue unless ALL of these hold:
--     1. DB_NAME() is PlantsIMS2_Test or PlantsIMS2_Scratch_*      2. MODE is PREVIEW or COMMIT (exact upper case)
--     3. @@SERVERNAME = $(TARGET_SERVER)                            4. SERVERPROPERTY('ServerName') = @@SERVERNAME
--     5. SERVERPROPERTY('MachineName') = $(TARGET_MACHINE)          6. DB_NAME() = $(TARGET_DB)
--     7. sys.databases.create_date of THIS database = $(TARGET_DB_CREATED)   (yyyy-mm-ddThh:mi:ss)
--     8. dbo.PlantSpecies.BedTransplantDays and CK_PlantSpecies_BedTransplantDays exist (2026-11-07_BedTransplantDays.sql)
--
-- LIVE (Linux, bash), after the identity query and a COPY_ONLY backup:
--   sqlcmd -S <server> -d PlantsIMS2_Test -U <login> -C -I -b -W -s"|" -v MODE=PREVIEW TARGET_SERVER=srv1202245 \
--          TARGET_MACHINE=srv1202245 TARGET_DB=PlantsIMS2_Test TARGET_DB_CREATED=2026-10-14T23:41:07 -i 01_BedTransplantDays_Live.sql
--   then the same with MODE=COMMIT (only with approval).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 54601, '01_BedTransplantDays_Live.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'PREVIEW', N'COMMIT')
    THROW 54602, 'Run with sqlcmd -v MODE=PREVIEW or MODE=COMMIT (exact upper case).', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 54603, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME
    THROW 54606, 'SERVERPROPERTY(''ServerName'') does not match @@SERVERNAME (renamed host?) -- refusing to run.', 1;
IF CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) IS NULL OR CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'
    THROW 54607, 'TARGET_MACHINE does not match SERVERPROPERTY(''MachineName'') -- refusing to run on an unexpected host.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 54608, 'TARGET_DB does not match DB_NAME() -- refusing to run against an unexpected database.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)')
    THROW 54609, 'TARGET_DB_CREATED does not match this database''s create_date -- refusing to run against an unexpected copy.', 1;
IF COL_LENGTH('dbo.PlantSpecies', 'BedTransplantDays') IS NULL OR OBJECT_ID('dbo.CK_PlantSpecies_BedTransplantDays', 'C') IS NULL
    THROW 54604, 'dbo.PlantSpecies.BedTransplantDays (or its CHECK) is missing -- run Database/Migrations/2026-11-07_BedTransplantDays.sql first.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

-- RowNo = the workbook row; GroupName / VarietyName as written (trimmed); NameKey = PlantSpeciesNameRules.Key.
DECLARE @Rows TABLE (RowNo INT PRIMARY KEY, GroupName NVARCHAR(200), VarietyName NVARCHAR(200), NameKey NVARCHAR(400) NOT NULL UNIQUE, Days INT NOT NULL);
INSERT @Rows (RowNo, GroupName, VarietyName, NameKey, Days) VALUES
 (2, N'Alyssum', N'Alyssum Fields Mix', N'ALYSSUM FIELDS MIX', 25)
,(3, N'Antirrhinum', N'Antirrhinum Chantilly', N'ANTIRRHINUM CHANTILLY', 20)
,(4, N'Antirrhinum', N'Antirrhinum Chimes Mix', N'ANTIRRHINUM CHIMES MIX', 20)
,(5, N'Antirrhinum', N'Antirrhinum DoubleShot', N'ANTIRRHINUM DOUBLESHOT', 20)
,(6, N'Antirrhinum', N'Antirrhinum Legend Double', N'ANTIRRHINUM LEGEND DOUBLE', 20)
,(7, N'Antirrhinum', N'Antirrhinum Majus Orleans Light Orange', N'ANTIRRHINUM MAJUS ORLEANS LIGHT ORANGE', 20)
,(8, N'Antirrhinum', N'Antirrhinum Majus Orleans Red', N'ANTIRRHINUM MAJUS ORLEANS RED', 20)
,(9, N'Antirrhinum', N'Antirrhinum Majus Orleans Yellow', N'ANTIRRHINUM MAJUS ORLEANS YELLOW', 20)
,(10, N'Antirrhinum', N'Antirrhinum Majus Toulon Apricot', N'ANTIRRHINUM MAJUS TOULON APRICOT', 20)
,(11, N'Antirrhinum', N'Antirrhinum Majus Toulon Deep Red', N'ANTIRRHINUM MAJUS TOULON DEEP RED', 20)
,(12, N'Antirrhinum', N'Antirrhinum Majus Toulon Lavender', N'ANTIRRHINUM MAJUS TOULON LAVENDER', 20)
,(13, N'Antirrhinum', N'Antirrhinum Majus Toulon Rose', N'ANTIRRHINUM MAJUS TOULON ROSE', 20)
,(14, N'Antirrhinum', N'Antirrhinum Snappy', N'ANTIRRHINUM SNAPPY', 20)
,(15, N'Antirrhinum', N'Antirrhinum Snappy Mix', N'ANTIRRHINUM SNAPPY MIX', 20)
,(16, N'Antirrhinum', N'Antirrhinum Snapshot Mix', N'ANTIRRHINUM SNAPSHOT MIX', 20)
,(17, N'Antirrhinum', N'Antirrhinum Snaptini Mix', N'ANTIRRHINUM SNAPTINI MIX', 20)
,(18, N'Antirrhinum', N'Antirrhinum Statement', N'ANTIRRHINUM STATEMENT', 20)
,(19, N'Antirrhinum', N'Antirrhinum Tulinny Mix', N'ANTIRRHINUM TULINNY MIX', 20)
,(20, N'Antirrhinum', N'Antirrhinum Twinny', N'ANTIRRHINUM TWINNY', 20)
,(22, N'Aster', N'Aster Callistephus Chinensis mix', N'ASTER CALLISTEPHUS CHINENSIS MIX', 22)
,(23, N'Aster', N'Aster chinensis Gala Mix', N'ASTER CHINENSIS GALA MIX', 22)
,(24, N'Aster', N'Aster chinensis Standy Mix', N'ASTER CHINENSIS STANDY MIX', 22)
,(25, N'Aster', N'Aster New Double Mix', N'ASTER NEW DOUBLE MIX', 22)
,(27, N'Begonia', N'Begonia Big Deluxxe Pink Green Leaf', N'BEGONIA BIG DELUXXE PINK GREEN LEAF', 45)
,(28, N'Begonia', N'Begonia Big Deluxxe Red Bronze Leaf', N'BEGONIA BIG DELUXXE RED BRONZE LEAF', 45)
,(29, N'Begonia', N'Begonia Big Deluxxe Rose Bronze Leaf', N'BEGONIA BIG DELUXXE ROSE BRONZE LEAF', 45)
,(30, N'Begonia', N'Begonia Big Deluxxe Rose espresso', N'BEGONIA BIG DELUXXE ROSE ESPRESSO', 45)
,(31, N'Begonia', N'Begonia Big Deluxxe Rose Green Leaf', N'BEGONIA BIG DELUXXE ROSE GREEN LEAF', 45)
,(32, N'Begonia', N'Begonia Big Deluxxe Salmon Bronze Leaf', N'BEGONIA BIG DELUXXE SALMON BRONZE LEAF', 45)
,(33, N'Begonia', N'Begonia Big Deluxxe white green Leaf', N'BEGONIA BIG DELUXXE WHITE GREEN LEAF', 45)
,(34, N'Begonia', N'Begonia Bonariensis Big Red Green Leaf', N'BEGONIA BONARIENSIS BIG RED GREEN LEAF', 45)
,(35, N'Begonia', N'Begonia Bonariensis Big Rose Bronze Leaf', N'BEGONIA BONARIENSIS BIG ROSE BRONZE LEAF', 45)
,(36, N'Begonia', N'Begonia Cocktail Mix', N'BEGONIA COCKTAIL MIX', 45)
,(37, N'Begonia', N'Begonia Fiona', N'BEGONIA FIONA', 45)
,(38, N'Begonia', N'Begonia Nonstop Mega Cool Mix', N'BEGONIA NONSTOP MEGA COOL MIX', 45)
,(39, N'Begonia', N'Begonia Nonstop Mocca Mix', N'BEGONIA NONSTOP MOCCA MIX', 45)
,(40, N'Begonia', N'Begonia Nonstop Select Mix', N'BEGONIA NONSTOP SELECT MIX', 45)
,(41, N'Begonia', N'Begonia Semperflorens Ambassador', N'BEGONIA SEMPERFLORENS AMBASSADOR', 45)
,(42, N'Begonia', N'Begonia Semperflorens Bada Boom', N'BEGONIA SEMPERFLORENS BADA BOOM', 45)
,(43, N'Begonia', N'Begonia Semperflorens Big Mix', N'BEGONIA SEMPERFLORENS BIG MIX', 45)
,(44, N'Begonia', N'Begonia Semperflorens Emperor', N'BEGONIA SEMPERFLORENS EMPEROR', 45)
,(45, N'Begonia', N'Begonia Semperflorens Senator IQ', N'BEGONIA SEMPERFLORENS SENATOR IQ', 45)
,(46, N'Begonia', N'Begonia Sprint Plus Mix', N'BEGONIA SPRINT PLUS MIX', 45)
,(47, N'Begonia', N'Begonia Stonehedge Rose Bronze Leaf', N'BEGONIA STONEHEDGE ROSE BRONZE LEAF', 45)
,(48, N'Begonia', N'Begonia Super Cool Blush', N'BEGONIA SUPER COOL BLUSH', 45)
,(49, N'Begonia', N'Begonia Super Cool Lipstick', N'BEGONIA SUPER COOL LIPSTICK', 45)
,(50, N'Begonia', N'Begonia Super Cool Mix', N'BEGONIA SUPER COOL MIX', 45)
,(51, N'Begonia', N'Begonia Super Cool Pink', N'BEGONIA SUPER COOL PINK', 45)
,(52, N'Begonia', N'Begonia tuberous Mocca Mix', N'BEGONIA TUBEROUS MOCCA MIX', 45)
,(53, N'Begonia', N'Begonia tuberous Nonstop', N'BEGONIA TUBEROUS NONSTOP', 45)
,(54, N'Begonia', N'Begonia Viking', N'BEGONIA VIKING', 45)
,(55, N'Begonia', N'Begonia Viking XL', N'BEGONIA VIKING XL', 45)
,(65, N'Callistephus', N'Callistephus Gala Mix', N'CALLISTEPHUS GALA MIX', 22)
,(68, N'Celosia', N'Celosia Armor', N'CELOSIA ARMOR', 25)
,(69, N'Celosia', N'Celosia Armor mix', N'CELOSIA ARMOR MIX', 25)
,(70, N'Celosia', N'Celosia Arrabona', N'CELOSIA ARRABONA', 25)
,(71, N'Celosia', N'Celosia Castle', N'CELOSIA CASTLE', 25)
,(72, N'Celosia', N'Celosia Century', N'CELOSIA CENTURY', 25)
,(73, N'Celosia', N'Celosia Cristata Amigo Mix', N'CELOSIA CRISTATA AMIGO MIX', 25)
,(74, N'Celosia', N'Celosia Cristata Mad. Magenta', N'CELOSIA CRISTATA MAD. MAGENTA', 25)
,(75, N'Celosia', N'Celosia Cristata Raven Red', N'CELOSIA CRISTATA RAVEN RED', 25)
,(76, N'Celosia', N'Celosia Cristata Robo Red', N'CELOSIA CRISTATA ROBO RED', 25)
,(77, N'Celosia', N'Celosia Cristata T. Mix', N'CELOSIA CRISTATA T. MIX', 25)
,(78, N'Celosia', N'Celosia Cristata Think Pink', N'CELOSIA CRISTATA THINK PINK', 25)
,(79, N'Celosia', N'Celosia Dragon''s Breath', N'CELOSIA DRAGON''S BREATH', 25)
,(80, N'Celosia', N'Celosia Flamma', N'CELOSIA FLAMMA', 25)
,(81, N'Celosia', N'Celosia Glorious Mix', N'CELOSIA GLORIOUS MIX', 25)
,(82, N'Celosia', N'Celosia Glorious Pink', N'CELOSIA GLORIOUS PINK', 25)
,(83, N'Celosia', N'Celosia Glorious Red', N'CELOSIA GLORIOUS RED', 25)
,(84, N'Celosia', N'Celosia Glorious Yellow', N'CELOSIA GLORIOUS YELLOW', 25)
,(85, N'Celosia', N'Celosia Ice cream Cherry', N'CELOSIA ICE CREAM CHERRY', 25)
,(86, N'Celosia', N'Celosia Ice cream Orange', N'CELOSIA ICE CREAM ORANGE', 25)
,(87, N'Celosia', N'Celosia Ice cream Pink', N'CELOSIA ICE CREAM PINK', 25)
,(88, N'Celosia', N'Celosia Ice cream Yellow', N'CELOSIA ICE CREAM YELLOW', 25)
,(89, N'Celosia', N'Celosia Icecream Salmon', N'CELOSIA ICECREAM SALMON', 25)
,(90, N'Celosia', N'Celosia Kimono', N'CELOSIA KIMONO', 25)
,(91, N'Celosia', N'Celosia Pinnatus Casanova Red', N'CELOSIA PINNATUS CASANOVA RED', 25)
,(92, N'Celosia', N'Celosia plumosa Fresh Look Orange', N'CELOSIA PLUMOSA FRESH LOOK ORANGE', 25)
,(93, N'Celosia', N'Celosia plumosa Fresh Look Red', N'CELOSIA PLUMOSA FRESH LOOK RED', 25)
,(94, N'Celosia', N'Celosia Plumosa Glorious Orange', N'CELOSIA PLUMOSA GLORIOUS ORANGE', 25)
,(95, N'Celosia', N'Celosia Plumosa Kimono Mix', N'CELOSIA PLUMOSA KIMONO MIX', 25)
,(96, N'Celosia', N'Celosia Raven Red', N'CELOSIA RAVEN RED', 25)
,(97, N'Celosia', N'Celosia Wizard Coral Sunrise', N'CELOSIA WIZARD CORAL SUNRISE', 25)
,(101, N'Cineraria', N'Cineraria Early Perfection Mix', N'CINERARIA EARLY PERFECTION MIX', 35)
,(102, N'Cineraria', N'Cineraria Quicksilver', N'CINERARIA QUICKSILVER', 35)
,(103, N'Cineraria', N'Cineraria Silver Dust', N'CINERARIA SILVER DUST', 35)
,(104, N'Cineraria', N'Cineraria Silverado', N'CINERARIA SILVERADO', 35)
,(105, N'Coleus', N'Coleus Extra DWF fairway Rose', N'COLEUS EXTRA DWF FAIRWAY ROSE', 20)
,(106, N'Coleus', N'Coleus Fairway', N'COLEUS FAIRWAY', 20)
,(107, N'Coleus', N'Coleus Fairway Red Velvet', N'COLEUS FAIRWAY RED VELVET', 20)
,(108, N'Coleus', N'Coleus Fareway Mix', N'COLEUS FAREWAY MIX', 20)
,(109, N'Coleus', N'Coleus Festive Dance', N'COLEUS FESTIVE DANCE', 20)
,(110, N'Coleus', N'Coleus Giant Exhibition', N'COLEUS GIANT EXHIBITION', 20)
,(111, N'Coleus', N'Coleus Jumbo', N'COLEUS JUMBO', 20)
,(112, N'Coleus', N'Coleus Kong', N'COLEUS KONG', 20)
,(113, N'Coleus', N'Coleus Orange', N'COLEUS ORANGE', 20)
,(114, N'Coleus', N'Coleus Rainbow', N'COLEUS RAINBOW', 20)
,(115, N'Coleus', N'Coleus Rainbow Color Palette', N'COLEUS RAINBOW COLOR PALETTE', 20)
,(116, N'Coleus', N'Coleus Rainbow Volcano', N'COLEUS RAINBOW VOLCANO', 20)
,(117, N'Coleus', N'Coleus Ruby', N'COLEUS RUBY', 20)
,(118, N'Coleus', N'Coleus Wizard', N'COLEUS WIZARD', 20)
,(119, N'Coleus', N'Coleus Wizard Coral Sunrise', N'COLEUS WIZARD CORAL SUNRISE', 20)
,(120, N'coleus', N'coleus Wizard Scarlet', N'COLEUS WIZARD SCARLET', 20)
,(138, N'Dianthus', N'Dianthus Chiba', N'DIANTHUS CHIBA', 15)
,(139, N'Dianthus', N'Dianthus Diamond', N'DIANTHUS DIAMOND', 15)
,(140, N'Dianthus', N'Dianthus Diana', N'DIANTHUS DIANA', 15)
,(141, N'Dianthus', N'Dianthus Divinity', N'DIANTHUS DIVINITY', 15)
,(142, N'Dianthus', N'Dianthus Elegance', N'DIANTHUS ELEGANCE', 15)
,(143, N'Dianthus', N'Dianthus F1 Telstar Mix', N'DIANTHUS F1 TELSTAR MIX', 15)
,(144, N'Dianthus', N'Dianthus Ideal Mix', N'DIANTHUS IDEAL MIX', 15)
,(145, N'Dianthus', N'Dianthus Ideal Select', N'DIANTHUS IDEAL SELECT', 15)
,(146, N'Dianthus', N'Dianthus Ideal Select Mix', N'DIANTHUS IDEAL SELECT MIX', 15)
,(147, N'Dianthus', N'Dianthus Lillipot', N'DIANTHUS LILLIPOT', 15)
,(148, N'Dianthus', N'Dianthus Mix', N'DIANTHUS MIX', 15)
,(149, N'Dianthus', N'Dianthus Super Parfait', N'DIANTHUS SUPER PARFAIT', 15)
,(150, N'Dianthus', N'Dianthus Super Parfait Series', N'DIANTHUS SUPER PARFAIT SERIES', 15)
,(151, N'Dianthus', N'Dianthus Supra', N'DIANTHUS SUPRA', 15)
,(152, N'Dianthus', N'Dianthus Telstar', N'DIANTHUS TELSTAR', 15)
,(153, N'Festuca', N'Festuca glauca Freddy', N'FESTUCA GLAUCA FREDDY', 50)
,(154, N'Festuca', N'Festuca valesiaca Buddy Blue', N'FESTUCA VALESIACA BUDDY BLUE', 50)
,(156, N'Gaillardia', N'Gaillardia Seedling', N'GAILLARDIA SEEDLING', 25)
,(157, N'Galardia', N'Galardia Mix', N'GALARDIA MIX', 25)
,(158, N'Gazania', N'Gazania Day Break', N'GAZANIA DAY BREAK', 20)
,(159, N'Gazania', N'Gazania Enorma', N'GAZANIA ENORMA', 20)
,(160, N'Gazania', N'Gazania Kiss Mix', N'GAZANIA KISS MIX', 20)
,(161, N'Gazania', N'Gazania New Day Mix', N'GAZANIA NEW DAY MIX', 20)
,(162, N'Gazania', N'Gazania Zanny Mix', N'GAZANIA ZANNY MIX', 20)
,(163, N'Gazania', N'Gazania Zany Orange Flame', N'GAZANIA ZANY ORANGE FLAME', 20)
,(172, N'Gloxinia', N'Gloxinia Double Brocade Mix', N'GLOXINIA DOUBLE BROCADE MIX', 40)
,(179, N'Hypoestes', N'Hypoestes Confetti Compact', N'HYPOESTES CONFETTI COMPACT', 15)
,(180, N'Hypoestes', N'Hypoestes Splash Mix', N'HYPOESTES SPLASH MIX', 15)
,(181, N'Hypoestes', N'Hypoestes Splash Pink', N'HYPOESTES SPLASH PINK', 15)
,(182, N'Hypoestes', N'Hypoestes Splash Red', N'HYPOESTES SPLASH RED', 15)
,(183, N'Hypoestes', N'Hypoestes Splash Rose', N'HYPOESTES SPLASH ROSE', 15)
,(184, N'Hypoestes', N'Hypoestes Splash White', N'HYPOESTES SPLASH WHITE', 15)
,(185, N'Impatiens', N'Impatiens Accent Star Mix', N'IMPATIENS ACCENT STAR MIX', 20)
,(186, N'Impatiens', N'Impatiens Balance', N'IMPATIENS BALANCE', 20)
,(187, N'Impatiens', N'Impatiens Imara Mix', N'IMPATIENS IMARA MIX', 20)
,(188, N'Impatiens', N'Impatiens Imara XDR', N'IMPATIENS IMARA XDR', 20)
,(189, N'Impatiens', N'Impatiens Magenta Bliss', N'IMPATIENS MAGENTA BLISS', 20)
,(190, N'Impatiens', N'Impatiens New Guinea', N'IMPATIENS NEW GUINEA', 20)
,(191, N'Impatiens', N'Impatiens Orange Boost', N'IMPATIENS ORANGE BOOST', 20)
,(192, N'Impatiens', N'Impatiens Pink Jellew', N'IMPATIENS PINK JELLEW', 20)
,(193, N'Impatiens', N'Impatiens Salmon Punch', N'IMPATIENS SALMON PUNCH', 20)
,(194, N'Impatiens', N'Impatiens Spark Pink Jellew', N'IMPATIENS SPARK PINK JELLEW', 20)
,(195, N'Impatiens', N'Impatiens walleriana Imara XDR Mix', N'IMPATIENS WALLERIANA IMARA XDR MIX', 20)
,(196, N'Impatiens', N'Impatiens White', N'IMPATIENS WHITE', 20)
,(197, N'Kochia', N'Kochia Burning Blush', N'KOCHIA BURNING BLUSH', 25)
,(213, N'Lobelia', N'Lobelia Aqua', N'LOBELIA AQUA', 25)
,(214, N'Lobelia', N'Lobelia Crystal', N'LOBELIA CRYSTAL', 25)
,(215, N'Lobelia', N'Lobelia Fan Scarlet Compact', N'LOBELIA FAN SCARLET COMPACT', 25)
,(216, N'Lobularia', N'Lobularia Maritima Giga White', N'LOBULARIA MARITIMA GIGA WHITE', 25)
,(241, N'Pansy', N'Pansy Colossus', N'PANSY COLOSSUS', 30)
,(242, N'Pansy', N'Pansy Cool Wave', N'PANSY COOL WAVE', 30)
,(243, N'Pansy', N'Pansy Crown', N'PANSY CROWN', 30)
,(244, N'Pansy', N'Pansy Delta', N'PANSY DELTA', 30)
,(245, N'Pansy', N'Pansy Inspire Deluxe Maxi Mix', N'PANSY INSPIRE DELUXE MAXI MIX', 30)
,(246, N'Pansy', N'Pansy Majus GTS Mix', N'PANSY MAJUS GTS MIX', 30)
,(247, N'Pansy', N'Pansy Matrix', N'PANSY MATRIX', 30)
,(248, N'Pansy', N'Pansy Panola', N'PANSY PANOLA', 30)
,(249, N'Pansy', N'Pansy Super Majestic Giants 2', N'PANSY SUPER MAJESTIC GIANTS 2', 30)
,(250, N'Pansy', N'Pansy Ultima', N'PANSY ULTIMA', 30)
,(251, N'Pansy', N'Pansy Viola Admire Maxi Mix', N'PANSY VIOLA ADMIRE MAXI MIX', 30)
,(252, N'Pansy', N'Pansy Viola Admire Orange Purple Wing', N'PANSY VIOLA ADMIRE ORANGE PURPLE WING', 30)
,(253, N'Pansy', N'Pansy Viola Cats Plus Mix', N'PANSY VIOLA CATS PLUS MIX', 30)
,(254, N'Pansy', N'Pansy Viola Cornuta Admire Maxi Mix', N'PANSY VIOLA CORNUTA ADMIRE MAXI MIX', 30)
,(255, N'Pansy', N'Pansy Viola Deluxe Maxi Mix', N'PANSY VIOLA DELUXE MAXI MIX', 30)
,(256, N'Pansy', N'Pansy Viola Highflyer Mix', N'PANSY VIOLA HIGHFLYER MIX', 30)
,(257, N'Pansy', N'Pansy Viola Highflyer Violet Face', N'PANSY VIOLA HIGHFLYER VIOLET FACE', 30)
,(258, N'Pansy', N'Pansy Viola Highflyer White', N'PANSY VIOLA HIGHFLYER WHITE', 30)
,(259, N'Pansy', N'Pansy Viola Highflyer Yellow', N'PANSY VIOLA HIGHFLYER YELLOW', 30)
,(260, N'Pansy', N'Pansy Viola Highflyer Yellow Red Wing (Trailing)', N'PANSY VIOLA HIGHFLYER YELLOW RED WING (TRAILING)', 30)
,(261, N'Pansy', N'Pansy Viola Hot Cakes Mix', N'PANSY VIOLA HOT CAKES MIX', 30)
,(262, N'Pansy', N'Pansy Viola Witrockiana Inspire Deluxe Maxi Mix', N'PANSY VIOLA WITROCKIANA INSPIRE DELUXE MAXI MIX', 30)
,(264, N'Pentas', N'Pentas BeeBright', N'PENTAS BEEBRIGHT', 35)
,(265, N'Pentas', N'Pentas Beehive', N'PENTAS BEEHIVE', 35)
,(266, N'Pentas', N'Pentas Butterfly', N'PENTAS BUTTERFLY', 35)
,(267, N'Pentas', N'Pentas Graffiti Lipstick', N'PENTAS GRAFFITI LIPSTICK', 35)
,(268, N'Pentas', N'Pentas Graffiti Red Velvet', N'PENTAS GRAFFITI RED VELVET', 35)
,(269, N'Pentas', N'Pentas Graffiti Ruby', N'PENTAS GRAFFITI RUBY', 35)
,(270, N'Pentas', N'Pentas Graffiti White', N'PENTAS GRAFFITI WHITE', 35)
,(271, N'Pentas', N'Pentas Lucky Star', N'PENTAS LUCKY STAR', 35)
,(272, N'Pentas', N'Pentas Starla', N'PENTAS STARLA', 35)
,(273, N'Petunia', N'Petunia 360 Success Pink', N'PETUNIA 360 SUCCESS PINK', 25)
,(274, N'Petunia', N'Petunia 360 Success Violet', N'PETUNIA 360 SUCCESS VIOLET', 25)
,(275, N'Petunia', N'Petunia Bello Mix', N'PETUNIA BELLO MIX', 25)
,(276, N'Petunia', N'Petunia Blue Star', N'PETUNIA BLUE STAR', 25)
,(277, N'Petunia', N'Petunia Boom HD Burgundy', N'PETUNIA BOOM HD BURGUNDY', 25)
,(278, N'Petunia', N'Petunia Boom HD Pink', N'PETUNIA BOOM HD PINK', 25)
,(279, N'Petunia', N'Petunia Brovo Mix', N'PETUNIA BROVO MIX', 25)
,(280, N'Petunia', N'Petunia Burgundy Star', N'PETUNIA BURGUNDY STAR', 25)
,(281, N'Petunia', N'Petunia Crimson Star', N'PETUNIA CRIMSON STAR', 25)
,(282, N'Petunia', N'Petunia Daddy', N'PETUNIA DADDY', 25)
,(283, N'Petunia', N'Petunia Double Double Duo Series', N'PETUNIA DOUBLE DOUBLE DUO SERIES', 25)
,(284, N'Petunia', N'Petunia Double Madness', N'PETUNIA DOUBLE MADNESS', 25)
,(285, N'Petunia', N'Petunia Dreams', N'PETUNIA DREAMS', 25)
,(286, N'Petunia', N'Petunia Easy Wave', N'PETUNIA EASY WAVE', 25)
,(287, N'Petunia', N'Petunia Evening Scentsation', N'PETUNIA EVENING SCENTSATION', 25)
,(288, N'Petunia', N'Petunia Ez Rider', N'PETUNIA EZ RIDER', 25)
,(289, N'Petunia', N'Petunia Grandiflora Bravo', N'PETUNIA GRANDIFLORA BRAVO', 25)
,(290, N'Petunia', N'Petunia Grandiflora Colourwise', N'PETUNIA GRANDIFLORA COLOURWISE', 25)
,(291, N'Petunia', N'Petunia Grandiflora Frost', N'PETUNIA GRANDIFLORA FROST', 25)
,(292, N'Petunia', N'Petunia Grandiflora Picotee Mix', N'PETUNIA GRANDIFLORA PICOTEE MIX', 25)
,(293, N'Petunia', N'Petunia Grandiflora Supercascade Formula Mix', N'PETUNIA GRANDIFLORA SUPERCASCADE FORMULA MIX', 25)
,(294, N'Petunia', N'Petunia Grandiflora Ultra Star Mix', N'PETUNIA GRANDIFLORA ULTRA STAR MIX', 25)
,(295, N'Petunia', N'Petunia Hulahoop Mix', N'PETUNIA HULAHOOP MIX', 25)
,(296, N'Petunia', N'Petunia Hybrid Boom Mix', N'PETUNIA HYBRID BOOM MIX', 25)
,(297, N'Petunia', N'Petunia Lambada', N'PETUNIA LAMBADA', 25)
,(298, N'Petunia', N'Petunia Limbo GP', N'PETUNIA LIMBO GP', 25)
,(299, N'Petunia', N'Petunia Mambo GP', N'PETUNIA MAMBO GP', 25)
,(300, N'Petunia', N'Petunia Opera Supreme', N'PETUNIA OPERA SUPREME', 25)
,(301, N'Petunia', N'Petunia Picobella', N'PETUNIA PICOBELLA', 25)
,(302, N'Petunia', N'Petunia Puffin', N'PETUNIA PUFFIN', 25)
,(303, N'Petunia', N'Petunia Ramblin', N'PETUNIA RAMBLIN', 25)
,(304, N'Petunia', N'Petunia Red Star', N'PETUNIA RED STAR', 25)
,(305, N'Petunia', N'Petunia Rose Star', N'PETUNIA ROSE STAR', 25)
,(306, N'Petunia', N'Petunia S.C. Mix', N'PETUNIA S.C. MIX', 25)
,(307, N'Petunia', N'Petunia S.C. White', N'PETUNIA S.C. WHITE', 25)
,(308, N'Petunia', N'Petunia Sanguna seed lines', N'PETUNIA SANGUNA SEED LINES', 25)
,(309, N'Petunia', N'Petunia Shake', N'PETUNIA SHAKE', 25)
,(310, N'Petunia', N'Petunia Shock Wave', N'PETUNIA SHOCK WAVE', 25)
,(311, N'Petunia', N'Petunia Spoty Mix', N'PETUNIA SPOTY MIX', 25)
,(312, N'Petunia', N'Petunia Success 360', N'PETUNIA SUCCESS 360', 25)
,(313, N'Petunia', N'Petunia Success 360 Blue', N'PETUNIA SUCCESS 360 BLUE', 25)
,(314, N'Petunia', N'Petunia Success 360 Burgundy', N'PETUNIA SUCCESS 360 BURGUNDY', 25)
,(315, N'Petunia', N'Petunia Success 360 Deep Pink', N'PETUNIA SUCCESS 360 DEEP PINK', 25)
,(316, N'Petunia', N'Petunia Success 360 Deep Rose', N'PETUNIA SUCCESS 360 DEEP ROSE', 25)
,(317, N'Petunia', N'Petunia Success 360 Deep Salmon', N'PETUNIA SUCCESS 360 DEEP SALMON', 25)
,(318, N'Petunia', N'Petunia Success 360 maximix', N'PETUNIA SUCCESS 360 MAXIMIX', 25)
,(319, N'Petunia', N'Petunia Success 360 Purple', N'PETUNIA SUCCESS 360 PURPLE', 25)
,(320, N'Petunia', N'Petunia Success 360 Red Star', N'PETUNIA SUCCESS 360 RED STAR', 25)
,(321, N'Petunia', N'Petunia Success 360 Redstar', N'PETUNIA SUCCESS 360 REDSTAR', 25)
,(322, N'Petunia', N'Petunia Success 360 Rose Star', N'PETUNIA SUCCESS 360 ROSE STAR', 25)
,(323, N'Petunia', N'Petunia Success 360 RoseStar', N'PETUNIA SUCCESS 360 ROSESTAR', 25)
,(324, N'Petunia', N'Petunia Success 360 White', N'PETUNIA SUCCESS 360 WHITE', 25)
,(325, N'Petunia', N'Petunia Success HD Maxi Mix', N'PETUNIA SUCCESS HD MAXI MIX', 25)
,(326, N'Petunia', N'Petunia Success HD Pink', N'PETUNIA SUCCESS HD PINK', 25)
,(327, N'Petunia', N'Petunia Success Maxi Mix', N'PETUNIA SUCCESS MAXI MIX', 25)
,(328, N'Petunia', N'Petunia Success TR Blue', N'PETUNIA SUCCESS TR BLUE', 25)
,(329, N'Petunia', N'Petunia Success TR Burgundy', N'PETUNIA SUCCESS TR BURGUNDY', 25)
,(330, N'Petunia', N'Petunia Success TR Maximix', N'PETUNIA SUCCESS TR MAXIMIX', 25)
,(331, N'Petunia', N'Petunia Success TR Pink', N'PETUNIA SUCCESS TR PINK', 25)
,(332, N'Petunia', N'Petunia Success TR Pink Morn', N'PETUNIA SUCCESS TR PINK MORN', 25)
,(333, N'Petunia', N'Petunia Success TR Red', N'PETUNIA SUCCESS TR RED', 25)
,(334, N'Petunia', N'Petunia Success TR Voilet', N'PETUNIA SUCCESS TR VOILET', 25)
,(335, N'Petunia', N'Petunia Success TR White', N'PETUNIA SUCCESS TR WHITE', 25)
,(336, N'Petunia', N'Petunia Sucess 360 Purple', N'PETUNIA SUCESS 360 PURPLE', 25)
,(337, N'Petunia', N'Petunia Supercascade White', N'PETUNIA SUPERCASCADE WHITE', 25)
,(338, N'Petunia', N'Petunia T. Pink Morn', N'PETUNIA T. PINK MORN', 25)
,(339, N'Petunia', N'Petunia T. Star Mix', N'PETUNIA T. STAR MIX', 25)
,(340, N'Petunia', N'Petunia T. Violet', N'PETUNIA T. VIOLET', 25)
,(341, N'Petunia', N'Petunia Tango', N'PETUNIA TANGO', 25)
,(342, N'Petunia', N'Petunia Tidal Wave', N'PETUNIA TIDAL WAVE', 25)
,(343, N'Petunia', N'Petunia Trailing Easy Wave', N'PETUNIA TRAILING EASY WAVE', 25)
,(344, N'Petunia', N'Petunia Trailing Ramblin', N'PETUNIA TRAILING RAMBLIN', 25)
,(345, N'Petunia', N'Petunia Trilogy', N'PETUNIA TRILOGY', 25)
,(346, N'Petunia', N'Petunia Tritunia Crimson star', N'PETUNIA TRITUNIA CRIMSON STAR', 25)
,(347, N'Petunia', N'Petunia Tritunia Mix', N'PETUNIA TRITUNIA MIX', 25)
,(348, N'Petunia', N'Petunia Tritunia Plum', N'PETUNIA TRITUNIA PLUM', 25)
,(349, N'Petunia', N'Petunia Tritunia purple star', N'PETUNIA TRITUNIA PURPLE STAR', 25)
,(350, N'Petunia', N'Petunia Tritunia Starmix', N'PETUNIA TRITUNIA STARMIX', 25)
,(351, N'Petunia', N'Petunia Wave', N'PETUNIA WAVE', 25)
,(352, N'Phlox', N'Phlox Promise Mix', N'PHLOX PROMISE MIX', 25)
,(353, N'Phlox', N'Phlox Twinkle', N'PHLOX TWINKLE', 25)
,(354, N'Platycodon', N'Platycodon Pop Star Mix', N'PLATYCODON POP STAR MIX', 35)
,(355, N'Platycodon', N'Platycodon Popstar Blue', N'PLATYCODON POPSTAR BLUE', 35)
,(356, N'Platycodon', N'Platycodon Popstar Pink', N'PLATYCODON POPSTAR PINK', 35)
,(357, N'Platycodon', N'Platycodon Popstar White', N'PLATYCODON POPSTAR WHITE', 35)
,(358, N'Portulaca', N'Portulaca Happy Hour', N'PORTULACA HAPPY HOUR', 25)
,(359, N'Portulaca', N'Portulaca Sundial Mix', N'PORTULACA SUNDIAL MIX', 25)
,(361, N'Rudbeckia', N'Rudbeckia Amarillo Gold', N'RUDBECKIA AMARILLO GOLD', 30)
,(362, N'Rudbeckia', N'Rudbeckia Goldsturm', N'RUDBECKIA GOLDSTURM', 30)
,(363, N'Rudbeckia', N'Rudbeckia Pavnee Spirit', N'RUDBECKIA PAVNEE SPIRIT', 30)
,(364, N'Rudbeckia', N'Rudbeckia Rustic', N'RUDBECKIA RUSTIC', 30)
,(365, N'Rudbeckia', N'Rudbeckia Sonata Begreen', N'RUDBECKIA SONATA BEGREEN', 30)
,(366, N'Rudbeckia', N'Rudbeckia Tabledance Gold', N'RUDBECKIA TABLEDANCE GOLD', 30)
,(367, N'Rudbeckia', N'Rudbeckia Toto', N'RUDBECKIA TOTO', 30)
,(368, N'Salvia', N'Salvia Amore', N'SALVIA AMORE', 20)
,(369, N'Salvia', N'Salvia Blue', N'SALVIA BLUE', 20)
,(370, N'Salvia', N'Salvia Cover Girl', N'SALVIA COVER GIRL', 20)
,(371, N'Salvia', N'Salvia Deep Purple', N'SALVIA DEEP PURPLE', 20)
,(372, N'Salvia', N'Salvia Evolution White', N'SALVIA EVOLUTION WHITE', 20)
,(373, N'Salvia', N'Salvia F. Evalution Violet', N'SALVIA F. EVALUTION VIOLET', 20)
,(374, N'Salvia', N'Salvia F. Fairy Queen', N'SALVIA F. FAIRY QUEEN', 20)
,(375, N'Salvia', N'Salvia F. Victoria Blue', N'SALVIA F. VICTORIA BLUE', 20)
,(376, N'Salvia', N'Salvia F. White', N'SALVIA F. WHITE', 20)
,(377, N'Salvia', N'Salvia Farinacea Victoria Blue', N'SALVIA FARINACEA VICTORIA BLUE', 20)
,(378, N'Salvia', N'Salvia Flamex 2000', N'SALVIA FLAMEX 2000', 20)
,(379, N'Salvia', N'Salvia Flamingo Red', N'SALVIA FLAMINGO RED', 20)
,(380, N'Salvia', N'Salvia Hummingbird', N'SALVIA HUMMINGBIRD', 20)
,(381, N'Salvia', N'Salvia Mojave', N'SALVIA MOJAVE', 20)
,(382, N'Salvia', N'Salvia Mojave Red', N'SALVIA MOJAVE RED', 20)
,(383, N'Salvia', N'Salvia Red Alert', N'SALVIA RED ALERT', 20)
,(384, N'Salvia', N'Salvia Red Hill', N'SALVIA RED HILL', 20)
,(385, N'Salvia', N'Salvia Red Hot', N'SALVIA RED HOT', 20)
,(386, N'Salvia', N'Salvia Reddy', N'SALVIA REDDY', 20)
,(387, N'Salvia', N'Salvia Sahara', N'SALVIA SAHARA', 20)
,(388, N'Salvia', N'Salvia Sahara Red', N'SALVIA SAHARA RED', 20)
,(389, N'Salvia', N'Salvia Salsa', N'SALVIA SALSA', 20)
,(390, N'Salvia', N'Salvia Salvano', N'SALVIA SALVANO', 20)
,(391, N'Salvia', N'Salvia Salvo', N'SALVIA SALVO', 20)
,(392, N'Salvia', N'Salvia Summer Jewel', N'SALVIA SUMMER JEWEL', 20)
,(393, N'Salvia', N'Salvia Sup. Blue Queen', N'SALVIA SUP. BLUE QUEEN', 20)
,(394, N'Salvia', N'Salvia Victoria Blue', N'SALVIA VICTORIA BLUE', 20)
,(395, N'Salvia', N'Salvia Vista', N'SALVIA VISTA', 20)
,(396, N'Salvia', N'Salvia Vista Mix', N'SALVIA VISTA MIX', 20)
,(397, N'Salvia', N'Salvia Vista Red', N'SALVIA VISTA RED', 20)
,(398, N'Salvia', N'Salvia X Superba Adorba Blue', N'SALVIA X SUPERBA ADORBA BLUE', 20)
,(399, N'Salvia', N'Salvia Zenith', N'SALVIA ZENITH', 20)
,(400, N'Silver', N'Silver Dust Millar', N'SILVER DUST MILLAR', 30)
,(409, N'Torenia', N'Torenia Fournieri Mix', N'TORENIA FOURNIERI MIX', 35)
,(410, N'Torenia', N'Torenia Hi Life Mix', N'TORENIA HI LIFE MIX', 35)
,(411, N'Torenia', N'Torenia Hi-Lite Magenta', N'TORENIA HI-LITE MAGENTA', 35)
,(412, N'Torenia', N'Torenia Kauai', N'TORENIA KAUAI', 35)
,(413, N'Torenia', N'Torenia Kauai Mix', N'TORENIA KAUAI MIX', 35)
,(414, N'Torenia', N'Torenia Little Kiss Mix', N'TORENIA LITTLE KISS MIX', 35)
,(416, N'Verbena', N'Verbena Apex Mix', N'VERBENA APEX MIX', 25)
,(417, N'Verbena', N'Verbena Apex Raw', N'VERBENA APEX RAW', 25)
,(418, N'Verbena', N'Verbena Dazzling Nights', N'VERBENA DAZZLING NIGHTS', 25)
,(419, N'Verbena', N'Verbena Obsession', N'VERBENA OBSESSION', 25)
,(420, N'Verbena', N'Verbena Polaris Pearl Apex', N'VERBENA POLARIS PEARL APEX', 25)
,(421, N'Verbena', N'Verbena Polaris Purple Apex', N'VERBENA POLARIS PURPLE APEX', 25)
,(422, N'Verbena', N'Verbena Purple Haze', N'VERBENA PURPLE HAZE', 25)
,(423, N'Verbena', N'Verbena Quartz Mix', N'VERBENA QUARTZ MIX', 25)
,(424, N'Verbena', N'Verbena Quartz XP Mix', N'VERBENA QUARTZ XP MIX', 25)
,(425, N'Verbena', N'Verbena Speciosa Imagination', N'VERBENA SPECIOSA IMAGINATION', 25)
,(426, N'Vinca', N'Vinca Blockbuster', N'VINCA BLOCKBUSTER', 25)
,(427, N'Vinca', N'Vinca Bold Mix', N'VINCA BOLD MIX', 25)
,(428, N'Vinca', N'Vinca Cora XDR', N'VINCA CORA XDR', 25)
,(429, N'Vinca', N'Vinca Cora XDR Mix', N'VINCA CORA XDR MIX', 25)
,(430, N'Vinca', N'Vinca Heatwave', N'VINCA HEATWAVE', 25)
,(431, N'Vinca', N'Vinca Pacific Red Dark', N'VINCA PACIFIC RED DARK', 25)
,(432, N'Vinca', N'Vinca Pacifica Mix', N'VINCA PACIFICA MIX', 25)
,(433, N'Vinca', N'Vinca Pacifica Punch', N'VINCA PACIFICA PUNCH', 25)
,(434, N'Vinca', N'Vinca Pacifica Red', N'VINCA PACIFICA RED', 25)
,(435, N'Vinca', N'Vinca Pacifica XP', N'VINCA PACIFICA XP', 25)
,(436, N'Vinca', N'Vinca Solar', N'VINCA SOLAR', 25)
,(437, N'Vinca', N'Vinca Solar Avalanche', N'VINCA SOLAR AVALANCHE', 25)
,(438, N'Vinca', N'Vinca Tattoo', N'VINCA TATTOO', 25)
,(439, N'Vinca', N'Vinca Tattoo Series', N'VINCA TATTOO SERIES', 25)
,(440, N'Vinca', N'Vinca Titan', N'VINCA TITAN', 25)
,(441, N'Vinca', N'Vinca Valiant', N'VINCA VALIANT', 25)
,(442, N'Vinca', N'Vinca Valiant Mix', N'VINCA VALIANT MIX', 25)
,(443, N'Vinca', N'Vinca Victory', N'VINCA VICTORY', 25)
,(444, N'Vinca', N'Vinca Virtuosa', N'VINCA VIRTUOSA', 25)
,(445, N'Vinca', N'Vinca XDR Red', N'VINCA XDR RED', 25)
,(446, N'Viola', N'Viola Cello', N'VIOLA CELLO', 30)
,(447, N'Viola', N'Viola Corina', N'VIOLA CORINA', 30)
,(448, N'Viola', N'Viola Highflyer Blue Heaven Begreen', N'VIOLA HIGHFLYER BLUE HEAVEN BEGREEN', 30)
,(449, N'Viola', N'Viola Highflyer Yellow Purple Wing', N'VIOLA HIGHFLYER YELLOW PURPLE WING', 30)
,(450, N'Viola', N'Viola Penny', N'VIOLA PENNY', 30)
,(451, N'Viola', N'Viola Sorbet XP', N'VIOLA SORBET XP', 30)
,(452, N'Viola', N'Viola Trumpet', N'VIOLA TRUMPET', 30)
,(453, N'Viola', N'Viola Wittrokiaha Mix', N'VIOLA WITTROKIAHA MIX', 30)
,(454, N'Viola', N'Viola Xtrada', N'VIOLA XTRADA', 30)
;
IF (SELECT COUNT(*) FROM @Rows) <> 355
    THROW 54605, 'List check failed: the row count does not match the generated list.', 1;

PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

-- Name key = Add Variety's rule (PlantSpeciesNameRules): upper case, trimmed, tabs/line breaks/nbsp -> space, runs collapsed.
IF OBJECT_ID('tempdb..#Species') IS NOT NULL DROP TABLE #Species;
CREATE TABLE #Species (Id INT PRIMARY KEY, Name NVARCHAR(200), PlantTypeName NVARCHAR(200), BedTransplantDays INT NULL,
                       NameKey NVARCHAR(400), IsActive BIT NOT NULL DEFAULT 1);
INSERT #Species (Id, Name, PlantTypeName, NameKey)
SELECT s.Id, s.Name, LTRIM(RTRIM(t.Name)),
       UPPER(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
           REPLACE(REPLACE(REPLACE(REPLACE(s.Name, CHAR(13), N' '), CHAR(10), N' '), CHAR(9), N' '), NCHAR(160), N' '),
           N'      ', N' '), N'     ', N' '), N'    ', N' '), N'   ', N' '), N'  ', N' '), N'  ', N' '), N'  ', N' '))))
FROM dbo.PlantSpecies s LEFT JOIN dbo.PlantTypes t ON t.Id = s.PlantTypeId;
UPDATE x SET BedTransplantDays = s.BedTransplantDays FROM #Species x JOIN dbo.PlantSpecies s ON s.Id = x.Id;
-- PlantSpecies.IsActive exists only after 2026-10-15_PlantSpeciesIsActive.sql; without it every variety is active.
IF COL_LENGTH('dbo.PlantSpecies', 'IsActive') IS NOT NULL
    EXEC (N'UPDATE x SET IsActive = s.IsActive FROM #Species x JOIN dbo.PlantSpecies s ON s.Id = x.Id;');

DECLARE @Plan TABLE (RowNo INT PRIMARY KEY, GroupName NVARCHAR(200), VarietyName NVARCHAR(200), NameKey NVARCHAR(400), Days INT,
                     ActiveMatches INT, InactiveMatches INT, SpeciesId INT NULL, PlantType NVARCHAR(200) NULL, ExistingDays INT NULL,
                     Action NVARCHAR(40));
INSERT @Plan (RowNo, GroupName, VarietyName, NameKey, Days)
SELECT RowNo, GroupName, VarietyName, NameKey, Days FROM @Rows;
UPDATE p SET ActiveMatches   = (SELECT COUNT(*) FROM #Species s WHERE s.NameKey = p.NameKey AND s.IsActive = 1),
             InactiveMatches = (SELECT COUNT(*) FROM #Species s WHERE s.NameKey = p.NameKey AND s.IsActive = 0)
FROM @Plan p;
UPDATE p SET SpeciesId = s.Id, PlantType = s.PlantTypeName, ExistingDays = s.BedTransplantDays
FROM @Plan p JOIN #Species s ON s.NameKey = p.NameKey AND s.IsActive = 1
WHERE p.ActiveMatches = 1;
UPDATE p SET Action = CASE
        WHEN p.ActiveMatches > 1 THEN N'ERROR: name matches 2+ varieties'
        WHEN p.ActiveMatches = 0 AND p.InactiveMatches > 0 THEN N'INACTIVE ONLY - skipped'
        WHEN p.ActiveMatches = 0 THEN N'NOT IN DATABASE - skipped'
        WHEN p.ExistingDays IS NULL THEN N'SET'
        WHEN p.ExistingDays <> p.Days THEN N'CHANGE'
        ELSE N'SAME' END
FROM @Plan p;

SELECT p.RowNo AS [Row], p.GroupName AS [Group], p.VarietyName AS Variety, p.Days, p.SpeciesId, p.PlantType,
       p.ExistingDays, p.Action
FROM @Plan p ORDER BY CASE WHEN p.Action IN (N'SET', N'SAME') THEN 1 ELSE 0 END, p.RowNo;
-- Counts for every category (0 included), so PREVIEW always shows SET / CHANGE / SAME / NOT IN DATABASE / INACTIVE ONLY / ERROR.
PRINT '=== Counts ===';
SELECT k.Action, (SELECT COUNT(*) FROM @Plan p WHERE p.Action = k.Action OR (k.Action = N'ERROR' AND p.Action LIKE N'ERROR%')) AS Varieties
FROM (VALUES (1, N'SET'), (2, N'CHANGE'), (3, N'SAME'), (4, N'NOT IN DATABASE - skipped'), (5, N'INACTIVE ONLY - skipped'), (6, N'ERROR'))
     AS k (Ord, Action)
ORDER BY k.Ord;

PRINT '=== Workbook names skipped (no exact active variety of that name in THIS database) ===';
SELECT p.RowNo AS [Row], p.GroupName AS [Group], p.VarietyName AS Variety, p.Days, p.Action
FROM @Plan p WHERE p.Action NOT IN (N'SET', N'CHANGE', N'SAME') ORDER BY p.RowNo;

PRINT '=== Varieties that already have Bed Transplant Days but are NOT in the workbook (left unchanged) ===';
SELECT s.Id, s.Name, s.PlantTypeName, s.BedTransplantDays
FROM #Species s
WHERE s.BedTransplantDays IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @Plan p WHERE p.SpeciesId = s.Id)
ORDER BY s.Name;

IF EXISTS (SELECT 1 FROM @Plan WHERE Action LIKE N'ERROR%')
    THROW 54610, 'Plan has ERROR rows (see above) -- nothing written. Resolve them (owner decision) before COMMIT.', 1;

-- ---- apply (both modes; PREVIEW rolls back) --------------------------------------------------------------------------
DECLARE @Expected INT = (SELECT COUNT(*) FROM @Plan WHERE Action IN (N'SET', N'CHANGE'));
DECLARE @Changed INT;
BEGIN TRANSACTION BedTransplantDaysLive;

-- Lock the varieties and write only where the value is still the one the plan saw (a Plant Master edit meanwhile = stop).
UPDATE s SET BedTransplantDays = p.Days
FROM dbo.PlantSpecies s WITH (UPDLOCK, HOLDLOCK)
JOIN @Plan p ON p.SpeciesId = s.Id
WHERE p.Action IN (N'SET', N'CHANGE') AND ISNULL(s.BedTransplantDays, -1) = ISNULL(p.ExistingDays, -1);
SET @Changed = @@ROWCOUNT;

IF @Changed <> @Expected
BEGIN
    ROLLBACK TRANSACTION BedTransplantDaysLive;
    THROW 54611, 'A variety changed while the script ran -- rolled back, nothing written. Run PREVIEW again.', 1;
END
IF EXISTS (SELECT 1 FROM dbo.PlantSpecies s JOIN @Plan p ON p.SpeciesId = s.Id
           WHERE p.Action IN (N'SET', N'CHANGE', N'SAME') AND ISNULL(s.BedTransplantDays, -1) <> p.Days)
BEGIN
    ROLLBACK TRANSACTION BedTransplantDaysLive;
    THROW 54612, 'Verification failed -- rolled back, nothing written.', 1;
END

PRINT '=== After the update (inside the transaction) ===';
SELECT (SELECT COUNT(*) FROM dbo.PlantSpecies WHERE BedTransplantDays IS NOT NULL) AS VarietiesWithDays,
       (SELECT MIN(BedTransplantDays) FROM dbo.PlantSpecies) AS MinDays,
       (SELECT MAX(BedTransplantDays) FROM dbo.PlantSpecies) AS MaxDays,
       (SELECT COUNT(*) FROM dbo.PlantSpecies) AS Varieties,
       @Changed AS RowsUpdated;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'COMMIT'
BEGIN
    COMMIT TRANSACTION BedTransplantDaysLive;
    PRINT '=== COMMITTED: ' + CAST(@Expected AS VARCHAR(10)) + ' varieties set or changed. ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION BedTransplantDaysLive;
    PRINT '=== PREVIEW -- ' + CAST(@Expected AS VARCHAR(10)) + ' varieties would be set or changed; rolled back, nothing written. ===';
END
GO
