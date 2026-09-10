"""
Alat cek: apakah VI LabVIEW sudah benar membaca 5 nilai (termasuk bukaan valve)?

Dipakai SETELAH mengubah block diagram, SEBELUM memakai dashboard di rig.
Tujuannya supaya kalau ada yang salah kawat, ketahuan dari angka di Front
Panel -- bukan dari valve yang tiba-tiba bergerak.

    python cek_valve_labview.py            -> cek slot (angka penanda)
    python cek_valve_labview.py --sapu     -> sapu bukaan valve 0..100
    python cek_valve_labview.py --lama     -> kirim 32 byte (VI versi lama)

Pilihan lain:
    --host 192.168.1.10   alamat LabVIEW (default 127.0.0.1)
    --port 6000           port TCP Listen di VI (default 6000)

PENTING sebelum menjalankan
---------------------------
1. TEKAN STOP di dashboard dulu (atau tutup PIDtest.py). VI melakukan
   TCP Listen di LUAR while loop, jadi ia hanya menerima SATU koneksi
   selama satu kali Run. Selama PIDtest.py masih nyambung, script ini
   tidak akan bisa masuk.
2. Jalankan ulang VI-nya supaya listener-nya segar.
3. Pastikan rig dalam keadaan aman: VI akan MENERAPKAN angka yang dikirim
   script ini, termasuk ke keluaran analog Dev1/ao0. Kalau ragu, lepas
   dulu jalur aktuatornya dan cukup lihat angka di Front Panel.
"""

import argparse
import socket
import struct
import sys
import time

# Angka penanda: tiap slot diberi angka kembar yang khas, jadi kalau ada
# pergeseran satu slot pun langsung kelihatan di Front Panel.
PENANDA = {"SP": 11.0, "KC": 22.0, "KI": 33.0, "KD": 44.0, "VALVE": 55.0}

# Sapuan bukaan valve: gain & setpoint DIBUAT TETAP di angka penanda supaya
# yang bergerak di Front Panel jelas cuma satu, yaitu bukaan valve.
SAPUAN = [0.0, 25.0, 50.0, 75.0, 100.0, 0.0]


def kirim(sock, sp, kc, ki, kd, valve):
    """Paket double big-endian, urutannya sama dengan PIDtest.py."""
    if valve is None:
        return sock.sendall(struct.pack(">dddd", sp, kc, ki, kd)) or 32
    sock.sendall(struct.pack(">ddddd", sp, kc, ki, kd, valve))
    return 40


def garis(judul=""):
    print("-" * 68 if not judul else f"--- {judul} " + "-" * (63 - len(judul)))


def mode_slot(sock, pakai_valve):
    p = PENANDA
    n = kirim(sock, p["SP"], p["KC"], p["KI"], p["KD"],
              p["VALVE"] if pakai_valve else None)
    garis("CEK SLOT")
    print(f"Terkirim {n} byte. Front Panel LabVIEW HARUS menunjukkan:\n")
    print(f"   Set Point / SP          =  {p['SP']:.0f}")
    print(f"   Kc                      =  {p['KC']:.0f}")
    print(f"   Ti (min) / KI           =  {p['KI']:.0f}")
    print(f"   Td (min) / KD           =  {p['KD']:.0f}")
    if pakai_valve:
        print(f"   % Bukaan Valve          =  {p['VALVE']:.0f}   <-- yang baru")
    print()
    garis()
    print("Kalau SEMUA angka cocok  -> VI sudah benar, silakan pakai dashboard.")
    print("Kalau angkanya BERGESER  -> 'TCP Read' belum diubah dari 32 ke 40 byte,")
    print("   misalnya Set Point terbaca 55 (itu angka valve yang salah kamar).")
    print("Kalau valve TIDAK BERUBAH tapi yang lain benar -> paketnya sudah benar,")
    print("   indeks ke-5 belum dikawat ke kontrol bukaan valve.")
    # Ditahan supaya nilainya tetap terpampang selama dilihat.
    print("\nMenahan nilai. Tekan Ctrl+C kalau sudah selesai melihat.")
    while True:
        kirim(sock, p["SP"], p["KC"], p["KI"], p["KD"],
              p["VALVE"] if pakai_valve else None)
        time.sleep(1.0)


def mode_sapu(sock, pakai_valve):
    p = PENANDA
    garis("SAPU BUKAAN VALVE")
    if not pakai_valve:
        print("Mode --lama tidak mengirim valve sama sekali, jadi tidak ada")
        print("yang bisa disapu. Jalankan tanpa --lama.")
        return
    print("Setpoint & gain ditahan tetap; HANYA bukaan valve yang bergerak.")
    print("Perhatikan angka '% Bukaan Valve' di Front Panel mengikuti.\n")
    for v in SAPUAN:
        for _ in range(3):          # 3 detik per langkah, VI baca 1 Hz
            kirim(sock, p["SP"], p["KC"], p["KI"], p["KD"], v)
            time.sleep(1.0)
        print(f"   terkirim  % Bukaan Valve = {v:6.1f}   "
              f"(Front Panel harus menunjukkan {v:.0f})")
    print()
    garis()
    print("Kalau angkanya ikut naik-turun -> bukaan valve SUDAH tersambung.")
    print("Kalau diam di satu angka       -> indeks ke-5 belum dikawat ke kontrolnya.")


def main():
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=6000)
    ap.add_argument("--sapu", action="store_true",
                    help="sapu bukaan valve 0..100, gain & setpoint ditahan")
    ap.add_argument("--lama", action="store_true",
                    help="kirim 32 byte / 4 nilai, seperti VI sebelum diubah")
    a = ap.parse_args()

    pakai_valve = not a.lama
    print("=" * 68)
    print(" Cek jembatan TLIG Dashboard -> LabVIEW")
    print(f"   Target        : {a.host}:{a.port}")
    print(f"   Panjang paket : {32 if a.lama else 40} byte "
          f"({4 if a.lama else 5} double)")
    print("=" * 68)
    print(" Ingat: STOP dulu dashboard/PIDtest.py, lalu Run ulang VI-nya.")
    print(" VI hanya menerima satu koneksi per Run.\n")

    try:
        with socket.create_connection((a.host, a.port), timeout=5) as sock:
            print(f"Tersambung ke {a.host}:{a.port}\n")
            if a.sapu:
                mode_sapu(sock, pakai_valve)
            else:
                mode_slot(sock, pakai_valve)
    except (ConnectionRefusedError, socket.timeout, TimeoutError):
        print(f"GAGAL menyambung ke {a.host}:{a.port}.")
        print("  - VI sudah di-Run dan TCP Listen aktif?")
        print("  - PIDtest.py masih nyambung? Tekan STOP di dashboard dulu,")
        print("    lalu Run ulang VI-nya (listener-nya sekali pakai).")
        print("  - IP/port benar? Firewall mengizinkan?")
        return 1
    except KeyboardInterrupt:
        print("\nSelesai.")
    except OSError as exc:
        print(f"\nKoneksi terputus: {exc}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
