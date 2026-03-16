-- Add a nonclustered index on Example_Orders.CustomerId so the engine can
-- seek directly to the matching orders instead of scanning all 1M rows.
CREATE NONCLUSTERED INDEX [IX_Example_Orders_CustomerId]
    ON dbo.Example_Orders (CustomerId)
    INCLUDE (OrderId, OrderDate, TotalAmount, Status);
