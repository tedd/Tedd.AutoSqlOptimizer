-- Same query as 1_before.sql.  After the index in 2_optimize.sql has been
-- applied the engine performs an index seek instead of a full table scan,
-- dramatically reducing I/O and elapsed time.
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
