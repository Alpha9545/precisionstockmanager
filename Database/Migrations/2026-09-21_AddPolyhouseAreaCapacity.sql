/*
    Migration: Add Area and Capacity columns to the existing Polyhouses table
    Database : PlantIMS2
    Notes    : Safe to run multiple times (checks for column existence first).
               Does NOT drop or modify any existing column/data.
               Existing rows get a default of 0 for the new columns so they
               remain valid until an admin edits them with real values.
*/

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Polyhouses') AND name = 'Area'
)
BEGIN
    ALTER TABLE dbo.Polyhouses
        ADD Area DECIMAL(10,2) NOT NULL CONSTRAINT DF_Polyhouses_Area DEFAULT (0);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Polyhouses') AND name = 'Capacity'
)
BEGIN
    ALTER TABLE dbo.Polyhouses
        ADD Capacity DECIMAL(12,2) NOT NULL CONSTRAINT DF_Polyhouses_Capacity DEFAULT (0);
END
GO
