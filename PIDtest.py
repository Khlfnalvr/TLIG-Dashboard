import socket
import struct
import time
import json
import os

HOST = "localhost"   # ganti ke IP komputer LabVIEW kalau beda komputer
PORT = 6000
SEND_INTERVAL = 1.0

# =========================================================================
# File "jembatan" dari TLIG Dashboard.
#
#   TLIG Dashboard --pid_bridge.json--> PIDtest.py --TCP--> LabVIEW
#
# Dashboard menulis parameter Kp/Ki/Kd/Setpoint (dan flag run) ke file ini
# setiap kali diubah. Script membacanya ULANG tiap kali mau kirim, jadi
# perubahan di dashboard langsung ikut terkirim tanpa perlu restart.
# File berada di folder yang sama dengan script ini (dashboard menaruhnya
# di sini berdasarkan lokasi PythonScriptPath).
# =========================================================================
BRIDGE_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pid_bridge.json")

# Nilai default dipakai kalau file jembatan belum ada / belum valid, supaya
# script tetap bisa dijalankan sendiri (tanpa dashboard) untuk tes.
DEFAULT_SP = 100
DEFAULT_KC = 25
DEFAULT_KI = 15
DEFAULT_KD = 10


def get_values():
    """Baca SP & PID dari file jembatan yang ditulis dashboard.

    Mengembalikan (sp, kc, ki, kd, run).
      - Kp di dashboard  -> KC di LabVIEW
      - run == False      -> dashboard menekan STOP, script keluar dari loop.

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
        return sp, kc, ki, kd, run
    except (FileNotFoundError, json.JSONDecodeError, ValueError, OSError):
        return DEFAULT_SP, DEFAULT_KC, DEFAULT_KI, DEFAULT_KD, True


def run_client():
    print(f"[CLIENT] Target server: {HOST}:{PORT}")
    print(f"[CLIENT] Baca parameter dari: {BRIDGE_FILE}")

    while True:
        sp, kc, ki, kd, run = get_values()

        if not run:
            print("[STOP] Perintah STOP dari dashboard. Client berhenti.")
            break

        try:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as client:
                client.connect((HOST, PORT))

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

        except (ConnectionRefusedError, OSError) as e:
            print(f"[ERROR] Gagal connect ke {HOST}:{PORT} -> {e}")
            print("        Pastikan VI LabVIEW sudah di-Run dan TCP Listen aktif.")

        time.sleep(SEND_INTERVAL)


if __name__ == "__main__":
    try:
        run_client()
    except KeyboardInterrupt:
        print("\nDihentikan oleh user.")