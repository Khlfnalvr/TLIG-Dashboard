"""
Pengumpul diagnosa jembatan TLIG Dashboard -> LabVIEW.

Jalankan di PC SERVER (yang tersambung ke LabVIEW), lalu salin seluruh
keluarannya ke chat. Dari situ ketahuan di titik mana rantainya putus:

    Dashboard -> pid_bridge.json -> PIDtest.py -> TCP 6000 -> LabVIEW

    python diagnosa_labview.py

AMAN: hanya MEMBACA. Tidak mengubah setelan, tidak menyentuh rig, tidak
menyambung ke LabVIEW (VI hanya menerima satu koneksi per Run, jadi
menyambung sembarangan justru merebut jatah PIDtest.py).

    python diagnosa_labview.py --probe

Menambahkan uji sambung ke port 6000. Pakai HANYA kalau dashboard sedang
STOP, dan JALANKAN ULANG VI sesudahnya karena jatah koneksinya terpakai.

PRIVASI: settings.json memuat API key dan token. Script ini hanya mencetak
enam kunci yang berkaitan dengan LabVIEW, tidak pernah seluruh file.
"""

import argparse
import json
import os
import platform
import socket
import subprocess
import sys

# Hanya kunci ini yang boleh tercetak. Sisanya (AiApiKey, ShareToken,
# ServerToken, AiProviderConfigs, ...) sengaja tidak pernah disentuh.
KUNCI_AMAN = [
    "PlcTcpHost", "PlcTcpPort", "HmiDataPort",
    "PythonExe", "PythonScriptPath", "SendValveToLabView",
]


def judul(t):
    print(f"\n{'=' * 66}\n {t}\n{'=' * 66}")


def baris(k, v):
    print(f"  {k:<26}: {v}")


def path_settings():
    base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~/.local/share")
    return os.path.join(base, "TLIGDashboard", "settings.json")


def cek_settings():
    judul("1. SETELAN DASHBOARD")
    p = path_settings()
    baris("settings.json", p)
    if not os.path.isfile(p):
        print("  -> TIDAK ADA. Dashboard belum pernah menyimpan setelan di PC ini.")
        return {}
    try:
        data = json.load(open(p, encoding="utf-8"))
    except Exception as exc:
        print(f"  -> GAGAL dibaca: {exc}")
        return {}
    for k in KUNCI_AMAN:
        baris(k, data.get(k, "(belum ada -> pakai default)"))
    if data.get("SendValveToLabView") is False:
        print("\n  CATATAN: SendValveToLabView = False, jadi bukaan valve memang")
        print("  TIDAK dikirim. Centang 'Kirim Bukaan Valve ke LabVIEW' di kartu")
        print("  PLC Connection kalau VI sudah siap membaca 40 byte.")
    return data


def cari_script(cfg):
    judul("2. SCRIPT PIDtest.py YANG DIPAKAI")
    kandidat = []
    dikonfigurasi = (cfg.get("PythonScriptPath") or r"D:\PIDtest.py").strip()
    kandidat.append(("dikonfigurasi", dikonfigurasi))
    kandidat.append(("di sebelah script ini",
                     os.path.join(os.path.dirname(os.path.abspath(__file__)), "PIDtest.py")))

    dipakai = None
    for label, p in kandidat:
        ada = os.path.isfile(p)
        baris(label, f"{p}  [{'ADA' if ada else 'tidak ada'}]")
        if ada and dipakai is None:
            dipakai = p

    if not dipakai:
        print("\n  -> Tidak ada PIDtest.py yang ditemukan. RUN tidak akan jalan.")
        return None

    print()
    baris("YANG MENANG", dipakai)
    try:
        isi = open(dipakai, encoding="utf-8", errors="replace").read()
    except Exception as exc:
        print(f"  -> gagal dibaca: {exc}")
        return dipakai

    baru = "send_valve" in isi
    baris("dukung bukaan valve", "YA (versi baru)" if baru else "TIDAK (versi lama)")
    if not baru:
        print("\n  -> INI PENYEBAB PALING SERING. File ini versi lama dan tidak")
        print("     tahu soal bukaan valve sama sekali. Salin PIDtest.py yang baru")
        print(f"     dari repo ke: {dipakai}")
    return dipakai


def cek_bridge(script):
    judul("3. FILE JEMBATAN pid_bridge.json")
    folder = os.path.dirname(script) if script else "."
    p = os.path.join(folder, "pid_bridge.json")
    baris("lokasi", p)
    if not os.path.isfile(p):
        print("  -> TIDAK ADA. Dashboard belum pernah menulisnya (belum pernah RUN,")
        print("     atau PythonScriptPath menunjuk folder lain).")
        return
    try:
        data = json.load(open(p, encoding="utf-8"))
    except Exception as exc:
        print(f"  -> gagal dibaca: {exc}")
        return
    for k in ("sp", "kp", "ki", "kd", "valve", "send_valve", "run", "host", "port"):
        baris(k, data.get(k, "(tidak ada)"))
    if "valve" not in data:
        print("\n  -> Tidak ada field 'valve'. Dashboard yang menulis file ini masih")
        print("     versi lama. Ambil branch claude/amazing-davinci-ejvabl lalu build ulang.")


def cek_log(script):
    judul("4. LOG BACK-END pid_bridge.log")
    folder = os.path.dirname(script) if script else "."
    p = os.path.join(folder, "pid_bridge.log")
    baris("lokasi", p)
    if not os.path.isfile(p):
        print("  -> TIDAK ADA. Belum pernah RUN sejak build yang punya log ini.")
        return
    try:
        garis = open(p, encoding="utf-8", errors="replace").read().splitlines()
    except Exception as exc:
        print(f"  -> gagal dibaca: {exc}")
        return
    print(f"  ({len(garis)} baris, 25 terakhir)\n")
    for g in garis[-25:]:
        print("   | " + g)


def cek_port(cfg, probe):
    judul("5. PORT")
    host = (cfg.get("PlcTcpHost") or "127.0.0.1").strip()
    port = cfg.get("PlcTcpPort") or 6000
    hmi = cfg.get("HmiDataPort") or 6001
    baris("LabVIEW (kontrol)", f"{host}:{port}")
    baris("dashboard (telemetri)", f"0.0.0.0:{hmi}")

    if not probe:
        print("\n  Uji sambung dilewati (aman). Tambahkan --probe kalau mau menguji,")
        print("  tapi STOP dashboard dulu dan Run ulang VI sesudahnya.")
        return
    print("\n  Menguji sambung ke LabVIEW ...")
    try:
        with socket.create_connection((host, port), timeout=5):
            print(f"  -> BERHASIL. Ada yang mendengarkan di {host}:{port}.")
            print("     JALANKAN ULANG VI sekarang: jatah koneksinya sudah terpakai.")
    except Exception as exc:
        print(f"  -> GAGAL: {exc}")
        print("     VI belum di-Run, TCP Listen belum aktif, jatah koneksinya sudah")
        print("     dipakai PIDtest.py, atau firewall memblokir.")


def cek_proses():
    judul("6. PROSES PYTHON YANG SEDANG JALAN")
    if platform.system() != "Windows":
        print("  (lewat: bukan Windows)")
        return
    try:
        out = subprocess.run(["tasklist", "/fi", "imagename eq python.exe"],
                             capture_output=True, text=True, timeout=15).stdout
        out += subprocess.run(["tasklist", "/fi", "imagename eq pythonw.exe"],
                              capture_output=True, text=True, timeout=15).stdout
    except Exception as exc:
        print(f"  gagal: {exc}")
        return
    hidup = [g for g in out.splitlines() if ".exe" in g.lower()]
    if hidup:
        for g in hidup:
            print("   | " + g)
    else:
        print("  Tidak ada python.exe berjalan -> PIDtest.py TIDAK aktif.")
        print("  Tekan RUN di dashboard supaya jembatannya hidup.")


def main():
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--probe", action="store_true",
                    help="uji sambung ke port LabVIEW (merebut jatah koneksi VI)")
    a = ap.parse_args()

    judul("DIAGNOSA JEMBATAN TLIG DASHBOARD -> LABVIEW")
    baris("tanggal", __import__("datetime").datetime.now().strftime("%Y-%m-%d %H:%M:%S"))
    baris("OS", f"{platform.system()} {platform.release()}")
    baris("Python", sys.version.split()[0])

    cfg = cek_settings()
    script = cari_script(cfg)
    cek_bridge(script)
    cek_log(script)
    cek_port(cfg, a.probe)
    cek_proses()

    judul("SELESAI - salin SELURUH keluaran di atas ke chat")
    return 0


if __name__ == "__main__":
    sys.exit(main())
