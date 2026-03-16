-- =============================================================================
-- Example tables for Manual_Example and AI_Example benchmarks
-- Two tables designed to produce a slow join without indexes.
-- =============================================================================

IF OBJECT_ID(N'dbo.Example_Orders',    N'U') IS NOT NULL DROP TABLE dbo.Example_Orders;
IF OBJECT_ID(N'dbo.Example_Customers', N'U') IS NOT NULL DROP TABLE dbo.Example_Customers;

-- Parent: 10,000 customers
CREATE TABLE dbo.Example_Customers
(
    CustomerId   INT            NOT NULL,
    CustomerName VARCHAR(100)   NOT NULL,
    Country      VARCHAR(50)    NOT NULL,
    CreatedDate  DATETIME       NOT NULL,
    Status       VARCHAR(20)    NOT NULL
);

-- Child: 1,000,000 orders — no indexes, no constraints, no FK
CREATE TABLE dbo.Example_Orders
(
    OrderId     INT            NOT NULL,
    CustomerId  INT            NOT NULL,   -- no index on this column
    OrderDate   DATETIME       NOT NULL,
    TotalAmount DECIMAL(18, 2) NOT NULL,
    Status      VARCHAR(20)    NOT NULL,
    Description VARCHAR(500)   NOT NULL
);

-- Populate Example_Customers (10,000 rows)
WITH
    L0  AS (SELECT 1 AS c UNION ALL SELECT 1),
    L1  AS (SELECT 1 AS c FROM L0  AS a CROSS JOIN L0  AS b),
    L2  AS (SELECT 1 AS c FROM L1  AS a CROSS JOIN L1  AS b),
    L3  AS (SELECT 1 AS c FROM L2  AS a CROSS JOIN L2  AS b),
    Nums AS (SELECT TOP (10000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM L3)
INSERT INTO dbo.Example_Customers (CustomerId, CustomerName, Country, CreatedDate, Status)
SELECT
    CAST(n AS INT),
    'Customer_' + CAST(n AS VARCHAR(10)),
    CASE n % 10
        WHEN 0 THEN 'Norway'
        WHEN 1 THEN 'Sweden'
        WHEN 2 THEN 'Denmark'
        WHEN 3 THEN 'Finland'
        WHEN 4 THEN 'Germany'
        WHEN 5 THEN 'France'
        WHEN 6 THEN 'Spain'
        WHEN 7 THEN 'Italy'
        WHEN 8 THEN 'Netherlands'
        ELSE        'Poland'
    END,
    DATEADD(day, -(n % 3650), '2024-01-01'),
    CASE n % 3 WHEN 0 THEN 'Active' WHEN 1 THEN 'Inactive' ELSE 'Pending' END
FROM Nums;

-- Populate Example_Orders (1,000,000 rows)
WITH
    L0  AS (SELECT 1 AS c UNION ALL SELECT 1),
    L1  AS (SELECT 1 AS c FROM L0  AS a CROSS JOIN L0  AS b),
    L2  AS (SELECT 1 AS c FROM L1  AS a CROSS JOIN L1  AS b),
    L3  AS (SELECT 1 AS c FROM L2  AS a CROSS JOIN L2  AS b),
    L4  AS (SELECT 1 AS c FROM L3  AS a CROSS JOIN L3  AS b),
    L5  AS (SELECT 1 AS c FROM L4  AS a CROSS JOIN L4  AS b),
    Nums AS (SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM L5)
INSERT INTO dbo.Example_Orders (OrderId, CustomerId, OrderDate, TotalAmount, Status, Description)
SELECT
    CAST(n AS INT),
    CAST((n % 10000) + 1 AS INT),
    DATEADD(second, -(n % 31536000), '2024-01-01'),
    CAST((n % 99900 + 100) / 100.0 AS DECIMAL(18, 2)),
    CASE n % 5
        WHEN 0 THEN 'Pending'
        WHEN 1 THEN 'Processing'
        WHEN 2 THEN 'Shipped'
        WHEN 3 THEN 'Delivered'
        ELSE        'Cancelled'
    END,
    'Order description for order ' + CAST(n AS VARCHAR(20))
FROM Nums;

CHECKPOINT;
