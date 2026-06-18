namespace LMS.PremiumProfile.Api.Middleware;

/// <summary>
/// Validates X-Api-Key and X-Api-Secret request headers against config values.
///
/// Config section (appsettings.json):
/// {
///   "ApiAuth": {
///     "ApiKey": "your-api-key",
///     "ApiSecret": "your-api-secret"
///   }
/// }
///
/// Both headers must be present and match — otherwise 401 is returned.
/// The /swagger and /health paths are excluded from auth.
/// </summary>
public class ApiKeyMiddleware
{
    private const string ApiKeyHeader    = "X-Api-Key";
    private const string ApiSecretHeader = "X-Api-Secret";

    private readonly RequestDelegate _next;
    private readonly string          _expectedKey;
    private readonly string          _expectedSecret;

    // Paths that bypass auth
    private static readonly string[] OpenPaths =
    [
        "/swagger",
        "/health",
        "/favicon.ico"
    ];

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next           = next;
        _expectedKey    = configuration["ApiAuth:ApiKey"]    ?? throw new InvalidOperationException("ApiAuth:ApiKey is not configured.");
        _expectedSecret = configuration["ApiAuth:ApiSecret"] ?? throw new InvalidOperationException("ApiAuth:ApiSecret is not configured.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Allow open paths through without auth
        if (OpenPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // Both headers must be present
        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var key) ||
            !context.Request.Headers.TryGetValue(ApiSecretHeader, out var secret))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Missing authentication headers. Provide X-Api-Key and X-Api-Secret."
            });
            return;
        }

        // Constant-time comparison to avoid timing attacks
        bool keyMatch    = string.Equals(key.ToString(),    _expectedKey,    StringComparison.Ordinal);
        bool secretMatch = string.Equals(secret.ToString(), _expectedSecret, StringComparison.Ordinal);

        if (!keyMatch || !secretMatch)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Invalid API key or secret."
            });
            return;
        }

        await _next(context);
    }
}
