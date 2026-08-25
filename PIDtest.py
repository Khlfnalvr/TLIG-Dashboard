import socket
import struct
import time
import json
import os

# ── Target LabVIEW (default) ───────────────────────────────────────────────
# Dipakai kalau dashboard belum menuliskan host/port ke file jembatan (mis.
# saat script dites sendiri tanpa dashboard).
#
# Kalau LabVIEW ada di komputer LAIN, JANGAN edit di sini — cukup isi kolom
# "Host / Alamat IP (HMI LabVIEW)" di dashboard. Nilainya mengalir lewat
# pid_bridge.json dan langsung dipakai (lihat get_values / run_client), jadi
# satu script yang sama jalan di semua laptop tanpa diubah-ubah.
HOST_DEFAULT = "localhost"
PORT_DEFAULT = 6000
SEND_INTERVAL = 1.0

# ── Tujuan meneruskan data chart ke dashboard (TCP 5005) ───────────────────
# Dashboard men-LISTEN di TCP 5005 (HmiDataService) dan membaca baris teks
# "key=value\n" (BUKAN JSON). PIDtest.py connect sebagai client lalu mengirim
# data chart dalam format itu. Dashboard biasanya di komputer yang sama dengan
# script (dashboard yang me-launch script), jadi localhost.
DASHBOARD_HOST = "localhost"
DASHBOARD_PORT = 5005

# ── Balasan data chart dari LabVIEW ────────────────────────────────────────
# Contoh: 5 double (PV, Flow Tube, Flow Shell, Sinyal mA, Sinyal %) =
# 5 x 8 byte = 40 byte, big-endian ">ddddd" — SAMA POLA dengan packet PID
# (">dddd", big-endian) yang sudah terbukti jalan.
#
# PENTING: urutan & jumlah field ini HARUS PERSIS SAMA dengan yang di-Build
# lalu di-TCP-Write oleh LabVIEW pada koneksi yang SAMA, SETELAH dia selesai
# baca 32 byte PID. Kalau field di LabVIEW beda (mis. cuma 4 nilai, atau
# ditambah SP), sesuaikan CHART_FIELDS dan CHART_STRUCT_FMT di bawah ini.
CHART_FIELDS = ["pv", "flow_tube", "flow_shell", "sinyal_ma", "sinyal_pct"]
CHART_STRUCT_FMT = ">ddddd"
CHART_RECV_SIZE = struct.calcsize(CHART_STRUCT_FMT)

# =========================================================================
# File "jembatan" dari TLIG Dashboard.
#
#   TLIG Dashboard --pid_bridge.json--> PIDtest.py --TCP--> LabVIEW
#   TLIG Dashboard <---TCP 5005-------- PIDtest.py <--TCP-- LabVIEW  (data chart)
#
# Dashboard menulis Kp/Ki/Kd/Setpoint, flag run, DAN alamat host/port LabVIEW
# ke file ini setiap kali diubah. Script membacanya ULANG tiap kali mau kirim,
# jadi perubahan di dashboard (termasuk IP LabVIEW) langsung ikut terkirim
# tanpa perlu restart. File berada di folder yang sama dengan script ini
# (dashboard menaruhnya di sini berdasarkan PythonScriptPath).
#
# Alur balik: setelah kirim PID, script menunggu sebentar balasan data chart
# dari LabVIEW di koneksi TCP yang SAMA, lalu meneruskannya ke dashboard di
# TCP 5005 (format key=value).
# =========================================================================
BRIDGE_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pid_bridge.json")

# Nilai default dipakai kalau file jembatan belum ada / belum valid, supaya
# script tetap bisa dijalankan sendiri (tanpa dashboard) untuk tes.
DEFAULT_SP = 100
DEFAULT_KC = 25
DEFAULT_KI = 15
DEFAULT_KD = 10


def get_values():
    """Baca SP, PID, run, dan alamat LabVIEW dari file jembatan.

    Mengembalikan (sp, kc, ki, kd, run, host, port).
      - Kp di dashboard  -> KC di LabVIEW
      - run == False      -> dashboard menekan STOP, script keluar dari loop.
      - host/port         -> dari kolom "Host / Alamat IP (HMI LabVIEW)" di
                             dashboard. Kalau belum diisi, pakai default.

    Kalau file belum ada / sedang ditulis / rusak, pakai nilai default dan
    tetap jalan (run=True) supaya tidak berhenti karena gangguan sesaat.
    """
    try:
        with open(BRIDGE_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
        sp = float(data.get("sp", DEFAULT_SP))
        kc = float(data.get("kp", DEFAULT_KC))   # Kp dashboard = KC LabVIEW
        ki = float(data.get("ki", DEFAULT_KI))
        kd = float(data.get("kd", DEFAULT_KD))
        run = bool(data.get("run", True))
        host = str(data.get("host", HOST_DEFAULT)).strip() or HOST_DEFAULT
        port = int(data.get("port", PORT_DEFAULT))
        if port <= 0 or port > 65535:
            port = PORT_DEFAULT
        return sp, kc, ki, kd, run, host, port
    except (FileNotFoundError, json.JSONDecodeError, ValueError, OSError, TypeError):
        return DEFAULT_SP, DEFAULT_KC, DEFAULT_KI, DEFAULT_KD, True, HOST_DEFAULT, PORT_DEFAULT


def recv_exact(sock, n):
    """Baca TEPAT n byte dari socket (TCP bisa memecah balasan jadi beberapa
    bagian). Kembalikan bytes sepanjang n, atau None kalau koneksi ditutup
    sebelum lengkap. socket.timeout dibiarkan naik ke pemanggil."""
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:            # koneksi ditutup peer
            return None
        buf.extend(chunk)
    return bytes(buf)


def forward_chart_to_dashboard(values: dict):
    """Kirim data chart yang baru diterima dari LabVIEW ke dashboard (TCP 5005).

    Dashboard (HmiDataService) membaca baris teks "key=value\\n" — jadi kita
    kirim satu baris per field, titik sebagai desimal (repr float Python selalu
    pakai '.'). Kalau dashboard nanti diubah untuk mengharapkan format lain,
    sesuaikan pembentukan `payload` di bawah.
    """
    payload = "".join(f"{k}={v}\n" for k, v in values.items()).encode("utf-8")
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.settimeout(2.0)
            s.connect((DASHBOARD_HOST, DASHBOARD_PORT))
            s.sendall(payload)
        print(f"[FORWARD] -> dashboard {DASHBOARD_HOST}:{DASHBOARD_PORT} : {values}")
    except (ConnectionRefusedError, OSError, socket.timeout) as e:
        print(f"[ERROR] Gagal kirim ke dashboard {DASHBOARD_HOST}:{DASHBOARD_PORT} -> {e}")


def run_client():
    print(f"[CLIENT] Baca parameter dari: {BRIDGE_FILE}")
    print(f"[CLIENT] Target Dashboard: {DASHBOARD_HOST}:{DASHBOARD_PORT}")

    last_target = None
    while True:
        sp, kc, ki, kd, run, host, port = get_values()

        if not run:
            print("[STOP] Perintah STOP dari dashboard. Client berhenti.")
            break

        # Cetak target hanya saat berubah, supaya log tidak berisik.
        if (host, port) != last_target:
            print(f"[CLIENT] Target LabVIEW: {host}:{port}")
            last_target = (host, port)

        try:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as client:
                client.connect((host, port))

                # Urutan HARUS sama dgn urutan Unflatten di LabVIEW:
                # SP -> KC -> KI -> KD
                packet = struct.pack(">dddd", sp, kc, ki, kd)
                assert len(packet) == 32
                client.sendall(packet)

                print(
                    f"[SEND] -> "
                    f"SP={sp} KC={kc} KI={ki} KD={kd} "
                    f"({len(packet)} byte)"
                )

                # Tunggu balasan data chart dari LabVIEW di koneksi yang SAMA.
                # Timeout supaya loop tidak macet kalau LabVIEW belum dimodif
                # untuk kirim balik (lihat catatan CHART_* di atas).
                client.settimeout(2.0)
                try:
                    reply = recv_exact(client, CHART_RECV_SIZE)
                    if reply is None:
                        print("[RECV] LabVIEW menutup koneksi tanpa kirim data chart.")
                    else:
                        parsed = struct.unpack(CHART_STRUCT_FMT, reply)
                        values = dict(zip(CHART_FIELDS, parsed))
                        print(f"[RECV] <- {values}")
                        forward_chart_to_dashboard(values)
                except socket.timeout:
                    print("[WARN] Tidak ada balasan chart dari LabVIEW (timeout 2s).")

        except (ConnectionRefusedError, OSError) as e:
            print(f"[ERROR] Gagal connect ke {host}:{port} -> {e}")
            print("        Pastikan VI LabVIEW sudah di-Run dan TCP Listen aktif,")
            print("        IP/port di dashboard benar, dan firewall mengizinkan port itu.")

        time.sleep(SEND_INTERVAL)


if __name__ == "__main__":
    try:
        run_client()
    except KeyboardInterrupt:
        print("\nDihentikan oleh user.")
