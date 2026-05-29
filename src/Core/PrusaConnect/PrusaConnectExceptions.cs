using System;
using System.Net;

namespace PrusaConnect.Core.PrusaConnect;

public class PrusaConnectException : Exception
{
    public PrusaConnectException(string message) : base(message) { }
    public PrusaConnectException(string message, Exception inner) : base(message, inner) { }
}

public sealed class PrusaConnectAuthRequiredException : PrusaConnectException
{
    public PrusaConnectAuthRequiredException()
        : base("Prusa Connect rejected the access token (HTTP 401) and refresh failed. Sign in again.") { }
}

public sealed class PrusaConnectHttpException : PrusaConnectException
{
    public HttpStatusCode StatusCode { get; }
    public PrusaConnectHttpException(HttpStatusCode status, string message)
        : base(message) => StatusCode = status;
}

public sealed class PrusaConnectUnreachableException : PrusaConnectException
{
    public PrusaConnectUnreachableException(string host, Exception inner)
        : base($"Prusa Connect at {host} is unreachable: {inner.Message}", inner) { }
}
