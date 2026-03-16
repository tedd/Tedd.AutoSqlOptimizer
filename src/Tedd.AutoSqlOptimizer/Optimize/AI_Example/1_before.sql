-- Slow query: full table scan on Example_Orders (1M rows) because there is no
-- index on Example_Orders.CustomerId.  SQL Server must scan the entire orders
-- table and hash-join it against Example_Customers to satisfy the Country filter.
SELECT
    c.CustomerName,
    c.Country,
    o.OrderId,
    o.OrderDate,
    o.TotalAmount,
    o.Status
FROM dbo.Example_Orders AS o
JOIN dbo.Example_Customers AS c ON o.CustomerId = c.CustomerId
WHERE c.Country = 'Norway'
ORDER BY o.OrderDate DESC;
