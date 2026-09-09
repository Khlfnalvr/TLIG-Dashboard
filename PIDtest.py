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

BUKAAN VALVE (opsional, default MATI)
    Kalau pid_bridge.json berisi "send_valve": true, paketnya jadi 5 double
    big-endian = 40 byte  ->  SP, KC, KI, KD, VALVE.  Ini SATU-SATUNYA cara
    bukaan valve dari dashboard sampai ke LabVIEW; port 6001 tidak dipakai
    untuk arah ini (lihat catatan di bawah).

    WAJIB diubah dulu di LabVIEW sebelum flag ini dinyalakan:
      1. TCP Read: 32 byte  ->  40 byte
      2. Unflatten From String: array 4 double  ->  5 double
      3. Kawat elemen ke-5 ke kontrol bukaan valve (dipakai saat Manual)
    Kalau VI masih baca 32 byte sementara script kirim 40 byte, sisa 8 byte
    menumpuk di buffer dan SEMUA pembacaan berikutnya bergeser -- Kp/Ki/Kd
    yang sekarang sudah benar ikut rusak. Karena itu defaultnya mati dan
    dinyalakan lewat "SendValveToLabView": true di settings.json dashboard.

Nilai yang dikirim dibaca ULANG dari pid_bridge.json setiap siklus, jadi
begitu kamu ubah Kp/Ki/Kd/Setpoint (dan bukaan valve) di dashboard, nilainya
langsung ikut terkirim tanpa perlu restart apa pun.

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
# Kp di dashboard = KC di LabVIEW. VALVE hanya ikut kalau send_valve = true.
FIELD_ORDER = ("SP", "KC", "KI", "KD")
FIELD_ORDER_WITH_VALVE = ("SP", "KC", "KI", "KD", "VALVE")

# Nilai default kalau file jembatan belum ada / rusak.
DEFAULT_SP = 40.0
DEFAULT_KC = 25.0
DEFAULT_KI = 30.0
DEFAULT_KD = 45.0
DEFAULT_VALVE = 0.0

# File "jembatan" yang ditulis dashboard. Letaknya SATU FOLDER dengan script
# ini (dashboard menaruhnya di situ berdasarkan PythonScriptPath).
BRIDGE_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           "pid_bridge.json")


def get_values():
    """Baca SP/KC/KI/KD, bukaan valve, flag run, dan alamat LabVIEW.

    Mengembalikan (sp, kc, ki, kd, valve, run, host, port).
      - Kp di dashboard  -> KC di LabVIEW
      - valve            -> bukaan valve (%), None kalau send_valve = false
                            (artinya paket tetap 4 double / 32 byte)
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

        # Bukaan valve hanya ikut dikirim kalau dashboard memang memintanya.
        # Tanpa flag ini panjang paket tidak berubah, jadi VI lama aman.
        valve = None
        if bool(data.get("send_valve", False)):
            valve = float(data.get("valve", DEFAULT_VALVE))

        host = str(data.get("host", HOST_DEFAULT)).strip() or HOST_DEFAULT
        port = int(data.get("port", PORT_DEFAULT))
        if port <= 0 or port > 65535:
            port = PORT_DEFAULT

        return sp, kc, ki, kd, valve, run, host, port

    except (FileNotFoundError, json.JSONDecodeError, ValueError, OSError, TypeError):
        return (DEFAULT_SP, DEFAULT_KC, DEFAULT_KI, DEFAULT_KD, None,
                True, HOST_DEFAULT, PORT_DEFAULT)


def build_packet(sp, kc, ki, kd, valve=None):
    """Susun paket double big-endian sesuai TCP Read di LabVIEW.

    Urutan HARUS sama dengan urutan Unflatten From String di LabVIEW:
      valve None  ->  SP, KC, KI, KD          = 4 double = 32 byte
      valve angka ->  SP, KC, KI, KD, VALVE   = 5 double = 40 byte
    """
    if valve is None:
        packet = struct.pack(">dddd", sp, kc, ki, kd)
        expected = 32
    else:
        packet = struct.pack(">ddddd", sp, kc, ki, kd, valve)
        expected = 40
    assert len(packet) == expected, \
        f"Panjang paket {len(packet)} != {expected} byte!"
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
    last_packet_len = None

    while True:
        sp, kc, ki, kd, valve, run, host, port = get_values()

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
                    sp, kc, ki, kd, valve, run, new_host, new_port = get_values()

                    if not run:
                        print("[STOP] Perintah STOP dari dashboard. Script berhenti.")
                        return

                    # Kalau user ganti IP/port LabVIEW di dashboard, putuskan
                    # koneksi lama supaya loop luar menyambung ke target baru.
                    if (new_host, new_port) != (host, port):
                        print(f"[6000] Target berubah -> {new_host}:{new_port}. "
                              f"Menyambung ulang...")
                        break

                    packet = build_packet(sp, kc, ki, kd, valve)

                    # Panjang paket berubah = VI harus diubah juga. Diingatkan
                    # sekali saat berubah, bukan tiap detik.
                    if len(packet) != last_packet_len:
                        print(f"[6000] Panjang paket sekarang {len(packet)} byte -- "
                              f"TCP Read di LabVIEW harus {len(packet)} byte.")
                        last_packet_len = len(packet)

                    sock.sendall(packet)
                    valve_txt = "" if valve is None else f"  VALVE={valve}"
                    print(f"[6000] TX  SP={sp}  KC={kc}  KI={ki}  KD={kd}{valve_txt}"
                          f"   ({len(packet)} byte)")

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
