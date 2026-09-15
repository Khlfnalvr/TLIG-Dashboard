# Database HE — Antrian Giliran & Cache Parameter

Dua database SQLite di sisi **Server** yang menopang pemakaian plant Heat
Exchanger secara bersama-sama:

| Database | File | Isi |
|---|---|---|
| Antrian giliran | `%LOCALAPPDATA%\TLIGDashboard\heQueue.db` | siapa yang sedang memegang plant, siapa yang mengantre, dan riwayat lengkapnya |
| Cache hasil parameter | `%LOCALAPPDATA%\TLIGDashboard\heParamCache.db` | hasil tiap kombinasi parameter yang pernah dijalankan, supaya tidak perlu dijalankan ulang |

Skemanya ada di folder ini (`HeQueueSchema.sql`, `HeParameterCacheSchema.sql`),
ditanam sebagai *embedded resource* di dalam `.exe`. Aplikasi memasang skema
sendiri saat start (`App.InitializeHeDatabases`), jadi tidak ada langkah
pemasangan manual: jalankan Server sekali, filenya terbentuk.

Client tidak punya file ini. Hanya Server yang benar-benar terhubung ke plant,
jadi hanya Server yang berhak memutuskan giliran dan menyimpan hasil.

---

## 1. Antrian giliran (`heQueue.db`)

### Aturan

1. Plant dipegang **satu orang** pada satu waktu.
2. Kalau plant sedang dipakai, pemohon **masuk antrian**. Urutannya:
   **Admin (1) → Dosen/Asisten (2) → Mahasiswa (3)**, dan di dalam tingkat yang
   sama siapa yang lebih dulu meminta, dia yang lebih dulu dilayani.
3. Prioritas yang **lebih tinggi** boleh mengambil alih kendali dari yang lebih
   rendah (Admin dari siapa pun; Dosen dari Mahasiswa). Sesama tingkat **tidak**
   saling merebut — Dosen tidak bisa memotong percobaan Dosen lain.
4. Yang diambil alih tidak dibuang: ia dikembalikan ke antrian dengan **waktu
   permintaan aslinya**, jadi ia paling depan di tingkatnya dan langsung dapat
   giliran begitu plant bebas.

Peran akun dipetakan ke prioritas oleh `HeQueuePriorityMap.FromRole`
(`Models/HeQueueModels.cs`). Asisten sengaja disamakan dengan Dosen; peran yang
tidak dikenal jatuh ke prioritas terendah supaya data yang rusak tidak pernah
mendahului siapa pun.

### Tabel

| Tabel | Isi |
|---|---|
| `he_queue_users` | salinan ringan identitas pengguna (nama + peran + prioritas) supaya baris antrian & log tetap terbaca walau akunnya dihapus |
| `he_control_state` | satu baris tunggal: siapa pemegang kendali sekarang dan sejak kapan |
| `he_queue_items` | satu baris = satu permintaan giliran; statusnya berjalan `Waiting → Granted → Released/Cancelled/Overridden/Expired`, barisnya tidak pernah dihapus sehingga sekaligus jadi riwayat |
| `he_queue_log` | audit: siapa melakukan apa, kapan, dan mengambil alih giliran siapa |

Pengaman yang ditanam langsung di database (bukan cuma di kode):

- `uq_queue_single_holder` — mustahil ada dua pemegang kendali sekaligus.
- `uq_queue_one_waiting_per_user` — satu orang maksimal satu antrian aktif, jadi
  menekan tombol dua kali tidak menggandakan antrean.
- `CHECK (priority BETWEEN 1 AND 3)` — prioritas di luar daftar ditolak.

### Cara memakai dari kode

```csharp
var keputusan = await App.HeQueue.RequestControlAsync(
    App.Session.Username, App.Session.DisplayName, App.Session.Role, HeRequestType.Run);

if (keputusan.CanRunNow)
{
    // Granted / GrantedByOverride / AlreadyHolding → boleh kirim RUN ke LabVIEW
    JalankanPlant();
}
else
{
    // Queued / AlreadyQueued
    Info.Message = $"Plant sedang dipakai {keputusan.Holder!.DisplayName}. " +
                   $"Anda antrean ke-{keputusan.Position}.";
}
```

```csharp
await App.HeQueue.ReleaseControlAsync(userId);   // STOP / selesai → giliran lanjut otomatis
await App.HeQueue.CancelRequestAsync(userId);    // keluar dari antrian
var snapshot = await App.HeQueue.GetSnapshotAsync();   // untuk panel status antrian
var histori  = await App.HeQueue.GetRecentLogAsync(50);
```

Untuk klien yang mati/putus tanpa menekan STOP, panggil berkala dari timer Server:

```csharp
await App.HeQueue.ExpireStaleHolderAsync(TimeSpan.FromMinutes(30));
```

Admin bisa mencabut paksa lewat `ForceReleaseAsync(adminUserId, alasan)`.

Semua keputusan berjalan di dalam satu transaksi dan satu gerbang tulis, jadi
dua permintaan yang datang berbarengan dari dua Client tidak mungkin sama-sama
merasa mendapat giliran.

---

## 2. Cache hasil parameter (`heParamCache.db`)

### Konsep

Kombinasi parameter yang sama akan menghasilkan respons yang sama, jadi tidak
ada gunanya menjalankan plant dua kali untuk pertanyaan yang sama. Setiap run
yang selesai disimpan utuh: parameter masukan, metrik ringkasan, dan seluruh
kurva responsnya. Percobaan yang sudah dilakukan satu mahasiswa langsung bisa
dipakai mahasiswa lain — cache ini milik Server, bukan per-pengguna.

Yang dianggap "kombinasi sama" adalah **SP, Kc, Ti, Td, dan Pump** yang
dibulatkan ke 3 desimal lalu digabung jadi satu kunci (`param_key`), mis.
`60.000|2.500|10.000|0.500|75.000`. Pembulatan ini yang membuat selisih
floating point (0.1 + 0.2 ≠ 0.3) tidak dianggap kombinasi baru. Toleransinya
diatur lewat `HeParameterInput.MatchDecimals`.

### Tabel

| Tabel | Isi |
|---|---|
| `he_parameter_runs` | 1 baris = 1 percobaan: parameter masukan, siapa yang menjalankan, kapan, berapa lama, dan berapa kali hasilnya sudah dipakai ulang |
| `he_parameter_run_metrics` | ringkasan kualitas respons (rise time, settling time, overshoot, ISE/IAE/ITAE) — 1:1 dengan run |
| `he_parameter_run_samples` | kurva respons per waktu, 7 kanal mengikuti `CHART_FIELDS` di `PIDtest.py` (Flow Tube, Flow Shell, Sinyal mA, Sinyal %, PV Shell in, Set Point, PV Shell out) — 1:N dengan run |

### Cara memakai dari kode

```csharp
var input = new HeParameterInput { Sp = sp, Kc = kc, Ti = ti, Td = td, Pump = pump };

// 1. Cek dulu sebelum menyentuh plant.
var cached = await App.HeParamCache.FindCachedRunAsync(input);
if (cached is not null)
{
    TampilkanHasil(cached.Metrics, cached.Samples);
    Info.Message = $"Kombinasi ini sudah pernah dijalankan " +
                   $"{cached.StartedAtUtc.ToLocalTime():g} oleh {cached.RequestedByName} — " +
                   "hasil diambil dari database, plant tidak dijalankan ulang.";
    return;
}

// 2. Belum pernah → jalankan seperti biasa, kumpulkan sample selama run berjalan.
// 3. Setelah selesai, simpan supaya berikutnya tinggal ambil.
var run = new HeParameterRun
{
    Input             = input,
    Status            = HeParameterRunStatus.Completed,
    RequestedByUserId = App.Session.Username,
    RequestedByName   = App.Session.DisplayName,
    StartedAtUtc      = mulaiUtc,
    FinishedAtUtc     = DateTime.UtcNow,
    DurationSeconds   = durasi,
    Metrics           = metrik,
};
run.Samples.AddRange(sampleTerkumpul);

await App.HeParamCache.SaveRunAsync(run);
```

Catatan penting:

- Run yang gagal atau dihentikan di tengah **tetap disimpan** (status `Failed` /
  `Aborted`) sebagai catatan, tapi tidak pernah disodorkan sebagai hasil cache.
  Jadi simpan apa adanya — jangan takut mencemari cache.
- Menjalankan ulang kombinasi yang sama tidak menimpa run lama; yang dipakai
  selalu yang **terbaru**. Kalau plant baru dikalibrasi, cukup jalankan sekali
  lagi untuk memperbarui hasilnya (atau `ClearAsync()` untuk mengosongkan cache).
- `FindCachedRunAsync` menaikkan penghitung `reuse_count`, jadi bisa dilaporkan
  berapa kali plant tidak perlu dijalankan. Pakai `countReuse: false` kalau hanya
  mengintip isi cache untuk daftar/tabel.

---

## 3. Cadangan & pemeriksaan manual

Kedua file berdiri sendiri — cukup salin filenya untuk backup (matikan Server
dulu, atau salin juga `-wal`/`-shm` di sebelahnya). Isinya bisa diperiksa dengan
alat SQLite apa pun:

```
sqlite3 %LOCALAPPDATA%\TLIGDashboard\heQueue.db "SELECT * FROM he_queue_log ORDER BY log_id DESC LIMIT 20;"
sqlite3 %LOCALAPPDATA%\TLIGDashboard\heParamCache.db "SELECT run_id, param_key, reuse_count FROM he_parameter_runs;"
```

## 4. Bagaimana layar dan jaringan memakainya

### Alur satu kali RUN

1. **Minta giliran.** Tombol RUN di kartu Control (`DashboardPage.RunPidAsync`)
   memanggil `HeControlService.RequestAsync(HeRequestType.Run)`. Kalau plant
   sedang dipakai orang lain, penekan RUN masuk antrian, sebuah InfoBar
   memberitahu posisinya, dan **tidak ada apa pun yang dikirim ke LabVIEW**.
2. **Cek cache.** Sebelum plant disentuh, `FindCachedRunAsync` dicari lebih
   dulu. Kalau kombinasi SP/Kc/Ti/Td/Pump-nya sudah pernah dijalankan, hasilnya
   ditampilkan dari database dan plant tidak dijalankan ulang. Tombol "tetap
   jalankan di plant" pada InfoBar itu melewati cache satu kali.
3. **Jalan + direkam.** Baru setelah itu perintah berangkat ke bridge, dan
   `HeRunRecorder` mulai mengumpulkan kurva responsnya dari `HmiDataService`.
4. **Selesai.** Tombol STOP menutup rekaman (tersimpan ke cache) lalu melepas
   giliran, sehingga antrean berikutnya langsung bisa jalan. RESET dan E-STOP
   sengaja tidak melepas giliran — keduanya dipakai justru saat ada yang tidak
   beres.

`HeControlService` menyembunyikan beda Server/Client persis seperti
`LearningTaskService`: di Server ia memakai repository di atas, di Client ia
memanggil endpoint di bawah. Panel statusnya `Controls/HeQueueStatusView`,
menyegarkan diri tiap 3 detik.

### Endpoint (Server)

| Endpoint | Isi |
|---|---|
| `GET  /he/queue` | pemegang kendali, antrean, posisi pemanggil, boleh-tidaknya mencabut paksa |
| `POST /he/queue/request` | minta giliran; `200` = boleh jalan, `409` = masuk antrian (berisi posisi) |
| `POST /he/queue/release` | lepas kendali, rekaman run ditutup, giliran lanjut |
| `POST /he/queue/cancel` | keluar dari antrian |
| `POST /he/queue/force-release` | cabut paksa — **Admin saja** (ditolak di server, bukan sekadar tombolnya disembunyikan) |
| `GET  /he/queue/log?limit=` | riwayat antrian — staf saja |
| `POST /he/params/lookup` | hasil cache untuk satu kombinasi parameter (kurvanya dijarangkan ≤1200 titik) |

`POST /sim/pid/run` — jalur yang benar-benar menggerakkan plant — ikut dijaga
antrian yang sama, jadi Client yang melewati layar tetap tidak bisa menyerobot:

* `run` → minta giliran; kalau belum giliran, jawabannya `409` dan perintahnya
  tidak diteruskan ke bridge;
* `sync` → hanya untuk pemegang kendali. **Kecuali** CMD STOP dan E-STOP, yang
  selalu lewat: tombol berhenti yang bisa ditolak antrian adalah tombol berhenti
  yang rusak;
* `stop` → tutup rekaman + lepas giliran.

### Penjaga di Server

`App.WatchStaleHolderAsync` berjalan tiap menit dan melepas kendali yang sudah
dipegang lebih dari `HeControlService.MaxHold` (30 menit) — klien yang mati atau
lupa menekan STOP tidak membuat antrian macet semalaman. Rekaman run yang ikut
menggantung ditutup sebagai `Aborted`, jadi tidak pernah dipakai ulang.

### Kanal yang terekam

Model cache menyediakan tujuh kanal, tapi VI yang dipakai sekarang mengirim
empat nama (`Flow Tube`, `PV`, `Flow Shell`, `Temp. Shell out` — lihat
`HmiDataService.DataLineFields`). Yang tidak dikirim disimpan **NULL**, bukan 0,
supaya "tidak diukur" tidak tertukar dengan "terukur nol"; `HeRunRecorder` sudah
mengenali nama-nama lainnya kalau suatu saat VI mulai mengirimnya. Set point
diambil dari nilai yang sedang diperintahkan, bukan dari balasan VI.

## 5. Yang belum dikerjakan

* Halaman riwayat/laporan yang memakai `GET /he/queue/log` dan `ListRunsAsync`
  belum ada — datanya sudah terkumpul, tampilannya belum dibuat.
* Kurva dari cache baru ditampilkan sebagai ringkasan metrik di InfoBar, belum
  digambar ulang ke chart respons.
* Kompilasi WinUI belum diverifikasi di lingkungan ini (net10-windows tidak bisa
  dibangun di Linux); lapisan non-UI-nya diuji lewat 44 skenario otomatis.
