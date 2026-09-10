"""
Cek baris kontrol dashboard -> LabVIEW, TANPA menyentuh LabVIEW sama sekali.

Script ini berpura-pura jadi VI: menyambung ke listener dashboard di
HmiDataPort (default 6001), persis seperti "TCP Open Connection" di block
diagram, lalu mencetak setiap baris yang dikirim dashboard.

    python cek_baris_kontrol.py

Gunanya: memastikan bukaan valve benar-benar keluar dari dashboard, dan
melihat ada di kolom ke berapa -- sebelum block diagram diubah. Kalau di
sini angkanya sudah bergerak, sisi dashboard beres dan sisanya murni
urusan VI.

Cara pakai:
  1. Jalankan TLIG Dashboard (Server) sampai terbuka.
  2. Jalankan script ini.
  3. Ubah kotak "Bukaan Valve (%)" di dashboard, tekan Enter.
  4. Lihat baris baru muncul di sini.

AMAN: hanya membaca. Tidak mengirim apa pun ke dashboard, tidak menyentuh
rig, dan tidak mengganggu VI (dashboard mengirim ke SEMUA klien yang
tersambung, jadi VI tetap menerima salinannya sendiri).

Pilihan:
    --host 127.0.0.1   alamat dashboard (default 127.0.0.1)
    --port 6001        HmiDataPort (default 6001)
"""

import argparse
import socket
import sys

# Urutan kolom yang ditulis SendControlLine() di dashboard.
KOLOM = ["Kp", "Ki", "Kd", "Setpoint", "Bukaan Valve", "Run"]
KOLOM_VALVE = 4          # indeks 0-based; kolom ke-5 di LabVIEW


def tampilkan(baris, no):
    bagian = baris.split(",")
    print(f"\n[{no}] baris diterima: {baris}")
    if len(bagian) < len(KOLOM):
        print(f"    (hanya {len(bagian)} kolom -- ini mungkin bukan baris kontrol)")
        return
    for i, nama in enumerate(KOLOM):
        tanda = "   <-- INI bukaan valve (kolom ke-5)" if i == KOLOM_VALVE else ""
        print(f"    kolom {i + 1}  {nama:<14} = {bagian[i]}{tanda}")


def main():
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=6001)
    a = ap.parse_args()

    print("=" * 66)
    print(" Menyadap baris kontrol dashboard (berpura-pura jadi VI)")
    print(f"   Menyambung ke : {a.host}:{a.port}")
    print("=" * 66)

    try:
        sock = socket.create_connection((a.host, a.port), timeout=5)
    except (ConnectionRefusedError, socket.timeout, TimeoutError, OSError) as exc:
        print(f"\nGAGAL menyambung: {exc}")
        print("  - TLIG Dashboard (Server) sudah dijalankan?")
        print("  - Port-nya benar? Lihat 'TCP Port (data <- LabVIEW)' di kartu")
        print("    PLC Connection.")
        return 1

    print("\nTersambung. Sekarang UBAH kotak 'Bukaan Valve (%)' di dashboard,")
    print("lalu tekan Enter di kotak itu. Baris baru akan muncul di bawah.")
    print("Tekan Ctrl+C untuk berhenti.\n")
    print("-" * 66)

    no = 0
    buf = b""
    try:
        with sock:
            while True:
                potongan = sock.recv(4096)
                if not potongan:
                    print("\nKoneksi ditutup dashboard.")
                    break
                buf += potongan
                # Dashboard mengakhiri baris kontrol dengan CRLF; frame lain
                # (kalau ada) diakhiri LF saja. Tangani keduanya.
                while b"\n" in buf:
                    mentah, buf = buf.split(b"\n", 1)
                    baris = mentah.decode("utf-8", "replace").strip()
                    if not baris:
                        continue
                    no += 1
                    tampilkan(baris, no)
    except KeyboardInterrupt:
        print("\n\nSelesai.")
        if no == 0:
            print("Tidak ada satu baris pun diterima. Kemungkinan:")
            print("  - kotak Bukaan Valve belum diubah (baris hanya dikirim saat berubah)")
            print("  - nilainya diketik tapi belum ditekan Enter / belum pindah fokus")
    except OSError as exc:
        print(f"\nKoneksi terputus: {exc}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
