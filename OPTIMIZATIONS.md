# TLIG Dashboard — Optimasi Ukuran & Performa

Ringkasan refactor yang dilakukan agar aplikasi lebih ringan dijalankan dan
ukuran publish-folder lebih kecil. Tidak ada perubahan fungsional — perilaku
aplikasi identik sebelum & sesudah.

## 1. Aset gambar (publish + runtime ⇣ ~120 KB)

| File              | Sebelum   | Sesudah  | Hemat    |
|-------------------|-----------|----------|----------|
| `logo.png`        | 100 288 B | 5 448 B  | **−95 %** |
| `Assets/logo.ico` |  34 296 B | 8 752 B  | **−74 %** |

- `logo.png` diubah dari 1051×1050 PNG truecolor menjadi 128 px dengan
  palette 256-warna (octree). Logo hanya ditampilkan setinggi 28 px di
  navbar — resolusi sebelumnya jauh berlebihan.
- `logo.ico` dibangun ulang hanya dengan ukuran yang benar-benar dipakai
  Windows (16/32/48/256). Ukuran 24/64/128 yang redundan dibuang —
  Windows menskalakan ukuran tetangga jika perlu.

## 2. `TLIGDashboard.csproj` — runtime feature flags (publish ⇣ beberapa MB)

Ditambahkan pada blok `Release` agar trimmer ILLink bisa membuang code-path
BCL yang tidak dipakai aplikasi:

```xml
<DebuggerSupport>false</DebuggerSupport>
<EventSourceSupport>false</EventSourceSupport>
<HttpActivityPropagationSupport>false</HttpActivityPropagationSupport>
<MetadataUpdaterSupport>false</MetadataUpdaterSupport>
<UseSystemResourceKeys>true</UseSystemResourceKeys>
<XmlResolverIsNetworkingEnabledByDefault>false</XmlResolverIsNetworkingEnabledByDefault>
<EnableUnsafeBinaryFormatterSerialization>false</EnableUnsafeBinaryFormatterSerialization>
<EnableUnsafeUTF7Encoding>false</EnableUnsafeUTF7Encoding>
<DebugType>none</DebugType>
<DebugSymbols>false</DebugSymbols>
<SatelliteResourceLanguages>en</SatelliteResourceLanguages>
```

Catatan: `TrimMode=partial` dipertahankan agar aman dengan refleksi XAML
WinUI 3. Fitur-fitur di atas semuanya aman untuk app desktop seperti ini.

## 3. Hot path — `MainViewModel.ApplyData`

Fungsi ini dipanggil **setiap snapshot CAN/USB** (~1 Hz s/d 100 Hz).
Alokasi yang dihilangkan:

- `data.Cells.Min()` + `.Max()` + `.Average()` → satu loop manual yang
  menghitung min, max, dan sum dalam satu lintasan (sebelumnya 3 enumerator
  LINQ teralokasi per frame).
- `LogColumns.Where(c => c.IsEnabled)` → `for` loop indeks; menghilangkan
  alokasi iterator Where setiap frame.
- `_cachedEarliest is null ? assign` → operator `??=`, lebih ringkas.

## 4. Hot path — `DashboardPage` chart redraws

`OnHistoryUpdated()` sekarang menyimpan satu snapshot `DateTime[]` dan
dipakai bersama oleh 3 fungsi Redraw + `UpdateTrimBar`. Sebelumnya tiap
chart memanggil `ViewModel.GetTimestamps()` yang menjalankan
`Queue<DateTime>.ToArray()` (alokasi penuh) setiap kali.

Pada `RedrawVIChart`:

- Dua slice array `voltages[n]` & `currents[n]` dibuang. Sekarang loop
  membaca langsung dari `fullV[rangeStart + j]` dan `fullI[rangeStart + j]`.
- `voltages.Min()` / `.Max()` / `currents.Min()` / `.Max()` → satu loop
  yang menghitung 4 nilai sekaligus.

## 5. `LoggingService.Log` — StringBuilder reuse

Sebelumnya tiap baris CSV/TSV memakai
`string.Join(sep, _activeColumns.Select(...))` — satu iterator LINQ +
satu `string[]` perantara + satu string hasil per frame. Sekarang reuse
`StringBuilder` instance milik service. Header CSV ditulis dengan jalur
yang sama.

## 6. `NotificationService.CheckAndNotify`

Dahulu setiap frame mengalokasikan `List<(string,string,string)>` lalu
diterate. Sekarang struktur `EvaluateConditions` di-inline jadi
`EvaluateAndFire` yang langsung memanggil `Fire(...)` saat kondisi
terdeteksi — tanpa list/tuple antara.

Bonus: min/max sel sudah dihitung di loop iterasi cel, sehingga
`data.Cells.Max() - data.Cells.Min()` di akhir (untuk cek imbalance)
tidak perlu dua pass LINQ lagi.

## 7. `Converters.StatusToForegroundConverter`

Konversi `status.ToLowerInvariant()` dihapus (alokasi string baru tiap
update binding). Diganti dengan `String.Equals(..., OrdinalIgnoreCase)`
yang nol-alokasi.

---

## Kesimpulan singkat

- **Ukuran aset:** −89 KB total (logo PNG + ICO).
- **Ukuran publish:** trimmer sekarang membuang code-path BCL yang tidak
  dipakai (diagnostics, event source, XML networking, dll.) — penghematan
  tambahan di binary terbit-akhir.
- **Memory churn per frame** turun signifikan: tidak ada lagi alokasi
  `List`, tuple, iterator LINQ, slice array, atau `ToArray()` ganda
  pada jalur kritis `ApplyData → CheckAndNotify → Log → HistoryUpdated`.
- **Tidak ada perubahan UI atau API publik** — drop-in replacement.
