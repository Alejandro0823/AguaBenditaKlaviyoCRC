/*
    Objetos de base de datos para la renovación automática del token de la API
    de CRC (RNE). El token que hoy vive fijo en appsettings.json (Authorization
    header) es un JWT que expira cada ~6 meses; a partir de este cambio el token
    vigente se guarda en esta tabla y se renueva automáticamente antes de cada
    corrida del scheduler cuando falta poco para su expiración (ver
    CrcApiTokenService.cs), llamando a:

        GET https://tramitescrcom.gov.co/excluidosback/consultaMasiva/generateApiToken

    usando el token actual como Bearer, y guardando el nuevo token retornado
    junto con su fecha de expiración (decodificada del claim "exp" del JWT).

    Es una sola fila (Id = 1) porque el token es global, no depende de la marca.

    Agrega:
      1. Tabla dbo.CrcApiToken.
      2. SP de lectura: dbo.GetCrcApiToken.
      3. SP de guardado (upsert): dbo.UpsertCrcApiToken.

    Ejecutar una sola vez contra la base de datos de destino.
*/

-- =========================================================
-- 1. Tabla dbo.CrcApiToken (una sola fila, Id = 1)
-- =========================================================
IF OBJECT_ID('dbo.CrcApiToken', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CrcApiToken
    (
        Id           TINYINT      NOT NULL CONSTRAINT PK_CrcApiToken PRIMARY KEY CONSTRAINT CK_CrcApiToken_SingleRow CHECK (Id = 1),
        Token        NVARCHAR(MAX) NOT NULL,
        ExpiresAtUtc DATETIME2    NOT NULL,
        UpdatedAtUtc DATETIME2    NOT NULL CONSTRAINT DF_CrcApiToken_UpdatedAtUtc DEFAULT (SYSUTCDATETIME())
    );
END
GO

-- =========================================================
-- 2. dbo.GetCrcApiToken
--    Retorna el token vigente y su expiración. Sin filas si aún no se ha
--    sembrado (primera corrida): CrcApiTokenService lo siembra desde
--    appsettings.json (CrcApiSettings:BootstrapToken).
-- =========================================================
IF OBJECT_ID('dbo.GetCrcApiToken', 'P') IS NOT NULL
    DROP PROCEDURE dbo.GetCrcApiToken;
GO
CREATE PROCEDURE dbo.GetCrcApiToken
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP 1 Token, ExpiresAtUtc, UpdatedAtUtc
    FROM dbo.CrcApiToken
    WHERE Id = 1;
END
GO

-- =========================================================
-- 3. dbo.UpsertCrcApiToken
--    Guarda el token vigente (siembra inicial o después de renovarlo).
-- =========================================================
IF OBJECT_ID('dbo.UpsertCrcApiToken', 'P') IS NOT NULL
    DROP PROCEDURE dbo.UpsertCrcApiToken;
GO
CREATE PROCEDURE dbo.UpsertCrcApiToken
    @Token        NVARCHAR(MAX),
    @ExpiresAtUtc DATETIME2
AS
BEGIN
    SET NOCOUNT ON;

    MERGE dbo.CrcApiToken AS target
    USING (SELECT CAST(1 AS TINYINT) AS Id) AS src
        ON target.Id = src.Id
    WHEN MATCHED THEN
        UPDATE SET Token = @Token, ExpiresAtUtc = @ExpiresAtUtc, UpdatedAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (Id, Token, ExpiresAtUtc, UpdatedAtUtc)
        VALUES (1, @Token, @ExpiresAtUtc, SYSUTCDATETIME());
END
GO
