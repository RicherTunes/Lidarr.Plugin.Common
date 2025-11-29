# Streaming Plugin Glossary

Common terminology used across Lidarr streaming plugins (Tidalarr, Qobuzarr, Brainarr).

## Audio Quality Terms

### Bitrate
The amount of data processed per second. Higher bitrate generally means better quality.
- **320 kbps**: High-quality MP3 (lossy compression)
- **1411 kbps**: CD quality (16-bit/44.1kHz FLAC)
- **4608 kbps**: Hi-Res (24-bit/96kHz FLAC)
- **9216 kbps**: Maximum Hi-Res (24-bit/192kHz FLAC)

### Lossless
Audio format that preserves all original audio data without compression artifacts. FLAC is the most common lossless format. Contrast with "lossy" formats like MP3.

### Hi-Res / Hi-Res Audio
Audio with resolution higher than CD quality (16-bit/44.1kHz). Typically 24-bit at 96kHz or 192kHz sample rates.

### Sample Rate
How many times per second audio is sampled. Measured in Hz or kHz.
- **44.1 kHz**: CD quality
- **48 kHz**: Standard for video
- **96 kHz**: Hi-Res
- **192 kHz**: Maximum Hi-Res

### Bit Depth
The number of bits used to represent each sample.
- **16-bit**: CD quality, 65,536 possible values
- **24-bit**: Hi-Res, 16.7 million possible values

### MQA (Master Quality Authenticated)
Tidal's proprietary format that encodes high-resolution audio in a smaller file. Requires MQA-capable hardware for full quality.

### FLAC (Free Lossless Audio Codec)
Open-source lossless audio format. Used by Qobuz, Tidal (non-MQA), and most high-quality downloads.

### AAC (Advanced Audio Coding)
Lossy audio format, often better quality than MP3 at same bitrate. Used in M4A containers.

### M4A
Container format that can hold AAC (lossy) or ALAC (lossless) audio. Tidal uses M4A containers.

## Authentication Terms

### OAuth 2.0
Industry-standard authorization protocol. Allows plugins to access streaming services on your behalf without storing your password.

### PKCE (Proof Key for Code Exchange)
Security extension for OAuth 2.0, prevents authorization code interception attacks. Pronounced "pixie."

### Access Token
Short-lived credential that authorizes API requests. Typically expires in minutes to hours.

### Refresh Token
Long-lived credential used to obtain new access tokens without re-authentication.

### App ID / Client ID
Unique identifier for the application accessing the API. Required for authentication.

### App Secret / Client Secret
Secret key paired with App ID. Should be kept confidential.

## Plugin Architecture Terms

### Indexer
Component that searches the streaming service for content. Returns search results to Lidarr.

### Download Client
Component that downloads content from the streaming service. Handles file transfer and storage.

### Import List
Component that recommends or imports content into Lidarr. Used by Brainarr for AI recommendations.

### ALC (AssemblyLoadContext)
.NET mechanism for loading assemblies in isolation. Allows plugins to run without conflicting with each other.

### Abstractions
Shared interfaces between host (Lidarr) and plugins. Defines the contract plugins must implement.

### Common Library (Lidarr.Plugin.Common)
Shared utility library providing HTTP clients, authentication, caching, and other infrastructure.

## Streaming Service Terms

### Region / Market
Geographic area for content availability. Some content is restricted to certain regions.

### Catalog
The complete library of content available on a streaming service.

### Track ID / Album ID
Unique identifier for content on the streaming service. Used for downloads and metadata lookup.

### Manifest
Metadata file describing available audio streams. Used for adaptive streaming and quality selection.

### DASH (Dynamic Adaptive Streaming over HTTP)
Streaming protocol that adapts quality based on network conditions. Used by Tidal.

## Caching Terms

### Cache Hit
When requested data is found in the cache, avoiding an API call.

### Cache Miss
When requested data is NOT in cache, requiring an API call.

### TTL (Time To Live)
How long cached data remains valid before expiring.

### ETag
HTTP header for cache validation. Allows checking if content changed without re-downloading.

### 304 Not Modified
HTTP response indicating cached content is still valid.

## Rate Limiting Terms

### Rate Limit
Maximum number of API requests allowed in a time period. Exceeding causes errors.

### Backoff
Waiting period after hitting rate limits. Can be exponential (increasing delays).

### Throttling
Slowing down request rate to stay within limits.

### Circuit Breaker
Pattern that stops requests to a failing service temporarily, preventing cascade failures.

## AI Provider Terms (Brainarr)

### Provider
AI service that generates recommendations (OpenAI, Anthropic, Ollama, etc.).

### Model
Specific AI model within a provider (GPT-4, Claude, Llama, etc.).

### Token
Unit of text for AI processing. Roughly 4 characters in English.

### Token Budget
Maximum tokens allowed for a request. Affects response length and cost.

### Extended Thinking
Anthropic's feature allowing Claude to "think" before responding, improving complex reasoning.

### Discovery Mode
How adventurous recommendations should be:
- **Similar**: Stay close to existing library
- **Adjacent**: Explore related genres
- **Exploratory**: Discover new territory

### Failover
Automatic switching to backup provider when primary fails.

### Local Provider
AI running on your own hardware (Ollama, LM Studio). No API costs, full privacy.

### Cloud Provider
AI service running on provider's servers (OpenAI, Anthropic). Requires API key, may incur costs.

## Lidarr Terms

### Root Folder
Directory where Lidarr stores downloaded music.

### Quality Profile
Settings defining acceptable audio quality for downloads.

### Metadata
Information about music: artist names, album titles, track listings, cover art.

### Tagging
Writing metadata into audio files (artist, title, album, etc.).

### Import
Moving/copying downloaded files to the library and updating the database.

## Error Terms

### 401 Unauthorized
Authentication failed. Usually means invalid or expired credentials.

### 403 Forbidden
Access denied. May indicate subscription level or geographic restrictions.

### 429 Too Many Requests
Rate limit exceeded. Wait before retrying.

### 500 Internal Server Error
Server-side problem. Usually temporary, retry later.

### 503 Service Unavailable
Service temporarily down. Retry later.

---

## See Also

- [Lidarr Documentation](https://wiki.servarr.com/lidarr)
- [OAuth 2.0 Specification](https://oauth.net/2/)
- [FLAC Format](https://xiph.org/flac/)
