using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Lidarr.Plugin.Common.Base;
using Lidarr.Plugin.Common.Models;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Services.Performance;
using Lidarr.Plugin.Common.Utilities;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Runtime.CompilerServices;

namespace Lidarr.Plugin.Common.Base
{
    /// <summary>
    /// Base class for streaming service indexers implementing common patterns
    /// Reduces implementation effort by 60-70% through shared infrastructure
    /// </summary>
    /// <typeparam name="TSettings">Settings type inheriting from BaseStreamingSettings</typeparam>
    public abstract class BaseStreamingIndexer<TSettings> : IDisposable
        where TSettings : BaseStreamingSettings, new()
    {
        #region Protected Properties

        /// <summary>
        /// Service name for logging and metrics (e.g., "Qobuz", "Tidal", "Spotify")
        /// </summary>
        protected abstract string ServiceName { get; }

        /// <summary>
        /// Protocol identifier for Lidarr integration
        /// </summary>
        protected abstract string ProtocolName { get; }

        /// <summary>
        /// Settings for this streaming service
        /// </summary>
        protected TSettings Settings { get; private set; }

        /// <summary>
        /// Logger instance for this indexer
        /// </summary>
        protected ILogger Logger { get; private set; }

        /// <summary>
        /// Performance monitoring for metrics collection
        /// </summary>
        protected PerformanceMonitor PerformanceMonitor { get; private set; }

        #endregion

        #region Private Fields

        private readonly StreamingApiRequestBuilder _requestBuilder;
        private readonly object _initializationLock = new object();
        private bool _isInitialized = false;
        private bool _disposed = false;

        private static readonly HttpClient SharedHttpClient = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(100)
        };

        /// <summary>
        /// Provides the HttpClient used for API calls. Override to inject custom handlers (e.g., OAuthDelegatingHandler).
        /// Defaults to a shared, decompression-enabled client.
        /// </summary>
        protected virtual HttpClient GetHttpClient() => SharedHttpClient;

        #endregion

        #region Constructor

        protected BaseStreamingIndexer(TSettings settings, ILogger logger = null)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Logger = logger ?? CreateDefaultLogger();
            
            PerformanceMonitor = new PerformanceMonitor(TimeSpan.FromMinutes(5));
            _requestBuilder = new StreamingApiRequestBuilder(settings.BaseUrl);
        }

        #endregion

        #region Abstract Methods - Service-Specific Implementation

        /// <summary>
        /// Authenticate with the streaming service
        /// </summary>
        protected abstract Task<bool> AuthenticateAsync();

        /// <summary>
        /// Search for albums on the streaming service
        /// </summary>
        protected abstract Task<List<StreamingAlbum>> SearchAlbumsAsync(string searchTerm);

        /// <summary>
        /// Optional streaming variant for album search. Default wraps the list-based implementation.
        /// </summary>
        protected virtual async IAsyncEnumerable<StreamingAlbum> SearchAlbumsStreamAsync(
            string searchTerm,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var list = await SearchAlbumsAsync(searchTerm).ConfigureAwait(false);
            foreach (var item in list)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }

        /// <summary>
        /// Search for tracks on the streaming service
        /// </summary>
        protected abstract Task<List<StreamingTrack>> SearchTracksAsync(string searchTerm);

        /// <summary>
        /// Optional streaming variant for track search. Default wraps the list-based implementation.
        /// </summary>
        protected virtual async IAsyncEnumerable<StreamingTrack> SearchTracksStreamAsync(
            string searchTerm,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var list = await SearchTracksAsync(searchTerm).ConfigureAwait(false);
            foreach (var item in list)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }

        /// <summary>
        /// Get album details including tracks
        /// </summary>
        protected abstract Task<StreamingAlbum> GetAlbumDetailsAsync(string albumId);

        /// <summary>
        /// Validate service-specific settings
        /// </summary>
        protected abstract ValidationResult ValidateSettings(TSettings settings);

        #endregion

        #region Virtual Methods - Override if Needed

        /// <summary>
        /// Pre-process search query (normalize, optimize, etc.)
        /// </summary>
        protected virtual string PreprocessQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return string.Empty;

            // Basic sanitization and normalization
            var sanitized = Guard.NotNullOrEmpty(query, nameof(query));
            
            // Remove extra whitespace
            sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"\s+", " ").Trim();

            return sanitized;
        }

        /// <summary>
        /// Post-process search results (filter, sort, deduplicate)
        /// </summary>
        protected virtual List<StreamingAlbum> PostprocessResults(List<StreamingAlbum> results)
        {
            if (results == null || !results.Any())
                return new List<StreamingAlbum>();

            // Enhanced deduplication by title + artist + year
            var deduplicated = results
                .GroupBy(album => $"{album.Title?.Trim().ToLowerInvariant()}|{album.Artist?.Name?.Trim().ToLowerInvariant() ?? ""}|{album.ReleaseDate?.Year}")
                .Select(group => group.First())
                .ToList();

            // Sort by relevance (albums with more tracks first)
            return deduplicated
                .OrderByDescending(album => album.TrackCount)
                .ThenBy(album => album.Title)
                .ToList();
        }

        /// <summary>
        /// Handle rate limiting before making API calls
        /// </summary>
        protected virtual async Task HandleRateLimitAsync()
        {
            // Basic rate limiting - services can override with more sophisticated logic
            await Task.Delay(100);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Initialize the indexer (authenticate, validate settings, etc.)
        /// </summary>
        public async Task<ValidationResult> InitializeAsync()
        {
            lock (_initializationLock)
            {
                if (_isInitialized)
                    return new ValidationResult();
            }

            try
            {
                Logger?.LogInformation($"Initializing {ServiceName} indexer");

                // Validate settings
                var validationResult = ValidateSettings(Settings);
                if (!validationResult.IsValid)
                {
                    Logger?.LogError($"{ServiceName} settings validation failed: {string.Join(", ", validationResult.Errors.Select(e => e.ErrorMessage))}");
                    return validationResult;
                }

                // Authenticate with service
                var authResult = await AuthenticateAsync();
                if (!authResult)
                {
                    var authError = new ValidationResult();
                    authError.Errors.Add(new FluentValidation.Results.ValidationFailure("Authentication", $"Failed to authenticate with {ServiceName}"));
                    return authError;
                }

                lock (_initializationLock)
                {
                    _isInitialized = true;
                }

                Logger?.LogInformation($"{ServiceName} indexer initialized successfully");
                return new ValidationResult();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, $"Failed to initialize {ServiceName} indexer");
                var errorResult = new ValidationResult();
                errorResult.Errors.Add(new FluentValidation.Results.ValidationFailure("Initialization", $"Initialization failed: {ex.Message}"));
                return errorResult;
            }
        }

        /// <summary>
        /// Perform a search across the streaming service
        /// </summary>
        public async Task<List<StreamingAlbum>> SearchAsync(string query)
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException($"{ServiceName} indexer not initialized. Call InitializeAsync() first.");
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<StreamingAlbum>();
            }

            try
            {
                var startTime = DateTime.UtcNow;
                
                // Pre-process query
                var processedQuery = PreprocessQuery(query);
                Logger?.LogDebug($"Searching {ServiceName} for: {processedQuery}");

                // Handle rate limiting
                await HandleRateLimitAsync();

                // Perform search
                var results = await SearchAlbumsAsync(processedQuery);

                // Post-process results
                var processedResults = PostprocessResults(results);

                // Record performance metrics
                var duration = DateTime.UtcNow - startTime;
                PerformanceMonitor?.RecordOperation($"search_{ServiceName.ToLowerInvariant()}", duration, success: (processedResults?.Count ?? 0) > 0);

                Logger?.LogDebug($"{ServiceName} search returned {processedResults?.Count ?? 0} results in {duration.TotalMilliseconds}ms");

                return processedResults ?? new List<StreamingAlbum>();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, $"{ServiceName} search failed for query: {query}");
                PerformanceMonitor?.RecordOperation($"search_{ServiceName.ToLowerInvariant()}", TimeSpan.Zero, success: false);
                throw;
            }
        }

        #endregion

        #region Protected Utilities

        /// <summary>
        /// Create HTTP request using shared library patterns
        /// </summary>
        protected System.Net.Http.HttpRequestMessage CreateRequest(string endpoint, Dictionary<string, string> queryParams = null)
        {
            var builder = new StreamingApiRequestBuilder(Settings.BaseUrl)
                .Endpoint(endpoint)
                .WithStreamingDefaults($"{ServiceName}arr/1.0");

            if (queryParams != null)
            {
                foreach (var param in queryParams)
                {
                    builder.Query(param.Key, param.Value);
                }
            }

            return builder.Build();
        }

        /// <summary>
        /// Execute HTTP request with retry logic and error handling
        /// </summary>
        protected async Task<string> ExecuteRequestAsync(System.Net.Http.HttpRequestMessage request)
        {
            using var response = await GetHttpClient().ExecuteWithResilienceAsync(
                request,
                maxRetries: 5,
                retryBudget: TimeSpan.FromSeconds(60),
                maxConcurrencyPerHost: 6
            );

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// Helper to page through API results by offset. Provide a fetcher that returns an empty list to terminate.
        /// </summary>
        protected async IAsyncEnumerable<T> FetchPagedAsync<T>(
            Func<int, Task<IReadOnlyList<T>>> fetchPageAsync,
            int pageSize,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (fetchPageAsync == null) yield break;
            var offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await fetchPageAsync(offset).ConfigureAwait(false);
                if (page == null || page.Count == 0) yield break;
                foreach (var item in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                }
                offset += Math.Max(1, pageSize);
            }
        }

        /// <summary>
        /// Streaming search across the service (optional). Default wraps the list-based search.
        /// </summary>
        public virtual async IAsyncEnumerable<StreamingAlbum> SearchStreamAsync(
            string query,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var list = await SearchAsync(query).ConfigureAwait(false);
            foreach (var item in list)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }

        /// <summary>
        /// Initialize with cancellation support. Default forwards to InitializeAsync().
        /// </summary>
        public virtual Task<ValidationResult> InitializeAsync(CancellationToken cancellationToken)
            => InitializeAsync();

        #endregion

        #region Private Methods

        private ILogger CreateDefaultLogger()
        {
            // Create a simple console logger if none provided
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            return loggerFactory.CreateLogger($"{ServiceName}Indexer");
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;

            PerformanceMonitor?.Dispose();
            _disposed = true;
        }

        #endregion
    }
}
