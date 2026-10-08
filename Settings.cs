using System.Windows.Media;
using Microsoft.Win32;

namespace DynamicIsland;

/// <summary>
/// The switches of the menu and the looks picked on the page next to them, remembered in the registry between runs.
/// All the switches start on.
/// </summary>
static class Settings
{
    const string Key = @"Software\DynamicIsland";
    const int MinScale = 85, MaxScale = 130, MaxGap = 24, MaxPulse = 3;

    static bool _lyrics = Read(nameof(Lyrics)), _lyricEffects = Read(nameof(LyricEffects));
    static bool _network = Read(nameof(Network)), _hideFullscreen = Read(nameof(HideFullscreen));
    static bool _rim = Read(nameof(Rim)), _appVolume = Read(nameof(AppVolume));
    static bool _captureFromDevice = Read(nameof(CaptureFromApp));
    static bool _hideOnIdle = Read(nameof(HideOnIdle));
    static int _scale = Math.Clamp(Read(nameof(Scale), 100), MinScale, MaxScale);
    static int _gap = Math.Clamp(Read(nameof(Gap), 8), 0, MaxGap);
    static int _accent = Read(nameof(Accent), 0);
    static int _pulse = Math.Clamp(Read(nameof(Pulse), 2), 0, MaxPulse);
    static string? _audioDevice = ReadString(nameof(AudioDevice));
    static string? _lyricsFolder = ReadString(nameof(LyricsFolder));
    static string? _placementMonitor = ReadString(nameof(PlacementMonitor));
    static bool _placementBottom = Read(nameof(PlacementBottom), 0) != 0;

    /// <summary>Look the lyrics of the track up and show them. Off: nothing is sent to LRCLIB.</summary>
    public static bool Lyrics
    {
        get => _lyrics;
        set => Write(nameof(Lyrics), _lyrics = value);
    }

    /// <summary>
    /// In the expanded player the line being sung fills with light and the others sit back smaller and out of focus.
    /// Off: the lines only differ in brightness.
    /// </summary>
    public static bool LyricEffects
    {
        get => _lyricEffects;
        set => Write(nameof(LyricEffects), _lyricEffects = value);
    }

    /// <summary>Folder searched first for local .lrc files.</summary>
    public static string? LyricsFolder
    {
        get => _lyricsFolder;
        set => WriteString(nameof(LyricsFolder), _lyricsFolder = value);
    }

    /// <summary>While music plays, the island's light edge takes the colour of the cover. Off: it stays white.</summary>
    public static bool Rim
    {
        get => _rim;
        set => Write(nameof(Rim), _rim = value);
    }

    /// <summary>Over the open player the wheel turns the app that plays up and down. Off: the whole system, as everywhere else.</summary>
    public static bool AppVolume
    {
        get => _appVolume;
        set => Write(nameof(AppVolume), _appVolume = value);
    }

    /// <summary>Capture audio from the playing application instead of from a specific device.</summary>
    public static bool CaptureFromApp
    {
        get => _captureFromDevice;
        set => Write(nameof(CaptureFromApp), _captureFromDevice = value);
    }

    /// <summary>Hide the island and show only the clock when there is no audio activity.</summary>
    public static bool HideOnIdle
    {
        get => _hideOnIdle;
        set => Write(nameof(HideOnIdle), _hideOnIdle = value);
    }

    /// <summary>Notices about Wi-Fi, Ethernet and VPN.</summary>
    public static bool Network
    {
        get => _network;
        set => Write(nameof(Network), _network = value);
    }

    /// <summary>Slide away while a game or a video covers the whole screen.</summary>
    public static bool HideFullscreen
    {
        get => _hideFullscreen;
        set => Write(nameof(HideFullscreen), _hideFullscreen = value);
    }

    /// <summary>Size of the island, in percent of the one it was drawn at.</summary>
    public static int Scale
    {
        get => _scale;
        set => Write(nameof(Scale), _scale = Math.Clamp(value, MinScale, MaxScale));
    }

    /// <summary>Px between the top of the screen and the island.</summary>
    public static int Gap
    {
        get => _gap;
        set => Write(nameof(Gap), _gap = Math.Clamp(value, 0, MaxGap));
    }

    /// <summary>One colour for everything that otherwise takes the colours of the cover; null leaves it to the cover.</summary>
    public static Color? Accent
    {
        get => _accent == 0 ? null : Color.FromRgb((byte)(_accent >> 16), (byte)(_accent >> 8), (byte)_accent);
        set => Write(nameof(Accent), _accent = value is { } c ? c.R << 16 | c.G << 8 | c.B : 0);
    }

    /// <summary>How brightly the island's edge lights up on the bass of the music: 0 (not at all) → 3.</summary>
    public static int Pulse
    {
        get => _pulse;
        set => Write(nameof(Pulse), _pulse = Math.Clamp(value, 0, MaxPulse));
    }

    /// <summary>ID of the audio device to capture spectrum from. Null means default device.</summary>
    public static string? AudioDevice
    {
        get => _audioDevice;
        set => WriteString(nameof(AudioDevice), _audioDevice = value);
    }

    /// <summary>Device name of the monitor where the island is placed; null means the primary monitor.</summary>
    public static string? PlacementMonitor
    {
        get => _placementMonitor;
        set => WriteString(nameof(PlacementMonitor), _placementMonitor = value);
    }

    /// <summary>Whether the island is attached to the bottom edge instead of the top edge.</summary>
    public static bool PlacementBottom
    {
        get => _placementBottom;
        set => Write(nameof(PlacementBottom), _placementBottom = value);
    }

    /// <summary>Paths of the files lying on the shelf, in the order they were put there.</summary>
    public static string[] Shelf
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Key);
                return key?.GetValue(nameof(Shelf)) as string[] ?? [];
            }
            catch
            {
                return [];
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(Key);
                key.SetValue(nameof(Shelf), value, RegistryValueKind.MultiString);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
    }

    static bool Read(string name) => Read(name, 1) != 0;

    static int Read(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    static string? ReadString(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    static void Write(string name, bool value) => Write(name, value ? 1 : 0);

    static void Write(string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            // not saved: the setting still holds until the island is closed
            App.Log(ex);
        }
    }

    static void WriteString(string name, string? value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (value != null)
                key.SetValue(name, value, RegistryValueKind.String);
            else
                key.DeleteValue(name, false);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
