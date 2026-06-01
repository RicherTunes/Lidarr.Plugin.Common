using System;
using Lidarr.Plugin.Common.Services.Drm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Conformance against PUBLISHED reference vectors from battle-tested projects (not self-constructed
    /// inputs) — validates our parser/crypto strategy matches independent implementations. The core AES math
    /// is already cross-checked against NIST SP 800-38A vectors in CencSampleDecryptorTests; these add
    /// real-world DRM fixtures from the reference tooling.
    /// </summary>
    public sealed class CencConformanceTests
    {
        // The canonical "widevine_test" PSSH shipped/used by devine-dl/pywidevine and unifiedstreaming/pycpix.
        // Decoded (verified): 91-byte box, type 'pssh', version 0, systemId edef8ba9-79d6-4ace-a3c8-27dcd51d21ed,
        // followed by a 59-byte WidevinePsshData protobuf (provider "widevine_test", a content KID).
        private const string PywidevineTestPssh =
            "AAAAW3Bzc2gAAAAA7e+LqXnWSs6jyCfc1R0h7QAAADsIARIQ62dqu8s0Xpa7z2FmMPGj2hoNd2lkZXZpbmVfdGVzdCIQZmtqM2xqYVNkZmFsa3IzaioCSEQyAA==";

        [Fact]
        public void PsshParser_ParsesPywidevineCanonicalTestVector()
        {
            var box = Convert.FromBase64String(PywidevineTestPssh);

            var pssh = PsshParser.Parse(box);

            Assert.Equal(PsshParser.WidevineSystemId, pssh.SystemId);
            Assert.Empty(pssh.KeyIds);           // v0 box carries no explicit KID list (the KID is inside Data)
            Assert.Equal(59, pssh.Data.Length);  // the WidevinePsshData protobuf payload
        }
    }
}
