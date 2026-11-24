using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Proxylayer.DevConduit.Models;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>Turning an outbound-call exception into a stable error kind.</summary>
public sealed class HttpFailureClassifierTests
{
    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, ProxyErrorKind.Dns)]
    [InlineData(HttpRequestError.SecureConnectionError, ProxyErrorKind.Tls)]
    [InlineData(HttpRequestError.ConnectionError, ProxyErrorKind.Connection)]
    [InlineData(HttpRequestError.ProxyTunnelError, ProxyErrorKind.Connection)]
    [InlineData(HttpRequestError.InvalidResponse, ProxyErrorKind.Unexpected)]
    [InlineData(HttpRequestError.ResponseEnded, ProxyErrorKind.Unexpected)]
    [InlineData(HttpRequestError.Unknown, ProxyErrorKind.Unexpected)]
    public void Classify_MapsTheReportedRequestError(HttpRequestError error, ProxyErrorKind expected)
    {
        var (kind, _) = HttpFailureClassifier.Classify(new HttpRequestException(error, "failed"));

        Assert.Equal(expected, kind);
    }

    [Fact]
    public void Classify_PrefersATlsHandshakeFailureOverTheReportedError()
    {
        var exception = new HttpRequestException(
            HttpRequestError.ConnectionError,
            "outer",
            new AuthenticationException("The remote certificate is invalid."));

        var (kind, message) = HttpFailureClassifier.Classify(exception);

        Assert.Equal(ProxyErrorKind.Tls, kind);
        Assert.Contains("TLS handshake failed", message);
        Assert.Contains("The remote certificate is invalid.", message);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, ProxyErrorKind.Dns)]
    [InlineData(SocketError.NoData, ProxyErrorKind.Dns)]
    [InlineData(SocketError.TryAgain, ProxyErrorKind.Dns)]
    [InlineData(SocketError.ConnectionRefused, ProxyErrorKind.Connection)]
    [InlineData(SocketError.ConnectionReset, ProxyErrorKind.Connection)]
    [InlineData(SocketError.HostUnreachable, ProxyErrorKind.Connection)]
    [InlineData(SocketError.NetworkUnreachable, ProxyErrorKind.Connection)]
    [InlineData(SocketError.TimedOut, ProxyErrorKind.Connection)]
    [InlineData(SocketError.AccessDenied, ProxyErrorKind.Unexpected)]
    public void Classify_FallsBackToTheSocketErrorWhenNothingElseIdentifiesTheFailure(
        SocketError socketError,
        ProxyErrorKind expected)
    {
        var exception = new HttpRequestException(
            HttpRequestError.Unknown,
            "outer",
            new SocketException((int)socketError));

        var (kind, _) = HttpFailureClassifier.Classify(exception);

        Assert.Equal(expected, kind);
    }

    [Fact]
    public void Classify_KeepsAnIdentifiedErrorEvenWhenASocketErrorIsAlsoPresent()
    {
        var exception = new HttpRequestException(
            HttpRequestError.ConnectionError,
            "outer",
            new SocketException((int)SocketError.HostNotFound));

        var (kind, _) = HttpFailureClassifier.Classify(exception);

        Assert.Equal(ProxyErrorKind.Connection, kind);
    }

    [Fact]
    public void Classify_ReportsADirectlyWrappedEgressBlockAsBlocked()
    {
        var blocked = new EgressBlockedException("meta", IPAddress.Parse("169.254.169.254"));
        var exception = new HttpRequestException(HttpRequestError.ConnectionError, "outer", blocked);

        var (kind, message) = HttpFailureClassifier.Classify(exception);

        Assert.Equal(ProxyErrorKind.Blocked, kind);
        Assert.Equal(blocked.Message, message);
    }

    [Fact]
    public void Classify_ReportsADeeplyNestedEgressBlockAsBlocked()
    {
        var blocked = new EgressBlockedException("meta", IPAddress.Parse("169.254.169.254"));
        var exception = new HttpRequestException(
            HttpRequestError.Unknown,
            "outer",
            new IOException("middle", new InvalidOperationException("inner", blocked)));

        var (kind, message) = HttpFailureClassifier.Classify(exception);

        Assert.Equal(ProxyErrorKind.Blocked, kind);
        Assert.Equal(blocked.Message, message);
    }

    [Fact]
    public void TryFindEgressBlock_FindsTheExceptionItself()
    {
        var blocked = new EgressBlockedException("meta", IPAddress.Parse("169.254.169.254"));

        Assert.True(HttpFailureClassifier.TryFindEgressBlock(blocked, out var found));
        Assert.Same(blocked, found);
    }

    [Fact]
    public void TryFindEgressBlock_FindsANestedException()
    {
        var blocked = new EgressBlockedException("meta", IPAddress.Parse("fe80::1"));
        var exception = new HttpRequestException("outer", new IOException("middle", blocked));

        Assert.True(HttpFailureClassifier.TryFindEgressBlock(exception, out var found));
        Assert.Same(blocked, found);
    }

    [Fact]
    public void TryFindEgressBlock_ReturnsFalseWhenThereIsNone()
    {
        var exception = new HttpRequestException(
            "outer",
            new IOException("middle", new SocketException((int)SocketError.ConnectionRefused)));

        Assert.False(HttpFailureClassifier.TryFindEgressBlock(exception, out var found));
        Assert.Null(found);
    }

    [Fact]
    public void Describe_JoinsTheMessageToItsInnerMessages()
    {
        var exception = new InvalidOperationException(
            "one",
            new InvalidOperationException("two", new InvalidOperationException("three")));

        Assert.Equal("one -> two -> three", HttpFailureClassifier.Describe(exception));
    }

    [Fact]
    public void Describe_StopsAfterThreeInnerMessages()
    {
        var exception = new InvalidOperationException(
            "one",
            new InvalidOperationException(
                "two",
                new InvalidOperationException(
                    "three",
                    new InvalidOperationException("four", new InvalidOperationException("five")))));

        Assert.Equal("one -> two -> three -> four", HttpFailureClassifier.Describe(exception));
    }

    [Fact]
    public void Describe_ReturnsTheMessageAloneWhenThereIsNoInnerException() =>
        Assert.Equal("only", HttpFailureClassifier.Describe(new InvalidOperationException("only")));
}
