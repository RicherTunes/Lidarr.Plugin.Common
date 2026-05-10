// <copyright file="HmacSignerTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.Collections.Generic;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Utilities;

public class HmacSignerTests
{
    [Fact]
    public void HmacSha256Signer_ProducesStableSignatureForSameInput()
    {
        IHmacSigner signer = new HmacSha256Signer("topsecret");
        var p1 = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" };
        var p2 = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };

        Assert.Equal(signer.Sign(p1), signer.Sign(p2));
    }

    [Fact]
    public void Md5ConcatSigner_ProducesStableSignatureForSameInput()
    {
        IHmacSigner signer = new Md5ConcatSigner("topsecret");
        var p1 = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" };
        var p2 = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };

        Assert.Equal(signer.Sign(p1), signer.Sign(p2));
    }

#pragma warning disable CS0618 // legacy IRequestSigner alias - tested intentionally
    [Fact]
    public void LegacyIRequestSigner_StillUsable_AsAlias()
    {
        // Plugins may still hold references to the old IRequestSigner name.
        // The alias must continue to work (Obsolete warning, no compile break).
        IRequestSigner legacy = new HmacSha256Signer("alpha");
        IHmacSigner modern = new HmacSha256Signer("alpha");

        var parameters = new Dictionary<string, string> { ["k"] = "v" };
        Assert.Equal(legacy.Sign(parameters), modern.Sign(parameters));
    }
#pragma warning restore CS0618
}
