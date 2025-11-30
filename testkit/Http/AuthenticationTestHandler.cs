using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.TestKit.Http;

/// <summary>
/// Test handler that simulates OAuth/token authentication flows.
/// Useful for testing authentication service implementations.
/// </summary>
public class AuthenticationTestHandler : DelegatingHandler
{
    private readonly AuthenticationTestOptions _options;
    private string? _currentAccessToken;
    private string? _currentRefreshToken;
    private DateTime _tokenExpiration = DateTime.MinValue;
    private int _refreshCount;

    public AuthenticationTestHandler(AuthenticationTestOptions? options = null)
    {
        _options = options ?? new AuthenticationTestOptions();
        InnerHandler = new HttpClientHandler();
    }

    /// <summary>Number of times the token was refreshed.</summary>
    public int RefreshCount => _refreshCount;

    /// <summary>Current access token (for test assertions).</summary>
    public string? CurrentAccessToken => _currentAccessToken;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await Task.Yield(); // Simulate async work

        // Handle token endpoint
        if (request.RequestUri?.AbsolutePath.Contains(_options.TokenEndpoint, StringComparison.OrdinalIgnoreCase) == true)
        {
            return await HandleTokenRequestAsync(request, cancellationToken);
        }

        // Handle authenticated requests
        if (request.Headers.Authorization != null)
        {
            var token = request.Headers.Authorization.Parameter;

            // Check if token matches current valid token
            if (token == _currentAccessToken && DateTime.UtcNow < _tokenExpiration)
            {
                return CreateSuccessResponse();
            }

            // Token expired or invalid
            if (DateTime.UtcNow >= _tokenExpiration)
            {
                return CreateErrorResponse(HttpStatusCode.Unauthorized, "token_expired", "Access token has expired");
            }

            return CreateErrorResponse(HttpStatusCode.Unauthorized, "invalid_token", "Access token is invalid");
        }

        // No authentication provided
        return CreateErrorResponse(HttpStatusCode.Unauthorized, "missing_auth", "Authentication required");
    }

    private Task<HttpResponseMessage> HandleTokenRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var content = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "";

        // Simulate grant_type handling
        if (content.Contains("grant_type=password") || content.Contains("grant_type=client_credentials"))
        {
            // Initial authentication
            if (_options.SimulateAuthFailure)
            {
                return Task.FromResult(CreateErrorResponse(HttpStatusCode.Unauthorized, "invalid_grant", "Invalid credentials"));
            }

            return Task.FromResult(CreateTokenResponse());
        }

        if (content.Contains("grant_type=refresh_token"))
        {
            _refreshCount++;

            if (_options.SimulateRefreshFailure)
            {
                return Task.FromResult(CreateErrorResponse(HttpStatusCode.Unauthorized, "invalid_grant", "Refresh token expired"));
            }

            return Task.FromResult(CreateTokenResponse());
        }

        return Task.FromResult(CreateErrorResponse(HttpStatusCode.BadRequest, "unsupported_grant_type", "Grant type not supported"));
    }

    private HttpResponseMessage CreateTokenResponse()
    {
        _currentAccessToken = Guid.NewGuid().ToString("N");
        _currentRefreshToken = Guid.NewGuid().ToString("N");
        _tokenExpiration = DateTime.UtcNow.Add(_options.TokenLifetime);

        var response = new
        {
            access_token = _currentAccessToken,
            refresh_token = _currentRefreshToken,
            token_type = "Bearer",
            expires_in = (int)_options.TokenLifetime.TotalSeconds,
            scope = _options.Scope
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(response),
                Encoding.UTF8,
                "application/json")
        };
    }

    private static HttpResponseMessage CreateSuccessResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage CreateErrorResponse(HttpStatusCode status, string error, string description)
    {
        var errorResponse = new
        {
            error = error,
            error_description = description
        };

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(errorResponse),
                Encoding.UTF8,
                "application/json")
        };
    }

    /// <summary>
    /// Forces the current token to expire for testing refresh flows.
    /// </summary>
    public void ExpireToken()
    {
        _tokenExpiration = DateTime.UtcNow.AddSeconds(-1);
    }

    /// <summary>
    /// Invalidates the current token (simulates server-side revocation).
    /// </summary>
    public void InvalidateToken()
    {
        _currentAccessToken = null;
        _tokenExpiration = DateTime.MinValue;
    }
}

/// <summary>
/// Options for configuring <see cref="AuthenticationTestHandler"/>.
/// </summary>
public class AuthenticationTestOptions
{
    /// <summary>Token endpoint path (default: "/oauth/token").</summary>
    public string TokenEndpoint { get; init; } = "/oauth/token";

    /// <summary>Token lifetime (default: 1 hour).</summary>
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>OAuth scope to include in response.</summary>
    public string Scope { get; init; } = "read write";

    /// <summary>If true, initial authentication will fail.</summary>
    public bool SimulateAuthFailure { get; init; }

    /// <summary>If true, token refresh will fail.</summary>
    public bool SimulateRefreshFailure { get; init; }
}

/// <summary>
/// Test handler that simulates rate limiting responses.
/// </summary>
public class RateLimitTestHandler : DelegatingHandler
{
    private readonly RateLimitTestOptions _options;
    private int _requestCount;
    private DateTime _windowStart = DateTime.UtcNow;

    public RateLimitTestHandler(RateLimitTestOptions? options = null)
    {
        _options = options ?? new RateLimitTestOptions();
        InnerHandler = new HttpClientHandler();
    }

    /// <summary>Total number of requests made.</summary>
    public int RequestCount => _requestCount;

    /// <summary>Number of requests that were rate limited.</summary>
    public int RateLimitedCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Reset window if expired
        if (DateTime.UtcNow - _windowStart > _options.WindowDuration)
        {
            _requestCount = 0;
            _windowStart = DateTime.UtcNow;
        }

        _requestCount++;

        // Check rate limit
        if (_requestCount > _options.RequestsPerWindow)
        {
            RateLimitedCount++;
            var retryAfter = (int)(_options.WindowDuration - (DateTime.UtcNow - _windowStart)).TotalSeconds;
            return Task.FromResult(CreateRateLimitResponse(retryAfter));
        }

        var remaining = _options.RequestsPerWindow - _requestCount;
        return Task.FromResult(CreateSuccessResponse(remaining));
    }

    private HttpResponseMessage CreateSuccessResponse(int remaining)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json")
        };

        response.Headers.Add("X-RateLimit-Limit", _options.RequestsPerWindow.ToString());
        response.Headers.Add("X-RateLimit-Remaining", remaining.ToString());
        response.Headers.Add("X-RateLimit-Reset", ((DateTimeOffset)_windowStart.Add(_options.WindowDuration)).ToUnixTimeSeconds().ToString());

        return response;
    }

    private HttpResponseMessage CreateRateLimitResponse(int retryAfter)
    {
        var response = new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = "rate_limit_exceeded", retry_after = retryAfter }),
                Encoding.UTF8,
                "application/json")
        };

        response.Headers.Add("Retry-After", retryAfter.ToString());
        response.Headers.Add("X-RateLimit-Limit", _options.RequestsPerWindow.ToString());
        response.Headers.Add("X-RateLimit-Remaining", "0");

        return response;
    }

    /// <summary>
    /// Resets the rate limit window for testing.
    /// </summary>
    public void ResetWindow()
    {
        _requestCount = 0;
        _windowStart = DateTime.UtcNow;
    }
}

/// <summary>
/// Options for configuring <see cref="RateLimitTestHandler"/>.
/// </summary>
public class RateLimitTestOptions
{
    /// <summary>Maximum requests per window (default: 100).</summary>
    public int RequestsPerWindow { get; init; } = 100;

    /// <summary>Duration of the rate limit window (default: 1 minute).</summary>
    public TimeSpan WindowDuration { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Test handler that simulates server errors with configurable patterns.
/// </summary>
public class ErrorSimulationHandler : DelegatingHandler
{
    private readonly ErrorSimulationOptions _options;
    private int _requestCount;
    private readonly Random _random = new();

    public ErrorSimulationHandler(ErrorSimulationOptions? options = null)
    {
        _options = options ?? new ErrorSimulationOptions();
        InnerHandler = new HttpClientHandler();
    }

    /// <summary>Total number of requests made.</summary>
    public int RequestCount => _requestCount;

    /// <summary>Number of errors simulated.</summary>
    public int ErrorCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _requestCount++;

        // Check for specific error patterns
        if (_options.FailOnRequestNumber > 0 && _requestCount == _options.FailOnRequestNumber)
        {
            ErrorCount++;
            return Task.FromResult(CreateErrorResponse(_options.ErrorStatusCode, _options.ErrorMessage));
        }

        // Random failure chance
        if (_options.FailureChance > 0 && _random.NextDouble() < _options.FailureChance)
        {
            ErrorCount++;
            return Task.FromResult(CreateErrorResponse(_options.ErrorStatusCode, _options.ErrorMessage));
        }

        // Simulate timeout
        if (_options.SimulateTimeout)
        {
            throw new TaskCanceledException("Request timeout simulated");
        }

        // Simulate network error
        if (_options.SimulateNetworkError)
        {
            throw new HttpRequestException("Network error simulated");
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json")
        });
    }

    private static HttpResponseMessage CreateErrorResponse(HttpStatusCode status, string message)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = message }),
                Encoding.UTF8,
                "application/json")
        };
    }
}

/// <summary>
/// Options for configuring <see cref="ErrorSimulationHandler"/>.
/// </summary>
public class ErrorSimulationOptions
{
    /// <summary>Fail on a specific request number (0 = disabled).</summary>
    public int FailOnRequestNumber { get; init; }

    /// <summary>Random failure chance (0.0 to 1.0).</summary>
    public double FailureChance { get; init; }

    /// <summary>HTTP status code for errors (default: 500).</summary>
    public HttpStatusCode ErrorStatusCode { get; init; } = HttpStatusCode.InternalServerError;

    /// <summary>Error message to return.</summary>
    public string ErrorMessage { get; init; } = "Internal server error";

    /// <summary>If true, simulate request timeout.</summary>
    public bool SimulateTimeout { get; init; }

    /// <summary>If true, simulate network error.</summary>
    public bool SimulateNetworkError { get; init; }
}
