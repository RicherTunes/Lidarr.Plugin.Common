using System;
using System.Text;
using Lidarr.Plugin.Common.Services.Drm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// GOLD-STANDARD CENC conformance against the reference implementation (Bento4). The fixture below is a
    /// REAL <c>cenc</c>-encrypted MPEG-DASH media segment (moof+mdat, 10 samples) produced by Bento4's
    /// <c>mp4encrypt --method MPEG-CENC</c> with a known content key + KID, and the expected plaintext is the
    /// exact output of Bento4's <c>mp4decrypt</c> on the same segment. Decrypting it with our
    /// <see cref="CencSegmentDecryptor"/> and getting byte-identical output proves our walker + senc/trun/tfhd
    /// parsers + AES-CTR core agree with an INDEPENDENT, battle-tested implementation — not just with our own
    /// synthetic NIST-vector fixtures (CencSegmentDecryptorTests) or hand-built boxes.
    ///
    /// <para>The fixture is embedded (not a binary file) so CI runs it without Bento4 installed. It was
    /// generated once locally: ffmpeg sine -> mp4fragment -> mp4encrypt (key 00112233445566778899aabbccddeeff,
    /// KID 9eb4050de44b4802932e27d75083e266, IV 1122334455667788...) -> the moof+mdat segment is SegmentBase64;
    /// mp4decrypt of the same -> the decrypted mdat payload is ExpectedPlaintextBase64.</para>
    /// </summary>
    public sealed class CencBento4ConformanceTests
    {
        // Content key + KID used with mp4encrypt (independent of our code).
        private static readonly byte[] Key = Convert.FromHexString("00112233445566778899aabbccddeeff");
        private static readonly byte[] Kid = Convert.FromHexString("9eb4050de44b4802932e27d75083e266");

        // From mp4dump of the fixture: 10 cenc samples, mdat payload starts at offset 397 within the segment,
        // payload length 934. tenc: per_sample_iv_size=16, scheme cenc (no crypt:skip pattern).
        private const int ExpectedSampleCount = 10;
        private const int MdatPayloadOffset = 397;
        private const int MdatPayloadLength = 934;

        [Fact]
        public void DecryptSegment_RealBento4CencSegment_MatchesMp4decryptOutput()
        {
            var segment = Convert.FromBase64String(SegmentBase64);
            var expected = Convert.FromBase64String(ExpectedPlaintextBase64);

            // Sanity: the embedded fixture really is a moof+mdat with mdat where mp4dump reported it.
            Assert.Equal("moof", Encoding.ASCII.GetString(segment, 4, 4));
            Assert.Equal("mdat", Encoding.ASCII.GetString(segment, MdatPayloadOffset - 4, 4));
            Assert.Equal(MdatPayloadLength, expected.Length);

            var tenc = new TencDefaults(
                IsProtected: true,
                PerSampleIvSize: 16,
                DefaultKid: Kid,
                CryptByteBlock: 0,
                SkipByteBlock: 0,
                DefaultConstantIv: null);

            var decryptedCount = CencSegmentDecryptor.DecryptSegmentInPlace(
                segment, Key, CencProtectionScheme.Cenc, tenc);

            Assert.Equal(ExpectedSampleCount, decryptedCount);

            // The mdat payload is now clear and must match mp4decrypt byte-for-byte.
            var actualPlaintext = segment.AsSpan(MdatPayloadOffset, MdatPayloadLength).ToArray();
            Assert.Equal(expected, actualPlaintext);
        }

        private const string SegmentBase64 =
            "AAABhW1vb2YAAAAQbWZoZAAAAAAAAAABAAABbXRyYWYAAAAYdGZoZAACACIAAAABAAAAAQAAAAAAAAAUdGZkdAEAAAAAAAAAAAAAAAAAAGR0cnVuAQADAQAAAAoAAAGNAAAEAAAAAJkAAAQAAAAAogAABAAAAAA8AAAEAAAAAD4AAAQAAAAAQQAABAAAAABCAAAEAAAAAF0AAAQAAAAAXQAABAAAAABhAAACdAAAAFMAAAARc2FpegAAAAAQAAAACgAAABRzYWlvAAAAAAAAAAEAAADlAAAAsHNlbmMAAAAAAAAAChEiM0RVZneIAAAAAAAAAAARIjNEVWZ3iAAAAAAAAAAKESIzRFVmd4gAAAAAAAAAFREiM0RVZneIAAAAAAAAABkRIjNEVWZ3iAAAAAAAAAAdESIzRFVmd4gAAAAAAAAAIhEiM0RVZneIAAAAAAAAACcRIjNEVWZ3iAAAAAAAAAAtESIzRFVmd4gAAAAAAAAAMxEiM0RVZneIAAAAAAAAADoAAAOubWRhdFvH1HyjklzzTjdVQ6PEYYaWJiJYAcCtPGsGTgdKqnv3/haleAS0tO75tlcN/bptIg/sHryjLkog4rPKl6mPmN9eAupOoMetxcQtOYZmBzWrLs3UEKu/N566enmmqIZNqEjCGU3AU8pDTALamL3XiTIiTiihzZMvugo2gL3J1SD+LkHikuiPEvY2bHcQezFbsamSn02i/iZmzIN6xqonnZq3a+ouoepMt1QwiYi+uhKs+ZQ7PM1oIfaxF8z1zj1/kyCFDiM8CehYTjhhOh5OZbi8r5niEJgo7W3mmcypmWCJFKWFutjekpAWmEQyeniv+j0e4o/J6F26ljKnZ1pZk/J2QkKVRYYxAghGC14G9hb5ERPelhXfTMXQGs18lzeGKlN0Pk7iC4NXQxFdbjTPXdK37tYXMRyXv1Cq54/opAm7QpE2GUSEHzNfDcn8TxrZfbwaw3CyM0eW8pUqu8MkJDQxdOGBzFDp0tYgR8x3a1zxSv5img1i3v9jqD2mUXqdBHner8quWTsb/pwTMF51eyWz28oQZe4kFKvXg0a2GyuVw0sXK7aXtX56Tylj0n+Qzmww1HcNg6XLvAwuLa8YG1y9Yaog48f0Z1veZs3El60O6Pg0AuTc3O6qvSkV7d+r5tSUNXc4THyVxFxYQQSNXV/aYj3h8zQQWzXhVqJf8/+kwaiIqtXYdOXmLTN1Qt4Fx1bt1vKiVJMxkMw63JfjEyw0a1d8vsHPFadVt3jIofk2y4iY4DWqr55ciGTsfrQDbWA0Ra2cEYzzmJp9i173YSy+kNE3p2OAhQPm9gY2y92wAsr7cVFx9u8UuDeq+PCnXl2mC+3jrHnMaYkVpB66KsWXEBFP+cj4+yS8w0AQ+eejRqlfi0MnBqsK8BBjWVSovWg2FRlz0dcLh6rr5vFhnhOHPAUslbATHV/cCPgiHy2PUszjIoLir8aoxs3WtOw+bDKzFZRC7apQAbDP8WevZfysmeM8vdS11Hu/79VeszEzXgeS04WBQ/HfvJlLXrpjO5Guk7aMaQTBfHLZcJjLeL6QREWzxKoul7omCMUxvvFrwJ07pS99prfRnRhBrfDXoPz55nAOh2goMyZyGFi1GlGPiL8zU5/VswInGhrdK+V4qIzXfUyhxUrPVGXCVSa76womxgBO8MaCqfB/xhLe+fHajGEUwE3e2QFlnJLQMMBUmW1t/4/SaBPiP3U70OIOpC7ubwZyz0Fktj93hotips7G3oxeyvE=";

        private const string ExpectedPlaintextBase64 =
            "3gIATGF2YzYyLjI4LjEwMQACYKVUkNpyMvPHp541XnLlquskR5SSL8wkG3cD8F6r2LvuGy2paR/j0vN73q/J4G1MVWNs1prz6yUmmqpiuYqjJpSyUslppSaMlClGSjJRJrQ2htDAxs2DAyJEDGzZs2iRIpTZs3FFFFFFFFFFFFFFFEiKKKKKKJERERIRFFERRRIiiiKKKKLgATiU2sldlFOrKdWS6c/z7cabPGr/+tfv1xrjV6//teP588a41ev/4vf+fPGutasN/rY0fRQYC6BMsIWfqdZvKY25wGc4DAzuEzAwM7hISDAzuEhIMDAzu4SErBgYGd3CQk5CbCXf4Qqw3U+cuV6UrelCb2ZuUJKkswaUJCSV4GlCQklecGNmwkJKg2bgwMbCQkqxy8Q1FFFBqKKKKNxtdFHAATryixrXjnv9n7+3xbpppeqlyOOSSSIkDuf3zpuzZjZ/chk+DDP7gZPgwz+8I+PgDP7gcPgDP7gZPgDgAQgyieKdayvn/+z/H/r/7XfF3qr317/W/H327cupVF5rYoXQjAiooNRRQagULoNaNhLMSzTM65vzU+Dt+ZwBCDKKIn1Ah0jK9f/37//X8XONcZPO68fFePjbuFaykywnnUc6innVPPOedQnnPVlKpvpce6qfleAc8XiYpgoVcAEKMooCjQiHVqr3//tfn/1/8l8alydevX3fP427PDJeKupg+UUS3zGiiiiiiiiq4oJERUqvWJS/oU0pkL0O2YBfgAEGMpTjXRiHREHQiFVev/6b/1/2l8XNZ1m+/v3v2ndw8JJTALq6MOfwcHp16/y1ym1zzzfSixlzD1VLPYdotXf2/0llgLWiN7cjDTreDXYybrcSccQiAySN2RrUcAEMMos6IQ6Ig6ERaMev/6Xf/t/7Xq9SWo1P0/fI628u8qJQ+n0+kibi+n0+n0o+n0+lA1wc/nA23Ase4ZAxDw3hQDbDE83KdLOkPdFjFFoCVpjvnNSzgXvrTVrFwAFIMpM6Eg6Mg6EhaMrn9vH/e+Jq5cklyS1uGXJxLXYGIw+CxQAwHtiDEZ+wAUHrEMRt2GLT6wBQe3AYjP2ADh6wBge3AYtD7ABQesfMeBSR4bRvF4HzH2LsjPcnePAdhuABUDKLGhIOlIOkIJW8zzrVy5dyXJJLcN2knUkaDuh/4f+De8Pw27oPmHuPg2eF9t33P8D3LI27oP8J9x8Gzw5Mzug7efQ/mSWQzsJ8wPmHuMhneA==";
    }
}
