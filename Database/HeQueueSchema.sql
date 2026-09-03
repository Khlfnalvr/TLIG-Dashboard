-- ============================================================================
-- Sistem Antrian HE (Heat Exchanger) — skema database (SQLite)
-- ============================================================================
-- Hanya SATU orang boleh memegang kendali plant HE pada satu waktu. Permintaan
-- yang datang saat plant sedang dipakai masuk ke antrian, dan giliran berikutnya
-- ditentukan oleh PRIORITAS lalu waktu masuk antrian (FIFO di dalam prioritas
-- yang sama):
--
--      1 = Admin        (paling tinggi — boleh override siapa pun)
--      2 = Dosen        (termasuk Asisten — boleh override Mahasiswa)
--      3 = Mahasiswa    (paling rendah — tidak boleh override, hanya mengantre)
--
-- Angka kecil = prioritas lebih tinggi, sehingga urutan antrian cukup
-- "ORDER BY priority ASC, requested_at_utc ASC".
--
-- Semua kolom waktu disimpan sebagai teks UTC ISO-8601 dengan format tetap
-- 'YYYY-MM-DDTHH:MM:SS.SSSZ' (lihat HeSqliteDatabase.ToDbTime). Format tetap ini
-- penting: pengurutan antrian memakai perbandingan teks, jadi campur format
-- (mis. datetime('now') yang memakai spasi) akan mengacaukan urutan.
--
-- Lokasi file DB: %LOCALAPPDATA%\TLIGDashboard\heQueue.db — dipakai oleh build
-- Server saja; Client meminta giliran ke Server lewat jaringan.
-- ============================================================================

PRAGMA foreign_keys = ON;

-- ── Cache identitas pengguna ────────────────────────────────────────────────
-- Salinan ringan dari UserStore (users.json) supaya baris antrian & log tetap
-- terbaca walau akunnya kemudian dihapus atau ganti peran.
CREATE TABLE IF NOT EXISTS he_queue_users (
    user_id         TEXT PRIMARY KEY,                -- == UserAccount.Username
    display_name    TEXT NOT NULL,
    role            TEXT NOT NULL,                   -- peran asli: Admin/Dosen/Asisten/Mahasiswa
    priority        INTEGER NOT NULL CHECK (priority BETWEEN 1 AND 3),
    updated_at_utc  TEXT NOT NULL
);

-- ── Status kendali HE saat ini — SELALU tepat satu baris (id = 1) ───────────
CREATE TABLE IF NOT EXISTS he_control_state (
    id                        INTEGER PRIMARY KEY CHECK (id = 1),
    holder_user_id            TEXT,
    holder_display_name       TEXT,
    holder_priority           INTEGER CHECK (holder_priority BETWEEN 1 AND 3),
    holder_queue_id           INTEGER,               -- baris he_queue_items yang sedang 'Granted'
    holder_request_type       TEXT,
    held_since_utc            TEXT,
    FOREIGN KEY (holder_user_id)  REFERENCES he_queue_users (user_id) ON DELETE SET NULL,
    FOREIGN KEY (holder_queue_id) REFERENCES he_queue_items (queue_id) ON DELETE SET NULL
);

-- ── Antrian + riwayat giliran ───────────────────────────────────────────────
-- Satu baris = satu permintaan giliran. Baris tidak dihapus saat giliran
-- selesai, hanya berubah status, sehingga tabel ini sekaligus jadi riwayat.
CREATE TABLE IF NOT EXISTS he_queue_items (
    queue_id          INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id           TEXT    NOT NULL,
    display_name      TEXT    NOT NULL,
    priority          INTEGER NOT NULL CHECK (priority BETWEEN 1 AND 3),
    request_type      TEXT    NOT NULL CHECK (request_type IN ('Run', 'Stop', 'Reset', 'UpdateParameter')),
    requested_at_utc  TEXT    NOT NULL,              -- masuk antrian (dipakai untuk urutan)
    granted_at_utc    TEXT,                          -- mulai memegang kendali
    ended_at_utc      TEXT,                          -- selesai/dibatalkan/di-override
    status            TEXT    NOT NULL DEFAULT 'Waiting'
                          CHECK (status IN ('Waiting', 'Granted', 'Released', 'Cancelled', 'Overridden', 'Expired')),
    note              TEXT,
    FOREIGN KEY (user_id) REFERENCES he_queue_users (user_id)
);

-- Urutan antrian: prioritas dulu, baru waktu masuk.
CREATE INDEX IF NOT EXISTS idx_queue_waiting
    ON he_queue_items (status, priority, requested_at_utc);

CREATE INDEX IF NOT EXISTS idx_queue_user
    ON he_queue_items (user_id, status);

-- Satu pengguna maksimal punya SATU antrian aktif (mencegah dobel submit) dan
-- SATU giliran aktif.
CREATE UNIQUE INDEX IF NOT EXISTS uq_queue_one_waiting_per_user
    ON he_queue_items (user_id) WHERE status = 'Waiting';

CREATE UNIQUE INDEX IF NOT EXISTS uq_queue_one_granted_per_user
    ON he_queue_items (user_id) WHERE status = 'Granted';

-- Hanya boleh ada satu pemegang kendali di seluruh tabel.
CREATE UNIQUE INDEX IF NOT EXISTS uq_queue_single_holder
    ON he_queue_items (status) WHERE status = 'Granted';

-- ── Audit log ───────────────────────────────────────────────────────────────
-- Riwayat lengkap "siapa melakukan apa, kapan" untuk laporan & penelusuran.
CREATE TABLE IF NOT EXISTS he_queue_log (
    log_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    event_type          TEXT NOT NULL CHECK (event_type IN (
                            'Granted',          -- langsung dapat giliran (plant kosong)
                            'GrantedFromQueue', -- dapat giliran setelah mengantre
                            'GrantedByOverride',-- merebut giliran dari prioritas lebih rendah
                            'Queued',           -- masuk antrian
                            'Released',         -- melepas kendali sendiri
                            'Cancelled',        -- membatalkan antrian
                            'Overridden',       -- kendalinya direbut orang lain
                            'Expired',          -- dilepas paksa karena melewati batas waktu
                            'ForceReleased'     -- dilepas paksa oleh Admin
                        )),
    user_id             TEXT NOT NULL,
    display_name        TEXT,
    priority            INTEGER CHECK (priority BETWEEN 1 AND 3),
    request_type        TEXT,
    queue_id            INTEGER,
    related_user_id     TEXT,                       -- lawan main: yang di-override / yang meng-override
    occurred_at_utc     TEXT NOT NULL,
    note                TEXT
);

CREATE INDEX IF NOT EXISTS idx_log_occurred ON he_queue_log (occurred_at_utc DESC);
CREATE INDEX IF NOT EXISTS idx_log_user     ON he_queue_log (user_id, occurred_at_utc DESC);

-- Baris tunggal status kendali dibuat sekali; INSERT OR IGNORE membuat file DB
-- yang sudah ada aman di-init ulang setiap startup.
INSERT OR IGNORE INTO he_control_state (id) VALUES (1);
