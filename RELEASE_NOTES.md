## v{VERSION} — Mode Manual/Auto Cascade seperti VI, gain PID benar di LabVIEW, simulasi flow sesuai rig

### Yang baru

- **Mode Manual/Auto di Cascade Control mengikuti VI.**
  - **Auto:** PID bebas mengatur valve 0–100%.
  - **Manual:** valve ditahan di nilai kotak **Bukaan Valve** tanpa PID, sehingga
    grafik menunjukkan respons open-loop plant pada bukaan itu, lengkap dengan
    penjelasan flow dan suhu tunak yang dicapai.
  - Kartu metrik di mode Manual kini terisi: rise time, overshoot dan settling diukur
    terhadap suhu akhir respons itu sendiri; error tunak dan IAE/ISE/ITAE tetap
    terhadap setpoint.
  - Radio Manual/Auto di Dashboard menentukan simulasi; halaman Cascade mengikuti
    mode Dashboard.
- **Rekomendasi tuning terbaik yang mungkin.** Bila tidak ada kandidat "Ideal",
  Advisor menawarkan tuning tercepat yang stabil dan patuh batas, asalkan lebih baik
  dari gain sekarang (mis. SP 60 °C: settling 799 s → 446 s).

### Perbaikan

- **Gain PID terkirim dengan benar ke LabVIEW.** PID NI di VI memakai bentuk
  Kc, Ti (menit), Td (menit), sedangkan Dashboard dulu mengirim Kp/Ki/Kd apa adanya —
  aksi integral jadi ribuan kali terlalu kuat. Kini dikonversi otomatis
  (Kc = Kp, Ti = Kp/Ki/60, Td = Kd/Kp/60) untuk Dashboard, halaman Parameter, dan run
  dari Client. `PIDtest.py` lama tetap jalan.
- **Model flow sesuai rig:** 0–4,65 L/min (sama dengan flow maksimum di LabVIEW),
  sebelumnya 1–7 L/min. Flow simulasi tidak bisa lagi bernilai negatif.
- **Grafik mencapai setpoint di semua suhu yang terjangkau.** Gangguan flow
  diskalakan ke sisa kapasitas valve, dan pembanding single-loop memakai gain yang
  setara (sebelumnya ~21× terlalu lemah sehingga keunggulan cascade dilebih-lebihkan).
- **Error tunak ditampilkan dalam persen** dan rekomendasi gain tanpa pembulatan
  (mis. Ki 0,0007, bukan 0.001).
- **Panel AI Advisor tidak muncul lagi** setelah "Ya (Terapkan)", dan gain yang
  diterapkan langsung terkirim ke LabVIEW tanpa menunggu RUN berikutnya.
- Kartu fungsi transfer: Gp2 = 0,0465 (sebelumnya 0,06).

### Kompatibilitas

- **Perbarui Server dulu, lalu Client.** Simulasi Cascade untuk Client dijalankan di
  Server, jadi model flow dan mode Manual baru berlaku setelah Server diperbarui.
  Client v2.2.6 tetap jalan dengan Server v{VERSION}; Client v{VERSION} di Server lama
  tidak mendapat mode Manual.

### Cara memasang

- Pemasangan baru: unduh `TLIGDashboard-Server-v{VERSION}-Setup.exe` untuk PC lab dan
  `TLIGDashboard-Client-v{VERSION}-Setup.exe` untuk laptop.
- Sudah terpasang: buka aplikasi, notifikasi pembaruan muncul sendiri; pembaruan baru
  dipasang setelah diklik.
