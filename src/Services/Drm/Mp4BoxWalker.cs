using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Services.Drm
{
    /// <summary>
    /// A located ISO-BMFF box: its 4-character type, where it starts, the header length (8, or 16 for a
    /// 64-bit <c>largesize</c> box), and its total size. <see cref="PayloadOffset"/>/<see cref="PayloadLength"/>
    /// address the box contents after the header; the full box is <see cref="Offset"/>..Offset+Size.
    /// </summary>
    public sealed record Mp4Box(string Type, int Offset, int HeaderLength, long Size)
    {
        public int PayloadOffset => Offset + HeaderLength;

        public int PayloadLength => checked((int)(Size - HeaderLength));
    }

    /// <summary>
    /// Minimal ISO-BMFF (MP4) box walker for locating the CENC boxes (<c>senc</c>, <c>tenc</c>, <c>pssh</c>)
    /// inside an init/media segment. Shared across CENC plugins. Handles 32-bit, 64-bit <c>largesize</c>, and
    /// size-extends-to-end boxes; every offset is bounds-checked against the buffer.
    /// </summary>
    public static class Mp4BoxWalker
    {
        // Container boxes whose payload is itself a sequence of boxes (no codec-specific header). Enough to
        // reach moof/traf/senc and moov-nested pssh. NOTE: 'stsd' and sample-entry boxes (the path to 'tenc')
        // carry their own headers and are handled separately.
        private static readonly HashSet<string> ContainerTypes = new(StringComparer.Ordinal)
        {
            "moov", "trak", "mdia", "minf", "stbl", "moof", "traf", "mvex", "edts", "dinf", "udta", "sinf", "schi",
        };

        // Real ISO-BMFF CENC nesting tops out around a dozen levels (moov/.../sinf/schi); 32 is generous and
        // bounds recursion so a maliciously deep box tree throws cleanly instead of crashing via StackOverflow.
        private const int MaxDepth = 32;

        /// <summary>Reads the sequence of boxes at the top level of <paramref name="data"/>.</summary>
        public static IReadOnlyList<Mp4Box> ReadBoxes(ReadOnlySpan<byte> data)
        {
            var boxes = new List<Mp4Box>();
            int pos = 0;
            while (pos + 8 <= data.Length)
            {
                long size = ReadUInt32(data, pos);
                string type = ReadType(data, pos + 4);
                int headerLength = 8;

                if (size == 1)
                {
                    if (data.Length - pos < 16)
                    {
                        throw new ArgumentException($"MP4 largesize box at {pos} is truncated.", nameof(data));
                    }

                    size = (long)ReadUInt64(data, pos + 8); // an absurd >Int64 size fails the size-vs-header/buffer checks below
                    headerLength = 16;
                }
                else if (size == 0)
                {
                    size = data.Length - pos; // extends to end of buffer
                }

                if (size < headerLength)
                {
                    throw new ArgumentException($"MP4 box '{type}' at {pos} has size {size} smaller than its header.", nameof(data));
                }

                // Compare against remaining bytes WITHOUT adding (pos+size could overflow long for a hostile
                // largesize and wrap negative, bypassing the check). size >= headerLength > 0 here.
                if (size > data.Length - pos)
                {
                    throw new ArgumentException($"MP4 box '{type}' at {pos} (size {size}) extends past the buffer ({data.Length}).", nameof(data));
                }

                boxes.Add(new Mp4Box(type, pos, headerLength, size));
                pos += (int)size;
            }

            return boxes;
        }

        /// <summary>
        /// Finds the first box of <paramref name="type"/> anywhere in the tree, descending into known
        /// container boxes. Returns absolute offsets into <paramref name="data"/>, or <c>null</c> if absent.
        /// </summary>
        public static Mp4Box? FindFirst(ReadOnlySpan<byte> data, string type)
            => FindFirst(data, type, baseOffset: 0, depth: 0);

        private static Mp4Box? FindFirst(ReadOnlySpan<byte> data, string type, int baseOffset, int depth)
        {
            if (depth >= MaxDepth)
            {
                throw new ArgumentException($"MP4 box nesting exceeds {MaxDepth} levels.", nameof(data));
            }

            foreach (var box in ReadBoxes(data))
            {
                if (box.Type == type)
                {
                    return box with { Offset = box.Offset + baseOffset };
                }

                // Bytes to skip past the box payload before its child boxes begin: 0 for plain containers,
                // 8 for stsd (FullBox ver/flags + entry_count), and the SampleEntry+codec header for the
                // protected sample entries (the path to tenc).
                int childSkip = ChildSkip(box.Type);
                if (childSkip >= 0 && box.PayloadLength >= childSkip)
                {
                    int childOffset = box.PayloadOffset + childSkip;
                    int childLength = box.PayloadLength - childSkip;
                    var inner = FindFirst(
                        data.Slice(childOffset, childLength),
                        type,
                        baseOffset + childOffset,
                        depth + 1);
                    if (inner is not null)
                    {
                        return inner;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Finds <b>all</b> boxes of <paramref name="type"/> in the tree, in document order, descending into
        /// container boxes. Returns absolute offsets. Use this when multiple matches are expected (e.g. the
        /// several pssh boxes of a multi-DRM segment, or one senc per traf).
        /// </summary>
        public static IReadOnlyList<Mp4Box> FindAll(ReadOnlySpan<byte> data, string type)
        {
            var results = new List<Mp4Box>();
            FindAll(data, type, baseOffset: 0, depth: 0, results);
            return results;
        }

        private static void FindAll(ReadOnlySpan<byte> data, string type, int baseOffset, int depth, List<Mp4Box> results)
        {
            if (depth >= MaxDepth)
            {
                throw new ArgumentException($"MP4 box nesting exceeds {MaxDepth} levels.", nameof(data));
            }

            foreach (var box in ReadBoxes(data))
            {
                if (box.Type == type)
                {
                    results.Add(box with { Offset = box.Offset + baseOffset });
                }

                int childSkip = ChildSkip(box.Type);
                if (childSkip >= 0 && box.PayloadLength >= childSkip)
                {
                    int childOffset = box.PayloadOffset + childSkip;
                    FindAll(data.Slice(childOffset, box.PayloadLength - childSkip), type, baseOffset + childOffset, depth + 1, results);
                }
            }
        }

        private static int ChildSkip(string type) => type switch
        {
            "stsd" => 8,           // FullBox: version(1) flags(3) entry_count(4)
            "enca" => 28,          // SampleEntry(8) + AudioSampleEntry(20)
            "encv" => 78,          // SampleEntry(8) + VisualSampleEntry(70)
            _ => ContainerTypes.Contains(type) ? 0 : -1,
        };

        private static uint ReadUInt32(ReadOnlySpan<byte> b, int pos)
            => (uint)((b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3]);

        private static ulong ReadUInt64(ReadOnlySpan<byte> b, int pos)
            => ((ulong)ReadUInt32(b, pos) << 32) | ReadUInt32(b, pos + 4);

        private static string ReadType(ReadOnlySpan<byte> b, int pos)
            => System.Text.Encoding.ASCII.GetString(b.Slice(pos, 4));
    }
}
