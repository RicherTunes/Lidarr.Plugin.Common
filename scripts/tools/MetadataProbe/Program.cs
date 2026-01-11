using System.Text.Json;
using TagLib;

namespace MetadataProbe;

internal static class Program
{
    private sealed record ProbeResult(
        string Path,
        string Name,
        string? Artist,
        string? Album,
        string? Title,
        uint? Track,
        uint? Disc,
        string? Isrc,
        string? MusicBrainzTrackId,
        string? MusicBrainzReleaseId,
        string? Error);

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: MetadataProbe <file1> [file2...]");
            return 2;
        }

        var results = new List<ProbeResult>(args.Length);
        foreach (string path in args)
        {
            results.Add(Probe(path));
        }

        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = false
        });
        Console.Out.WriteLine(json);
        return results.Any(r => r.Error is not null) ? 1 : 0;
    }

    private static ProbeResult Probe(string path)
    {
        try
        {
            using TagLib.File file = TagLib.File.Create(path);
            var tag = file.Tag;

            string? artist =
                FirstNonEmpty(tag.AlbumArtists) ??
                FirstNonEmpty(tag.Performers);

            uint? track = tag.Track > 0 ? tag.Track : null;
            uint? disc = tag.Disc > 0 ? tag.Disc : null;

            // Read ISRC using format-specific tag access
            string? isrc = ReadIsrc(file);

            // MusicBrainz IDs are built into TagLib's Tag interface
            string? mbTrackId = NullIfWhitespace(tag.MusicBrainzTrackId);
            string? mbReleaseId = NullIfWhitespace(tag.MusicBrainzReleaseId);

            return new ProbeResult(
                Path: path,
                Name: System.IO.Path.GetFileName(path),
                Artist: NullIfWhitespace(artist),
                Album: NullIfWhitespace(tag.Album),
                Title: NullIfWhitespace(tag.Title),
                Track: track,
                Disc: disc,
                Isrc: isrc,
                MusicBrainzTrackId: mbTrackId,
                MusicBrainzReleaseId: mbReleaseId,
                Error: null);
        }
        catch (Exception ex)
        {
            return new ProbeResult(
                Path: path,
                Name: System.IO.Path.GetFileName(path),
                Artist: null,
                Album: null,
                Title: null,
                Track: null,
                Disc: null,
                Isrc: null,
                MusicBrainzTrackId: null,
                MusicBrainzReleaseId: null,
                Error: ex.Message);
        }
    }

    /// <summary>
    /// Reads ISRC using format-specific tag access.
    /// ID3v2: TSRC frame (MP3)
    /// Vorbis/FLAC/Ogg: ISRC field
    /// </summary>
    private static string? ReadIsrc(TagLib.File file)
    {
        // Try Xiph/Vorbis comment (FLAC, Ogg Vorbis)
        if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiphComment)
        {
            var isrcValues = xiphComment.GetField("ISRC");
            if (isrcValues?.Length > 0 && !string.IsNullOrWhiteSpace(isrcValues[0]))
            {
                return isrcValues[0];
            }
        }

        // Try ID3v2 (MP3)
        if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2Tag)
        {
            var tsrcFrame = TagLib.Id3v2.TextInformationFrame.Get(
                id3v2Tag,
                TagLib.ByteVector.FromString("TSRC", TagLib.StringType.Latin1),
                false);
            if (tsrcFrame?.Text?.Length > 0 && !string.IsNullOrWhiteSpace(tsrcFrame.Text[0]))
            {
                return tsrcFrame.Text[0];
            }
        }

        return null;
    }

    private static string? FirstNonEmpty(string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? NullIfWhitespace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
