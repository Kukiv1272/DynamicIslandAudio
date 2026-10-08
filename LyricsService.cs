using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DynamicIsland;

/// <summary>Time-synced lyrics for the current track, from local LRC/YAML files or LRCLIB.</summary>
sealed class LyricsService
{
    public readonly record struct Line(TimeSpan Time, string Text);

    /// <param name="End">When the last sung line starts, in seconds.</param>
    sealed record Candidate(double Duration, double End, Line[] Lines, bool HasDuration);

    const double Tolerance = 4;

    static readonly Line[] None = [];
    static readonly HttpClient Http = CreateClient();
    const string Rework = @"remix|rmx|sped\s*up|speed\s*up|slowed|reverb|nightcore|hardstyle|phonk|bootleg|mashup|ремикс";

    static readonly Regex Noise = new(
        @"\s*[\(\[][^\)\]]*\b(official|video|audio|lyrics?|visuali[sz]er|remaster(ed)?|hd|hq|4k|mv|feat|ft|prod|edit|mix|version|cover|клип|премьера|"
        + Rework + @")\b[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Reworked = new(@"\b(" + Rework + @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ReworkTail = new(@"\s*[-\u2013\u2014+]?\s*\b(" + Rework + @")\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Pipes = new(@"\s*\|[^|]*\|\s*|\s+\|\s.*$", RegexOptions.Compiled);
    static readonly Regex Channel = new(@"\s*-\s*Topic$|\s*VEVO$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Stamped = new(@"^((?:\[\d+:\d+(?:\.\d+)?\])+)(.*)$", RegexOptions.Compiled);
    static readonly Regex Stamp = new(@"\[(\d+):(\d+(?:\.\d+)?)\]", RegexOptions.Compiled);

    Candidate[] _candidates = [];
    Line[] _stretched = None;
    double _stretchedFor;
    bool _reworked;
    string _key = "";
    int _version;

    public event Action? Changed;
    public bool Pending { get; private set; }

    /// <summary>Call from the UI thread whenever the track or LRC folder may have changed.</summary>
    public void Track(string title, string artist, string? localFolder = null)
    {
        string key = title + "\n" + artist + "\n" + localFolder;
        if (key == _key) return;

        _key = key;
        _candidates = [];
        _stretchedFor = 0;
        _reworked = Reworked.IsMatch(title);
        int version = ++_version;
        Pending = title.Length > 0;
        if (Pending) _ = LoadAsync(title, artist, localFolder, version);
    }

    /// <summary>Lines of the version whose length matches the playing one; empty when there is none.</summary>
    public Line[] For(TimeSpan duration)
    {
        double seconds = duration.TotalSeconds;
        if (seconds < 1) return None;

        Line[] best = None;
        double bestGap = Tolerance;
        foreach (Candidate c in _candidates)
        {
            if (c.End > seconds + 1) continue;

            // A local LRC normally has no reliable duration. Its timestamps are enough
            // to sync it, so it wins over every online candidate.
            if (!c.HasDuration)
            {
                best = c.Lines;
                bestGap = 0;
                continue;
            }

            double gap = Math.Abs(c.Duration - seconds);
            if (gap > bestGap || (gap == bestGap && best.Length > 0)) continue;
            best = c.Lines;
            bestGap = gap;
        }
        if (best.Length > 0 || !_reworked) return best;

        if (Math.Abs(seconds - _stretchedFor) > 0.5)
        {
            _stretched = Stretch(seconds);
            _stretchedFor = seconds;
        }
        return _stretched;
    }

    Line[] Stretch(double seconds)
    {
        Candidate? usual = _candidates.Where(c => c.HasDuration && c.Duration >= 30 && c.End <= c.Duration + 1)
            .GroupBy(c => Math.Round(c.Duration)).OrderByDescending(g => g.Count()).FirstOrDefault()?.First();
        if (usual == null) return None;

        double ratio = seconds / usual.Duration;
        if (ratio is < 0.5 or > 2) return None;
        return Array.ConvertAll(usual.Lines, l => new Line(l.Time * ratio, l.Text));
    }

    async Task LoadAsync(string title, string artist, string? localFolder, int version)
    {
        Candidate[] found = [];
        try
        {
            found = await Task.Run(() => FetchAsync(title, artist, localFolder));
        }
        catch
        {
            // Offline or LRCLIB is down: the island simply shows no lyrics.
        }
        if (version != _version) return;

        _candidates = found;
        _stretchedFor = 0;
        Pending = false;
        Changed?.Invoke();
    }

    static async Task<Candidate[]> FetchAsync(string title, string artist, string? localFolder)
    {
        Candidate[] local = FindLocal(title, artist, localFolder);
        if (local.Length > 0) return local;

        foreach (string query in Queries(title, artist))
        {
            using JsonDocument? json = await GetAsync("https://lrclib.net/api/search?" + query);
            if (json == null || json.RootElement.ValueKind != JsonValueKind.Array) continue;

            var found = new List<Candidate>();
            foreach (JsonElement item in json.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("syncedLyrics", out JsonElement synced) || synced.ValueKind != JsonValueKind.String) continue;
                if (!item.TryGetProperty("duration", out JsonElement duration) || duration.ValueKind != JsonValueKind.Number) continue;

                Line[] lines = Parse(synced.GetString()!);
                if (lines.Length == 0) continue;
                double end = lines.LastOrDefault(l => l.Text.Length > 0).Time.TotalSeconds;
                found.Add(new Candidate(duration.GetDouble(), end, lines, true));
            }
            if (found.Count > 0) return found.ToArray();
        }
        return [];
    }

    static Candidate[] FindLocal(string title, string artist, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        AddName(title);
        AddName($"{artist} - {title}");

        int dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && dash + 3 < title.Length)
        {
            string titleArtist = title[..dash];
            string titleSong = title[(dash + 3)..];
            AddName(titleSong);
            AddName($"{titleArtist} - {titleSong}");
        }

        try
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(path);
                if (!extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)) continue;

                string? content = null;
                if (!names.Contains(NormalizeName(path)))
                {
                    if (!extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        content = File.ReadAllText(path);
                        if (!YamlMetadataMatches(content, names)) continue;
                    }
                    catch
                    {
                        continue;
                    }
                }

                Line[] lines;
                try
                {
                    content ??= File.ReadAllText(path);
                    lines = extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase)
                        ? Parse(content)
                        : ParseYaml(content);
                }
                catch
                {
                    continue;
                }
                if (lines.Length == 0) continue;

                double end = lines.LastOrDefault(l => l.Text.Length > 0).Time.TotalSeconds;
                return [new Candidate(0, end, lines, false)];
            }
        }
        catch
        {
            // A missing or inaccessible local folder should not disable online lyrics.
        }

        return [];

        void AddName(string value)
        {
            string normalized = NormalizeName(value);
            if (normalized.Length > 0) names.Add(normalized);
        }
    }

    static string NormalizeName(string value)
    {
        value = Path.GetFileNameWithoutExtension(value)
            .Replace('\u2013', '-')
            .Replace('\u2014', '-');
        value = Noise.Replace(value, " " );
        value = Regex.Replace(value, @"[^\p{L}\p{N}]+", " ");
        return value.Trim().ToUpperInvariant();
    }

    static Line[] ParseYaml(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count == 0) return None;

        var lines = new List<Line>();
        ReadYamlNode(stream.Documents[0].RootNode, lines);
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines.ToArray();
    }

    static void ReadYamlNode(YamlNode node, List<Line> lines)
    {
        switch (node)
        {
            case YamlSequenceNode sequence:
                foreach (YamlNode child in sequence.Children) ReadYamlNode(child, lines);
                break;

            case YamlMappingNode mapping:
                YamlNode? timeNode = Child(mapping, "time", "timestamp", "start", "at");
                YamlNode? startMsNode = Child(mapping, "start_ms", "timestamp_ms", "time_ms");
                YamlNode? textNode = Child(mapping, "text", "lyric", "line", "value", "content");
                if (textNode is YamlScalarNode text
                    && TryParseYamlTime(timeNode, startMsNode, out TimeSpan parsedTime))
                {
                    lines.Add(new Line(parsedTime, text.Value?.Trim() ?? ""));
                    return;
                }

                foreach ((YamlNode key, YamlNode value) in mapping.Children)
                {
                    if (key is YamlScalarNode scalarKey
                        && TryParseYamlTime(scalarKey.Value, out TimeSpan keyTime)
                        && value is YamlScalarNode scalarValue)
                    {
                        lines.Add(new Line(keyTime, scalarValue.Value?.Trim() ?? ""));
                    }
                    else
                    {
                        ReadYamlNode(value, lines);
                    }
                }
                break;

            case YamlScalarNode scalar when scalar.Value is { } value:
                Line[] parsed = Parse(value);
                lines.AddRange(parsed);
                break;
        }
    }

    static bool YamlMetadataMatches(string yaml, HashSet<string> names)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return false;
        if (Child(root, "metadata") is not YamlMappingNode metadata) return false;

        string title = ScalarValue(Child(metadata, "title"));
        string artist = ScalarValue(Child(metadata, "artist"));
        return names.Contains(NormalizeName(title))
            || names.Contains(NormalizeName($"{artist} - {title}"));
    }

    static string ScalarValue(YamlNode? node) => node is YamlScalarNode scalar ? scalar.Value ?? "" : "";

    static YamlNode? Child(YamlMappingNode mapping, params string[] names)
    {
        foreach ((YamlNode key, YamlNode value) in mapping.Children)
        {
            if (key is YamlScalarNode scalar
                && scalar.Value is { } name
                && names.Contains(name, StringComparer.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    static bool TryParseYamlTime(YamlNode? timeNode, YamlNode? startMsNode, out TimeSpan time)
    {
        time = default;
        if (startMsNode is YamlScalarNode startMs
            && double.TryParse(startMs.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double milliseconds)
            && milliseconds >= 0)
        {
            time = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }

        string? value = timeNode is YamlScalarNode scalar ? scalar.Value : null;
        return TryParseYamlTimeValue(value, out time);
    }

    static bool TryParseYamlTime(string? value, out TimeSpan time) => TryParseYamlTimeValue(value, out time);

    static bool TryParseYamlTimeValue(string? value, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        string text = value.Trim().Trim('[', ']');
        if (text.Contains(':'))
        {
            string[] parts = text.Split(':');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out double minutes)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                time = TimeSpan.FromSeconds(minutes * 60 + seconds);
                return time >= TimeSpan.Zero;
            }

            if (parts.Length == 3
                && double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out double hours)
                && double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out double mins)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
            {
                time = TimeSpan.FromSeconds(hours * 3600 + mins * 60 + secs);
                return time >= TimeSpan.Zero;
            }
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double totalSeconds)
            && totalSeconds >= 0)
        {
            time = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }

        return false;
    }

    static async Task<JsonDocument?> GetAsync(string url)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var stream = await Http.GetStreamAsync(url);
                return await JsonDocument.ParseAsync(stream);
            }
            catch (HttpRequestException)
            {
                if (attempt == 0) await Task.Delay(1500);
            }
            catch (Exception ex) when (ex is TaskCanceledException or JsonException)
            {
                return null;
            }
        }
        return null;
    }

    static IEnumerable<string> Queries(string title, string artist)
    {
        string song = ReworkTail.Replace(Pipes.Replace(Noise.Replace(title, ""), " "), "")
            .Replace('\u2014', '-')
            .Replace('\u2013', '-')
            .Trim();
        string by = Channel.Replace(artist, "").Trim();
        if (song.Length == 0) song = title;

        if (by.Length > 0) yield return $"track_name={Uri.EscapeDataString(song)}&artist_name={Uri.EscapeDataString(by)}";

        int dash = song.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            yield return $"track_name={Uri.EscapeDataString(song[(dash + 3)..])}&artist_name={Uri.EscapeDataString(song[..dash])}";
            yield return "q=" + Uri.EscapeDataString(song.Replace(" - ", " "));
        }
        else
        {
            yield return "q=" + Uri.EscapeDataString(by.Length > 0 ? by + " " + song : song);
        }
    }

    /// <summary>LRC: "[mm:ss.xx] text", possibly with several stamps in front of one line.</summary>
    static Line[] Parse(string lrc)
    {
        var lines = new List<Line>();
        foreach (string raw in lrc.Split('\n'))
        {
            Match m = Stamped.Match(raw.Trim());
            if (!m.Success) continue;

            string text = m.Groups[2].Value.Trim();
            foreach (Match stamp in Stamp.Matches(m.Groups[1].Value))
            {
                if (!int.TryParse(stamp.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
                    || !double.TryParse(stamp.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double rest)
                    || minutes > 24 * 60) continue;
                lines.Add(new Line(TimeSpan.FromSeconds(minutes * 60 + rest), text));
            }
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines.ToArray();
    }

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/1.0");
        return http;
    }
}
