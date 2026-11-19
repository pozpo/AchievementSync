using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using Application = System.Windows.Application;

namespace AchievementSync.Desktop.Services;

/// <summary>
/// Plays the achievement sound chosen in settings when an achievement unlocks.
/// Built-in chimes are embedded and extracted to the app data folder on first use. Best-effort:
/// sound failures never block notifications or other application work.
/// </summary>
internal static class UnlockSoundPlayer
{
    internal const string AchievementSync = "AchievementSync";
    internal const string SoftTick = "SoftTick";
    internal const string VictoryArpeggio = "VictoryArpeggio";
    internal const string Custom = "Custom";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string?> _extracted = new(StringComparer.Ordinal);
    private static MediaPlayer? _player;

    public static void TryPlay(bool enabled, int volumePercent, string? sound, string? customPath)
    {
        if (!enabled) return;
        try
        {
            var path = ResolveSound(sound, customPath);
            if (path is null) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null)
                dispatcher.BeginInvoke(new Action(() => PlayCore(path, volumePercent)));
            else
                PlayCore(path, volumePercent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            AppLog.Warning($"The achievement sound could not be played: {exception.GetType().Name}.");
        }
    }

    private static string? ResolveSound(string? sound, string? customPath)
    {
        var kind = string.Equals(sound, SoftTick, StringComparison.OrdinalIgnoreCase) ? SoftTick
            : string.Equals(sound, VictoryArpeggio, StringComparison.OrdinalIgnoreCase) ? VictoryArpeggio
            : string.Equals(sound, Custom, StringComparison.OrdinalIgnoreCase) ? Custom
            : AchievementSync;
        if (kind == Custom)
        {
            // A custom sound is fully configured when the stored path is a non-null, non-empty
            // reference to an existing .wav file (case-insensitive). Anything else means no file
            // has been chosen yet – fall back to the built-in unlock chime rather than silence.
            var candidate = customPath?.Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return ResolveSound(AchievementSync, null);
            }

            try
            {
                var fullPath = Path.GetFullPath(candidate);
                var extension = Path.GetExtension(fullPath);
                if (File.Exists(fullPath) && string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
                {
                    return fullPath;
                }

                AppLog.Warning("Custom achievement sound path is not a valid .wav file; falling back to the built-in unlock chime.");
                return ResolveSound(AchievementSync, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                AppLog.Warning($"Custom achievement sound could not be resolved ({exception.GetType().Name}); falling back to the built-in unlock chime.");
                return ResolveSound(AchievementSync, null);
            }
        }

        return EnsureExtracted(kind);
    }

    private static void PlayCore(string path, int volumePercent)
    {
        try
        {
            MediaPlayer player;
            lock (Gate)
            {
                _player?.Close();
                player = new MediaPlayer { Volume = Math.Clamp(volumePercent, 0, 100) / 100.0 };
                _player = player;
            }

            player.Open(new Uri(path, UriKind.Absolute));
            player.Play();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            AppLog.Warning($"The achievement sound could not be played: {exception.GetType().Name}.");
        }
    }

    private static string? EnsureExtracted(string soundKind)
    {
        lock (Gate)
        {
            if (_extracted.TryGetValue(soundKind, out var cached) && cached is not null && File.Exists(cached))
                return cached;
        }

        try
        {
            var resourceName = soundKind switch
            {
                SoftTick => "achievement-tick.wav",
                VictoryArpeggio => "achievement-arpeggio.wav",
                _ => "achievement-unlock.wav",
            };
            var destination = Path.Combine(AppDataDirectory, Path.ChangeExtension(resourceName, ".wav"));
            if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resource = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));
                if (resource is null) return null;
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null) return null;
                using var file = File.Create(destination);
                stream.CopyTo(file);
            }

            lock (Gate) _extracted[soundKind] = destination;
            return destination;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Warning($"The achievement sound could not be extracted: {exception.GetType().Name}.");
            return null;
        }
    }

    private static string AppDataDirectory
    {
        get
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievementSync");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
