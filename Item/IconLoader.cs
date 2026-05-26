using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace CustomDesktop.Item;

/// <summary>
/// Loads a 64 px shell thumbnail for any file or folder path via WinRT.
/// Using StorageFile/Folder thumbnails is simpler and higher quality than
/// raw SHGetFileInfo HICON → GDI conversion, and handles .lnk overlays
/// transparently through the shell thumbnail provider.
/// </summary>
internal static class IconLoader
{
    private const uint IconSize = 64;

    /// <summary>
    /// Asynchronously fetches the shell thumbnail for <paramref name="path"/>.
    /// Returns <see langword="null"/> on any failure (file not found, access
    /// denied, no thumbnail provider registered, etc.).
    /// </summary>
    internal static async Task<BitmapImage?> LoadAsync(string path)
    {
        try
        {
            StorageItemThumbnail thumbnail;

            if (Directory.Exists(path))
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(path);
                thumbnail  = await folder.GetThumbnailAsync(
                                 ThumbnailMode.SingleItem, IconSize);
            }
            else
            {
                var file  = await StorageFile.GetFileFromPathAsync(path);
                thumbnail = await file.GetThumbnailAsync(
                                ThumbnailMode.SingleItem, IconSize);
            }

            var image = new BitmapImage();
            await image.SetSourceAsync(thumbnail);
            return image;
        }
        catch
        {
            return null;  // degrade gracefully — control shows placeholder space
        }
    }
}
