-- ============================================================================
-- Cache Hasil Parameter HE — skema database (SQLite)
-- ============================================================================
-- Menjalankan satu kombinasi parameter ke plant HE fisik makan waktu (dan
-- antrian). Karena itu setiap run yang selesai disimpan lengkap di sini:
-- parameter masukannya, metrik ringkasannya, dan kurva responsnya. Kalau ada
-- orang lain yang ingin mencoba kombinasi parameter yang SAMA, hasilnya tinggal
-- diambil dari tabel ini — plant tidak perlu dijalankan ulang.
--
-- Kunci pencocokan adalah param_key: SP/Kc/Ti/Td/Pump dibulatkan ke 3 desimal
-- lalu digabung jadi satu teks, mis. "60.000|2.500|10.000|0.500|75.000".
-- Pembulatan ini menghindari selisih floating point sepersejuta dianggap
-- kombinasi baru (lihat HeParameterInput.ParamKey untuk mengubah toleransinya).
--
-- Kolom waktu memakai teks UTC ISO-8601 'YYYY-MM-DDTHH:MM:SS.SSSZ', sama seperti
-- database antrian (lihat HeSqliteDatabase.ToDbTime).
--
-- Lokasi file DB: %LOCALAPPDATA%\TLIGDashboard\heParamCache.db — di sisi Server,
-- karena hanya Server yang benar-benar terhubung ke plant/LabVIEW.
-- ============================================================================

PRAGMA foreign_keys = ON;

-- ── Satu baris = satu run (satu kombinasi parameter yang pernah dijalankan) ─
CREATE TABLE IF NOT EXISTS he_parameter_runs (
    run_id                  INTEGER PRIMARY KEY AUTOINCREMENT,

    -- Parameter masukan yang dikirim ke LabVIEW.
    sp                      REAL NOT NULL,           -- set point (°C)
    kc                      REAL NOT NULL,           -- gain proporsional
    ti                      REAL NOT NULL,           -- waktu integral
    td                      REAL NOT NULL,           -- waktu derivatif
    pump                    REAL NOT NULL,           -- bukaan valve/pompa (%)

    param_key               TEXT NOT NULL,           -- kunci cocok cepat (lihat header)

    source                  TEXT NOT NULL DEFAULT 'Plant'
                                CHECK (source IN ('Plant', 'Simulation')),
    status                  TEXT NOT NULL DEFAULT 'Completed'
                                CHECK (status IN ('Completed', 'Failed', 'Aborted')),

    requested_by_user_id    TEXT,
    requested_by_name       TEXT,
    started_at_utc          TEXT NOT NULL,
    finished_at_utc         TEXT,
    duration_seconds        REAL,

    -- Statistik pemakaian ulang: tiap kali hasil ini dipakai lagi oleh orang
    -- lain (bukan dijalankan ulang), reuse_count naik satu.
    reuse_count             INTEGER NOT NULL DEFAULT 0,
    last_reused_at_utc      TEXT,

    note                    TEXT
);

-- Index utama cache lookup: "kombinasi ini sudah pernah dijalankan belum?"
-- Kolom status ikut supaya pencarian run 'Completed' terbaru selesai di index.
CREATE INDEX IF NOT EXISTS idx_runs_param_key
    ON he_parameter_runs (param_key, status, started_at_utc DESC);

CREATE INDEX IF NOT EXISTS idx_runs_started
    ON he_parameter_runs (started_at_utc DESC);

-- ── Metrik ringkasan — 1:1 dengan run, dihitung sekali saat run selesai ─────
CREATE TABLE IF NOT EXISTS he_parameter_run_metrics (
    run_id                  INTEGER PRIMARY KEY,

    -- Nilai akhir (steady state) tiap kanal.
    final_pv_shell_out      REAL,
    final_pv_shell_in       REAL,
    final_flow_tube         REAL,
    final_flow_shell        REAL,
    final_signal_percent    REAL,

    -- Kualitas respons.
    steady_state_error      REAL,
    rise_time_seconds       REAL,
    settling_time_seconds   REAL,
    overshoot_percent       REAL,
    peak_value              REAL,

    ise                     REAL,   -- Integral Square Error
    iae                     REAL,   -- Integral Absolute Error
    itae                    REAL,   -- Integral Time-weighted Absolute Error

    FOREIGN KEY (run_id) REFERENCES he_parameter_runs (run_id) ON DELETE CASCADE
);

-- ── Kurva respons — 1:N dengan run ──────────────────────────────────────────
-- Tujuh kanal ini persis mengikuti CHART_FIELDS di PIDtest.py (urutan variabel
-- yang dikirim LabVIEW). Kalau daftar di LabVIEW berubah, ubah juga di sini.
CREATE TABLE IF NOT EXISTS he_parameter_run_samples (
    run_id              INTEGER NOT NULL,
    t_seconds           REAL    NOT NULL,            -- detik sejak run dimulai (t = 0)

    flow_tube           REAL,                        -- L/min
    flow_shell          REAL,                        -- L/min
    signal_ma           REAL,                        -- mA
    signal_percent      REAL,                        -- %
    pv_shell_in         REAL,                        -- °C
    set_point           REAL,                        -- °C
    pv_shell_out        REAL,                        -- °C

    PRIMARY KEY (run_id, t_seconds),
    FOREIGN KEY (run_id) REFERENCES he_parameter_runs (run_id) ON DELETE CASCADE
) WITHOUT ROWID;
