using System;

namespace Cloudflare.QuicheNet;

public class QuicheException : Exception
{
    private const string DEFAULT_MESSAGE = "An uncaught exception was raised by quiche!";

    public QuicheError ErrorCode { get; }

    /// <summary>
    /// La alerta TLS con la que este lado cortó el saludo —la 48, <c>unknown_ca</c>, si no pudo verificar el certificado
    /// del par—, o <c>null</c> si no fue así. quiche la guarda como su error local, 0x100 más la alerta.
    /// </summary>
    public byte? AlertaTls { get; internal init; }

    internal QuicheException(QuicheError errorCode, string? message)
        : base($"{message ?? DEFAULT_MESSAGE}\nCode: {errorCode.ToString()}")
    {
        ErrorCode = errorCode;
        HResult = (int)errorCode;
    }

    public static void ThrowIfError(QuicheError errorCode, string? message = null)
    {
        if (errorCode >= 0)
        {
            return;
        }

        throw new QuicheException(errorCode, message);
    }
}
