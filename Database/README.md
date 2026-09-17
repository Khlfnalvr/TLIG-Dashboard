# Database HE — Antrian Giliran & Arsip Percobaan

Dua database SQLite di sisi **Server** yang menopang pemakaian plant Heat
Exchanger secara bersama-sama:

| Database | File | Isi |
|---|---|---|
| Antrian giliran | `%LOCALAPPDATA%\TLIGDashboard\heQueue.db` | siapa yang sedang memegang plant, siapa yang mengantre, dan riwayat lengkapnya |
| Arsip hasil percobaan | `%LOCALAPPDATA%\TLIGDashboard\heParamCache.db` | hasil tiap percobaan yang pernah dijalankan: parameter, metrik, dan kurva responsnya |

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
| `he_queue_users` | salinan ringan identitas pengguna (nama + peran + prioritas) supaya baris antrian & log tetap terbaca walau akunnya dihapus; kolom `last_seen_utc` mencatat denyut terakhir untuk status kehadiran (Aktif = terlihat dalam semenit terakhir, NULL = pamit) |
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

## 2. Arsip hasil percobaan (`heParamCache.db`)

### Konsep

Setiap run yang selesai disimpan utuh: parameter masukan, metrik ringkasan, dan
seluruh kurva responsnya. Gunanya satu — supaya mahasiswa bisa membaca lagi
percobaannya sendiri dan melampirkannya ke laporan.

**Arsip ini tidak pernah menggantikan percobaan baru.** Dulu ada mekanisme yang
menjawab RUN dengan hasil simpanan ketika kombinasi parameternya sama persis,
sehingga plant tidak dijalankan ulang; mekanisme itu sudah dihapus. Setiap RUN
benar-benar menjalankan plant, dan yang mengatur urutannya hanya antrian.

Kolom `param_key` masih diisi saat menyimpan — **SP, Kc, Ti, Td, dan Pump**
dibulatkan ke 3 desimal lalu digabung, mis. `60.000|2.500|10.000|0.500|75.000`
(toleransinya di `HeParameterInput.MatchDecimals`) — tapi tidak ada lagi yang
mencarinya. Begitu pula `reuse_count` dan `last_reused_at_utc`: keduanya tetap
ada sebagai jejak data lama, dan tidak ada lagi yang menaikkannya.

### Tabel

| Tabel | Isi |
|---|---|
| `he_parameter_runs` | 1 baris = 1 percobaan: parameter masukan, siapa yang menjalankan, kapan, berapa lama, dan statusnya |
| `he_parameter_run_metrics` | ringkasan kualitas respons (rise time, settling time, overshoot, ISE/IAE/ITAE) — 1:1 dengan run |
| `he_parameter_run_samples` | kurva respons per waktu, 7 kanal mengikuti `CHART_FIELDS` di `PIDtest.py` (Flow Tube, Flow Shell, Sinyal mA, Sinyal %, PV Shell in, Set Point, PV Shell out) — 1:N dengan run |

### Cara memakai dari kode

```csharp
// Sesudah satu run selesai — simpan apa adanya, termasuk yang gagal.
var run = new HeParameterRun
{
    Input             = new HeParameterInput { Sp = sp, Kc = kc, Ti = ti, Td = td, Pump = pump },
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

// Membacanya lagi: tabel "Riwayat Percobaan Saya". Siapa "saya" ditentukan
// Server dari identitas sesi, bukan dari permintaan Client.
var milikSaya = await App.HeParamCache.ListRunsByUserAsync(username);
```

Catatan penting:

- Run yang gagal atau dihentikan di tengah **tetap disimpan** (status `Failed` /
  `Aborted`): riwayat memang mencatat yang gagal juga.
- Run yang kendalinya dicabut selagi plant berjalan disimpan sebagai `Aborted`
  dengan catatan berawalan `[terputus]` (`HeParameterRun.InterruptedMarker`) —
  kolom `status` dikunci `CHECK` pada tiga nilai, jadi nilai baru berarti
  membangun ulang tabelnya. Layar menampilkannya sebagai "Terputus".
- Menjalankan ulang kombinasi yang sama tidak menimpa run lama; keduanya
  tersimpan sebagai dua baris riwayat.
- `ListRunsByUserAsync` menyaring di SQL, bukan dengan menyaring daftar lengkap
  di memori: mahasiswa tidak boleh bisa melihat percobaan mahasiswa lain, dan
  data yang tidak pernah dibaca dari disk tidak bisa bocor karena salah tulis di
  lapisan atasnya.

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
2. **Jalan + direkam.** Begitu giliran didapat, perintahnya berangkat ke bridge
   dan `HeRunRecorder` mulai mengumpulkan kurva responsnya dari `HmiDataService`.
   Tidak ada pemeriksaan "sudah pernah dijalankan atau belum": setiap RUN
   benar-benar menjalankan plant.
3. **Selesai.** Tombol STOP menutup rekaman (tersimpan ke arsip) lalu melepas
   giliran, sehingga antrean berikutnya langsung bisa jalan. RESET dan E-STOP
   sengaja tidak melepas giliran — keduanya dipakai justru saat ada yang tidak
   beres.

`HeControlService` menyembunyikan beda Server/Client persis seperti
`LearningTaskService`: di Server ia memakai repository di atas, di Client ia
memanggil endpoint di bawah. Panel statusnya `Controls/HeQueueStatusView`,
menyegarkan diri tiap 3 detik.

### Riwayat percobaan mahasiswa

Tabel **Riwayat Percobaan Saya** di `Views/ChallengeLearningPage` (di bawah kartu
"Aktivitas Saya", hanya untuk mahasiswa) membaca `he_parameter_runs` lewat
`ListRunsByUserAsync`: parameter, metrik ringkas, dan status tiap percobaan —
`Completed`, `Failed`, `Aborted`, atau "Terputus" untuk `Aborted` yang catatannya
berawalan `[terputus]`. Kurva responsnya tidak ikut dibawa (satu run bisa ribuan
titik) dan tidak ada layar yang membukanya.

Penyaringan per pengguna **selalu** dikerjakan Server dari identitas sesi: di
Server memakai username yang sedang login, di Client lewat `/he/params/my-runs`
yang tidak menerima parameter pengguna sama sekali. Tidak ada permintaan yang
bisa diubah Client untuk melihat percobaan orang lain.

Tabelnya bisa diekspor ke **CSV** untuk lampiran laporan praktikum
(`Services/HeCsvExport.Runs`). Yang masuk file persis baris yang sedang tampil.


### Endpoint (Server)

| Endpoint | Isi |
|---|---|
| `GET  /he/queue` | pemegang kendali, antrean, posisi pemanggil, boleh-tidaknya mencabut paksa |
| `POST /he/queue/request` | minta giliran; `200` = boleh jalan, `409` = masuk antrian (berisi posisi) |
| `POST /he/queue/release` | lepas kendali, rekaman run ditutup, giliran lanjut |
| `POST /he/queue/cancel` | keluar dari antrian |
| `POST /he/queue/force-release` | cabut paksa — **staf saja** (ditolak di server, bukan sekadar tombolnya disembunyikan) |
| `GET  /he/queue/log?limit=` | riwayat antrian — staf saja |
| `GET  /he/params/my-runs?limit=` | percobaan **milik pemanggil sendiri**, tanpa kurva — disaring Server dari identitas sesi |

`POST /sim/pid/run` — jalur yang benar-benar menggerakkan plant — ikut dijaga
antrian yang sama, jadi Client yang melewati layar tetap tidak bisa menyerobot:

* `run` → minta giliran; kalau belum giliran, jawabannya `409` dan perintahnya
  tidak diteruskan ke bridge;
* `sync` → hanya untuk pemegang kendali. **Kecuali** CMD STOP dan E-STOP, yang
  selalu lewat: tombol berhenti yang bisa ditolak antrian adalah tombol berhenti
  yang rusak;
* `stop` → tutup rekaman + lepas giliran.

### Penjaga di Server

`App.WatchStaleHolderAsync` berjalan tiap menit dan memutus kendali **Mahasiswa**
yang sudah dipegang lebih dari `HeControlService.MaxHold` (30 menit) — klien yang
mati atau lupa menekan BERHENTI tidak membuat antrian macet semalaman. Dosen,
Asisten, dan Admin tidak dibatasi.

Urutannya selalu **hentikan rig dulu, baru lepas giliran** (`HeRigRelease`), dan
kalau perintah berhentinya gagal sampai, gilirannya sengaja tidak dilepas. Rekaman
run yang ikut menggantung ditutup sebagai `Aborted` dengan catatan `[terputus]`,
jadi datanya tetap tersimpan lengkap untuk laporan.

### Kanal yang terekam

Model arsip menyediakan tujuh kanal, tapi VI yang dipakai sekarang mengirim
empat nama (`Flow Tube`, `PV`, `Flow Shell`, `Temp. Shell out` — lihat
`HmiDataService.DataLineFields`). Yang tidak dikirim disimpan **NULL**, bukan 0,
supaya "tidak diukur" tidak tertukar dengan "terukur nol"; `HeRunRecorder` sudah
mengenali nama-nama lainnya kalau suatu saat VI mulai mengirimnya. Set point
diambil dari nilai yang sedang diperintahkan, bukan dari balasan VI.

## 5. Yang belum dikerjakan

* Kurva respons tersimpan lengkap di `he_parameter_run_samples`, tapi belum ada
  layar yang membukanya kembali — `GetRunAsync` ada di repository sebagai satu-
  satunya jalan membacanya kalau suatu saat dibutuhkan.
* `he_queue_log` masih bisa dibaca lewat `GET /he/queue/log`, tapi untuk sementara
  tidak ada layar yang menampilkannya.
* Lapisan non-UI diuji lewat 92 skenario otomatis; kompilasi WinUI-nya
  diverifikasi oleh workflow `Build` di GitHub Actions (net10-windows tidak bisa
  dibangun di Linux).
