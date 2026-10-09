## v{VERSION} — Grafik native tanpa WebView, alamat Server tetap, tampilan lebih rapi

### Yang baru

- **Grafik digambar native (tanpa WebView2 / Chart.js).** Grafik respons loop luar,
  loop dalam, dan step response PID kini digambar langsung oleh aplikasi:
  - lebih ringan — tidak ada lagi proses Edge (`msedgewebview2.exe`) per grafik;
  - tidak butuh internet publik untuk menggambar (Chart.js dulu diunduh dari CDN), jadi
    PC lab tanpa akses internet tetap menampilkan grafik;
  - memperbaiki error **"We couldn't create the data directory"** dan grafik kosong saat
    aplikasi terpasang di Program Files.
  - Interaksi: **Ctrl + scroll** untuk zoom (scroll biasa tetap menggulung halaman),
    seret untuk geser, klik ganda atau tombol Reset untuk kembali, arahkan kursor untuk
    melihat nilai, klik legenda untuk menyembunyikan/menampilkan garis.
  - Sumber datanya tidak berubah: simulasi dari sesi cascade, data PLC dari Server.
- **Alamat Server tetap `icolaboratory.com`.** Login dan daftar akun di Client tidak lagi
  meminta alamat Server. Laptop yang sebelumnya menyimpan alamat lain otomatis
  dipindahkan ke `icolaboratory.com` dan diminta login ulang sekali.
- **Kamera di Client menyesuaikan.** Di Dashboard dan Live View, kartu kamera
  disembunyikan dan HMI melebar penuh selama Server tidak menyiarkan kamera; kartu
  kamera muncul lagi begitu siaran dimulai.
- **Diagram blok tanpa latar putih.** Diagram blok cascade di Dashboard dan halaman
  Parameter digambar ulang sebagai vektor; warnanya mengikuti tema terang/gelap dan
  ikut berubah saat tema di-switch.

### Penting untuk PC Server

Client hanya bisa tersambung lewat **`icolaboratory.com`**. Pastikan Cloudflare Tunnel
di Server memakai **domain kustom `icolaboratory.com`** (Named Tunnel). Quick Tunnel
(`xxxx.trycloudflare.com`) dan alamat LAN langsung (`192.168.x.x:8088`) tidak bisa dipakai
Client lagi.

### Kompatibilitas

- Perbarui **Server dulu**, lalu Client. Tidak ada perubahan protokol maupun database
  sejak v2.2.1, jadi Client v{VERSION} tetap jalan dengan Server v2.2.1 dan sebaliknya.

### Cara memasang

- Pemasangan baru: unduh `TLIGDashboard-Server-v{VERSION}-Setup.exe` untuk PC lab dan
  `TLIGDashboard-Client-v{VERSION}-Setup.exe` untuk laptop.
- Sudah terpasang: buka aplikasi, notifikasi pembaruan muncul sendiri; pembaruan baru
  dipasang setelah diklik.
