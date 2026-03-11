# How-to: Manage Sessions/Tokens with StreamingTokenManager

Use `StreamingTokenManager<TSession, TCredentials>` when your service exposes session-like tokens that must be refreshed and optionally persisted. It coordinates refreshes, exposes status, and integrates with `IStreamingTokenProvider` or your own HTTP handlers.

## When to use

- OAuth flows where you cache a session model and want proactive refresh before expiry.
- Non-OAuth services with session cookies or signed tokens that rotate.
- You need thread-safe, single-flight refresh with events and persistence.

## Minimal setup

```csharp
// Define your credential + session types (or reuse existing)
public sealed record MyCredentials(string ClientId, string ClientSecret, string RefreshToken);
public sealed record MySession(string AccessToken, DateTime ExpiresAt);

// Implement a thin auth service the manager can call
public sealed class MyAuthService : IStreamingTokenAuthenticationService<MySession, MyCredentials>
{
    public Task<MySession> AuthenticateAsync(MyCredentials c)
        => ExchangeRefreshTokenAsync(c);

    public Task<bool> ValidateSessionAsync(MySession s)
        => Task.FromResult(s.ExpiresAt > DateTime.UtcNow.AddMinutes(1));

    private static Task<MySession> ExchangeRefreshTokenAsync(MyCredentials c)
        => Task.FromResult(new MySession("token", DateTime.UtcNow.AddHours(1)));
}

// Optional: choose a token store (file/OS secrets/etc.) implementing ITokenStore<T>
ITokenStore<MySession>? store = new FileTokenStore<MySession>(paths, logger);

var options = new StreamingTokenManagerOptions<MySession>
{
    DefaultSessionLifetime = TimeSpan.FromHours(1),
    RefreshBuffer = TimeSpan.FromMinutes(5),
    GetSessionExpiry = s => s.ExpiresAt
};

var mgr = new StreamingTokenManager<MySession, MyCredentials>(
    authService: new MyAuthService(),
    logger: loggerFactory.CreateLogger<StreamingTokenManager<MySession, MyCredentials>>(),
    tokenStore: store,
    options: options);
```

## Getting a valid session

```csharp
var session = await mgr.GetValidSessionAsync(new MyCredentials(clientId: cid, clientSecret: secret, refreshToken: refreshToken));
// Use session.AccessToken in your delegating handler or client
```

## With OAuthDelegatingHandler

If you already expose an `IStreamingTokenProvider`, adapt the manager behind it and plug into `OAuthDelegatingHandler`.

```csharp
public sealed class ManagerBackedTokenProvider : IStreamingTokenProvider
{
    private readonly StreamingTokenManager<MySession, MyCredentials> _mgr;
    private readonly MyCredentials _credentials;
    public string ServiceName => "MyService";
    public bool SupportsRefresh => true;

    public ManagerBackedTokenProvider(StreamingTokenManager<MySession, MyCredentials> mgr, MyCredentials credentials)
    { _mgr = mgr; _credentials = credentials; }

    public async Task<string> GetAccessTokenAsync()
        => (await _mgr.GetValidSessionAsync(_credentials)).AccessToken;

    public async Task<string> RefreshTokenAsync()
    { await _mgr.RefreshSessionAsync(_credentials); return (await _mgr.GetValidSessionAsync()).AccessToken; }

    public Task<bool> ValidateTokenAsync(string token) => Task.FromResult(!string.IsNullOrEmpty(token));
    public DateTime? GetTokenExpiration(string token) => _mgr.GetSessionStatus().ExpiresAt;
    public void ClearAuthenticationCache() => _mgr.ClearSession();
}

var http = HttpClientFactory.Create(
    new OAuthDelegatingHandler(new ManagerBackedTokenProvider(mgr, credentials), logger));
```

## Events and status

```csharp
mgr.SessionRefreshed += (_, e) => logger.LogInformation("Session refresh ok; expires {Expiry}", e.ExpiresAt);
mgr.SessionRefreshFailed += (_, e) => logger.LogWarning(e.Exception, "Session refresh failed #{Attempt}", e.AttemptNumber);
var status = mgr.GetSessionStatus(); // IsValid, TimeUntilExpiry, etc.
```

## Proactive (Background) Token Refresh

By default, the manager only refreshes tokens on-demand (when `GetValidSessionAsync` is called). To enable automatic background refresh before tokens expire, configure a credentials provider:

```csharp
var options = new StreamingTokenManagerOptions<MySession>
{
    DefaultSessionLifetime = TimeSpan.FromHours(1),
    RefreshBuffer = TimeSpan.FromMinutes(5),  // Refresh 5 min before expiry   
    RefreshCheckInterval = TimeSpan.FromMinutes(1),  // Check every minute     
    GetSessionExpiry = s => s.ExpiresAt,

    // Enable proactive refresh by providing a credentials factory
    EnableProactiveRefresh = true,
    ProactiveRefreshCredentialsProvider = () => new MyCredentials(clientId, clientSecret, refreshToken)
};
```

When configured:
- A background timer runs every `RefreshCheckInterval`
- If the session is within `RefreshBuffer` of expiry, the manager calls the credentials provider
- If credentials are available, it performs a refresh automatically
- Refresh failures are logged but don't throw - the timer will retry on the next interval

This is particularly useful for long-running operations that may exceed token lifetimes.

## Tips

- Persist sessions with `ITokenStore<T>` (file or OS-backed) so restarts don't force re-auth.
- Do not log raw tokens. Use masking helpers and structured logs.
- Keep refresh single-flight; the manager already guards with a semaphore.
- Use `OAuthDelegatingHandler` for 401-triggered single-flight refresh on top of proactive refresh.

See also: AUTHENTICATE_OAUTH.md, reference/KEY_SERVICES.md.
