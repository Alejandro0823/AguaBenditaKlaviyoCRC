/*
    Objetos de base de datos para el nuevo proceso de sincronización de exclusión
    de SMS (CRC) hacia Klaviyo.

    Klaviyo no soporta el canal nativo de SMS marketing para números de Colombia
    (limitación confirmada de la plataforma), así que este proceso NO usa
    profile-subscription-bulk-delete-jobs como el de email. En su lugar, registra
    el estado de exclusión CRC vía SMS como una custom property del profile
    ("Consentimiento SMS CRC", booleana) mediante PATCH /api/profiles/{profile_id}/,
    una llamada por perfil (no hay bulk endpoint para custom properties).

    Por eso, a diferencia de GetPendingEmailExclusionSyncKlaviyo_* (que agrupa en
    bloques de 100 con STRING_AGG), este SP retorna UNA fila por perfil, cada una
    con su JSON individual ya armado en base de datos (mismo patrón de "el body
    sale completo del SP" que ya usan los procesos de email/CRC existentes). El
    profile_id se toma directamente de la columna ProfileId ya existente en
    Customers_AGB/Customers_ABB: no requiere ningún lookup adicional contra Klaviyo.

    El valor de "Consentimiento SMS CRC" se fija siempre en false: estos son
    clientes con IsSmsExcludedCRC = 1, es decir, sin consentimiento para SMS.

    Agrega:
      1. Columnas de control de idempotencia en Customers_AGB / Customers_ABB.
      2. SPs de lectura: GetPendingSmsExclusionSyncKlaviyo_AGB / _ABB.
      3. SPs de marcado: MarkSmsExclusionSyncedKlaviyo_AGB / _ABB (parámetro
         escalar @ProfileId, no TVP: a diferencia del email -que marca un lote
         completo tras una sola respuesta 202-, aquí cada PATCH se confirma
         individualmente, así que se marca un perfil a la vez inmediatamente
         después de cada respuesta exitosa).

    Ejecutar una sola vez contra la base de datos de destino.
*/

-- =========================================================
-- 1. Columnas de control (idempotencia) en Customers_AGB / Customers_ABB
-- =========================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Customers_AGB') AND name = 'IsSmsExclusionSyncedToKlaviyo')
BEGIN
    ALTER TABLE dbo.Customers_AGB
        ADD IsSmsExclusionSyncedToKlaviyo BIT NOT NULL CONSTRAINT DF_Customers_AGB_IsSmsExclusionSyncedToKlaviyo DEFAULT (0),
            SmsExclusionSyncedAt DATETIME NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Customers_ABB') AND name = 'IsSmsExclusionSyncedToKlaviyo')
BEGIN
    ALTER TABLE dbo.Customers_ABB
        ADD IsSmsExclusionSyncedToKlaviyo BIT NOT NULL CONSTRAINT DF_Customers_ABB_IsSmsExclusionSyncedToKlaviyo DEFAULT (0),
            SmsExclusionSyncedAt DATETIME NULL;
END
GO

-- =========================================================
-- 2. dbo.GetPendingSmsExclusionSyncKlaviyo_AGB / _ABB
--    Retorna una fila por perfil pendiente, con:
--      - ProfileId: identificador de Klaviyo (ya existente en la tabla), usado
--        tanto para armar la URL del PATCH como para el marcado post-envío.
--      - KlaviyoSmsUpdatePayload: JSON completo listo para enviar tal cual como
--        body de PATCH /api/profiles/{ProfileId}/.
-- =========================================================
IF OBJECT_ID('dbo.GetPendingSmsExclusionSyncKlaviyo_AGB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.GetPendingSmsExclusionSyncKlaviyo_AGB;
GO
CREATE PROCEDURE dbo.GetPendingSmsExclusionSyncKlaviyo_AGB
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        ProfileId,
        CAST(
            '{"data":{"type":"profile","id":"' + STRING_ESCAPE(ProfileId, 'json') +
            '","attributes":{"properties":{"Consentimiento SMS CRC":false}}}}'
        AS NVARCHAR(MAX)) AS KlaviyoSmsUpdatePayload
    FROM dbo.Customers_AGB
    WHERE IsSmsExcludedCRC = 1
      AND IsSmsExclusionSyncedToKlaviyo = 0
      AND ProfileId IS NOT NULL
      AND ProfileId <> '';
END;
GO

IF OBJECT_ID('dbo.GetPendingSmsExclusionSyncKlaviyo_ABB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.GetPendingSmsExclusionSyncKlaviyo_ABB;
GO
CREATE PROCEDURE dbo.GetPendingSmsExclusionSyncKlaviyo_ABB
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        ProfileId,
        CAST(
            '{"data":{"type":"profile","id":"' + STRING_ESCAPE(ProfileId, 'json') +
            '","attributes":{"properties":{"Consentimiento SMS CRC":false}}}}'
        AS NVARCHAR(MAX)) AS KlaviyoSmsUpdatePayload
    FROM dbo.Customers_ABB
    WHERE IsSmsExcludedCRC = 1
      AND IsSmsExclusionSyncedToKlaviyo = 0
      AND ProfileId IS NOT NULL
      AND ProfileId <> '';
END;
GO

-- =========================================================
-- 3. dbo.MarkSmsExclusionSyncedKlaviyo_AGB / _ABB
--    Marca un único perfil (parámetro escalar) inmediatamente después de que
--    Klaviyo confirme (200/202) el PATCH de ese perfil puntual.
-- =========================================================
IF OBJECT_ID('dbo.MarkSmsExclusionSyncedKlaviyo_AGB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.MarkSmsExclusionSyncedKlaviyo_AGB;
GO
CREATE PROCEDURE dbo.MarkSmsExclusionSyncedKlaviyo_AGB
    @ProfileId NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT OFF;

    UPDATE dbo.Customers_AGB
    SET
        IsSmsExclusionSyncedToKlaviyo = 1,
        SmsExclusionSyncedAt          = GETDATE()
    WHERE
        ProfileId = @ProfileId;

    PRINT CONCAT('Registro marcado como sincronizado con Klaviyo (IsSmsExclusionSyncedToKlaviyo): ', @@ROWCOUNT);
END
GO

IF OBJECT_ID('dbo.MarkSmsExclusionSyncedKlaviyo_ABB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.MarkSmsExclusionSyncedKlaviyo_ABB;
GO
CREATE PROCEDURE dbo.MarkSmsExclusionSyncedKlaviyo_ABB
    @ProfileId NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT OFF;

    UPDATE dbo.Customers_ABB
    SET
        IsSmsExclusionSyncedToKlaviyo = 1,
        SmsExclusionSyncedAt          = GETDATE()
    WHERE
        ProfileId = @ProfileId;

    PRINT CONCAT('Registro marcado como sincronizado con Klaviyo (IsSmsExclusionSyncedToKlaviyo): ', @@ROWCOUNT);
END
GO
