# TLIGDashboard Release Guidelines

Panduan ini menjelaskan file yang wajib dibuat saat release agar fitur update
aplikasi berjalan normal, terutama silent update yang mengunduh ZIP lalu
me-restart TLIGDashboard.

## Asset Wajib Di GitHub Release

Setiap release GitHub wajib memiliki dua asset berikut:

1. `TLIGDashboardSetup-vX.Y.Z.exe`
   - Installer penuh untuk instalasi manual atau fresh install.
   - Dibuat dari `installer.iss` menggunakan Inno Setup.

2. `TLIGDashboardUpdate-vX.Y.Z.zip`
   - Paket update untuk fitur update otomatis di aplikasi.
   - Nama file harus berakhiran `.zip`.
   - Nama file sebaiknya mengandung kata `Update`, karena updater akan
     memprioritaskan asset ZIP dengan nama tersebut.

Contoh untuk versi `2.0.2`:

```text
TLIGDashboardSetup-v2.0.2.exe
TLIGDashboardUpdate-v2.0.2.zip
```

## Format ZIP Update

ZIP update harus berisi isi folder publish secara langsung di root ZIP.
Jangan bungkus file publish di dalam folder tambahan.

Benar:

```text
TLIGDashboardUpdate-v2.0.2.zip
+-- TLIGDashboard.exe
+-- TLIGDashboard.dll
+-- TLIGDashboard.deps.json
+-- TLIGDashboard.runtimeconfig.json
+-- resources.pri
+-- Microsoft.ui.xaml.dll
+-- WinRT.Runtime.dll
+-- Assets/
+-- ...file dan folder publish lainnya
```

Salah:

```text
TLIGDashboardUpdate-v2.0.2.zip
+-- AppFiles/
    +-- TLIGDashboard.exe
    +-- ...
```

Salah:

```text
TLIGDashboardUpdate-v2.0.2.zip
+-- TLIGDashboard-v2.0.2/
    +-- TLIGDashboard.exe
    +-- ...
```

Minimal, ZIP harus memiliki `TLIGDashboard.exe` di root atau di folder payload
yang dapat ditemukan updater. Format root tetap wajib dipakai untuk menjaga
release konsisten dan mudah diverifikasi.

## Cara Kerja Updater

Updater membaca release terbaru dari:

```text
https://api.github.com/repos/Khlfnalvr/TLIGDashboard/releases/latest
```

Lalu updater:

1. Membandingkan versi aplikasi saat ini dengan `tag_name` release GitHub.
2. Mencari asset ZIP, dengan prioritas nama yang mengandung `Update`.
3. Mengunduh ZIP ke folder temp.
4. Mengekstrak ZIP dan memastikan payload berisi `TLIGDashboard.exe`.
5. Menjalankan helper PowerShell dengan privilege admin.
6. Menunggu proses TLIGDashboard lama keluar.
7. Menyalin file payload ke folder instalasi.
8. Menjalankan ulang `TLIGDashboard.exe`.

Karena itu, release tanpa ZIP update yang benar akan membuat tombol
`Download & Apply` gagal atau tidak dapat melakukan silent update.

## Checklist Sebelum Release

Update versi di file berikut:

```text
TLIGDashboard.csproj
installer.iss
```

Pastikan nilai versinya sama:

```xml
<Version>X.Y.Z</Version>
<AssemblyVersion>X.Y.Z.0</AssemblyVersion>
<FileVersion>X.Y.Z.0</FileVersion>
<InformationalVersion>X.Y.Z</InformationalVersion>
```

Di `installer.iss`:

```text
#define AppVersion   "X.Y.Z"
OutputBaseFilename=TLIGDashboardSetup-vX.Y.Z
VersionInfoVersion=X.Y.Z.0
```

## Build Release

Bersihkan output publish lama:

```powershell
Remove-Item -LiteralPath .\Publish\AppFiles -Recurse -Force -ErrorAction SilentlyContinue
```

Publish aplikasi:

```powershell
dotnet publish .\TLIGDashboard.csproj -c Release -r win-x64 --self-contained true -o .\Publish\AppFiles
```

Buat ZIP update:

```powershell
Compress-Archive -Path .\Publish\AppFiles\* -DestinationPath .\Publish\TLIGDashboardUpdate-vX.Y.Z.zip -Force
```

Buat installer:

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\installer.iss
```

Hasil yang harus ada:

```text
Publish\TLIGDashboardSetup-vX.Y.Z.exe
Publish\TLIGDashboardUpdate-vX.Y.Z.zip
```

## Verifikasi ZIP

Pastikan ZIP berisi `TLIGDashboard.exe` dan file publish utama:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::OpenRead(".\Publish\TLIGDashboardUpdate-vX.Y.Z.zip").Entries |
    Where-Object {
        $_.FullName -eq "TLIGDashboard.exe" -or
        $_.FullName -eq "TLIGDashboard.dll" -or
        $_.FullName -eq "TLIGDashboard.runtimeconfig.json"
    } |
    Select-Object FullName, Length
```

Output minimal harus menampilkan:

```text
TLIGDashboard.exe
TLIGDashboard.dll
TLIGDashboard.runtimeconfig.json
```

## Buat GitHub Release

Tag release harus memakai format:

```text
vX.Y.Z
```

Upload kedua asset:

```powershell
gh release create vX.Y.Z `
  ".\Publish\TLIGDashboardSetup-vX.Y.Z.exe#TLIGDashboardSetup-vX.Y.Z.exe" `
  ".\Publish\TLIGDashboardUpdate-vX.Y.Z.zip#TLIGDashboardUpdate-vX.Y.Z.zip" `
  --repo Khlfnalvr/TLIGDashboard `
  --target master `
  --title "vX.Y.Z - Release title" `
  --notes "Ringkasan perubahan release."
```

Verifikasi asset release:

```powershell
gh release view vX.Y.Z --repo Khlfnalvr/TLIGDashboard --json tagName,name,url,assets,publishedAt
```

## Checklist Akhir

Sebelum mengumumkan release, pastikan:

- `TLIGDashboard.csproj` sudah memakai versi baru.
- `installer.iss` sudah memakai versi baru.
- GitHub release memakai tag `vX.Y.Z`.
- Release memiliki asset installer `.exe`.
- Release memiliki asset update `.zip`.
- Nama ZIP update mengandung `Update`.
- ZIP update berisi `TLIGDashboard.exe` di root.
- ZIP update dibuat dari output `Publish\AppFiles` versi terbaru.
- Commit versi dan fix sudah di-push ke branch release, biasanya `master`.

Jika salah satu item di atas tidak terpenuhi, fitur update aplikasi berisiko
tidak mendeteksi release, salah memilih asset, gagal mengekstrak payload, atau
gagal menjalankan silent update.
