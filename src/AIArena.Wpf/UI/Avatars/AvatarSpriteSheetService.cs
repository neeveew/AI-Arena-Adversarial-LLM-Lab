using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using AIArena.Core.Services;

namespace AIArena.Wpf.Services;

public static class AvatarSpriteSheetService
{
    private const string SpriteSheetPath = "pack://application:,,,/Assets/Avatars/robot_heads_48.png";
    private const string ManifestPath = "pack://application:,,,/Assets/Avatars/robot_heads_48.json";
    private static readonly object Sync = new();
    private static readonly Dictionary<int, ImageSource> Cache = new();
    private static BitmapSource? spriteSheet;
    private static AvatarSpriteManifest? manifest;
    private static bool loadAttempted;
    private static bool manifestLoadAttempted;

    public static ImageSource? GetAvatarForSpeaker(
        string? agentId,
        string? displayName,
        string? persona,
        string? model)
    {
        try
        {
            var sheet = LoadSpriteSheet();
            if (sheet is null)
            {
                return null;
            }

            var layout = LoadManifest();
            var selection = AvatarSpriteSelector.Select(agentId, displayName, persona, model, layout);
            lock (Sync)
            {
                if (Cache.TryGetValue(selection.TileIndex, out var cached))
                {
                    return cached;
                }

                var tileWidth = sheet.PixelWidth / layout.Columns;
                var tileHeight = sheet.PixelHeight / layout.Rows;
                if (tileWidth <= 0 || tileHeight <= 0)
                {
                    return null;
                }

                var crop = new CroppedBitmap(
                    sheet,
                    new Int32Rect(
                        selection.Column * tileWidth,
                        selection.Row * tileHeight,
                        tileWidth,
                        tileHeight));
                crop.Freeze();
                Cache[selection.TileIndex] = crop;
                return crop;
            }
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadSpriteSheet()
    {
        if (loadAttempted)
        {
            return spriteSheet;
        }

        lock (Sync)
        {
            if (loadAttempted)
            {
                return spriteSheet;
            }

            loadAttempted = true;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(SpriteSheetPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                spriteSheet = bitmap;
            }
            catch
            {
                spriteSheet = null;
            }

            return spriteSheet;
        }
    }

    private static AvatarSpriteManifest LoadManifest()
    {
        if (manifestLoadAttempted)
        {
            return manifest ?? AvatarSpriteSelector.DefaultManifest;
        }

        lock (Sync)
        {
            if (manifestLoadAttempted)
            {
                return manifest ?? AvatarSpriteSelector.DefaultManifest;
            }

            manifestLoadAttempted = true;
            try
            {
                var resource = Application.GetResourceStream(new Uri(ManifestPath, UriKind.Absolute));
                if (resource?.Stream is null)
                {
                    manifest = AvatarSpriteSelector.DefaultManifest;
                }
                else
                {
                    using var stream = resource.Stream;
                    var loaded = JsonSerializer.Deserialize<AvatarSpriteManifest>(
                        stream,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    manifest = AvatarSpriteSelector.NormalizeManifest(loaded);
                }
            }
            catch
            {
                manifest = AvatarSpriteSelector.DefaultManifest;
            }

            return manifest;
        }
    }
}
