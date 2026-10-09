## v{VERSION} — Grafik plant vs simulasi, Client sepenuhnya lewat Server

Rilis ini menggabungkan isi rilis V2.2 (aset v1.2.0, yang dibangun dari branch
terpisah) ke master, ditambah perbaikan baru. Nomor versinya sengaja dinaikkan ke
{VERSION} supaya PC yang sudah memasang V2.2 mendeteksi pembaruan dengan benar.

### Yang baru

- **Dua grafik respons, plant vs simulasi.** System Response di Dashboard dan
  halaman Parameter kini terbagi dua:
  - *Loop luar (temperatur)*: simulasi RK4, baseline single-loop, setpoint, dan
    suhu **Temp. Shell out** hasil ukur PLC.
  - *Loop dalam (flow)*: simulasi flow, setpoint flow dari PID luar, dan
    **Flow Tube** hasil ukur PLC.
- **Siapa melihat grafik apa.** RUN dari Server hanya tampil di Server. RUN dari
  Client tampil di Server *dan* di Client itu — termasuk kurva simulasinya, yang
  digambar ulang di layar Server. Kurva PLC tetap dipajang setelah STOP sampai
  RUN berikutnya.
- **Kp/Ki loop dalam di Dashboard.** Kartu PID Parameters kini berisi loop luar
  (Kp, Ki, Kd) dan loop dalam (Kp, Ki), sama seperti halaman Parameter. Gain loop
  dalam hanya dipakai simulasi; paket ke LabVIEW tetap 6 angka (SP, KC, KI, KD,
  PUMP, CMD).
- **Hanya temperatur.** Pemilih Flow/Level/Temperature dihapus karena rig HE
  hanya mengendalikan temperatur. Challenge selalu bertarget Temperature; contoh
  challenge bawaan ditulis ulang untuk heat exchanger. Data challenge lama
  dimigrasikan otomatis (submission tidak hilang).
- **Panel Status Sistem.** Di Server, PLC *Online* bila tombol Connect tersambung
  atau VI tersambung ke port 6001; Sensor *Aktif* bila bacaan LabVIEW berumur di
  bawah 3 detik. Client menyalin status itu dari Server. Lampu kamera di Client
  padam lagi bila siaran kamera berhenti.

### Dari V2.2 (v1.2.0), kini resmi di master

- Client tidak punya jalur TCP sendiri ke LabVIEW/PLC: tab PLC disembunyikan,
  listener 6001 lokal tidak dibuka.
- Simulasi Cascade untuk Client dihitung Server (`POST /sim/cascade`).
- Status PLC/Sensor untuk Client lewat `GET /system/status`.
- Metrik PID dari PLC ikut direlay lewat `/hmi/latest` untuk tugas Challenge.

### Yang tidak ikut

Fitur memori chat lintas device (ringkasan otomatis + sync riwayat chat per akun)
yang ikut terbawa di aset V2.2 **tidak** ada di versi ini: fitur itu sudah
di-revert di master dan disimpan di branch `fitur/chat-memory-sync`. Riwayat chat
kembali tersimpan per laptop seperti v1.0.9.

### Kompatibilitas

- Perbarui **Server dulu**, lalu Client. Disarankan keduanya v{VERSION}.
- Client v{VERSION} ke Server V2.2/v1.2.0: tetap jalan, tetapi kurva PLC di
  grafik kosong (endpoint `/hmi/trace` belum ada di Server lama).
- Client lama ke Server v{VERSION}: tetap jalan seperti sebelumnya.

### Cara memasang

- Pemasangan baru: unduh `TLIGDashboard-Server-v{VERSION}-Setup.exe` untuk PC lab
  dan `TLIGDashboard-Client-v{VERSION}-Setup.exe` untuk laptop.
- Sudah terpasang: buka aplikasi, notifikasi pembaruan muncul sendiri; pembaruan
  baru dipasang setelah diklik.
