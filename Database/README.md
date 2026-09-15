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

## 4. Yang belum dikerjakan

Lapisan database dan aturan antriannya sudah lengkap dan bisa dipakai. Yang
belum tersambung: **UI dan endpoint jaringannya** — tombol RUN belum memanggil
`RequestControlAsync`, dan Client belum punya jalur HTTP ke Server untuk meminta
giliran (menyusul di `ShareProtocol`, sejalan dengan `/tasks` dan `/students`
yang sudah ada). Sampai itu dipasang, kedua database ini terbentuk dan siap,
tapi belum ada yang mengisinya dari layar.
