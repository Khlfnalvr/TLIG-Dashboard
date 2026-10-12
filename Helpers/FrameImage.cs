using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace TLIGDashboard.Helpers;

/// <summary>
/// Turns one encoded frame (JPEG from the camera / HMI stream, or PNG from the local
/// HMI capture preview) into a <see cref="BitmapImage"/> an <c>Image</c> can show.
///
/// <para>One place instead of a copy per view, because the two builds need different
/// plumbing: WinUI feeds the decoder through a WinRT <see cref="DataWriter"/>, while the
/// Uno head (Android) has no <c>DataWriter.DetachStream</c> and reads the bytes straight
/// from a managed stream. Callers keep their own try/catch — a frame that fails to
/// decode is simply dropped.</para>
/// </summary>
internal static class FrameImage
{
    public static async Task<BitmapImage> DecodeAsync(byte[] bytes)
    {
        var bitmap = new BitmapImage();
#if HAS_UNO
        using var stream = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
        await bitmap.SetSourceAsync(stream);
#else
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
#endif
        return bitmap;
    }
}
