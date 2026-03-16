-- Same query as 1_before.sql.  After the AI-suggested optimization has been
-- applied the engine should perform significantly fewer I/O operations.
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
