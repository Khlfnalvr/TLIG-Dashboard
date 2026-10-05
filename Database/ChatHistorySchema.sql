-- ============================================================================
-- Riwayat chat AI per akun (long-term memory lintas device)
-- ============================================================================
-- Satu baris = satu sesi chat milik satu akun. Server adalah sumber kebenaran:
-- Client mendorong (POST /chat/sync) tiap ada perubahan dan menarik
-- (GET /chat/sessions) saat login, sehingga akun yang sama melihat riwayat +
-- ringkasan yang sama di laptop mana pun. File JSON lokal di tiap device hanya
-- cache/offline fallback.
--
-- Semua kolom waktu memakai format teks UTC tetap 'YYYY-MM-DDTHH:MM:SS.SSSZ'
-- (lihat HeSqliteDatabase.ToDbTime) agar perbandingan teks = urutan waktu.
-- Lokasi file DB: %LOCALAPPDATA%\TLIGDashboard\chatHistory.db — dipakai oleh
-- build Server saja; Client mengaksesnya lewat endpoint HTTP.
-- ============================================================================

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS chat_sessions (
    username        TEXT NOT NULL,                 -- == UserAccount.Username (huruf kecil)
    session_id      TEXT NOT NULL,                 -- == ChatSession.Id
    title           TEXT NOT NULL DEFAULT '',
    summary         TEXT NOT NULL DEFAULT '',      -- rolling summary long-term memory
    messages_json   TEXT NOT NULL DEFAULT '[]',    -- [{"role":..,"content":..}, ...]
    updated_at_utc  TEXT NOT NULL,
    PRIMARY KEY (username, session_id)
);

CREATE INDEX IF NOT EXISTS idx_chat_user_updated
    ON chat_sessions (username, updated_at_utc DESC);
