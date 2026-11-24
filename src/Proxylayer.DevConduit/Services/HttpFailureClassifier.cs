using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Turns an outbound-call exception into a stable error kind and a readable message.
/// Shared by the console executor and the forwarding endpoint.
/// </summary>
public static class HttpFailureClassifier
{
    public static (ProxyErrorKind Kind, string Message) Classify(HttpRequestException exception)
    {
        if (TryFindEgressBlock(exception, out var blocked))
        {
            return (ProxyErrorKind.Blocked, blocked.Message);
        }

        if (TryFindTargetRefusal(exception, out var refused))
        {
            return (ProxyErrorKind.TargetNotAllowed, refused.Message);
        }

        if (exception.InnerException is AuthenticationException tlsException)
        {
            return (ProxyErrorKind.Tls, $"TLS handshake failed: {tlsException.Message}");
        }

        var kind = exception.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => ProxyErrorKind.Dns,
            HttpRequestError.SecureConnectionError => ProxyErrorKind.Tls,
            HttpRequestError.ConnectionError => ProxyErrorKind.Connection,
            HttpRequestError.ProxyTunnelError => ProxyErrorKind.Connection,
            _ => ProxyErrorKind.Unexpected
        };

        if (kind == ProxyErrorKind.Unexpected && exception.InnerException is SocketException socketException)
        {
            kind = socketException.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => ProxyErrorKind.Dns,
                SocketError.ConnectionRefused
                    or SocketError.ConnectionReset
                    or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable
                    or SocketError.TimedOut => ProxyErrorKind.Connection,
                _ => ProxyErrorKind.Unexpected
            };
        }

        return (kind, Describe(exception));
    }

    /// <summary>
    /// Finds an <see cref="EgressBlockedException"/> in <paramref name="exception"/> or its inner
    /// exceptions. The handler wraps whatever its connect callback throws, so the refusal
    /// arrives one or more levels down.
    /// </summary>
    public static bool TryFindEgressBlock(Exception exception, [NotNullWhen(true)] out EgressBlockedException? blocked) =>
        TryFind(exception, out blocked);

    /// <summary>
    /// Finds a <see cref="TargetNotAllowedException"/> in <paramref name="exception"/> or its inner
    /// exceptions, where the handler leaves it as it does a blocked network.
    /// </summary>
    public static bool TryFindTargetRefusal(Exception exception, [NotNullWhen(true)] out TargetNotAllowedException? refused) =>
        TryFind(exception, out refused);

    private static bool TryFind<T>(Exception exception, [NotNullWhen(true)] out T? found)
        where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                found = match;
                return true;
            }
        }

        found = null;
        return false;
    }

    /// <summary>The exception message plus up to three inner messages, which is where the detail usually is.</summary>
    public static string Describe(Exception exception)
    {
        var message = new StringBuilder(exception.Message);
        var inner = exception.InnerException;
        var depth = 0;
        while (inner is not null && depth < 3)
        {
            message.Append(" -> ").Append(inner.Message);
            inner = inner.InnerException;
            depth++;
        }

        return message.ToString();
    }
}
