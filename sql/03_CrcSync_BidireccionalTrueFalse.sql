/*
    Ajusta la sincronización CRC (email y teléfono) para que sea bidireccional:
    el cliente puede activar o desactivar cada canal en CRC en cualquier momento,
    así que cada corrida debe reflejar el estado actual completo, no solo marcar
    exclusiones nuevas.

    Reglas (aplican a todos los emails/teléfonos ENVIADOS en cada bloque):
      - CRC responde true  (quiere ser contactado)    -> columna *ExcludedCRC = 0
      - CRC responde false (no quiere ser contactado) -> columna *ExcludedCRC = 1
      - CRC no encuentra rastro del email/teléfono     -> columna *ExcludedCRC = 0

    Cambios:
      1. EmailExclusionType_AGB / _ABB: se agrega la columna IsExcluded (bit).
      2. UpdateEmailExclusionCRC_AGB / _ABB: ahora sincroniza IsEmailExcludedCRC
         con el valor del TVP para TODOS los registros recibidos (ya no solo
         marca exclusión, también revierte a 0 cuando corresponde).
      3. UpdatePhoneExclusionCRC_AGB / _ABB: se renombra el parámetro
         @ExcludedPhones -> @Phones (la lógica interna ya sincronizaba en ambas
         direcciones porque toma los valores directamente del TVP).

    Ejecutar una sola vez contra la base de datos de destino.
*/

-- =========================================================
-- 1. Eliminar los SPs que dependen de EmailExclusionType (AGB y ABB)
-- =========================================================
IF OBJECT_ID('dbo.UpdateEmailExclusionCRC_AGB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.UpdateEmailExclusionCRC_AGB;
IF OBJECT_ID('dbo.UpdateEmailExclusionCRC_ABB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.UpdateEmailExclusionCRC_ABB;
GO

-- =========================================================
-- 2. Recrear EmailExclusionType_AGB / _ABB con la columna IsExcluded
-- =========================================================
IF TYPE_ID('dbo.EmailExclusionType_AGB') IS NOT NULL
    DROP TYPE dbo.EmailExclusionType_AGB;
GO
CREATE TYPE dbo.EmailExclusionType_AGB AS TABLE
(
    Email      NVARCHAR(640) NOT NULL,
    IsExcluded BIT           NOT NULL
);
GO

IF TYPE_ID('dbo.EmailExclusionType_ABB') IS NOT NULL
    DROP TYPE dbo.EmailExclusionType_ABB;
GO
CREATE TYPE dbo.EmailExclusionType_ABB AS TABLE
(
    Email      NVARCHAR(640) NOT NULL,
    IsExcluded BIT           NOT NULL
);
GO

-- =========================================================
-- 3. Recrear UpdateEmailExclusionCRC_AGB / _ABB con sincronización bidireccional
-- =========================================================
CREATE PROCEDURE dbo.UpdateEmailExclusionCRC_AGB
    @Emails dbo.EmailExclusionType_AGB READONLY
AS
BEGIN
    SET NOCOUNT OFF;

    UPDATE c
    SET
        c.IsEmailExcludedCRC = e.IsExcluded,
        c.CrcLastCheckedAt   = GETDATE()
    FROM
        dbo.Customers_AGB AS c
        INNER JOIN @Emails AS e
            ON c.Email = e.Email;

    PRINT CONCAT('Registros sincronizados (IsEmailExcludedCRC): ', @@ROWCOUNT);
END
GO

CREATE PROCEDURE dbo.UpdateEmailExclusionCRC_ABB
    @Emails dbo.EmailExclusionType_ABB READONLY
AS
BEGIN
    SET NOCOUNT OFF;

    UPDATE c
    SET
        c.IsEmailExcludedCRC = e.IsExcluded,
        c.CrcLastCheckedAt   = GETDATE()
    FROM
        dbo.Customers_ABB AS c
        INNER JOIN @Emails AS e
            ON c.Email = e.Email;

    PRINT CONCAT('Registros sincronizados (IsEmailExcludedCRC): ', @@ROWCOUNT);
END
GO

-- =========================================================
-- 4. Recrear UpdatePhoneExclusionCRC_AGB / _ABB solo para renombrar el
--    parámetro @ExcludedPhones -> @Phones (la lógica ya era bidireccional).
-- =========================================================
IF OBJECT_ID('dbo.UpdatePhoneExclusionCRC_AGB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.UpdatePhoneExclusionCRC_AGB;
GO
CREATE PROCEDURE dbo.UpdatePhoneExclusionCRC_AGB
    @Phones dbo.PhoneExclusionType_AGB READONLY
AS
BEGIN
    SET NOCOUNT OFF;

    UPDATE c
    SET
        c.IsSmsExcludedCRC  = p.IsSmsExcluded,
        c.IsCallExcludedCRC = p.IsCallExcluded,
        c.CrcLastCheckedAt  = GETDATE()
    FROM
        dbo.Customers_AGB AS c
        INNER JOIN @Phones AS p
            ON c.PhoneNumber = p.Phone;

    DECLARE @RowsUpdated INT = @@ROWCOUNT;
    PRINT 'Registros sincronizados en Customers_AGB: ' + CAST(@RowsUpdated AS NVARCHAR(10));
END
GO

IF OBJECT_ID('dbo.UpdatePhoneExclusionCRC_ABB', 'P') IS NOT NULL
    DROP PROCEDURE dbo.UpdatePhoneExclusionCRC_ABB;
GO
CREATE PROCEDURE dbo.UpdatePhoneExclusionCRC_ABB
    @Phones dbo.PhoneExclusionType_ABB READONLY
AS
BEGIN
    SET NOCOUNT OFF;

    UPDATE c
    SET
        c.IsSmsExcludedCRC  = p.IsSmsExcluded,
        c.IsCallExcludedCRC = p.IsCallExcluded,
        c.CrcLastCheckedAt  = GETDATE()
    FROM
        dbo.Customers_ABB AS c
        INNER JOIN @Phones AS p
            ON c.PhoneNumber = p.Phone;

    DECLARE @RowsUpdated INT = @@ROWCOUNT;
    PRINT 'Registros sincronizados en Customers_ABB: ' + CAST(@RowsUpdated AS NVARCHAR(10));
END
GO
