# Lidarr.Plugin.Abstractions

Core abstractions and interfaces for building Lidarr plugins.

## Overview

This package provides the contract layer between Lidarr plugins and the host application. It contains:

- **Interfaces** for plugin services (token stores, settings stores, etc.)
- **Base types** for common plugin patterns
- **Data contracts** shared between plugins and the host

## Installation

```bash
dotnet add package Lidarr.Plugin.Abstractions
```

## Usage

Reference this package in your plugin project to implement the required interfaces:

```csharp
using Lidarr.Plugin.Abstractions;

public class MyTokenStore : ITokenStore<MySession>
{
    // Implementation
}
```

## Related Packages

- **Lidarr.Plugin.Common** - Full implementation library with utilities, HTTP helpers, and more

## Requirements

- .NET 8.0 or later
- Lidarr 3.x (plugins branch)

## License

MIT
