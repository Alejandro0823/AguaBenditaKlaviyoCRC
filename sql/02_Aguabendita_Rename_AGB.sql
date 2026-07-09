/*
    Migra los objetos existentes de la marca Aguabendita (hoy sin sufijo) a la
    nomenclatura con sufijo _AGB, para quedar simétricos con los objetos _ABB
    de Agua By Aguabendita.

    La tabla dbo.Customers se RENOMBRA (sp_rename) para preservar sus datos.
    Los Table Types y los stored procedures no tienen datos, así que se
    recrean directamente con el nuevo nombre.

    Ejecutar una sola vez contra la base de datos de destino.
*/

-- =========================================================
-- 1. Eliminar los stored procedures actuales (sin sufijo)
--    Deben eliminarse primero porque referencian los Table Types.
-- =========================================================
IF OBJECT_ID('dbo.InsertCustomers', 'P') IS NOT NULL
    DROP PROCEDURE dbo.InsertCustomers;
IF OBJECT_ID('dbo.GetCustomerEmailsForCRC', 'P') IS NOT NULL
    DROP PROCEDURE dbo.GetCustomerEmailsForCRC;
IF OBJECT_ID('dbo.UpdateEmailExclusionCRC', 'P') IS NOT NULL
    DROP PROCEDURE dbo.UpdateEmailExclusionCRC;
IF OBJECT_ID('dbo.GetCustomerPhonesForCRC', 'P') IS NOT NULL
    DROP PROCEDURE dbo.GetCustomerPhonesForCRC;
IF OBJECT_ID('dbo.UpdatePhoneExclusionCRC', 'P') IS NOT NULL
    DROP PROCEDURE dbo.UpdatePhoneExclusionCRC;
GO

-- =========================================================
-- 2. Eliminar los Table Types actuales (sin sufijo, sin datos)
-- =========================================================
IF TYPE_ID('dbo.CustomerType') IS NOT NULL
    DROP TYPE dbo.CustomerType;
IF TYPE_ID('dbo.EmailExclusionType') IS NOT NULL
    DROP TYPE dbo.EmailExclusionType;
IF TYPE_ID('dbo.PhoneExclusionType') IS NOT NULL
    DROP TYPE dbo.PhoneExclusionType;
GO

-- =========================================================
-- 3. Renombrar la tabla Customers -> Customers_AGB (preserva los datos)
-- =========================================================
IF OBJECT_ID('dbo.Customers', 'U') IS NOT NULL AND OBJECT_ID('dbo.Customers_AGB', 'U') IS NULL
    EXEC sp_rename 'dbo.Customers', 'Customers_AGB';
GO

-- =========================================================
-- 4. Recrear los Table Types con sufijo _AGB
-- =========================================================
IF TYPE_ID('dbo.CustomerType_AGB') IS NULL
BEGIN
    CREATE TYPE dbo.CustomerType_AGB AS TABLE
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

IF TYPE_ID('dbo.EmailExclusionType_AGB') IS NULL
BEGIN
    CREATE TYPE dbo.EmailExclusionType_AGB AS TABLE
    (
        Email NVARCHAR(640) NOT NULL
    );
END
GO

IF TYPE_ID('dbo.PhoneExclusionType_AGB') IS NULL
BEGIN
    CREATE TYPE dbo.PhoneExclusionType_AGB AS TABLE
    (
        Phone          NVARCHAR(100) NOT NULL,
        IsSmsExcluded  BIT           NOT NULL,
        IsCallExcluded BIT           NOT NULL
    );
END
GO

-- =========================================================
-- 5. dbo.InsertCustomers_AGB
-- =========================================================
IF OBJECT_ID('dbo.InsertCustomers_AGB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.InsertCustomers_AGB;
GO

CREATE PROCEDURE dbo.InsertCustomers_AGB
    @Customers dbo.CustomerType_AGB READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE dbo.Customers_AGB AS target
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
-- 6. dbo.GetCustomerEmailsForCRC_AGB
-- =========================================================
IF OBJECT_ID('dbo.GetCustomerEmailsForCRC_AGB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.GetCustomerEmailsForCRC_AGB;
GO

CREATE PROCEDURE dbo.GetCustomerEmailsForCRC_AGB
AS
BEGIN
    SET NOCOUNT ON;

    WITH SequencedCustomers AS (
        SELECT
            Email,
            ROW_NUMBER() OVER (ORDER BY Email) - 1 AS RowNum
        FROM dbo.Customers_AGB
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
-- 7. dbo.UpdateEmailExclusionCRC_AGB
-- =========================================================
IF OBJECT_ID('dbo.UpdateEmailExclusionCRC_AGB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.UpdateEmailExclusionCRC_AGB;
GO

CREATE PROCEDURE dbo.UpdateEmailExclusionCRC_AGB
    @ExcludedEmails dbo.EmailExclusionType_AGB READONLY
AS
BEGIN
    SET NOCOUNT OFF;

    UPDATE c
    SET
        c.IsEmailExcludedCRC = 1,
        c.CrcLastCheckedAt   = GETDATE()
    FROM
        dbo.Customers_AGB AS c
        INNER JOIN @ExcludedEmails AS ex
            ON c.Email = ex.Email
    WHERE
        c.IsEmailExcludedCRC = 0;

    PRINT CONCAT('Registros actualizados (IsEmailExcludedCRC = 1): ', @@ROWCOUNT);
END
GO

-- =========================================================
-- 8. dbo.GetCustomerPhonesForCRC_AGB
-- =========================================================
IF OBJECT_ID('dbo.GetCustomerPhonesForCRC_AGB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.GetCustomerPhonesForCRC_AGB;
GO

CREATE PROCEDURE dbo.GetCustomerPhonesForCRC_AGB
AS
BEGIN
    SET NOCOUNT ON;

    WITH SequencedCustomers AS (
        SELECT
            PhoneNumber,
            ROW_NUMBER() OVER (ORDER BY PhoneNumber) - 1 AS RowNum
        FROM dbo.Customers_AGB
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
-- 9. dbo.UpdatePhoneExclusionCRC_AGB
-- =========================================================
IF OBJECT_ID('dbo.UpdatePhoneExclusionCRC_AGB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.UpdatePhoneExclusionCRC_AGB;
GO

CREATE PROCEDURE dbo.UpdatePhoneExclusionCRC_AGB
    @ExcludedPhones dbo.PhoneExclusionType_AGB READONLY
AS
BEGIN
    SET NOCOUNT OFF;

    UPDATE c
    SET
        c.IsSmsExcludedCRC  = ep.IsSmsExcluded,
        c.IsCallExcludedCRC = ep.IsCallExcluded,
        c.CrcLastCheckedAt  = GETDATE()
    FROM
        dbo.Customers_AGB AS c
        INNER JOIN @ExcludedPhones AS ep
            ON c.PhoneNumber = ep.Phone;

    DECLARE @RowsUpdated INT = @@ROWCOUNT;
    PRINT 'Registros actualizados en Customers_AGB: ' + CAST(@RowsUpdated AS NVARCHAR(10));
END
GO
