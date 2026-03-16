-- Remove the index added by 2_optimize.sql to restore the original (slow) state.
DROP INDEX IF EXISTS [IX_Example_Orders_CustomerId] ON dbo.Example_Orders;
