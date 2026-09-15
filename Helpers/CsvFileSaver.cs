using System.Text;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TLIGDashboard.Helpers;

/// <summary>
/// Dialog "simpan sebagai" untuk file CSV, dipakai halaman Riwayat dan dialog
/// detail percobaan — satu tempat supaya keduanya tidak pelan-pelan berbeda soal
/// pengodean maupun lokasi awal.
/// </summary>
internal static class CsvFileSaver
{
    /// <summary>
    /// Menanyakan lokasi simpan lalu menulis isinya.
    ///
    /// Ditulis UTF-8 <b>dengan BOM</b>: tanpa itu Excel membaca file sebagai ANSI
    /// dan "°C" berikut huruf beraksen di nama mahasiswa jadi rusak.
    /// </summary>
    /// <param name="csv">Isi file, sudah dirakit <see cref="Services.HeCsvExport"/>.</param>
    /// <param name="baseName">Nama berkas tanpa ekstensi; stempel waktu ditambahkan di sini.</param>
    /// <returns>
    /// Path file yang tersimpan, atau <c>null</c> kalau pengguna membatalkan —
    /// membatalkan bukan kegagalan, jadi tidak dilempar sebagai exception.
    /// Kegagalan sungguhan (disk penuh, tidak ada izin) tetap dilempar supaya
    /// pemanggilnya bisa menampilkan alasannya apa adanya.
    /// </returns>
    public static async Task<string?> SaveAsync(string csv, string baseName)
    {
        if (App.CurrentWindow is not { } window) return null;

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName      = $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}",
        };
        picker.FileTypeChoices.Add(Services.LocalizationManager.Instance.Get("Export_FileTypeCsv"), new[] { ".csv" });

        // WinUI 3: picker perlu tahu jendela pemiliknya, kalau tidak ia melempar
        // saat dibuka (pola yang sama dipakai ChartExportService).
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return null;

        await FileIO.WriteBytesAsync(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv));
        return file.Path;
    }
}
