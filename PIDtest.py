import socket
import struct
import time
import json
import os
import re

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
# 7 variabel yang tampil di tab "Chart" LabVIEW dan yang HARUS ikut tampil sama
# persis di panel "LabVIEW Data" dashboard. Label di bawah = teks yang muncul di
# dashboard, jadi sengaja dibuat SAMA dengan indikator LabVIEW.
#
# Ada DUA cara LabVIEW boleh mengirim balasan (parse_reply mengenali keduanya):
#
#   1) TEKS BERLABEL  (PALING andal — DISARANKAN)
#      Setiap siklus, "Format Into String" lalu "TCP Write":
#          Flow Tube=%.2f\nFlow Shell=%.2f\nSinyal mA=%.2f\nSinyal %%=%.2f\n
#          PV Shell in=%.2f\nSet Point=%.2f\nPV Shell out=%.2f\n
#      Label ikut terkirim, jadi URUTAN tidak perlu disepakati — dashboard
#      meniru label & nilai apa adanya. Anti salah-petakan.
#
#   2) BINER  (kalau tetap pakai array double seperti packet PID)
#      "Build Array" 7 DBL DALAM URUTAN PERSIS seperti CHART_FIELDS lalu kirim
#      sebagai byte mentah big-endian (Type Cast ke string, ATAU Flatten To
#      String dengan "prepend size?"=FALSE). = 7 x 8 = 56 byte. Kalau size ikut
#      ter-prepend (4 byte I32) pun parser tetap otomatis membuangnya.
#
# Kalau daftar/urutan variabel berubah di LabVIEW, cukup sesuaikan CHART_FIELDS
# ini (dan Build Array di LabVIEW) — nilai biner dipetakan menurut urutan ini.
CHART_FIELDS = [
    "Flow Tube",     # L/min
    "Flow Shell",    # L/min
    "Sinyal mA",     # mA
    "Sinyal %",      # %
    "PV Shell in",   # Celcius
    "Set Point",     # Celcius
    "PV Shell out",  # Celcius
]
CHART_STRUCT_FMT = ">" + "d" * len(CHART_FIELDS)
CHART_RECV_SIZE = struct.calcsize(CHART_STRUCT_FMT)

# ── Diagnosa balasan LabVIEW ───────────────────────────────────────────────
# File log untuk MEMBUKTIKAN format kawat balasan LabVIEW. Setiap balasan yang
# BERUBAH dicatat: hex mentah + SEMUA interpretasi (double/single big-endian &
# teks). Bandingkan angka di log ini dengan angka di panel depan LabVIEW
# (Flow Tube, Flow Shell, Sinyal mA, PV, dst.) untuk tahu urutan/isi asli yang
# dikirim VI — lalu set CHART_FIELDS/urutan di atas agar dashboard = LabVIEW.
# Aman dihapus kapan saja; dibuat ulang saat script jalan lagi.
DIAG_LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "labview_reply.log")

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


# Regex balasan LabVIEW:
#   _KV_RE  : pasangan "Label = angka" (label boleh spasi, %, /, kurung, dsb;
#             non-greedy supaya tidak melahap unit/teks setelah angka).
#   _NUM_RE : angka polos (fallback kalau LabVIEW kirim angka tanpa label).
_KV_RE = re.compile(r"([A-Za-z][\w %/().+\-]*?)\s*=\s*([-+]?\d*\.?\d+(?:[eE][-+]?\d+)?)")
_NUM_RE = re.compile(r"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?")


def _looks_sane(vals) -> bool:
    """True kalau semua nilai wajar untuk data proses (bukan NaN/inf, tidak
    berskala ekstrem). Dipakai untuk menolak pembacaan biner yang salah-align
    (mis. karena ada/tidak-adanya prefiks panjang) lalu mencoba tafsir lain."""
    return all(v == v and abs(v) < 1e12 for v in vals)


def parse_reply(raw: bytes) -> dict:
    """Ubah balasan mentah LabVIEW jadi {label: nilai}. Deteksi otomatis:

      1) TEKS BERLABEL "Label=nilai"  -> diteruskan APA ADANYA (label asli
         LabVIEW dipakai; URUTAN tak penting). Jalur paling andal & anti salah
         petakan — ini yang disarankan dipakai di LabVIEW.
      2) TEKS ANGKA POLOS ("1.36,1.37,…") -> dipetakan berurutan ke CHART_FIELDS.
      3) BINER -> N double (lalu N single) big-endian, dicoba apa adanya LALU
         setelah membuang prefiks panjang I32 4-byte; hasil yang jelas ngawur
         ditolak (_looks_sane) supaya alignmen yang benar yang dipakai.

    Untuk jalur biner urutan DIANGGAP = urutan CHART_FIELDS, jadi Build Array di
    LabVIEW harus dalam urutan itu. Jalur teks berlabel tidak butuh kesepakatan
    urutan sama sekali.
    """
    if not raw:
        return {}

    printable = sum(1 for b in raw if b in (9, 10, 13) or 32 <= b <= 126)
    if printable >= 0.8 * len(raw):                       # kelihatan teks
        text = raw.decode("ascii", "ignore")
        pairs = _KV_RE.findall(text)
        if pairs:                                         # (1) teks berlabel
            # Pertahankan STRING asli (mis. "60.00", "0.01") supaya format angka
            # di dashboard sama persis dengan yang tampil di LabVIEW.
            return {k.strip(): v for k, v in pairs}
        nums = _NUM_RE.findall(text)
        if nums:                                          # (2) angka polos → urut
            return dict(zip(CHART_FIELDS, (float(x) for x in nums)))

    # (3) Biner: utamakan double, coba apa adanya lalu buang prefiks 4-byte.
    n = len(CHART_FIELDS)
    for ch in ("d", "f"):
        size = struct.calcsize(">" + ch)
        for body in (raw, raw[4:]):
            if len(body) >= n * size:
                vals = struct.unpack(">" + ch * n, body[:n * size])
                if _looks_sane(vals):
                    return dict(zip(CHART_FIELDS, vals))
    return {}


def recv_reply(sock, first_timeout=2.0, drain_timeout=0.3, max_bytes=65536):
    """Baca balasan LabVIEW SELENGKAP mungkin.

    TCP adalah stream: satu balasan bisa terpecah jadi beberapa segmen, dan
    satu `recv` bisa mengembalikan hanya sebagian. Kita tunggu segmen pertama
    (timeout `first_timeout`), lalu terus membaca dengan timeout pendek sampai
    LabVIEW berhenti mengirim. Ini penting supaya diagnosa melihat SELURUH
    packet (bukan cuma 32 byte pertama), termasuk field yang selama ini
    terlewat karena hanya 4 double pertama yang dibaca."""
    sock.settimeout(first_timeout)
    try:
        first = sock.recv(4096)
    except socket.timeout:
        return b""
    if not first:
        return b""
    buf = bytearray(first)
    sock.settimeout(drain_timeout)
    while len(buf) < max_bytes:
        try:
            chunk = sock.recv(4096)
        except socket.timeout:
            break
        if not chunk:
            break
        buf.extend(chunk)
    return bytes(buf)


def decode_all_numbers(raw: bytes) -> dict:
    """Semua cara masuk akal membaca `raw`, untuk diagnosa. Tidak memutuskan
    apa-apa — hanya memaparkan agar kita bisa mencocokkan dengan panel LabVIEW."""
    out = {}
    printable = sum(1 for b in raw if b in (9, 10, 13) or 32 <= b <= 126)
    out["printable_ratio"] = round(printable / max(1, len(raw)), 3)
    out["as_text"] = raw.decode("ascii", "ignore")
    n8 = len(raw) // 8
    if n8:
        out[f"doubles_BE(x{n8})"] = [round(x, 6) for x in struct.unpack(f">{n8}d", raw[:n8 * 8])]
        out[f"doubles_LE(x{n8})"] = [round(x, 6) for x in struct.unpack(f"<{n8}d", raw[:n8 * 8])]
    n4 = len(raw) // 4
    if n4:
        out[f"singles_BE(x{n4})"] = [round(x, 6) for x in struct.unpack(f">{n4}f", raw[:n4 * 4])]
        out[f"singles_LE(x{n4})"] = [round(x, 6) for x in struct.unpack(f"<{n4}f", raw[:n4 * 4])]
    return out


_last_logged_hex = None


def log_reply(raw: bytes, forwarded: dict):
    """Catat balasan yang BERUBAH ke DIAG_LOG (hex + semua interpretasi).

    De-dup: kalau bytes-nya sama dgn yang terakhir dicatat, dilewati supaya log
    tidak membengkak saat nilai diam."""
    global _last_logged_hex
    hx = raw.hex(" ")
    if hx == _last_logged_hex:
        return
    _last_logged_hex = hx
    try:
        with open(DIAG_LOG, "a", encoding="utf-8") as f:
            f.write("=" * 72 + "\n")
            f.write(f"waktu     : {time.strftime('%Y-%m-%d %H:%M:%S')}\n")
            f.write(f"panjang   : {len(raw)} byte\n")
            f.write(f"hex       : {hx}\n")
            for k, v in decode_all_numbers(raw).items():
                f.write(f"{k:16s}: {v}\n")
            f.write(f"diteruskan: {forwarded}\n")
    except OSError:
        pass


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

                # Tunggu balasan data dari LabVIEW di koneksi yang SAMA (drain
                # sampai lengkap), lalu deteksi format otomatis (teks / biner).
                raw = recv_reply(client)
                if not raw:
                    print("[WARN] Tidak ada balasan dari LabVIEW (timeout).")
                else:
                    print(f"[RECV-RAW] {len(raw)} byte: {raw!r}")   # untuk verifikasi
                    # Cetak SEMUA double big-endian agar mudah dibandingkan
                    # langsung dengan angka di panel depan LabVIEW.
                    n8 = len(raw) // 8
                    if n8:
                        alld = struct.unpack(f">{n8}d", raw[:n8 * 8])
                        print(f"[RECV-ALL doubles BE x{n8}]: {[round(x, 5) for x in alld]}")
                    values = parse_reply(raw)
                    log_reply(raw, values)          # simpan bukti ke labview_reply.log
                    if values:
                        print(f"[RECV] -> {values}")
                        forward_chart_to_dashboard(values)
                    else:
                        print("[WARN] Balasan tak bisa di-parse (lihat RAW di atas).")

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
