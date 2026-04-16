# PULSAR Architektur

> **Ziel:** Bester Offline-Encoder der Welt – keine Echtzeitbeschränkungen,
> maximale Qualität durch globale Analyse und psychoakustisch gesteuertes Budgeting.

---

## Synthese-Philosophie

PULSAR ist eine **Clean-Room-Synthese** aus den mathematischen Kernkonzepten
der wichtigsten Open-Source-Codecs. Wir erfinden kein Rad neu, sondern nutzen
nachgewiesene Algorithmen in eigenem Code:

| Konzept | Referenz-Codec | PULSAR-Umsetzung |
|---|---|---|
| thrExp^4 Rate Control | FDK-AAC `adj_thr.cpp` | `PulsarAllocator.QuantizeFrameVbr` |
| Magic Rounding Offset | FDK-AAC `quantize.cpp` (k=0.23), LAME magic float | `PulsarQuantizer.ComputeMagicRoundOffset` |
| PE-basierte Bitverteilung | LAME `vbrquantize.c` | `PulsarDemandModel` |
| Opus-Stereo-Budget | Opus `opus_multistream_encoder.c` | `PulsarAllocator.AllocateSong` |
| Spreading & Masking | ISO MPEG-1 Psycho-Modell | `PulsarMaskingSpreading` |
| Adaptive Block-Switching | FDK-AAC `block_switch.cpp` | `PulsarPlanner` |

---

## Kern-Pipeline

```
PCM Input
    │
    ▼
PulsarPlanner          – Globale Offline-Analyse, Block-Pfad-Optimierung
    │                    (Transientendetektierung, Pre-Echo-Risiko, SpectralFlux)
    ▼
PulsarPsychoCore       – Psychoakustische Analyse pro Frame
    │                    (Band-Energien, Maskierungs-Schwelle, SMR, PE, Tonalität)
    ▼
PulsarDemandModel      – PE-gewichtete Frame-Demand-Berechnung
    │                    (Stille-Erkennung, Band-Demand-Normierung)
    ▼
PulsarAllocator        – thrExp^4 Global-Gain, BandBit-Zuweisung
    │
    ▼
PulsarTransformEngine  – MDCT/IMDCT mit Block-Ladder-Switching
    │
    ▼
PulsarQuantizer        – Kompander-Quantisierung + Magic Rounding Offset
    │
    ▼
PulsarSuperframeArchiveCodec / PulsarRangeCoder  – Entropy-Coding, PLSR-Format
    │
    ▼
Output (WAV / PLSR)
```

---

## Algorithmen im Detail

### 1. thrExp^4 Rate Control (FDK-AAC Konzept)

**Ziel:** Mathematisch korrekte Ableitung des Global Gains aus der Maskierungs-Schwelle.

**Formel (aus FDK-AAC `adj_thr.cpp`):**
```
// float tmp = thrExp[ch][sfb] + redVal;
// sfbThrReduced = tmp * tmp * tmp * tmp;  // (thrExp + redVal)^4
```

**PULSAR Clean-Room Umsetzung (dB-Domäne):**
```
thrExpDb  = maskingThresholdDb / 4.0          // threshold^(1/4) in dB-Domäne
redValDb  = QualityRedVals[quality]           // qualitätsgest. Verschiebung
effectiveThrDb = (thrExpDb + redValDb) * 4.0  // (thrExp + redVal)^4 in dB
snrTargetDb    = avgBandEnergyDb - effectiveThrDb
frameGain      = snrTargetDb / 6.0            // 6 dB ≈ 1 Bit
```

**Quality-Tabelle (redValDb):**
| Stufe | V0 | V1 | V2 | V3 | V4 | V5 | V6 | V7 | V8 | V9 |
|---|---|---|---|---|---|---|---|---|---|---|
| redValDb | -9 | -7.5 | -6 | -4.5 | -3 | -1.5 | 0 | +1.5 | +3 | +4.5 |

- Negativ: Effektive Schwelle sinkt → mehr Bits nötig → hohe Qualität (V0)
- Positiv: Effektive Schwelle steigt → weniger Bits → stärkere Kompression (V9)

---

### 2. Magic Rounding Offset (MRO)

**Ziel:** Psychoakustisch irrelevante Koeffizienten aggressiv auf Null runden,
um Entropie-Effizienz zu maximieren (mehr Null-Runs für den Coder).

**Referenz (FDK-AAC `quantize.cpp`):**
```c
if (dZoneQuantEnable)
    k = 0.23f;      // Wide dead zone: values < 0.23*step → 0
else
    k = 0.4054f;    // Narrow dead zone: near-standard rounding
```

**PULSAR Clean-Room Umsetzung:**
```csharp
// SMR < -3 dB (masked):   bias → 0.23 (many zeros, better entropy)
// SMR > 9 dB (audible):   bias → 0.50 (normal rounding, no loss)
float t = Clamp((smrDb - (-3)) / (9 - (-3)), 0, 1);
float magicRoundOffset = 0.23f + t * (0.50f - 0.23f);

// Quantization: floor(|x|/step + magicRoundOffset) * sign(x)
```

---

### 3. Globale Bit-Planung (LAME VBR Konzept)

**Ziel:** Stille = minimale Bits; Transient/Komplex = maximale Bits.

- `PulsarDemandModel.Analyze()` berechnet pro Frame einen `FrameDemand`
  aus PE, SMR, MaskingPressure, TransientScore, BassProtection, TonalProtection.
- **Adaptive Stille-Erkennung:** Noise Floor = `avg_active_energy - 50 dB`
  (Song-relativ, nicht fix bei -80 dB).
- **Smoothing:** Gauss-ähnliches Fenstern über ±2 Frames vermeidet Bit-Sprünge.
- **Band-Level:** `BandDemands[]` gewichtet Bänder nach Energie, SMR, Tonalität,
  Bass-Bias und Transient-Boost für hohe Frequenzen.

---

### 4. Stereo-Budget (Opus Konzept)

**Referenz (Opus `opus_multistream_encoder.c`):**
```c
per_stream_rate = total_rate / st->layout.nb_streams;
for (i = 0; i < st->layout.nb_streams; i++)
    rate[i] = per_stream_rate;
```

**PULSAR:** `PulsarAllocator.AllocateSong` verteilt `nominalBitsPerFrame`
gleichmäßig über alle Kanäle. Erweiterung auf M/S-Stereo-Accounting
(Budget nur für Mid-Kanal, Side anteilig) ist als nächste Stufe geplant.

---

### 5. Kompander-Quantisierung (Vorbis-artig)

- Jedes Band wird skaliert (`scale = max(peak, rms)`), dann durch
  einen Power-Law-Kompander (`gamma ≈ 0.72..0.98`) geformt.
- `gamma` ist bandweise aus Tonalität und TransientScore abgeleitet:
  - Tonales Band → gamma nahe 0.90 (mehr Linearität, Oberton-Präzision)
  - Transienten → gamma nahe 0.72 (stärkere Kompression der Hüllkurve)
- Alle drei Quantisierungsparameter (scale, normalizedStep, gamma) werden
  log/linear quantisiert und **vor der Levels-Berechnung** wieder dekodiert
  (Sync-Fix), damit Encoder und Decoder exakt gleiche Werte sehen.

---

## Geplante Erweiterungen (Roadmap)

- [ ] **M/S-Stereo-Accounting:** Mid hat >70% Budget, Side <30%; psychoakustische
      Stereobreiten-Kontrolle per Band (Opus-Inspiration).
- [ ] **Iterative Gain-Refinement:** Einfacher 2-Pass: Pass 1 = thrExp^4 Gain-Schätzung,
      Pass 2 = Feinjustierung per Band-Overflow-Check (FDK-AAC crashRecovery-Konzept).
- [ ] **SIMD-Optimierungen:** `System.Runtime.Intrinsics` für MDCT, Quantisierung und
      Psycho-FFT auf AVX2/NEON.
- [ ] **Finales PLSR-Format:** Festlegung des Bitstream-Formats mit RangeCoder-Entropy.
- [ ] **TNS (Temporal Noise Shaping):** Für stark tonale Frames (Tonality > 0.8)
      Filterung im MDCT-Spektrum vor Quantisierung (FDK-AAC-Konzept).

---

## Dateistruktur

| Ordner | Inhalt |
|---|---|
| `Core/` | Transform-Engine, Quantizer, Archiv-Codec, Packer |
| `Logic/` | Planner, Allocator (thrExp^4), DemandModel |
| `Psycho/` | PsychoCore, Masking, PE, Tonality, Transient |
| `IO/` | Bitstream-Reader/Writer, Referenz-Snippets |
| `Reference Open Source/` | FDK-AAC, LAME, Opus – nur zur Referenz, kein kopierter Code |
| `Artifacts/` | Ausgabe-WAVs und PLSR-Dateien |

---

## Wichtige Konstanten

| Konstante | Wert | Bedeutung |
|---|---|---|
| `GlobalGainStep` | 0.015625 | 1/64 – Gain-Auflösung im Quantizer |
| `ThrExpGainStep` | 6.0 dB | 6 dB SNR ≈ 1 Bit/Sample |
| `QualityRedVals[0]` | -9.0 dB | V0: stärkste Qualität |
| `QualityRedVals[9]` | +4.5 dB | V9: stärkste Kompression |
| `kMasked` (MRO) | 0.23 | FDK-AAC dZone-Offset für maskierte Bänder |
| `kAudible` (MRO) | 0.50 | Standard-Runden für hörbare Bänder |
| `MinBlockSize` | 256 | Kleinster Block für Transienten |
| `MaxBlockSize` | 16384 | Größter Block für stationäre Signale |

---

*Letzte Aktualisierung: 2026-04-16 – thrExp^4 Rate Control + Magic Rounding Offset implementiert.*
