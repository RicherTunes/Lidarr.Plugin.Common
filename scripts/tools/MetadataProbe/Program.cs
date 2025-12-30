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

            return new ProbeResult(
                Path: path,
                Name: System.IO.Path.GetFileName(path),
                Artist: NullIfWhitespace(artist),
                Album: NullIfWhitespace(tag.Album),
                Title: NullIfWhitespace(tag.Title),
                Track: track,
                Disc: disc,
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
                Error: ex.Message);
        }
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
