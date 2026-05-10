using System;
using System.Collections.Generic;
using Lidarr.Plugin.Common.Services.Caching;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Caching;

public class CachePolicyRegistryTests
{
    private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();

    [Fact]
    public void Ctor_NoArg_UsesCachePolicyDefault()
    {
        var registry = new CachePolicyRegistry();
        var policy = registry.GetPolicy("anything", NoParams);
        Assert.Same(CachePolicy.Default, policy);
    }

    [Fact]
    public void Ctor_CustomDefault_UsedWhenNoRulesMatch()
    {
        var custom = CachePolicy.ShortLived;
        var registry = new CachePolicyRegistry(custom);
        Assert.Same(custom, registry.GetPolicy("nope", NoParams));
    }

    [Fact]
    public void SetDefault_NullPolicy_Throws()
    {
        var registry = new CachePolicyRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.SetDefault(null!));
    }

    [Fact]
    public void SetDefault_ReplacesFallback()
    {
        var registry = new CachePolicyRegistry();
        var newDefault = CachePolicy.LongLived;

        registry.SetDefault(newDefault);

        Assert.Same(newDefault, registry.GetPolicy("any", NoParams));
    }

    [Fact]
    public void AddEndpoint_ExactMatch_ReturnsRulePolicy()
    {
        var search = CachePolicy.ShortLived;
        var registry = new CachePolicyRegistry();
        registry.AddEndpoint("/api/search", search);

        Assert.Same(search, registry.GetPolicy("/api/search", NoParams));
        Assert.Same(CachePolicy.Default, registry.GetPolicy("/api/details", NoParams));
    }

    [Fact]
    public void AddEndpoint_IsCaseInsensitive()
    {
        var policy = CachePolicy.ShortLived;
        var registry = new CachePolicyRegistry();
        registry.AddEndpoint("/api/Search", policy);

        Assert.Same(policy, registry.GetPolicy("/api/search", NoParams));
        Assert.Same(policy, registry.GetPolicy("/API/SEARCH", NoParams));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddEndpoint_NullOrWhitespace_Throws(string? endpoint)
    {
        var registry = new CachePolicyRegistry();
        Assert.Throws<ArgumentException>(() => registry.AddEndpoint(endpoint!, CachePolicy.Default));
    }

    [Fact]
    public void AddPrefix_MatchesAllEndpointsStartingWithPrefix()
    {
        var registry = new CachePolicyRegistry();
        registry.AddPrefix("/api/", CachePolicy.MediumLived);

        Assert.Same(CachePolicy.MediumLived, registry.GetPolicy("/api/search", NoParams));
        Assert.Same(CachePolicy.MediumLived, registry.GetPolicy("/api/details/123", NoParams));
        Assert.Same(CachePolicy.Default, registry.GetPolicy("/auth/login", NoParams));
    }

    [Fact]
    public void AddPrefix_IsCaseInsensitive()
    {
        var registry = new CachePolicyRegistry();
        registry.AddPrefix("/API/", CachePolicy.ShortLived);
        Assert.Same(CachePolicy.ShortLived, registry.GetPolicy("/api/anything", NoParams));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void AddPrefix_NullOrWhitespace_Throws(string? prefix)
    {
        var registry = new CachePolicyRegistry();
        Assert.Throws<ArgumentException>(() => registry.AddPrefix(prefix!, CachePolicy.Default));
    }

    [Fact]
    public void AddPredicate_NullPredicate_Throws()
    {
        var registry = new CachePolicyRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.AddPredicate(null!, CachePolicy.Default));
    }

    [Fact]
    public void AddPredicate_NullPolicy_Throws()
    {
        var registry = new CachePolicyRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.AddPredicate((_, _) => true, null!));
    }

    [Fact]
    public void AddPredicate_ParameterAware_MatchesByQueryString()
    {
        var registry = new CachePolicyRegistry();
        registry.AddPredicate(
            (ep, p) => ep == "/search" && p.TryGetValue("type", out var t) && t == "artist",
            CachePolicy.LongLived);

        var artistParams = new Dictionary<string, string> { ["type"] = "artist" };
        var albumParams = new Dictionary<string, string> { ["type"] = "album" };

        Assert.Same(CachePolicy.LongLived, registry.GetPolicy("/search", artistParams));
        Assert.Same(CachePolicy.Default, registry.GetPolicy("/search", albumParams));
    }

    [Fact]
    public void GetPolicy_LaterRuleWins()
    {
        // Documented contract: "Later registrations override earlier ones."
        // Registry walks rules from newest to oldest.
        var registry = new CachePolicyRegistry();
        registry.AddPrefix("/api/", CachePolicy.ShortLived);
        registry.AddEndpoint("/api/search", CachePolicy.LongLived);   // more specific, registered later

        Assert.Same(CachePolicy.LongLived, registry.GetPolicy("/api/search", NoParams));
        Assert.Same(CachePolicy.ShortLived, registry.GetPolicy("/api/details", NoParams));
    }

    [Fact]
    public void GetPolicy_NullEndpoint_TreatedAsEmpty()
    {
        var registry = new CachePolicyRegistry();
        registry.AddEndpoint("specific", CachePolicy.ShortLived);

        // null endpoint should not crash; falls through to default since no rule matches "".
        var result = registry.GetPolicy(null!, NoParams);
        Assert.Same(CachePolicy.Default, result);
    }

    [Fact]
    public void GetPolicy_NullParameters_TreatedAsEmpty()
    {
        var registry = new CachePolicyRegistry();
        var sawParams = false;
        registry.AddPredicate((_, p) => { sawParams = p != null; return true; }, CachePolicy.LongLived);

        var policy = registry.GetPolicy("/any", null!);

        Assert.True(sawParams, "predicate should always observe a non-null parameters dictionary");
        Assert.Same(CachePolicy.LongLived, policy);
    }

    [Fact]
    public void Builder_FluentChain_ReturnsSelf()
    {
        var registry = new CachePolicyRegistry();
        var ret1 = registry.SetDefault(CachePolicy.MediumLived);
        var ret2 = registry.AddEndpoint("/x", CachePolicy.ShortLived);
        var ret3 = registry.AddPrefix("/y/", CachePolicy.ShortLived);
        var ret4 = registry.AddPredicate((_, _) => false, CachePolicy.LongLived);

        Assert.Same(registry, ret1);
        Assert.Same(registry, ret2);
        Assert.Same(registry, ret3);
        Assert.Same(registry, ret4);
    }
}
