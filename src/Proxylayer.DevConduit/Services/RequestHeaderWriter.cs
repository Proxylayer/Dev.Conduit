namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Places a header on an outbound message, choosing between the request headers and the
/// content headers the way <see cref="System.Net.Http.HttpRequestMessage"/> insists on.
/// </summary>
public static class RequestHeaderWriter
{
    public static void Apply(HttpRequestMessage message, string name, string value, ILogger logger)
    {
        if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
        {
            // The framework computes this; a user-supplied value would only ever be wrong.
            return;
        }

        if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                message.Headers.Host = value;
            }
            catch (FormatException)
            {
                logger.LogInformation("Ignoring an invalid Host header override: {Value}.", value);
            }

            return;
        }

        if (HttpContentTypes.ContentHeaderNames.Contains(name))
        {
            message.Content ??= new ByteArrayContent([]);

            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                message.Content.Headers.Remove("Content-Type");
            }

            if (!message.Content.Headers.TryAddWithoutValidation(name, value))
            {
                logger.LogInformation("Ignoring header {Name}: the value was rejected.", name);
            }

            return;
        }

        if (message.Headers.TryAddWithoutValidation(name, value))
        {
            return;
        }

        if (message.Content is not null && message.Content.Headers.TryAddWithoutValidation(name, value))
        {
            return;
        }

        logger.LogInformation("Ignoring header {Name}: the value was rejected.", name);
    }
}
