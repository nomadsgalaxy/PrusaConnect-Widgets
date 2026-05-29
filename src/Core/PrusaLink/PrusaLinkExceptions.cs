using System;
using System.Net;

namespace PrusaConnect.Core.PrusaLink;

public class PrusaLinkException : Exception
{
    public PrusaLinkException(string message) : base(message) { }
    public PrusaLinkException(string message, Exception inner) : base(message, inner) { }
}

public sealed class PrusaLinkUnauthorizedException : PrusaLinkException
{
    public PrusaLinkUnauthorizedException()
        : base("PrusaLink rejected the API key (HTTP 401). Re-check the printer's API key.") { }
}

public sealed class PrusaLinkUnreachableException : PrusaLinkException
{
    public PrusaLinkUnreachableException(string host, Exception inner)
        : base($"PrusaLink at {host} is unreachable: {inner.Message}", inner) { }
}

public sealed class PrusaLinkHttpException : PrusaLinkException
{
    public HttpStatusCode StatusCode { get; }

    public PrusaLinkHttpException(HttpStatusCode status, string message)
        : base(message) => StatusCode = status;
}
