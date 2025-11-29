# Common Troubleshooting Guide

This guide covers issues common to all Lidarr streaming plugins (Brainarr, Tidalarr, Qobuzarr). For plugin-specific issues, see the individual plugin troubleshooting guides.

## Plugin Discovery Issues

### Plugin not appearing in Lidarr

**Symptoms:**
- Plugin doesn't show in Settings > General > About > Plugins
- Plugin type (Indexer/Download Client/Import List) doesn't appear in Add dialog

**Solutions:**

1. **Verify file placement:**
   ```
   plugins/
   └── RicherTunes/
       └── [PluginName]/
           ├── Lidarr.Plugin.[Name].dll
           └── plugin.json
   ```

2. **Check Lidarr version:**
   - Requires Lidarr 2.14.2.4786+ on plugins/nightly branch
   - Check: Settings > General > Updates > Branch = `nightly`

3. **Restart Lidarr:**
   - Plugins are loaded at startup
   - Full restart required, not just refresh

4. **Check logs for loading errors:**
   ```bash
   # Docker
   docker logs lidarr 2>&1 | grep -i "plugin\|error"

   # Linux
   grep -i "plugin\|error" ~/.config/Lidarr/logs/lidarr.txt
   ```

### Plugin loads but shows errors

**Check log for:**
- `ReflectionTypeLoadException` - Assembly version mismatch
- `FileNotFoundException` - Missing dependency
- `TypeLoadException` - Interface incompatibility

**Solution:** Ensure plugin version matches your Lidarr version.

## Authentication Issues

### "Authentication failed" or "Invalid credentials"

**Common causes:**
1. Expired tokens (for OAuth-based services)
2. Incorrect API keys or secrets
3. Account subscription expired
4. Regional restrictions

**Solutions:**

1. **Re-authenticate:**
   - Go to Settings > [Plugin Type] > [Plugin Name]
   - Clear existing credentials
   - Re-enter or re-authorize

2. **Check service status:**
   - Verify your account works in the service's web interface
   - Check for service outages

3. **Token refresh:**
   - For OAuth services, tokens expire
   - Plugin should auto-refresh, but manual re-auth may be needed

### OAuth callback issues

**Symptoms:**
- OAuth flow starts but never completes
- "Callback URL mismatch" errors

**Solutions:**
1. Ensure Lidarr is accessible at the expected URL
2. Check firewall/proxy settings
3. Try from a different browser

## Connection Issues

### "Connection timeout" or "Service unavailable"

**Solutions:**

1. **Check network connectivity:**
   ```bash
   # Test basic connectivity
   curl -I https://api.example.com/health
   ```

2. **Check for rate limiting:**
   - Many services limit requests per minute/hour
   - Wait and retry later
   - Reduce request frequency in settings

3. **Increase timeout:**
   - Settings > [Plugin] > Advanced > Timeout
   - Try 60-120 seconds for slow connections

4. **Check proxy settings:**
   - If using a proxy, verify it's configured in Lidarr
   - Settings > General > Proxy

### SSL/TLS errors

**Symptoms:**
- "SSL certificate problem"
- "Unable to establish secure connection"

**Solutions:**
1. Update system certificates
2. Check system date/time is correct
3. If self-signed cert required, configure in Lidarr settings

## Search and Results Issues

### "No results found"

**Common causes:**
1. Search query too specific
2. Content not available in your region
3. API returned empty response
4. Rate limited (appears as empty results)

**Solutions:**

1. **Try simpler searches:**
   - Search for popular artists first
   - Reduce search specificity

2. **Check regional availability:**
   - Some content is region-locked
   - Verify in service's web interface

3. **Check logs for API response:**
   - Enable debug logging: Settings > General > Log Level = Debug
   - Look for actual API response in logs

### Search results but can't download

**Check:**
1. Download client is configured (separate from indexer)
2. Quality settings match available content
3. Subscription tier supports requested quality

## Download Issues

### Downloads stuck at 0%

**Common causes:**
1. Stream URL expired
2. Rate limited
3. Network interruption
4. Quality not available

**Solutions:**
1. Retry the download
2. Check for rate limiting messages in logs
3. Verify quality is available on service

### Downloads complete but import fails

**Common causes:**
1. File format not recognized
2. Metadata tagging failed
3. Destination folder permissions
4. Disk space

**Solutions:**

1. **Check Activity > History:**
   - Look for specific error messages
   - Note the failure reason

2. **Verify destination:**
   ```bash
   # Check permissions
   ls -la /path/to/music/library

   # Check disk space
   df -h /path/to/music/library
   ```

3. **Check file integrity:**
   - Some downloads may be corrupted
   - Retry the download

## Performance Issues

### Slow response times

**Solutions:**

1. **Enable caching:**
   - Most plugins cache responses
   - Settings > [Plugin] > Enable Cache

2. **Reduce concurrent requests:**
   - Lower parallel download limits
   - Increase delays between requests

3. **Check system resources:**
   - CPU, memory, disk I/O
   - Consider upgrading hardware

### High memory usage

**Solutions:**
1. Reduce cache size in plugin settings
2. Limit concurrent operations
3. Restart Lidarr periodically if memory grows

## Log Analysis

### Finding relevant logs

**Lidarr log locations:**
- Docker: `docker logs lidarr`
- Linux: `~/.config/Lidarr/logs/lidarr.txt`
- Windows: `%APPDATA%\Lidarr\logs\lidarr.txt`

**Useful grep patterns:**
```bash
# Plugin-specific
grep -i "pluginname" lidarr.txt

# Errors only
grep -i "error\|exception\|failed" lidarr.txt

# Authentication
grep -i "auth\|token\|oauth" lidarr.txt

# API calls
grep -i "api\|request\|response" lidarr.txt
```

### Enabling debug logging

1. Settings > General > Log Level = Debug
2. Reproduce the issue
3. Check logs
4. **Important:** Set back to Info when done (debug logs are verbose)

## Getting Help

If these steps don't resolve your issue:

1. **Check existing issues:**
   - Search the plugin's GitHub Issues
   - Someone may have reported the same problem

2. **Gather information:**
   - Lidarr version
   - Plugin version
   - Relevant log excerpts
   - Steps to reproduce

3. **Report the issue:**
   - Open a GitHub issue with the gathered information
   - Include logs (redact any credentials)

---

## See Also

- [Plugin Isolation](../PLUGIN_ISOLATION.md) - How plugins load
- [Compatibility Matrix](../COMPATIBILITY.md) - Version requirements
- [Resilience Policy](../reference/RESILIENCE_POLICY.md) - Retry and timeout behavior
