"""
Jembatan PID: TLIG Dashboard -> LabVIEW  (HANYA port 6000)

Script ini yang dijalankan OTOMATIS oleh tombol RUN di TLIG Dashboard.
Berbeda dari "KODE PHYTON FIX BGT.py" (simulator), script ini SENGAJA
tidak menyentuh port 6001 sama sekali, supaya bisa jalan BERSAMAAN dengan
dashboard tanpa rebutan port.

    Dashboard --pid_bridge.json--> script ini --TCP 6000--> LabVIEW
    LabVIEW ----------TCP 6001-------------------------> Dashboard (langsung)

Jadi arah data balik (LabVIEW -> dashboard) TIDAK lewat script ini. LabVIEW
connect langsung ke listener milik dashboard. Itu sebabnya bagian
data_server()/port 6001 dari simulator dibuang di sini.

Port 6000 -- KONTROL (Python = CLIENT, LabVIEW = SERVER)
    LabVIEW pakai TCP Listen di 6000, script ini yang menyambung masuk.
    Mengirim 4 double big-endian = 32 byte, cocok dengan TCP Read 32 byte
    di diagram LabVIEW  ->  SP, KC, KI, KD

Nilai yang dikirim dibaca ULANG dari pid_bridge.json setiap siklus, jadi
begitu kamu ubah Kp/Ki/Kd/Setpoint di dashboard, nilainya langsung ikut
terkirim tanpa perlu restart apa pun.

Bisa juga dijalankan manual untuk tes (tanpa dashboard): kalau
pid_bridge.json belum ada, script pakai nilai default di bawah.

Hentikan dengan Ctrl+C, atau lewat tombol STOP di dashboard (dashboard
menulis run=false ke pid_bridge.json dan script keluar sendiri).
"""

import json
import os
import socket
import struct
import time

# ---------------------------------------------------------------- konfigurasi

# Dipakai HANYA kalau pid_bridge.json belum ada / belum valid (mis. saat
# script dites sendiri tanpa dashboard). Kalau LabVIEW ada di komputer lain,
# JANGAN edit di sini -- cukup isi kolom "Host / Alamat IP (HMI LabVIEW)"
# di dashboard; nilainya mengalir lewat pid_bridge.json.
HOST_DEFAULT = "127.0.0.1"
PORT_DEFAULT = 6000

SEND_INTERVAL = 1.0          # detik, jeda antar pengiriman parameter PID
RECONNECT_DELAY = 2.0        # detik, jeda sebelum mencoba menyambung lagi

# Urutan nilai yang DIKIRIM ke LabVIEW. Sudah terbukti benar lewat Front Panel.
# Kp di dashboard = KC di LabVIEW.
FIELD_ORDER = ("SP", "KC", "KI", "KD")

# Nilai default kalau file jembatan belum ada / rusak.
DEFAULT_SP = 40.0
DEFAULT_KC = 25.0
DEFAULT_KI = 30.0
DEFAULT_KD = 45.0

# File "jembatan" yang ditulis dashboard. Letaknya SATU FOLDER dengan script
# ini (dashboard menaruhnya di situ berdasarkan PythonScriptPath).
BRIDGE_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           "pid_bridge.json")


def get_values():
    """Baca SP/KC/KI/KD, flag run, dan alamat LabVIEW dari file jembatan.

    Mengembalikan (sp, kc, ki, kd, run, host, port).
      - Kp di dashboard  -> KC di LabVIEW
      - run == False     -> dashboard menekan STOP, script keluar dari loop.

    Kalau file belum ada / sedang ditulis / rusak, pakai nilai default dan
    tetap jalan (run=True) supaya tidak berhenti karena gangguan sesaat.
    """
    try:
        with open(BRIDGE_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)

        sp = float(data.get("sp", DEFAULT_SP))
        kc = float(data.get("kp", DEFAULT_KC))    # Kp dashboard = KC LabVIEW
        ki = float(data.get("ki", DEFAULT_KI))
        kd = float(data.get("kd", DEFAULT_KD))
        run = bool(data.get("run", True))

        host = str(data.get("host", HOST_DEFAULT)).strip() or HOST_DEFAULT
        port = int(data.get("port", PORT_DEFAULT))
        if port <= 0 or port > 65535:
            port = PORT_DEFAULT

        return sp, kc, ki, kd, run, host, port

    except (FileNotFoundError, json.JSONDecodeError, ValueError, OSError, TypeError):
        return (DEFAULT_SP, DEFAULT_KC, DEFAULT_KI, DEFAULT_KD,
                True, HOST_DEFAULT, PORT_DEFAULT)


def build_packet(sp, kc, ki, kd):
    """Susun 4 double big-endian = 32 byte, sesuai TCP Read 32 byte di LabVIEW.

    Urutan HARUS sama dengan urutan Unflatten From String di LabVIEW:
    SP -> KC -> KI -> KD.
    """
    packet = struct.pack(">dddd", sp, kc, ki, kd)
    assert len(packet) == 32, f"Panjang paket {len(packet)} != 32 byte!"
    return packet


def run_client():
    print("=" * 66)
    print(" Jembatan PID  --  HANYA port 6000 (kontrol -> LabVIEW)")
    print(f"   Baca parameter dari : {BRIDGE_FILE}")
    print("   Port 6001 TIDAK dipakai script ini -- LabVIEW kirim data")
    print("   langsung ke dashboard, jadi tidak ada rebutan port.")
    print("=" * 66)
    print(" Tekan Ctrl+C untuk berhenti (atau tombol STOP di dashboard).\n")

    last_target = None

    while True:
        sp, kc, ki, kd, run, host, port = get_values()

        if not run:
            print("[STOP] Perintah STOP dari dashboard. Script berhenti.")
            return

        # Cetak target hanya saat berubah, supaya log tidak berisik.
        if (host, port) != last_target:
            print(f"[6000] Target LabVIEW: {host}:{port}")
            last_target = (host, port)

        try:
            with socket.create_connection((host, port), timeout=5) as sock:
                print(f"[6000] Tersambung ke LabVIEW di {host}:{port}")

                # Tetap di dalam satu koneksi selama LabVIEW masih hidup --
                # buka-tutup socket tiap detik bikin LabVIEW sering re-accept.
                while True:
                    sp, kc, ki, kd, run, new_host, new_port = get_values()

                    if not run:
                        print("[STOP] Perintah STOP dari dashboard. Script berhenti.")
                        return

                    # Kalau user ganti IP/port LabVIEW di dashboard, putuskan
                    # koneksi lama supaya loop luar menyambung ke target baru.
                    if (new_host, new_port) != (host, port):
                        print(f"[6000] Target berubah -> {new_host}:{new_port}. "
                              f"Menyambung ulang...")
                        break

                    sock.sendall(build_packet(sp, kc, ki, kd))
                    print(f"[6000] TX  SP={sp}  KC={kc}  KI={ki}  KD={kd}   (32 byte)")

                    time.sleep(SEND_INTERVAL)

        except (ConnectionRefusedError, socket.timeout, TimeoutError):
            print(f"[6000] LabVIEW belum siap menerima koneksi di {host}:{port}. "
                  f"Coba lagi {RECONNECT_DELAY:.0f} detik lagi...")
            print("       Pastikan VI LabVIEW sudah di-Run dan TCP Listen aktif,")
            print("       IP/port di dashboard benar, dan firewall mengizinkan port itu.")
            time.sleep(RECONNECT_DELAY)

        except OSError as exc:
            print(f"[6000] Koneksi terputus ({exc}). Menyambung ulang...")
            time.sleep(RECONNECT_DELAY)


if __name__ == "__main__":
    try:
        run_client()
    except KeyboardInterrupt:
        print("\nDihentikan oleh user.")
