# PulsarCodec Architektur

## Zweck

Dieses Dokument beschreibt den technischen Aufbau von PulsarCodec und trennt bewusst zwischen dem aktuell implementierten Stand im Repository und der Zielarchitektur. Pulsar zielt mittelfristig auf einen hochwertigen Offline-Codec, bei dem Analyse, Blockwahl, Allokation und Rekonstruktion als ein kohärentes System betrachtet werden.

Das langfristige Ziel ist nicht nur Laufzeitoptimierung, sondern auch ein möglichst sparsamer Bitstream, der dort spart, wo keine Audiobearbeitung nötig ist, und die eingesparten Bits in kritische Signalanteile investiert.

## Philosophie von Pulsar

Pulsar ist bewusst kein Echtzeitcodecs, sondern ein Offline-/Archivierungs-Codec. Encoder-Laufzeit, Lookahead und Mehrpass-Analyse sind daher nicht per se schlecht, sondern gewollte Werkzeuge zur Verbesserung der hörbaren Genauigkeit.

Der zentrale Anspruch ist eine möglichst passende Zeit-Frequenz-Abbildung für das Quellmaterial. Ruhiges, tonales Material soll von großen Fenstern und hoher Frequenzauflösung profitieren, impulsives Material hingegen von kleinen Fenstern, um Pre-Echo und verwischte Transienten zu vermeiden. Das Fenster soll der Musik folgen, nicht die Musik einem starren Raster.

Langfristig sollen Blockentscheidungen nicht nur lokal heuristisch getroffen werden. Die Zielarchitektur ist ein global geplanter Encoder mit Lookahead und Pfadoptimierung über Blockgrößen, Umschaltungen und Bitverteilung. Praktisch bedeutet das: aus mehreren gültigen Kandidaten soll der Pfad mit dem kleinsten erwarteten hörbaren Gesamtfehler gewählt werden.

Zusatzwerkzeuge wie TNS dürfen nur dann eingesetzt werden, wenn sie klar messbar und hörbar einen Nutzen bringen und nicht nur Analysefehler kaschieren.

## Aktueller Ist-Zustand

Der aktuelle Codebestand ist ein Prototyp. Die aktuell funktionalen Bausteine sind:

1. `Core/PulsarTransformEngine.cs` liefert den transformbasierten Renderpfad mit MDCT/IMDCT und einer blockleiterbasierten Umschaltlogik.
2. `Logic/PulsarPlanner.cs` analysiert das gesamte Lied offline, trifft Transienten- und Bandentscheidungen und wählt einen Blockpfad mit Kostenfunktionen für Switching, Richtung und Stabilität.
3. `Program.cs` steuert den Experimentierpfad, inklusive `--legacy`, `--legacyP`, `--legacyP-fast` und `--vbr`.
4. `Logic/PulsarAllocator.cs` implementiert einen ersten VBR-Allocator, der Frame-Budgets auf Bandbits verteilt.

Die aktuell vorhandenen, aber noch nicht aktiv im Standard-Renderpfad genutzten Komponenten sind:

- `Core/PulsarPacker.cs`
- `IO/PulsarBitstream.cs`
- `IO/PulsarContainer.cs`
- `Logic/PulsarPsycho.cs`

## Wichtige aktuelle Änderungen

- Der Planner wurde auf einen sequenziellen, zero-allocation Analysepfad umgestellt. Das bedeutet:
  - keine `Parallel.For`-Analyse mehr für die Segmentauswertung
  - keine wiederholten `ExtractFrame`-Allokationen pro Segment
  - keine permanenten temporären `double[]`-Arrays pro Frame
  - wiederverwendete Analysefenster mit gecachten Fenstermustern

- `Program.cs` hat einen neuen CLI-Modus `--legacyP-fast`, der einen kleineren Analyse-FFT-Wert verwendet und die schnelle Legacy-Planer-Ausführung bevorzugt.

- `Core/PulsarTransformEngine.cs` führt jetzt wiederverwendbare MDCT/IMDCT-Puffer und eine Blockleiter-basierte Umschaltlogik, die stationäre Pfade für die aktuell benötigten Blockgrößen rendern kann.

- Der Outputpfad schreibt bei den Experimenten jetzt Planner-Logs und Residual-Checks, um die Wirkung der aktuellen Analyse- und Renderentscheidungen zu dokumentieren.

## Aktueller Signalfluss

Der aktive experimentelle Workflow ist derzeit:

1. PCM-Eingang lesen.
2. `PulsarPlanner` analysiert das komplette Lied offline.
3. `PulsarTransformEngine` rendert für die geplanten Blockgrößen.
4. Bei `--legacyP` wird der Planner-Pfad ohne weitere Bitstreamverarbeitung gerendert.
5. Bei `--legacyP-fast` wird zusätzlich ein günstigerer Analysemodus verwendet.
6. `Program.cs` schreibt Ergebnisse nach WAV und erzeugt Logfiles.

## Rolle der Komponenten

### `Core/PulsarTransformEngine.cs`

- Implementiert die Kern-MDCT/IMDCT-Pfade
- Verwendet eine Blockleiter statt AAC-Start-/Stop-Modi
- Rendert stationäre Pfade und blendet sie an Planner-Übergängen

### `Logic/PulsarPlanner.cs`

- Analysiert Transienten, Energie, Crest-Factor und Spektralinformationen
- Wählt einen globalen Blockpfad mit Kosten für Wechsel und Richtung
- Läuft jetzt sequenziell und mit recycelten Pufferobjekten

### `Program.cs`

- Steuert den CLI-Testpfad
- Unterstützt `--legacy`, `--legacyP`, `--legacyP-fast`, `--vbr` und `--compare`
- Schreibt Logdateien und Residual-Vergleiche

### `Logic/PulsarAllocator.cs`

- Erstes VBR-Allokationsmodell für Frame-Bitbudgets
- Basiert aktuell auf Planner-Merkmalen wie Transientenstärke und Bandenergie

### `Core/PulsarPacker.cs`, `IO/PulsarBitstream.cs`, `IO/PulsarContainer.cs`

- Sind als Skelett für einen späteren Bitstream-Encoder vorbereitet
- Werden aktuell noch nicht im direkten WAV-Renderpfad genutzt

## Technische Grenzen

Der aktuelle Stand ist ein experimenteller Prototyp, nicht ein fertiger Codec. Die wichtigsten fehlenden Teile sind:

1. Kein echter Pulsar-Bitstream.
2. Keine Quantisierung/Entquantisierung.
3. Keine vollständige Psychoakustiksteuerung.
4. Die Container-/Packer-Komponenten sind noch nicht produktiv eingebunden.
5. Das System rendert aktuell direkt nach WAV, nicht in einen finalen Bitstrom.

## Nächste Schritte

1. Die Blockwechsel- und Planner-Kosten weiter vereinfachen und stabilisieren.
2. Die Allokation enger an eine einfache Quantisierung koppeln.
3. Den Bitstream-Pfad aktiv an den Planungs- und Renderpfad anschließen.
4. Später Psychoakustik und Bit-Allokation gemeinsam optimieren.
