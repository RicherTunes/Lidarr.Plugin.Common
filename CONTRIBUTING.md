# 🤝 Contributing to Lidarr.Plugin.Common

Thank you for your interest in contributing to the shared library for Lidarr streaming service plugins! This project enables **74% code reduction** for streaming plugins through collaborative development.

## 🎯 **How You Can Contribute**

### **🐛 Bug Reports**
- **Found an issue?** Open an issue with clear steps to reproduce
- **Security concerns?** Report privately via [security@richertunes.com](mailto:security@richertunes.com)
- **Performance problems?** Include benchmarks and specific use cases

### **💡 Feature Requests**  
- **New utility needed?** Describe the use case and how it benefits multiple services
- **Better patterns discovered?** Share successful approaches from your plugin development
- **API improvements?** Suggest enhancements with backward compatibility in mind

### **🔧 Code Contributions**
- **Bug fixes** - Fix issues in existing utilities and services
- **New utilities** - Add generic functionality that benefits multiple streaming services
- **Documentation** - Improve API docs, examples, and usage guides
- **Testing** - Add test coverage, mock data, and validation scenarios

---

## 📋 **Contribution Guidelines**

### **Code Requirements**

#### **1. Generic Implementation Required**
```csharp
// ❌ Service-specific code in shared library
public static void HandleQobuzError(QobuzException ex) { }

// ✅ Generic pattern that works for all services
public static void HandleStreamingError<T>(T ex, string serviceName) where T : Exception { }
```

#### **2. Comprehensive Testing Required**
```csharp
// All shared library additions must include:
[Test]
public void NewUtility_ShouldWork_WithAllStreamingServices()
{
    // Test with multiple service scenarios
    var qobuzResult = TestWithMockQobuzData();
    var tidalResult = TestWithMockTidalData();
    var spotifyResult = TestWithMockSpotifyData();
    
    // All should work consistently
    Assert.That(qobuzResult.Success, Is.True);
    Assert.That(tidalResult.Success, Is.True);  
    Assert.That(spotifyResult.Success, Is.True);
}
```

#### **3. Security-First Implementation**
```csharp
// All HTTP utilities must include:
- Parameter masking for sensitive data
- Input validation and sanitization
- Error handling without information leakage
- Thread-safe operations where applicable

// Example:
public static Dictionary<string, string> MaskSensitiveParams(Dictionary<string, string> parameters)
{
    return parameters.Where(p => !IsSensitiveParameter(p.Key))
                    .ToDictionary(p => p.Key, p => MaskValue(p.Value));
}
```

#### **4. Performance Considerations**
- **No blocking calls** in async methods
- **Proper disposal** of resources
- **Memory efficient** implementations
- **Configurable timeouts** and limits

### **Documentation Standards**

#### **XML Documentation Required**
```csharp
/// <summary>
/// Brief description of what this method/class does.
/// </summary>
/// <param name="parameter">Description of parameter purpose and constraints</param>
/// <returns>Description of return value and possible states</returns>
/// <exception cref="ArgumentException">When thrown and why</exception>
/// <example>
/// <code>
/// var result = YourMethod("example", 123);
/// // Expected: result.Success == true
/// </code>
/// </example>
```

#### **Usage Examples Required**
- **Realistic examples** showing common usage patterns
- **Multiple service examples** demonstrating cross-service compatibility
- **Edge case handling** with proper error scenarios
- **Performance considerations** and best practices

---

## 🏗️ **Development Process**

### **Setting Up Development Environment**

#### **Prerequisites**
- .NET 8.0 SDK or later
- Git with proper configuration
- IDE with C# support (VS, VS Code, Rider)

#### **Local Setup**
```bash
# Clone the repository
git clone https://github.com/RicherTunes/Lidarr.Plugin.Common.git
cd Lidarr.Plugin.Common

# Restore dependencies
dotnet restore src/Lidarr.Plugin.Common.csproj

# Build and test
dotnet build src/Lidarr.Plugin.Common.csproj --configuration Debug
dotnet test tests/ --verbosity normal

> API compatibility runs in CI via the `apicompat` tool—no local PackageValidation packages required.

# Run examples validation
cd examples/
# Validate examples compile and work correctly
```

### **Making Changes**

#### **1. Create Feature Branch**
```bash
git checkout -b feature/your-feature-name
# or
git checkout -b bugfix/issue-description
```

#### **2. Implement Changes**
- **Write tests first** - TDD approach preferred
- **Implement functionality** - Follow existing patterns
- **Update documentation** - Keep docs in sync with code
- **Validate examples** - Ensure examples still work

#### **3. Quality Checks**
```bash
# Build and test
dotnet build --configuration Release
dotnet test --configuration Release --verbosity normal

# Run local validation
./scripts/validate-contribution.sh  # If available

# Check code formatting
dotnet format --verify-no-changes
```

#### **4. Submit Pull Request**
- **Clear title** describing the change
- **Detailed description** explaining the motivation and approach
- **Link to issues** if fixing reported bugs
- **Examples updated** if API changes affect usage

---

## 🎯 **Contribution Categories**

### **🛠️ Utilities & Helpers**
#### High Value, Low Risk
- File system utilities (naming, path handling)
- HTTP utilities (retry, error handling, security)
- Validation helpers (input sanitization, format checking)
- Performance utilities (caching, rate limiting, monitoring)

**Example Contributions:**
- Enhanced file naming for international characters
- OAuth2 authentication helper for multiple services
- Advanced retry patterns with custom policies
- Cross-service error code normalization

### **📋 Models & Standards**
#### Medium Value, Medium Risk
- Universal data models for streaming content
- Quality tier definitions and mapping utilities  
- Standard interfaces for cross-service compatibility
- Common configuration patterns

**Example Contributions:**
- Podcast/audiobook model extensions
- Advanced quality detection (spatial audio, lossless variants)
- Playlist and compilation models
- Subscription and licensing models

### **⚙️ Service Frameworks**
#### High Value, High Risk
- Authentication service abstractions
- Caching framework enhancements
- Plugin registration and DI patterns
- Advanced integration helpers

**Example Contributions:**
- Enhanced authentication with 2FA support
- Distributed caching for multi-instance scenarios
- Advanced plugin discovery and coordination
- Cross-service content matching algorithms

---

## 📊 **Review Process**

### **Automated Checks**
- **Build validation** - Must compile on all target platforms
- **Test execution** - All tests must pass with new changes
- **Code coverage** - New code should maintain or improve coverage
- **Security scan** - Automated scanning for potential vulnerabilities

### **Manual Review**
- **Code quality** - Follows established patterns and standards
- **Cross-service compatibility** - Works with multiple streaming services
- **Documentation** - Clear and comprehensive API documentation
- **Examples validation** - Usage examples work and are helpful

### **Review Criteria**
1. **Does it solve a real problem** for multiple streaming service plugins?
2. **Is the implementation generic enough** to work across different services?
3. **Does it maintain backward compatibility** with existing plugins?
4. **Is the code well-tested** with comprehensive coverage?
5. **Is the documentation clear** and includes usage examples?

---

## 🚀 **Release Process**

### **Version Management**
- **Patch releases (1.0.x)**: Bug fixes, documentation updates
- **Minor releases (1.x.0)**: New features, backward-compatible changes
- **Major releases (x.0.0)**: Breaking changes, architectural updates

### **Release Checklist**
- [ ] All tests passing
- [ ] Documentation updated
- [ ] Examples validated
- [ ] CHANGELOG.md updated
- [ ] Version numbers incremented
- [ ] Security review completed
- [ ] Performance regression testing
- [ ] Community notification prepared

---

## 🎯 **Contribution Impact**

### **Your Contributions Help**
- **Accelerate plugin development** for the entire ecosystem
- **Improve quality** across all streaming service plugins
- **Reduce maintenance burden** through shared bug fixes
- **Enable new features** that benefit multiple services

### **Recognition**
- **Contributors list** in README and releases
- **Credit in CHANGELOG** for significant contributions
- **Community recognition** in ecosystem documentation
- **Priority support** for active contributors

---

## 🤝 **Community Standards**

### **Code of Conduct**
- **Be respectful** - Treat all community members with respect
- **Be collaborative** - Focus on what's best for the ecosystem
- **Be constructive** - Provide helpful feedback and suggestions
- **Be inclusive** - Welcome developers of all experience levels

### **Communication Guidelines**
- **Use GitHub Issues** for bug reports and feature requests
- **Use Discussions** for general questions and community conversation
- **Use Pull Requests** for code contributions and documentation updates
- **Tag maintainers** for urgent issues or architectural questions

---

## 🎵 **Join the Revolution!**

The shared library ecosystem transforms streaming plugin development from individual efforts into collaborative professional development. Your contributions help:

- **Reduce development time** for all streaming service plugins
- **Improve quality standards** across the entire ecosystem  
- **Enable rapid expansion** to new streaming services
- **Establish technology leadership** in media automation

Every contribution, no matter how small, helps build the future of streaming automation ! 

### **Get Started Contributing**
1. **Check existing issues** for good first issues
2. **Join discussions** to understand community needs
3. **Study working examples** to understand patterns
4. **Start with small improvements** and build up to larger contributions

Together, we're building something amazing ! 

