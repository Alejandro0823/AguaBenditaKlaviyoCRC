/*
    Objetos de base de datos para el nuevo proceso de sincronización de exclusión
    de email (CRC/RNE) hacia Klaviyo (bulk unsubscribe vía
    profile-subscription-bulk-delete-jobs).

    Este proceso es independiente del flujo existente de lectura/validación CRC:
    toma los clientes que ya quedaron con IsEmailExcludedCRC = 1 y los desuscribe
    de marketing por email en Klaviyo, marcando cada registro individualmente
    solo después de que Klaviyo confirme el lote (202).

    El SP de lectura arma el JSON completo que espera el body de Klaviyo
    directamente en base de datos (vía STRING_AGG), siguiendo el mismo patrón que
    ya usan GetCustomerEmailsForCRC_AGB/_ABB y GetCustomerPhonesForCRC_AGB/_ABB
    para el flujo CRC existente. Junto al JSON retorna también, por bloque, el
    listado de ProfileId incluidos (columna ProfileIds), ya que ese identificador
    no viaja en el body de Klaviyo pero se necesita para el marcado post-envío.

    Agrega:
      1. Columnas de control de idempotencia en Customers_AGB / Customers_ABB.
      2. Table Types (TVPs) EmailExclusionSyncType_AGB / _ABB.
      3. SPs de lectura: GetPendingEmailExclusionSyncKlaviyo_AGB / _ABB.
      4. SPs de marcado: MarkEmailExclusionSyncedKlaviyo_AGB / _ABB.

    Ejecutar una sola vez contra la base de datos de destino.
*/

-- =========================================================
-- 1. Columnas de control (idempotencia) en Customers_AGB / Customers_ABB
-- =========================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Customers_AGB') AND name = 'IsEmailExclusionSyncedToKlaviyo')
BEGIN
    ALTER TABLE dbo.Customers_AGB
        ADD IsEmailExclusionSyncedToKlaviyo BIT NOT NULL CONSTRAINT DF_Customers_AGB_IsEmailExclusionSyncedToKlaviyo DEFAULT (0),
            EmailExclusionSyncedAt DATETIME NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Customers_ABB') AND name = 'IsEmailExclusionSyncedToKlaviyo')
BEGIN
    ALTER TABLE dbo.Customers_ABB
        ADD IsEmailExclusionSyncedToKlaviyo BIT NOT NULL CONSTRAINT DF_Customers_ABB_IsEmailExclusionSyncedToKlaviyo DEFAULT (0),
            EmailExclusionSyncedAt DATETIME NULL;
END
GO

-- =========================================================
-- 2. Table Types (TVPs) para el marcado batch post-envío
-- =========================================================
IF TYPE_ID('dbo.EmailExclusionSyncType_AGB') IS NULL
BEGIN
    CREATE TYPE dbo.EmailExclusionSyncType_AGB AS TABLE
    (
        ProfileId NVARCHAR(200) NOT NULL
    );
END
GO

IF TYPE_ID('dbo.EmailExclusionSyncType_ABB') IS NULL
BEGIN
    CREATE TYPE dbo.EmailExclusionSyncType_ABB AS TABLE
    (
        ProfileId NVARCHAR(200) NOT NULL
    );
END
GO

-- =========================================================
-- 3. dbo.GetPendingEmailExclusionSyncKlaviyo_AGB / _ABB
--    Retorna, por bloque de máximo 100 perfiles:
--      - KlaviyoBulkDeletePayload: JSON completo listo para enviar tal cual como
--        body de POST /profile-subscription-bulk-delete-jobs.
--      - ProfileIds: arreglo JSON con los ProfileId incluidos en ese bloque,
--        para el marcado post-envío (no viaja en el body de Klaviyo).
-- =========================================================
IF OBJECT_ID('dbo.GetPendingEmailExclusionSyncKlaviyo_AGB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.GetPendingEmailExclusionSyncKlaviyo_AGB;
GO
CREATE PROCEDURE dbo.GetPendingEmailExclusionSyncKlaviyo_AGB
AS
BEGIN
    SET NOCOUNT ON;

    WITH SequencedCustomers AS (
        SELECT
            ProfileId,
            Email,
            ROW_NUMBER() OVER (ORDER BY Email) - 1 AS RowNum
        FROM dbo.Customers_AGB
        WHERE IsEmailExcludedCRC = 1
          AND IsEmailExclusionSyncedToKlaviyo = 0
          AND Email IS NOT NULL
          AND Email <> ''
    ),
    GroupedCustomers AS (
        SELECT
            ProfileId,
            Email,
            RowNum / 100 AS GroupId
        FROM SequencedCustomers
    )
    SELECT
        CAST(
            '{"data":{"type":"profile-subscription-bulk-delete-job","attributes":{"profiles":{"data":[' +
            STRING_AGG(
                CAST(
                    '{"type":"profile","attributes":{"email":"' + STRING_ESCAPE(Email, 'json') +
                    '","subscriptions":{"email":{"marketing":{"consent":"UNSUBSCRIBED"}}}}}'
                AS NVARCHAR(MAX)),
                ','
            ) +
            ']}}}}' AS NVARCHAR(MAX)
        ) AS KlaviyoBulkDeletePayload,
        CAST(
            '[' + STRING_AGG(CAST('"' + STRING_ESCAPE(ProfileId, 'json') + '"' AS NVARCHAR(MAX)), ',') + ']' AS NVARCHAR(MAX)
        ) AS ProfileIds
    FROM GroupedCustomers
    GROUP BY GroupId;
END;
GO

IF OBJECT_ID('dbo.GetPendingEmailExclusionSyncKlaviyo_ABB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.GetPendingEmailExclusionSyncKlaviyo_ABB;
GO
CREATE PROCEDURE dbo.GetPendingEmailExclusionSyncKlaviyo_ABB
AS
BEGIN
    SET NOCOUNT ON;

    WITH SequencedCustomers AS (
        SELECT
            ProfileId,
            Email,
            ROW_NUMBER() OVER (ORDER BY Email) - 1 AS RowNum
        FROM dbo.Customers_ABB
        WHERE IsEmailExcludedCRC = 1
          AND IsEmailExclusionSyncedToKlaviyo = 0
          AND Email IS NOT NULL
          AND Email <> ''
    ),
    GroupedCustomers AS (
        SELECT
            ProfileId,
            Email,
            RowNum / 100 AS GroupId
        FROM SequencedCustomers
    )
    SELECT
        CAST(
            '{"data":{"type":"profile-subscription-bulk-delete-job","attributes":{"profiles":{"data":[' +
            STRING_AGG(
                CAST(
                    '{"type":"profile","attributes":{"email":"' + STRING_ESCAPE(Email, 'json') +
                    '","subscriptions":{"email":{"marketing":{"consent":"UNSUBSCRIBED"}}}}}'
                AS NVARCHAR(MAX)),
                ','
            ) +
            ']}}}}' AS NVARCHAR(MAX)
        ) AS KlaviyoBulkDeletePayload,
        CAST(
            '[' + STRING_AGG(CAST('"' + STRING_ESCAPE(ProfileId, 'json') + '"' AS NVARCHAR(MAX)), ',') + ']' AS NVARCHAR(MAX)
        ) AS ProfileIds
    FROM GroupedCustomers
    GROUP BY GroupId;
END;
GO

-- =========================================================
-- 4. dbo.MarkEmailExclusionSyncedKlaviyo_AGB / _ABB
--    Marca únicamente los ProfileId del lote que Klaviyo ya confirmó (202).
-- =========================================================
IF OBJECT_ID('dbo.MarkEmailExclusionSyncedKlaviyo_AGB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.MarkEmailExclusionSyncedKlaviyo_AGB;
GO
CREATE PROCEDURE dbo.MarkEmailExclusionSyncedKlaviyo_AGB
    @ProfileIds dbo.EmailExclusionSyncType_AGB READONLY
AS
BEGIN
    SET NOCOUNT OFF;

    UPDATE c
    SET
        c.IsEmailExclusionSyncedToKlaviyo = 1,
        c.EmailExclusionSyncedAt          = GETDATE()
    FROM
        dbo.Customers_AGB AS c
        INNER JOIN @ProfileIds AS p
            ON c.ProfileId = p.ProfileId;

    PRINT CONCAT('Registros marcados como sincronizados con Klaviyo (IsEmailExclusionSyncedToKlaviyo): ', @@ROWCOUNT);
END
GO

IF OBJECT_ID('dbo.MarkEmailExclusionSyncedKlaviyo_ABB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.MarkEmailExclusionSyncedKlaviyo_ABB;
GO
CREATE PROCEDURE dbo.MarkEmailExclusionSyncedKlaviyo_ABB
    @ProfileIds dbo.EmailExclusionSyncType_ABB READONLY
AS
BEGIN
    SET NOCOUNT OFF;

    UPDATE c
    SET
        c.IsEmailExclusionSyncedToKlaviyo = 1,
        c.EmailExclusionSyncedAt          = GETDATE()
    FROM
        dbo.Customers_ABB AS c
        INNER JOIN @ProfileIds AS p
            ON c.ProfileId = p.ProfileId;

    PRINT CONCAT('Registros marcados como sincronizados con Klaviyo (IsEmailExclusionSyncedToKlaviyo): ', @@ROWCOUNT);
END
GO