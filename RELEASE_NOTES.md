## v{VERSION} — Kelas dan NRP mahasiswa cukup diisi sekali

### Perbaikan

- **Dialog "Complete Your Profile" tidak muncul lagi di setiap login.** Kelas dan NRP
  yang diisi mahasiswa di Client kini disimpan ke akun di Server dan dikirim balik saat
  login, jadi dialog cukup dijawab sekali.
  - Client juga menyimpan salinannya sendiri, sehingga dialog tetap tidak muncul lagi
    walaupun Server sedang tidak terjangkau saat itu; salinan tersebut dikirim ulang ke
    Server pada login berikutnya.
  - Tombol **Save & Continue** baru aktif setelah Kelas dipilih dan NRP diisi, supaya
    profil tidak tersimpan setengah.

### Kompatibilitas

- **Perbarui Server dulu, lalu Client.** Penyimpanan Kelas/NRP di akun memerlukan
  Server v{VERSION}. Client v{VERSION} di Server lama tetap jalan: dialog hanya muncul
  sekali per laptop, tetapi Kelas/NRP belum tercatat di sisi dosen sampai Server
  diperbarui.

### Cara memasang

- Pemasangan baru: unduh `TLIGDashboard-Server-v{VERSION}-Setup.exe` untuk PC lab dan
  `TLIGDashboard-Client-v{VERSION}-Setup.exe` untuk laptop.
- Sudah terpasang: buka aplikasi, notifikasi pembaruan muncul sendiri; pembaruan baru
  dipasang setelah diklik.
