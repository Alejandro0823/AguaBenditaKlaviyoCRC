namespace KlaviyoCRC;

/// <summary>
/// Distingue, dentro de un catch(Exception), si la excepción fue causada por una cancelación
/// deliberada (botón "Detener procesos" de la UI, o cierre de la app) en vez de un error real de
/// la API o la base de datos — para loguearla como información esperada y no como Error con
/// stack trace.
///
/// No se filtra por tipo de excepción (p.ej. OperationCanceledException): cada librería envuelve
/// la cancelación de forma distinta — RestSharp ya se maneja aparte con ThrowIfCancellationRequested,
/// pero Microsoft.Data.SqlClient, por ejemplo, la reporta como SqlException ("Operación cancelada
/// por el usuario"), no como OperationCanceledException. Como <paramref name="cancellationToken"/>
/// es siempre el token exclusivo de esa corrida (nadie más lo cancela), basta con preguntar si él
/// mismo fue cancelado para saber que cualquier falla posterior es efecto esperado de la cancelación.
/// </summary>
internal static class CancellationLogHelper
{
    public static bool WasCancelledByRequest(this Exception ex, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested;
}
