# Panduan Penggunaan TLIG Dashboard

Panduan ini menjelaskan cara memakai **TLIG Dashboard** — aplikasi desktop Windows (WinUI 3) untuk memantau dan mengendalikan sistem kontrol PID di ICO Laboratory, sekaligus platform pembelajaran (challenge, penilaian) dan chat AI. Aplikasi ini dirilis dalam **dua varian dari kode yang sama**: **Server** dan **Client**. Panduan berlaku untuk v1.0.0-Kilo.

---

## Daftar Isi

1. [Konsep Dasar: Server vs Client](#1-konsep-dasar-server-vs-client)
2. [Instalasi](#2-instalasi)
3. [Peran Pengguna (Role)](#3-peran-pengguna-role)
4. [Login, Registrasi, dan Onboarding](#4-login-registrasi-dan-onboarding)
5. [Panduan untuk Server](#5-panduan-untuk-server)
6. [Panduan untuk Client](#6-panduan-untuk-client)
7. [Halaman-Halaman Aplikasi](#7-halaman-halaman-aplikasi)
8. [Fitur Antarmuka Umum](#8-fitur-antarmuka-umum)
9. [Pemecahan Masalah](#9-pemecahan-masalah)

---

## 1. Konsep Dasar: Server vs Client

| | **Server** (`TLIGDashboard.Server.exe`) | **Client** (`TLIGDashboard.Client.exe`) |
|---|---|---|
| Dijalankan di | PC lab yang tersambung langsung ke HMI LabVIEW/PLC | Laptop/PC mahasiswa atau dosen di mana saja |
| Kamera & layar HMI | **Menyiarkan** (broadcast) ke semua Client yang terhubung | **Menerima** siaran dari Server |
| Kunci API AI | Disimpan lokal di Server (tidak pernah dikirim ke Client) | Tidak perlu kunci sendiri — chat diteruskan lewat Server |
| Login | Hanya akun **staf (Admin/Dosen/Asisten)** yang boleh login langsung di Server | Semua peran bisa login, harus mengisi alamat Server |
| Halaman navigasi | Sama persis dengan Client, ditambah halaman **Settings** dan **Users** | Sama persis dengan Server, kecuali Settings & Users tidak muncul |
| Registrasi mandiri | Tidak ada (akun dibuat lewat halaman Users) | Ada, lewat tautan "Buat akun" di layar login |

Intinya: **satu PC di lab menjalankan Server** (terhubung ke PLC via TCP, menyalakan siaran kamera/HMI, menyimpan kunci AI), sedangkan **semua orang lain memakai Client** dan menyambung ke Server itu lewat alamat IP/host + port, baik di jaringan lokal (LAN) maupun dari luar kampus lewat Cloudflare Tunnel.

---

## 2. Instalasi

Ambil installer sesuai perannya dari rilis GitHub (`TLIGDashboard-Server-v{versi}-Setup.exe` atau `TLIGDashboard-Client-v{versi}-Setup.exe`), lalu jalankan sebagai administrator (installer meminta hak admin). Setelah instalasi selesai, aplikasi bisa langsung dijalankan dari shortcut Start Menu/Desktop.

- **PC di laboratorium yang tersambung ke PLC/HMI** → install **Server**.
- **Laptop dosen, asisten, atau mahasiswa** → install **Client**.

Jangan pasang kedua varian di komputer yang sama untuk keperluan produksi — keduanya independen (folder instalasi dan proses terpisah), tapi perannya memang dibuat untuk mesin yang berbeda.

Aplikasi memiliki **pembaruan otomatis**: 3 detik setelah dibuka, aplikasi mengecek rilis terbaru di GitHub. Jika ada versi baru, muncul notifikasi dengan tombol untuk mengunduh dan memasang otomatis (aplikasi akan menutup diri sebentar lalu terbuka kembali di versi baru).

---

## 3. Peran Pengguna (Role)

| Peran | Deskripsi | Bisa login di Server? | Prioritas antrian HE |
|---|---|---|---|
| **Admin** | Pengelola lab, akses penuh | Ya | 1 (paling didahulukan) |
| **Dosen** | Staf pengajar, akses penuh | Ya | 2 |
| **Asisten** | Asisten praktikum, akses penuh setara Dosen | Ya | 2 (setara Dosen) |
| **Mahasiswa** | Peserta praktikum, akses terbatas (hanya lihat/kerjakan tugas & challenge) | **Tidak** — akun mahasiswa hanya bisa login lewat Client | 3 |

Admin, Dosen, dan Asisten disebut **staf**. Mereka yang boleh: mengelola pengguna, membuat/menilai tugas & challenge, mengatur siaran dan koneksi PLC, serta mengonfigurasi provider AI. Akun awal bawaan (seed) adalah `admin` / `admin` dengan peran Admin — segera ganti kata sandinya lewat halaman **Users** setelah instalasi pertama. (Pada instalasi lama yang databasenya sudah ada, akun `admin` dinaikkan sekali ke peran Admin saat aplikasi dijalankan, selama belum ada akun Admin lain.)

Kolom **prioritas antrian HE** menentukan siapa yang lebih dulu boleh mengoperasikan plant Heat Exchanger saat beberapa orang ingin memakainya bersamaan — lihat [Database/README.md](Database/README.md).

---

## 4. Login, Registrasi, dan Onboarding

Saat aplikasi dibuka, layar login (overlay) langsung tampil di atas jendela utama.

### Login di Server
Isi **Username** dan **Password**, lalu klik tombol login. Server memvalidasi langsung ke database pengguna lokal. Jika akun yang dipakai berperan Mahasiswa, login **ditolak** dengan pesan bahwa akun mahasiswa tidak bisa masuk ke Server — gunakan akun Dosen/Asisten.

### Login di Client
Selain Username dan Password, ada kolom tambahan **Alamat Server** (host, boleh dengan port seperti `192.168.1.10:8088`, atau domain seperti `xxxx.trycloudflare.com` tanpa port untuk koneksi HTTPS). Client mengirim kredensial ke Server lewat jaringan; jika berhasil, sesi (token) tersimpan sehingga tidak perlu login ulang setiap membuka aplikasi (kecuali logout).

### Registrasi mandiri (khusus Client)
Klik tautan "Buat akun" di layar login untuk membuka form pendaftaran. Field yang diminta: **Email** dan **Kata sandi + konfirmasi**. Aturan email:
- Harus domain kampus **`its.ac.id`** atau subdomainnya (mis. `nama@mhs.its.ac.id`), **atau**
- Domain staf **`ep.itc.ac.id`**.

Tidak ada verifikasi email lewat kode/OTP — begitu format & domain valid dan kata sandi ≥ 6 karakter, akun langsung dibuat. **Catatan penting:** akun hasil pendaftaran mandiri **selalu berperan Mahasiswa**, walau didaftarkan dengan email domain staf. Akun Dosen/Asisten hanya bisa dibuat manual oleh staf lain lewat halaman **Users** di Server. Setelah berhasil daftar, Anda diarahkan kembali ke form login untuk masuk secara terpisah (tidak otomatis login).

### Onboarding mahasiswa
Saat mahasiswa login pertama kali dan datanya belum lengkap, muncul dialog untuk mengisi **Kelas** (A/B/C/D) dan **NRP**. Data ini dipakai untuk pencocokan roster di fitur Challenge Learning dan Penilaian.

---

## 5. Panduan untuk Server

Setelah login sebagai Dosen/Asisten di aplikasi Server, buka halaman **Settings** (nav bar, hanya muncul di Server setelah login) untuk mengatur semuanya: siaran, akses jarak jauh, koneksi PLC, dan AI.

### 5.1 Menyalakan siaran kamera & layar HMI (Share Server)
Kartu **Server** di halaman Settings:
1. Atur **Port** (default `8088`). Port tidak bisa diubah selagi server sedang berjalan.
2. Centang **Share Camera** dan/atau **Share HMI** sesuai yang ingin disiarkan ke Client.
3. Klik tombol **Start** untuk menyalakan.
4. Status koneksi, jumlah client yang tersambung, serta alamat LAN dan IP publik ditampilkan otomatis di bawah tombol.

Client di jaringan yang sama (LAN kampus/lab) cukup memasukkan alamat seperti `192.168.x.x:8088` saat login — tidak perlu setup tambahan.

### 5.2 Akses dari luar jaringan kampus (Cloudflare Tunnel)
Jika mahasiswa/dosen perlu tersambung dari luar LAN (mis. dari rumah, atau jaringan kampus yang memblokir port masuk), gunakan kartu **Cloudflare Tunnel**:
- Klik tombol toggle untuk menyalakan **Quick Tunnel** — ini otomatis membuat URL publik acak berbentuk `xxxx.trycloudflare.com` tanpa perlu akun Cloudflare maupun buka port router. URL ini berubah setiap kali tunnel dinyalakan ulang.
- Untuk domain tetap (tidak berubah-ubah), centang **Gunakan domain kustom**, isi nama domain, lalu klik **Login** untuk autentikasi ke akun Cloudflare (Named Tunnel) — opsi ini butuh akun & domain Cloudflare sendiri.
- Bagikan URL yang muncul (ada tombol salin) ke Client — mereka memasukkannya sebagai **Alamat Server** saat login (tanpa port, karena otomatis pakai HTTPS/WSS).

### 5.3 Menghubungkan PLC (HMI LabVIEW via TCP)
Kartu **PLC Connection** (label lama "OPC UA" masih terlihat di beberapa tempat, tapi protokolnya sekarang TCP langsung ke HMI LabVIEW, bukan OPC UA/DA):
1. Isi **Host** (alamat IP mesin yang menjalankan HMI LabVIEW, mis. `192.168.1.10`) dan **Port**.
2. Klik **Connect**. Titik indikator berubah hijau saat tersambung.
3. Setelah tersambung, nilai PID (rise time, overshoot, settling time, steady-state error) yang dikirim HMI otomatis mengalir ke Dashboard dan fitur Challenge (untuk penilaian otomatis).

Koneksi ini otomatis mencoba menyambung ulang setiap 5 detik jika terputus, sampai pengguna menekan Disconnect secara manual. Tab yang sama juga bisa dibuka lewat ikon status PLC di title bar (pojok kanan atas jendela) tanpa harus membuka halaman Settings.

### 5.4 Mengonfigurasi AI
Kartu **AI** di halaman Settings (atau tab "AI" di flyout title bar) membuka dialog **Pengaturan AI**, berisi satu kartu per provider:

| Provider | Model yang tersedia |
|---|---|
| DeepSeek | `deepseek-v4-flash`, `deepseek-v4-pro` |
| OpenAI | `gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`, `gpt-5.4-nano` |
| Anthropic | `claude-opus-4-8`, `claude-sonnet-5`, `claude-sonnet-4-6`, `claude-haiku-4-5` |
| Google Gemini | `gemini-3.1-pro-preview`, `gemini-3.5-flash`, `gemini-3.1-flash-lite` |

Untuk tiap provider yang ingin diaktifkan:
1. Nyalakan **toggle** provider.
2. Masukkan **API Key** milik provider tersebut (kunci ini hanya tersimpan di Server, tidak pernah dikirim ke Client).
3. Centang model mana saja yang boleh dipilih pengguna.
4. (Opsional) Isi **System Prompt** bersama yang berlaku untuk semua provider.
5. Klik **Save**.

Semua pengguna (Server maupun Client, staf maupun mahasiswa) lalu bisa memilih provider + model yang sudah diaktifkan lewat dropdown di halaman AI Chat / Dashboard — hanya staf yang bisa membuka dialog konfigurasi ini (ikon gerigi/gear di sebelah dropdown).

### 5.5 Mengelola pengguna (halaman Users)
Halaman **Users** hanya tampil di Server, untuk akun berperan staf. Di sini staf bisa:
- **Tambah pengguna**: isi Username, Nama Tampilan, Password, NRP (opsional, untuk mahasiswa), Kelas (opsional), dan pilih Role (Admin/Dosen/Asisten/Mahasiswa).
- **Edit**: ubah Nama Tampilan, NRP, Kelas.
- **Reset Password**: set kata sandi baru untuk akun tertentu.
- **Aktifkan/Nonaktifkan**: akun nonaktif tidak bisa login.
- **Hapus** akun.

Sistem selalu menjaga minimal **satu akun staf yang aktif** — Anda tidak bisa menonaktifkan, menurunkan peran, atau menghapus staf terakhir yang tersisa.

---

## 6. Panduan untuk Client

1. Buka aplikasi, isi **Username**, **Password**, dan **Alamat Server** (minta ke admin lab/dosen — bisa berupa IP LAN `192.168.x.x:port` atau URL Cloudflare Tunnel `xxxx.trycloudflare.com`).
2. Kalau belum punya akun, klik **Buat akun** dan daftar dengan email kampus (lihat [bagian 4](#4-login-registrasi-dan-onboarding)).
3. Setelah login berhasil, semua halaman navigasi yang sama seperti Server akan tampil (kecuali Settings & Users) — Dashboard, Parameter, Live View, Learning Analytic, AI Chat.
4. Kamera dan layar HMI di halaman **Live View**/Dashboard otomatis menampilkan siaran dari Server begitu tersambung — Client tidak membuka kamera device sendiri.
5. Untuk melihat/mengubah status koneksi ke Server (siapa yang login, ganti server, logout), buka flyout koneksi di title bar → tab **Connect**.

**Batasan khusus Client di halaman Parameter:** penerapan parameter PID (tombol **Terapkan**) dibatasi maksimal **3 kali**; setelah limit tercapai tombol terkunci dan muncul info bar peringatan. Batasan ini tidak berlaku di Server.

---

## 7. Halaman-Halaman Aplikasi

Navigasi utama (bar atas): **Dashboard · Parameter · Live View · Learning Analytic · AI Chat**, ditambah **Settings** dan **Users** khusus Server. Urutan dan isi sama di kedua varian kecuali disebutkan lain.

### 7.1 Dashboard
Ringkasan sistem kontrol dalam satu layar:
- **Diagram blok PID** — alur Setpoint → PID → Plant.
- Input **Kp / Ki / Kd** dan **grafik respons sistem** (step response).
- Kartu metrik: **Rise Time, Overshoot, Settling Time, Steady-State Error**.
- Panel **Status Sistem** dan **Alarm**.
- Pratinjau ringkas Live View, Learning Analytic, dan AI Chat tanpa perlu pindah halaman.

### 7.2 Parameter
Tempat mengatur dan menjalankan proses PID di HMI LabVIEW lewat koneksi PLC TCP:
- Isi nilai **Kp, Ki, Kd**, klik **Terapkan** untuk mengirim ke HMI.
- Tombol **Run** / **Stop** mengirim perintah jalankan/hentikan proses ke HMI.
- (Client) Ada badge penghitung "x/3" pemakaian — lihat batasan di [bagian 6](#6-panduan-untuk-client).

### 7.3 Live View
Menampilkan **kamera** dan **layar HMI** secara langsung. Di Server, halaman ini adalah sumber siaran (jika diaktifkan lewat halaman Settings); di Client, halaman ini menampilkan hasil terima siaran tersebut secara real-time.

### 7.4 Learning Analytic
Halaman ini berbeda isi tergantung siapa yang login, tersusun dalam tiga bagian:

**a) Ringkasan performa (khusus mahasiswa di Client)** — gauge lingkaran persentase tugas selesai, ringkasan Total/Selesai/Sisa tugas, dan daftar tugas yang bisa diklik untuk melihat detail (objektif, target metrik jika ada, tombol tandai selesai). Staf melihat tombol tambah/edit/hapus tugas alih-alih tombol tandai selesai. Bagian ini disembunyikan untuk staf yang sedang login di Client maupun untuk siapa pun di Server.

**b) Challenge Learning** — lihat [bagian 7.5](#75-challenge-learning) di bawah; ditampilkan tertanam di dalam halaman ini untuk semua peran.

**c) Penilaian (khusus staf, tampil di Server maupun Client)** — pilih tugas kelompok dari dropdown, lalu kelola rekap nilai seluruh mahasiswa dan aktivitas kelompok mereka.

### 7.5 Challenge Learning
Challenge adalah tugas praktikum terstruktur (judul, sistem terkait, daftar sub-tugas, tenggat, bobot penilaian Dosen/AI/Peer).

**Sebagai mahasiswa:**
- Lihat daftar challenge berstatus **Aktif**.
- Buka detail, kerjakan, lalu **unggah/kirim** hasil (submission) lewat pemilih berkas.
- Pantau status submission dan nilai yang sudah diberikan (badge "Dinilai: skor").

**Sebagai staf (Dosen/Asisten):**
- Lihat semua challenge apa pun statusnya (Aktif/Draft/Ditutup).
- Klik **+ Buat Challenge** untuk membuat baru, atur bobot penilaian (Dosen/AI/Peer, default 50/30/20), simpan sebagai Draft atau publikasikan langsung sebagai Aktif.
- Pilih mahasiswa dari daftar untuk melihat submission-nya, beri **skor** dan **umpan balik**, lalu simpan nilai.

### 7.6 AI Chat
Chat berbasis AI untuk membantu analisis sistem kontrol:
- Ketik pertanyaan di kolom input, atau klik salah satu **saran cepat** di panel kanan.
- Jawaban AI dirender sebagai markdown (termasuk rumus matematika) dan ditampilkan bertahap (streaming); tombol **Stop** membatalkan respons yang sedang berjalan.
- Dropdown **provider + model** di bagian atas untuk memilih AI yang dipakai — hanya menampilkan provider/model yang sudah diaktifkan staf (lihat [5.4](#54-mengonfigurasi-ai)).
- Staf bisa membuka konfigurasi provider langsung dari ikon gerigi di sebelah dropdown ini, baik di Server maupun Client.
- Jika belum ada provider yang diaktifkan/diberi kunci API, chat menampilkan pesan error alih-alih mencoba mengirim.

---

## 8. Fitur Antarmuka Umum

Tersedia di kedua varian, lewat title bar dan menu hamburger (ikon garis tiga):

- **Bahasa** — tombol globe di title bar, pilih **Indonesia** atau **English**; seluruh teks aplikasi berubah seketika.
- **Tema** — tombol matahari/bulan untuk beralih Light/Dark; mengikuti tema Windows secara default.
- **Zoom** — `Ctrl` `+` memperbesar, `Ctrl` `-` memperkecil, `Ctrl` `0` ukuran normal (rentang 50%–250%).
- **Refresh halaman** — `Ctrl` `R` atau menu *Refresh App*, memuat ulang halaman yang sedang aktif.
- **Tour** — menu *Tour* membuka panduan fitur interaktif bergambar (bilingual).
- **Cek pembaruan manual** — menu *Check Update*.
- **Menu View** — sembunyikan/tampilkan item navigasi tertentu sesuai preferensi.
- **Menu Developer** — buka repositori GitHub, buka folder log aplikasi, buka file `settings.json`, laporkan bug, serta toggle **Early Access** (ikut menerima update versi alpha/beta).
- **Tentang** — menu *About* menampilkan nama produk, versi, lisensi, dan hak cipta.
- Klik-kanan pada title bar membuka menu kustomisasi logo/branding.

---

## 9. Pemecahan Masalah

| Gejala | Kemungkinan penyebab | Solusi |
|---|---|---|
| Login mahasiswa ditolak di Server | Akun Mahasiswa tidak diizinkan login langsung di Server | Login lewat Client, atau gunakan akun Dosen/Asisten |
| Client gagal konek ke Server | Alamat/port salah, Server belum di-Start, atau firewall memblokir | Pastikan Share Server sudah **Start** di Server; cek port; jika lewat internet gunakan URL Cloudflare Tunnel |
| Kamera/HMI kosong di Client | Server belum mencentang Share Camera/Share HMI, atau belum Start | Aktifkan centang yang sesuai di kartu Server, lalu Start ulang |
| Parameter PID tidak bisa diterapkan (Client) | Sudah mencapai batas 3 kali penerapan | Hubungi staf/gunakan akun Server untuk penerapan lebih lanjut |
| Chat AI error "tidak ada kunci" | Belum ada provider yang diaktifkan/diberi API key oleh staf | Staf membuka Pengaturan AI (gear icon), aktifkan provider & isi API key |
| Status PLC tetap merah/terputus | Host/port HMI LabVIEW salah, atau HMI belum berjalan | Cek IP mesin HMI dan port di kartu PLC Connection, pastikan HMI sedang berjalan dan dapat dijangkau di jaringan |
| Registrasi mandiri ditolak | Domain email bukan `its.ac.id` (atau subdomain) / `ep.itc.ac.id`, atau kata sandi < 6 karakter | Gunakan email kampus yang valid dan kata sandi lebih panjang |
| Update otomatis gagal | Antivirus memblokir proses penyalinan, atau app terpasang di folder non-standar | Tambahkan folder instalasi ke whitelist antivirus; instal ulang lewat installer resmi |
