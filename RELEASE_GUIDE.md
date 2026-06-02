# Release Guide — TLIG Dashboard

Panduan ini menjelaskan langkah-langkah yang harus dilakukan setiap kali menerbitkan versi baru,
mulai dari menaikkan versi hingga artifact yang wajib ada agar **auto-update berjalan dengan benar**.

---

## Daftar isi

1. [Konvensi Versi](#1-konvensi-versi)
2. [File yang Wajib Diubah Setiap Rilis](#2-file-yang-wajib-diubah-setiap-rilis)
3. [Cara Auto-Update Bekerja](#3-cara-auto-update-bekerja)
4. [Aturan Penamaan Artifact](#4-aturan-penamaan-artifact)
5. [Langkah-Langkah Rilis (Checklist)](#5-langkah-langkah-rilis-checklist)
6. [Referensi Perintah](#6-referensi-perintah)
7. [Troubleshooting](#7-troubleshooting)

---

## 1. Konvensi Versi

Format versi mengikuti **Semantic Versioning** (`MAJOR.MINOR.PATCH`) dengan opsional suffix pre-release.

```
1.0.0-alpha   →   1.0.0-beta   →   1.0.0   →   1.1.0   →   2.0.0
```

| Suffix | Kapan digunakan |
|--------|----------------|
| `-alpha` | Build internal pertama, fitur belum lengkap |
| `-beta` | Fitur lengkap, masih dalam pengujian |
| *(tanpa suffix)* | Rilis stabil untuk produksi |

> **Penting untuk perbandingan versi:** `UpdateService.IsNewer()` menggunakan `System.Version.TryParse`.
> String seperti `1.0.0-beta` **tidak dapat di-parse** sebagai `System.Version` — sistem fallback ke
> `string.Compare` ordinal. Urutan yang dihasilkan adalah: `1.0.0-alpha < 1.0.0-beta < 1.0.0` karena
> secara leksikografis `""` < `"a"` < `"b"`. Konvensi ini **harus dijaga konsisten**.

---

## 2. File yang Wajib Diubah Setiap Rilis

Empat file harus diperbarui secara bersamaan. Jika satu tertinggal, versi yang tampil di UI,
nama installer, dan deteksi update akan tidak sinkron.

### `TLIGDashboard.csproj`

```xml
<Version>1.0.0</Version>           <!-- MAJOR.MINOR.PATCH saja, tanpa suffix -->
<AssemblyVersion>1.0.0.0</AssemblyVersion>
<FileVersion>1.0.0.0</FileVersion>
<InformationalVersion>1.0.0-beta</InformationalVersion>  <!-- ← ini yang dibaca di runtime dan dibandingkan dengan tag GitHub -->
```

> `InformationalVersion` adalah satu-satunya field yang dibaca oleh aplikasi saat runtime
> (`AssemblyInformationalVersionAttribute`) dan yang dibandingkan dengan tag GitHub release.

### `installer_server.iss`

```ini
#define AppVersion   "1.0.0-beta"
OutputBaseFilename=TLIGDashboard-Server-v1.0.0-beta-Setup
```

### `installer_client.iss`

```ini
#define AppVersion   "1.0.0-beta"
OutputBaseFilename=TLIGDashboard-Client-v1.0.0-beta-Setup
```

### `release.ps1`

```powershell
$Version = "1.0.0-beta"
```

---

## 3. Cara Auto-Update Bekerja

Memahami mekanisme ini penting agar artifact yang diupload ke GitHub Release **persis** sesuai
dengan yang diharapkan oleh `UpdateService`.

```
Saat app dibuka (setelah 3 detik)
        │
        ▼
UpdateService.CheckAsync(currentVersion)
  │  Memanggil: GET https://api.github.com/repos/Khlfnalvr/TLIG-Dashboard/releases/latest
  │  Membandingkan tag release (misal "1.0.1") dengan InformationalVersion app ("1.0.0-beta")
  │
  ├─ Versi sama / lebih lama → tidak ada notifikasi
  │
  └─ Versi lebih baru → tampilkan notifikasi
              │
              ▼
        User klik "Update"
              │
              ▼
        UpdateService.DownloadAndExtractAsync(zipUrl)
          │  Mengunduh ZIP yang dipilih berdasarkan aturan:
          │    1. *.zip  AND  Contains("Update")  AND  Contains("Server"|"Client")  ← diprioritaskan
          │    2. *.zip  AND  Contains("Server"|"Client")
          │    3. *.zip  apapun  (fallback terakhir)
          │  Mengekstrak ke folder temp
          │  Mencari: TLIGDashboard.Server.exe  atau  TLIGDashboard.Client.exe  di dalam ZIP
              │
              ▼
        UpdateService.WriteApplyScript(payloadPath, cleanupPath, appDir)
          │  Menulis apply.ps1 ke folder temp
          │  Script menunggu app ini exit, lalu robocopy payload → AppDir, restart app
              │
              ▼
        App exit → PowerShell apply.ps1 berjalan (elevated)
          │  robocopy menyalin semua file baru ke folder install
          │  Menjalankan kembali TLIGDashboard.Server.exe / .Client.exe
          │  Menghapus folder temp
```

### Aturan pemilihan ZIP (kritis)

`UpdateService` memilih ZIP pertama yang memenuhi **ketiga** kriteria secara berurutan:

```
Prioritas 1 (ideal)  : nama berakhir .zip  AND  mengandung "Update"  AND  mengandung "Server" atau "Client"
Prioritas 2 (fallback): nama berakhir .zip  AND  mengandung "Server" atau "Client"
Prioritas 3 (darurat) : nama berakhir .zip  apapun
```

**Kesimpulan praktis:** ZIP update **harus** mengandung kata `Update` dan kata `Server` atau `Client`
di nama filenya. Lihat [Aturan Penamaan Artifact](#4-aturan-penamaan-artifact).

---

## 4. Aturan Penamaan Artifact

Setiap GitHub Release **harus** mengandung tepat empat file berikut. Nama tidak boleh diubah-ubah —
`UpdateService` dan Inno Setup bergantung pada pola nama ini.

| File | Pola Nama | Dihasilkan oleh |
|------|-----------|----------------|
| Installer Server | `TLIGDashboard-Server-v{VERSION}-Setup.exe` | Inno Setup (`installer_server.iss`) |
| Installer Client | `TLIGDashboard-Client-v{VERSION}-Setup.exe` | Inno Setup (`installer_client.iss`) |
| Update ZIP Server | `TLIGDashboard-Server-v{VERSION}-Update.zip` | `release.ps1` |
| Update ZIP Client | `TLIGDashboard-Client-v{VERSION}-Update.zip` | `release.ps1` |

**Contoh untuk `v1.1.0`:**

```
TLIGDashboard-Server-v1.1.0-Setup.exe
TLIGDashboard-Client-v1.1.0-Setup.exe
TLIGDashboard-Server-v1.1.0-Update.zip
TLIGDashboard-Client-v1.1.0-Update.zip
```

### Struktur isi ZIP (wajib)

ZIP update **harus** memiliki executable di root (level paling atas), bukan di dalam subfolder.
`FindPayloadPath` mencari `TLIGDashboard.Server.exe` atau `TLIGDashboard.Client.exe` dari root
ke dalam. Struktur yang benar:

```
TLIGDashboard-Server-v1.1.0-Update.zip
├── TLIGDashboard.Server.exe      ← wajib ada di root
├── TLIGDashboard.Server.dll
├── cloudflared.exe               ← hanya di Server ZIP
├── logo.png
├── Assets/
│   └── logo.ico
└── ... (semua file dari dotnet publish)
```

> `release.ps1` menghasilkan ZIP dengan struktur yang benar secara otomatis menggunakan
> `ZipFile.CreateFromDirectory(src, zipPath, 'Optimal', $false)` — parameter `$false`
> berarti tidak membungkus dalam subfolder.

---

## 5. Langkah-Langkah Rilis (Checklist)

### A. Perbarui versi (lakukan di awal, sebelum coding selesai)

- [ ] Edit `TLIGDashboard.csproj` — ubah `InformationalVersion` ke versi baru
- [ ] Edit `installer_server.iss` — ubah `AppVersion` dan `OutputBaseFilename`
- [ ] Edit `installer_client.iss` — ubah `AppVersion` dan `OutputBaseFilename`
- [ ] Edit `release.ps1` — ubah variabel `$Version`

### B. Selesaikan dan commit kode

- [ ] Tulis/selesaikan semua perubahan kode
- [ ] Stage semua file yang berubah (`git add ...`)
- [ ] Buat commit dengan judul `Version X.Y.Z — <deskripsi singkat>`

  ```
  git commit -m "Version 1.1.0 — deskripsi perubahan"
  ```

### C. Build semua artifact

Jalankan dari root repo (membutuhkan .NET 10 SDK dan Inno Setup 6):

```powershell
.\release.ps1
```

Atau per-bagian:

```powershell
.\release.ps1 -SkipInstaller   # hanya build + ZIP
.\release.ps1 -SkipBuild       # hanya ZIP + installer dari output yang sudah ada
.\release.ps1 -Flavor Server   # hanya flavor Server
```

Setelah selesai, folder `publish\` akan berisi:

```
publish\
├── Server\                                          ← output dotnet publish Server
├── Client\                                          ← output dotnet publish Client
├── TLIGDashboard-Server-vX.Y.Z-Setup.exe
├── TLIGDashboard-Client-vX.Y.Z-Setup.exe
├── TLIGDashboard-Server-vX.Y.Z-Update.zip
└── TLIGDashboard-Client-vX.Y.Z-Update.zip
```

- [ ] Verifikasi keempat file artifact ada di `publish\`
- [ ] Pastikan ZIP berisi `.Server.exe` / `.Client.exe` di root (buka ZIP dan periksa)

### D. Push ke GitHub

```powershell
git push origin master
```

- [ ] Push berhasil dan commit tampil di GitHub

### E. Buat GitHub Release

```powershell
gh release create vX.Y.Z `
  --title "Version X.Y.Z — deskripsi" `
  --prerelease `      # hapus flag ini untuk rilis stabil
  --notes "..." `
  "publish\TLIGDashboard-Server-vX.Y.Z-Setup.exe#Installer Server" `
  "publish\TLIGDashboard-Client-vX.Y.Z-Setup.exe#Installer Client" `
  "publish\TLIGDashboard-Server-vX.Y.Z-Update.zip#Update Package Server" `
  "publish\TLIGDashboard-Client-vX.Y.Z-Update.zip#Update Package Client"
```

> **Perhatikan urutan upload:** Server sebelum Client, Setup sebelum Update.
> Urutan ini memastikan `UpdateService` menemukan Server ZIP terlebih dahulu saat
> iterasi asset — meskipun filter flavor sudah menangani ini dengan benar.

- [ ] Tag release sesuai format `vX.Y.Z` (dengan huruf `v` di depan)
- [ ] Keempat artifact terupload
- [ ] Tandai sebagai **Prerelease** untuk alpha/beta, hilangkan flag untuk rilis stabil

### F. Verifikasi auto-update (opsional tapi direkomendasikan)

Dari mesin dengan versi lama terinstall:

- [ ] Buka aplikasi → tunggu 3 detik → muncul notifikasi update
- [ ] Klik Update → progress bar berjalan → app restart ke versi baru
- [ ] Periksa versi di About — harus menampilkan versi baru

---

## 6. Referensi Perintah

### Cek versi yang sedang terinstall di build lokal

```powershell
# Baca InformationalVersion dari .csproj
(Select-Xml -Path TLIGDashboard.csproj -XPath "//InformationalVersion").Node.InnerText
```

### Buat release tanpa membangun ulang (artifact sudah ada)

```powershell
.\release.ps1 -SkipBuild
```

### Buat hanya ZIP update tanpa installer

```powershell
.\release.ps1 -SkipInstaller
```

### Cek release terbaru di GitHub

```powershell
gh release view --repo Khlfnalvr/TLIG-Dashboard
```

### Upload artifact tambahan ke release yang sudah ada

```powershell
gh release upload vX.Y.Z "publish\file.ext" --repo Khlfnalvr/TLIG-Dashboard
```

### Hapus dan buat ulang release (jika ada kesalahan artifact)

```powershell
gh release delete vX.Y.Z --yes --repo Khlfnalvr/TLIG-Dashboard
git tag -d vX.Y.Z
git push origin :refs/tags/vX.Y.Z
# kemudian buat ulang dengan gh release create ...
```

---

## 7. Troubleshooting

### Auto-update tidak mendeteksi versi baru

| Kemungkinan penyebab | Cara periksa |
|----------------------|-------------|
| Tag release tidak diawali `v` | Buka GitHub Release — tag harus `v1.1.0` bukan `1.1.0` |
| `InformationalVersion` di `.csproj` tidak diperbarui | Lihat About di aplikasi, bandingkan dengan tag release |
| Versi lama ≥ versi baru secara leksikografis | `IsNewer()` membandingkan string; pastikan konvensi suffix konsisten |
| Release ditandai sebagai Draft | Draft tidak dikembalikan oleh endpoint `releases/latest` |

### Update gagal saat ekstrak ZIP ("does not contain .exe")

| Kemungkinan penyebab | Cara periksa |
|----------------------|-------------|
| ZIP berisi subfolder, exe tidak di root | Buka ZIP — `TLIGDashboard.Server.exe` harus ada langsung, bukan di `publish\Server\TLIGDashboard.Server.exe` |
| ZIP flavor salah diunduh | Periksa nama asset di GitHub Release — harus ada kata "Server" atau "Client" |
| `release.ps1` salah path output | Jalankan ulang dengan `-Flavor Server` dan `-Flavor Client` secara terpisah |

### App tidak restart setelah update

| Kemungkinan penyebab | Cara periksa |
|----------------------|-------------|
| Apply script gagal (permission denied) | Buka `%TEMP%\TLIGDashboardUpdate\last-update-error.log` |
| App terinstall di folder non-standard | Pastikan shortcut menunjuk ke folder yang sama dengan `AppContext.BaseDirectory` |
| Antivirus memblokir robocopy / PowerShell | Tambahkan folder install ke whitelist antivirus |

### Inno Setup tidak ditemukan saat menjalankan `release.ps1`

```
Install Inno Setup 6 dari: https://jrsoftware.org/isdl.php
Path default yang diharapkan: C:\Program Files (x86)\Inno Setup 6\ISCC.exe
```

Atau jalankan tanpa installer:

```powershell
.\release.ps1 -SkipInstaller
```
