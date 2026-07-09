/*
    Objetos de base de datos para la marca "Agua By Aguabendita" (código ABB).
    Réplica independiente de la tabla Customers y de los stored procedures/Table Types
    que hoy usa Aguabendita (código AGB, objetos sin sufijo), para que ambas marcas
    operen sobre datos completamente separados dentro de la misma base de datos
    "Integracion-Claviyo".

    Ejecutar una sola vez contra la base de datos de destino.
*/

-- =========================================================
-- 1. Tabla Customers_ABB
-- =========================================================
IF OBJECT_ID('dbo.Customers_ABB', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Customers_ABB
    (
        ProfileId            NVARCHAR(50)    NOT NULL,
        FirstName            NVARCHAR(100)   NULL,
        LastName             NVARCHAR(100)   NULL,
        PhoneNumber          NVARCHAR(50)    NULL,
        Email                NVARCHAR(255)   NULL,
        IdentificationNumber NVARCHAR(50)    NULL,
        IdentificationType   NVARCHAR(5)     NULL,
        IsEmailExcludedCRC   BIT             NOT NULL CONSTRAINT DF_Customers_ABB_IsEmailExcludedCRC DEFAULT (0),
        IsSmsExcludedCRC     BIT             NOT NULL CONSTRAINT DF_Customers_ABB_IsSmsExcludedCRC   DEFAULT (0),
        IsCallExcludedCRC    BIT             NOT NULL CONSTRAINT DF_Customers_ABB_IsCallExcludedCRC  DEFAULT (0),
        CrcLastCheckedAt     DATETIME        NULL
    );
END
GO

-- =========================================================
-- 2. Table Types (TVPs)
-- =========================================================
IF TYPE_ID('dbo.CustomerType_ABB') IS NULL
BEGIN
    CREATE TYPE dbo.CustomerType_ABB AS TABLE
    (
        ProfileId            NVARCHAR(200)   NULL,
        FirstName            NVARCHAR(400)   NULL,
        LastName             NVARCHAR(400)   NULL,
        PhoneNumber          NVARCHAR(200)   NULL,
        Email                NVARCHAR(510)   NULL,
        IdentificationNumber NVARCHAR(200)   NULL,
        IdentificationType   NVARCHAR(20)    NULL
    );
END
GO

IF TYPE_ID('dbo.EmailExclusionType_ABB') IS NULL
BEGIN
    CREATE TYPE dbo.EmailExclusionType_ABB AS TABLE
    (
        Email NVARCHAR(640) NOT NULL
    );
END
GO

IF TYPE_ID('dbo.PhoneExclusionType_ABB') IS NULL
BEGIN
    CREATE TYPE dbo.PhoneExclusionType_ABB AS TABLE
    (
        Phone          NVARCHAR(100) NOT NULL,
        IsSmsExcluded  BIT           NOT NULL,
        IsCallExcluded BIT           NOT NULL
    );
END
GO

-- =========================================================
-- 3. dbo.InsertCustomers_ABB
-- =========================================================
IF OBJECT_ID('dbo.InsertCustomers_ABB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.InsertCustomers_ABB;
GO

CREATE PROCEDURE dbo.InsertCustomers_ABB
    @Customers dbo.CustomerType_ABB READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE dbo.Customers_ABB AS target
    USING (
        SELECT
            ProfileId,
            FirstName,
            LastName,
            CASE
                WHEN LEN(PhoneNumber) = 10 THEN PhoneNumber
                ELSE RIGHT(PhoneNumber, 10)
            END AS PhoneNumber,
            Email,
            IdentificationNumber,
            IdentificationType
        FROM @Customers
    ) AS source
    ON target.ProfileId = source.ProfileId

    WHEN MATCHED THEN
        UPDATE SET
            target.FirstName            = source.FirstName,
            target.LastName             = source.LastName,
            target.PhoneNumber          = source.PhoneNumber,
            target.Email                = source.Email,
            target.IdentificationNumber = source.IdentificationNumber,
            target.IdentificationType   = source.IdentificationType

    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ProfileId, FirstName, LastName, PhoneNumber, Email, IdentificationNumber, IdentificationType)
        VALUES (source.ProfileId, source.FirstName, source.LastName, source.PhoneNumber, source.Email, source.IdentificationNumber, source.IdentificationType);
END;
GO

-- =========================================================
-- 4. dbo.GetCustomerEmailsForCRC_ABB
-- =========================================================
IF OBJECT_ID('dbo.GetCustomerEmailsForCRC_ABB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.GetCustomerEmailsForCRC_ABB;
GO

CREATE PROCEDURE dbo.GetCustomerEmailsForCRC_ABB
AS
BEGIN
    SET NOCOUNT ON;

    WITH SequencedCustomers AS (
        SELECT
            Email,
            ROW_NUMBER() OVER (ORDER BY Email) - 1 AS RowNum
        FROM dbo.Customers_ABB
        WHERE Email IS NOT NULL
          AND Email <> ''
    ),
    GroupedCustomers AS (
        SELECT
            Email,
            RowNum / 200000 AS GroupId
        FROM SequencedCustomers
    )
    SELECT
        CAST('{"type": "COR", "keys": [' +
        STRING_AGG(CAST('"' + Email + '"' AS NVARCHAR(MAX)), ', ') +
        ']}' AS NVARCHAR(MAX)) AS CrcEmailPayload
    FROM GroupedCustomers
    GROUP BY GroupId;
END;
GO

-- =========================================================
-- 5. dbo.UpdateEmailExclusionCRC_ABB
-- =========================================================
IF OBJECT_ID('dbo.UpdateEmailExclusionCRC_ABB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.UpdateEmailExclusionCRC_ABB;
GO

CREATE PROCEDURE dbo.UpdateEmailExclusionCRC_ABB
    @ExcludedEmails dbo.EmailExclusionType_ABB READONLY
AS
BEGIN
    SET NOCOUNT OFF;

    -- Actualizar únicamente los registros que CRC reportó
    -- con correo_electronico = false (emails que vienen en el TVP)
    UPDATE c
    SET
        c.IsEmailExcludedCRC = 1,
        c.CrcLastCheckedAt   = GETDATE()
    FROM
        dbo.Customers_ABB AS c
        INNER JOIN @ExcludedEmails AS ex
            ON c.Email = ex.Email
    WHERE
        c.IsEmailExcludedCRC = 0;   -- Solo actualizar si aún no estaba excluido
                                    -- (evita escrituras innecesarias en registros ya marcados)

    PRINT CONCAT('Registros actualizados (IsEmailExcludedCRC = 1): ', @@ROWCOUNT);
END
GO

-- =========================================================
-- 6. dbo.GetCustomerPhonesForCRC_ABB
-- =========================================================
IF OBJECT_ID('dbo.GetCustomerPhonesForCRC_ABB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.GetCustomerPhonesForCRC_ABB;
GO

CREATE PROCEDURE dbo.GetCustomerPhonesForCRC_ABB
AS
BEGIN
    SET NOCOUNT ON;

    WITH SequencedCustomers AS (
        SELECT
            PhoneNumber,
            ROW_NUMBER() OVER (ORDER BY PhoneNumber) - 1 AS RowNum
        FROM dbo.Customers_ABB
        WHERE PhoneNumber IS NOT NULL
          AND PhoneNumber <> ''
    ),
    GroupedCustomers AS (
        SELECT
            PhoneNumber,
            RowNum / 200000 AS GroupId
        FROM SequencedCustomers
    )
    SELECT
        CAST('{"type": "TEL", "keys": [' +
        STRING_AGG(CAST('"' + PhoneNumber + '"' AS NVARCHAR(MAX)), ', ') +
        ']}' AS NVARCHAR(MAX)) AS CrcPhonePayload
    FROM GroupedCustomers
    GROUP BY GroupId;
END;
GO

-- =========================================================
-- 7. dbo.UpdatePhoneExclusionCRC_ABB
-- =========================================================
IF OBJECT_ID('dbo.UpdatePhoneExclusionCRC_ABB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.UpdatePhoneExclusionCRC_ABB;
GO

CREATE PROCEDURE dbo.UpdatePhoneExclusionCRC_ABB
    @ExcludedPhones dbo.PhoneExclusionType_ABB READONLY
AS
BEGIN
    SET NOCOUNT OFF;

    UPDATE c
    SET
        c.IsSmsExcludedCRC  = ep.IsSmsExcluded,
        c.IsCallExcludedCRC = ep.IsCallExcluded,
        c.CrcLastCheckedAt  = GETDATE()
    FROM
        dbo.Customers_ABB AS c
        INNER JOIN @ExcludedPhones AS ep
            ON c.PhoneNumber = ep.Phone;

    DECLARE @RowsUpdated INT = @@ROWCOUNT;
    PRINT 'Registros actualizados en Customers_ABB: ' + CAST(@RowsUpdated AS NVARCHAR(10));
END
GO