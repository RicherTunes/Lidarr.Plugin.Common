using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Services.Drm
{
    /// <summary>
    /// A parsed ISO-BMFF 'pssh' (Protection System Specific Header) box: the DRM system ID, the optional
    /// explicit KID list (v1), and the system-specific data blob (e.g. the Widevine PSSH protobuf).
    /// </summary>
    public sealed record PsshBox(byte[] SystemId, IReadOnlyList<byte[]> KeyIds, byte[] Data);

    /// <summary>
    /// Parses a full 'pssh' box (size + type + FullBox content) — the form base64-encoded in a DASH
    /// <c>&lt;cenc:pssh&gt;</c> element or embedded in an init segment. Shared across CENC plugins; supersedes
    /// the in-tree amazon parser that only handled v0 with no KID list.
    /// </summary>
    public static class PsshParser
    {
        /// <summary>The Widevine DRM system ID (edef8ba9-79d6-4ace-a3c8-27dcd51d21ed).</summary>
        public static readonly byte[] WidevineSystemId =
            Convert.FromHexString("edef8ba979d64acea3c827dcd51d21ed");

        public static PsshBox Parse(ReadOnlySpan<byte> box)
        {
            // size(4) type(4) version(1) flags(3) systemId(16) = 28 minimum.
            if (box.Length < 28)
            {
                throw new ArgumentException($"pssh box too short ({box.Length} bytes, need >= 28).", nameof(box));
            }

            if (box[4] != (byte)'p' || box[5] != (byte)'s' || box[6] != (byte)'s' || box[7] != (byte)'h')
            {
                throw new ArgumentException("Not a 'pssh' box.", nameof(box));
            }

            byte version = box[8];
            int pos = 12;
            var systemId = box.Slice(pos, 16).ToArray();
            pos += 16;

            var kids = new List<byte[]>();
            if (version > 0)
            {
                Require(box, pos, 4, "KID_count");
                long kidCount = ReadUInt32(box, pos);
                pos += 4;
                for (long i = 0; i < kidCount; i++)
                {
                    Require(box, pos, 16, "KID");
                    kids.Add(box.Slice(pos, 16).ToArray());
                    pos += 16;
                }
            }

            Require(box, pos, 4, "DataSize");
            long dataSize = ReadUInt32(box, pos);
            pos += 4;
            if (pos + dataSize > box.Length)
            {
                throw new ArgumentException(
                    $"pssh DataSize ({dataSize}) exceeds box ({box.Length - pos} available).", nameof(box));
            }

            var data = box.Slice(pos, (int)dataSize).ToArray();
            return new PsshBox(systemId, kids, data);
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> b, int pos)
            => (uint)((b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3]);

        private static void Require(ReadOnlySpan<byte> box, int pos, int need, string what)
        {
            if (pos + need > box.Length)
            {
                throw new ArgumentException($"pssh box truncated reading {what} (need {need} at {pos}, have {box.Length}).", nameof(box));
            }
        }
    }
}
