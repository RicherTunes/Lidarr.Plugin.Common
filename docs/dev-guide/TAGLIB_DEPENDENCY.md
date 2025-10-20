# TagLib dependency (public vs. private)

By default, the library uses the public `TagLibSharp` package from nuget.org so that forks and contributors can build without any private feeds.

## Defaults

- Public: `TagLibSharp` (nuget.org)
- Optional CI/private override: `TagLibSharp-Lidarr` (Azure DevOps feed)

The project file has conditional references:

- Default (no action needed): `TagLibSharp`
- Set `UseLidarrTaglib=true` to switch to `TagLibSharp-Lidarr`

## CI usage (optional)

1. Add a private feed only in CI (do not commit it):

```bash
dotnet nuget add source --name lidarr-taglib-ci "${LIDARR_TAGLIB_FEED_URL}" --store-password-in-clear-text
```

1. Build with the Lidarr package:

```bash
dotnet build -c Release -p:UseLidarrTaglib=true
```

Our CI includes an optional step to add the feed when the secret `LIDARR_TAGLIB_FEED_URL` is present. Local developers remain on the public package unless they choose to add the feed and pass the property.
