# PulsarCodec Architektur

## Zweck

Dieses Dokument beschreibt den technischen Aufbau von PulsarCodec und trennt bewusst zwischen dem aktuell implementierten Stand im Repository und der Zielarchitektur. Pulsar zielt mittelfristig auf einen hochwertigen Offline-Codec, bei dem Analyse, Blockwahl, Allokation und Rekonstruktion als ein kohärentes System betrachtet werden.

Das langfristige Ziel ist nicht nur Laufzeitoptimierung, sondern auch ein möglichst sparsamer Bitstream, der dort spart, wo keine Audiobearbeitung nötig ist, und die eingesparten Bits in kritische Signalanteile investiert.

## Philosophie von Pulsar

Pulsar ist bewusst kein Echtzeitcodecs, sondern ein Offline-/Archivierungs-Codec. Encoder-Laufzeit, Lookahead und Mehrpass-Analyse sind daher nicht per se schlecht, sondern gewollte Werkzeuge zur Verbesserung der hörbaren Genauigkeit.

Der zentrale Anspruch ist eine möglichst passende Zeit-Frequenz-Abbildung für das Quellmaterial. Ruhiges, tonales Material soll von großen Fenstern und hoher Frequenzauflösung profitieren, impulsives Material hingegen von kleinen Fenstern, um Pre-Echo und verwischte Transienten zu vermeiden. Das Fenster soll der Musik folgen, nicht die Musik einem starren Raster.

Langfristig sollen Blockentscheidungen nicht nur lokal heuristisch getroffen werden. Die Zielarchitektur ist ein global geplanter Encoder mit Lookahead und Pfadoptimierung über Blockgrößen, Umschaltungen und Bitverteilung. Praktisch bedeutet das: aus mehreren gültigen Kandidaten soll der Pfad mit dem kleinsten erwarteten hörbaren Gesamtfehler gewählt werden.

Zusatzwerkzeuge wie TNS dürfen nur dann eingesetzt werden, wenn sie klar messbar und hörbar einen Nutzen bringen und nicht nur Analysefehler kaschieren.

Ein wichtiger Grundsatz dabei ist: Pulsar will das Rad nicht unnötig neu erfinden. Dort, wo etablierte Open-Source-Codecs seit Jahren robuste und gut verstandene Lösungen haben, übernimmt Pulsar bewusst funktionierende Ideen und übersetzt sie in die eigene Architektur. Das Ziel ist nicht Originalität um jeden Preis, sondern ein stabiler, nachvollziehbarer Encoder.

Konkret heißt das aktuell:

- Für Perceptual Entropy nutzt Pulsar die grundlegende PE-Idee und Gewichtung aus LAME.
- Für Masking und Spreading nutzt Pulsar FDK-AAC-inspirierte Masking- und Spreading-Mechanismen.
- Für die Transient-Erkennung nutzt Pulsar die Opus/CELT-Transient-Analyse als robuste Onset-Basis.

Das ist ausdrücklich Absicht: bewährte Psychoakustik- und Analysebausteine sollen übernommen werden, damit Pulsar seine Energie in die eigene Blocklogik, Allokation, Offline-Planung und Bitstream-Architektur stecken kann, statt schlechte Neuentwürfe für bereits gelöste Teilprobleme zu bauen.

## True VBR Masterplan (Pulsar Architektur 2.0)

Das neue Architekturziel für Pulsar ist ein echter VBR-Encoder, der sich nicht mehr an einem starren Bitlimit pro Frame orientiert, sondern an einem globalen Qualitätsziel für das gesamte Lied.

1. Echtes VBR statt Frame-Panik

- 128 kbps oder 256 kbps sind nur noch globale Qualitätsanker.
- Die Dateigröße darf variieren wie bei Opus oder LAME VBR.
- Komplexe Abschnitte bekommen viele Bits, ruhige/leichte Stellen nur noch sehr wenige.
- Es gibt keinen harten Bitdeckel pro Frame mehr; stattdessen gibt es ein globales Budget und einen proportionalen Verteilungsschlüssel.
- Der aktuelle CLI-Entwurf unterstützt nur `-V 0` bis `-V 9` als Qualitätslevels und zielt auf PCM-Ausgabe, nicht auf eine feste Bitrate.

2. Globaler 2-Pass-Scan

- Pass 1: gesamter Track wird analysiert und die Perceptual Entropy (PE) aller Frames zusammengefasst.
- Es wird ein globales Qualitätsziel für das ganze Lied berechnet.
- Pass 2: der Encoder quantisiert Frame für Frame aus diesem globalen Pool.
- Jeder Frame darf sich seinen Anteil aus dem globalen Budget proportional zum PE-Bedarf nehmen.

3. Psychoakustik ist Gesetz

- LAME-PE und FDK-Masking gelten als Referenz.
- Das Quantisierungsziel ist primär, das Rauschen unter die Hörschwelle zu drücken (NMR-Targeting).
- Der Akzent liegt auf echter wahrnehmbarer Rauschkontrolle statt auf trockenen Bitkosten.

4. Tod dem Shredder

- Die alte „Outer-Loop“-Schleife, die Frames nachträglich zerstört hat, um lokale Grenzwerte zu erreichen, wird abgeschafft.
- GlobalGain springt nicht mehr wild umher.
- GlobalGain wird zu einem statischen oder sehr sanften globalen Schieberegler (λ).
- Die Quantisierung arbeitet mit stabilen Parametern statt mit nachträglicher Panik.

5. Rückkehr des Gottmodus

- Ohne hysterischen Gain bleibt die Mathematik der Quantisierung sauber.
- Gamma, Deadzone und die eigentlichen Quantisierergebnisse werden nicht mehr nachträglich gebrochen.
- Das Signal bleibt in der Form erhalten, die der Quantisierer ursprünglich berechnet hat.

## Implementierungsstatus des Masterplans

Das ist der Zielentwurf; mittlerweile ist ein großer Teil der Architektur praktisch aufgenommen worden. Der Code befindet sich in einer Übergangsphase, aber die Richtung ist jetzt konkret implementiert:

- Die neue spektrale PCM-Renderkette ist aktiv und wird als Referenzpfad genutzt.
- `--vbrplsrpcm` kann heute bereits spektrale PCM aus den neuen Psycho- und Planerdaten rendern.
- `--vbrplsr` erzeugt einen ersten PLSR-Archiv-Bitstrom und kann ihn wieder dekodieren.
- `--decodeplsr` dekodiert bestehende PLSR-Archive und wird zur Validierung genutzt.
- Der PLSR-Archivpfad läuft über `Core/PulsarSuperframeArchiveCodec.cs` und `IO/PulsarRangeCoder.cs`.
- `Core/PulsarQuantizer.cs` ist jetzt als aktiver Referenzpfad vorhanden und nutzt Psycho-Ergebnisse plus Bandbits zur quantisierten Banddarstellung.
- Die Archive-Decoder-Qualität wird aktiv verfeinert: die direkte spektrale PCM-Reproduktion ist validiert, der vollständige PLSR-Roundtrip ist noch im Debugging.
- Der Masterplan bleibt Leitlinie für globale Budgetverteilung, NMR-Targeting und eine nicht-hysteretische, stabile Quantisierungsarchitektur.

## Aktueller Ist-Zustand

Der aktuelle Codebestand ist ein Prototyp. Die aktuell funktionalen Bausteine sind:

1. `Core/PulsarTransformEngine.cs` liefert den transformbasierten Renderpfad mit MDCT/IMDCT und einer blockleiterbasierten Umschaltlogik.
2. `Logic/PulsarPlanner.cs` analysiert das gesamte Lied offline, trifft Transienten- und Bandentscheidungen und wählt einen Blockpfad mit Kostenfunktionen für Switching, Richtung und Stabilität.
3. `Program.cs` steuert den Experimentierpfad, inklusive `--legacy`, `--legacyP`, `--legacyP-fast`, `--vbr`, `--vbrplsr`, `--vbrplsrpcm` und `--decodeplsr`.
4. `Logic/PulsarAllocator.cs` implementiert jetzt einen psycho-getriebenen VBR-Allocator, der Frame-Budgets und Bandbits aus PE-, SMR- und Masking-Daten ableitet.
5. `Psycho/PulsarPsychoCore.cs`, `Psycho/PulsarPerceptualEntropy.cs`, `Psycho/PulsarMaskingSpreading.cs` und `Psycho/PulsarPsychoTonality.cs` bilden die erste echte Psychoakustik-Kette.
6. `Core/PulsarSuperframeArchiveCodec.cs` ist jetzt aktiv als Archive-Codec in Entwicklung und realisiert spektrale PCM-Darstellung, Archivzerlegung und PLSR-Bitstromaufbau.
7. `IO/PulsarRangeCoder.cs` ist Teil des aktuellen Archivpfads und wurde zuletzt für ICDF-Kodierung/Decodierung angepasst.

Die aktuell vorhandenen, aber noch nicht vollständig eingegliederten Komponenten sind:

- `Core/PulsarPacker.cs`
- `IO/PulsarBitstream.cs`
- `IO/PulsarContainer.cs`

## Wichtige aktuelle Änderungen

- Der Planner wurde auf einen sequenziellen, zero-allocation Analysepfad umgestellt. Das bedeutet:
  - keine `Parallel.For`-Analyse mehr für die Segmentauswertung
  - keine wiederholten `ExtractFrame`-Allokationen pro Segment
  - keine permanenten temporären `double[]`-Arrays pro Frame
  - wiederverwendete Analysefenster mit gecachten Fenstermustern

- `Program.cs` hat einen neuen CLI-Modus `--legacyP-fast`, der einen kleineren Analyse-FFT-Wert verwendet und die schnelle Legacy-Planer-Ausführung bevorzugt.

- `Core/PulsarTransformEngine.cs` führt jetzt wiederverwendbare MDCT/IMDCT-Puffer und eine Blockleiter-basierte Umschaltlogik, die stationäre Pfade für die aktuell benötigten Blockgrößen rendern kann.

- Der Outputpfad schreibt bei den Experimenten jetzt Planner-Logs und Residual-Checks, um die Wirkung der aktuellen Analyse- und Renderentscheidungen zu dokumentieren.

- `Psycho/PulsarPsychoCore.cs` ist jetzt nicht mehr nur Platzhalter, sondern liefert erste echte Psycho-Frames mit:
  - Bark-/SFB-Bändern
  - Bandenergien und Peaks
  - Tonality
  - Masking Thresholds
  - SMR
  - Perceptual Entropy
  - globalen Budget-Sharen

- `Logic/PulsarAllocator.cs` ist bewusst von alten Planner-Heuristiken befreit worden. Der Allocator soll nicht länger selbst raten, wo Bits nötig sind, sondern auf echte Psycho-Daten reagieren.

## Aktueller Signalfluss

Der aktive experimentelle Workflow ist derzeit:

1. PCM-Eingang lesen.
2. `PulsarPlanner` analysiert das komplette Lied offline und wählt einen Blockpfad.
3. `PulsarPsychoCore` analysiert dieselben Frames psychoakustisch.
4. `PulsarAllocator` übersetzt Psycho-Daten in Frame-Budgets und Bandbitverteilungen.
5. `PulsarTransformEngine` rendert für die geplanten Blockgrößen.
5.5 `PulsarQuantizer` übersetzt Bandbits und Psycho-Ergebnisse in quantisierte Bänder und dequantisiert diese für den Renderpfad.
6. Bei `--legacyP` wird der Planner-Pfad ohne weiteren Bitstream gerendert.
7. Bei `--vbr` läuft zusätzlich die psycho-getriebene Allokationskette mit.
8. Bei `--vbrplsrpcm` wird der aktuelle Archive-Pfad genutzt, um spektrale PCM direkt aus dem neuen PLSR-Plan zu rendern.
9. Bei `--vbrplsr` wird ein echtes PLSR-Archiv erzeugt, anschließend dekodiert und als WAV ausgegeben.
10. Bei `--decodeplsr` wird ein bestehendes Archiv dekodiert und zur Validierung mit dem Referenzpfad verglichen.
11. `Program.cs` schreibt Ergebnisse nach WAV und erzeugt Logfiles.

## Referenzbausteine

Pulsar verwendet bewusst bekannte Referenzideen aus anderen Codecs:

### LAME / MP3

- `Psycho/PulsarPerceptualEntropy.cs`
- Nutzt die legendäre LAME-PE-Idee als grobe Entropie-/Bitbedarfsmetrik
- Diese Werte sind starke Indikatoren dafür, wann ein Frame viele Bits braucht

Pulsar übernimmt hier nicht blind einen kompletten MP3-Psycho-Block, sondern vor allem die zentrale Einsicht: hohe PE ist ein sehr guter Marker für hohen Bitbedarf, aber sie darf nicht ungezügelt einzelne Frames sprengen.

### FDK-AAC

- `Psycho/PulsarMaskingSpreading.cs`
- Nutzt FDK-AAC-inspirierte Spreading- und Masking-Mechanismen
- Liefert eine robuste Basis für Masking Thresholds und SMR

Die Idee dahinter ist einfach: statt eigene, fragwürdige Spreading-Kurven zu erfinden, verwendet Pulsar bewährte Formen, die in AAC-ähnlichen Systemen seit Jahren funktionieren.

### Opus / CELT

- `Psycho/PulsarTransientDetector.cs`
- Nutzt die CELT-Transient-Analyse als Grundlage für Onset-Erkennung

Das ist besonders wichtig für den Planner. Kleine Blöcke sollen nicht wegen “viel Energie” entstehen, sondern wegen echter zeitlicher Veränderung und Pre-Echo-Risiko. Genau dafür ist der Opus-Ansatz eine sehr gute Startbasis.

## Rolle der Komponenten

### `Core/PulsarTransformEngine.cs`

- Implementiert die Kern-MDCT/IMDCT-Pfade
- Verwendet eine Blockleiter statt AAC-Start-/Stop-Modi
- Rendert stationäre Pfade und blendet sie an Planner-Übergängen

### `Logic/PulsarPlanner.cs`

- Analysiert Transienten, Energie, Crest-Factor und Spektralinformationen
- Wählt einen globalen Blockpfad mit Kosten für Wechsel und Richtung
- Läuft jetzt sequenziell und mit recycelten Pufferobjekten
- Nutzt eine 2048-Superframe-Logik mit dyadischen Zerlegungen
- Soll kleine Blöcke nur lokal bei echter zeitlicher Veränderung einsetzen und danach wieder auf 2048+ zurückkehren

### `Psycho/PulsarPsychoCore.cs`

- Verpackt die psychoakustischen Analysedaten pro Frame
- Nutzt MDCT-basierte Bandanalyse
- Berechnet Tonality, Masking Thresholds, SMR und Perceptual Entropy
- Erzeugt `PulsarPsychoResult`, das anschließend an den Allocator geht

Kurz gesagt:
- `PsychoCore` misst, wie schwierig ein Frame psychoakustisch ist
- `Allocator` entscheidet, wie viele Bits dieser Frame dafür wirklich bekommt

### `Program.cs`

- Steuert den CLI-Testpfad
- Unterstützt `--legacy`, `--legacyP`, `--legacyP-fast`, `--vbr`, `--vbrplsr`, `--vbrplsrpcm`, `--decodeplsr` und `--compare`
- Schreibt Logdateien und Residual-Vergleiche

### `Core/PulsarSuperframeArchiveCodec.cs`

- Implementiert den aktuellen spektralen Archive-Pfad
- Erzeugt PLSR-Bitstrom und spektrale PCM-Renderings
- Dient als Prüfstand für Archive-Kodierung und -Dekodierung

### `Logic/PulsarAllocator.cs`

- Erster psycho-getriebener VBR-Allocator
- Nutzt jetzt primär:
  - `PerceptualEntropy`
  - `SMR`
  - `MaskingThresholdDb`
  - Bandenergien
  - Tonality
- Enthält Soft-Limits und Caps, damit einzelne Frames nicht unendlich overspenden

Der Allocator soll also nicht blind “viele Bits bei hohem PE” machen, sondern:

1. hohen Bitbedarf erkennen
2. trotzdem Frame-Budgets deckeln
3. Bits sinnvoll über die Bänder verteilen

### `Core/PulsarPacker.cs`, `IO/PulsarBitstream.cs`, `IO/PulsarContainer.cs`

- Sind als Skelett für einen späteren Bitstream-Encoder vorbereitet
- Werden aktuell noch nicht im direkten WAV-Renderpfad genutzt

## Technische Grenzen

Der aktuelle Stand ist ein experimenteller Prototyp, nicht ein fertiger Codec. Die wichtigsten fehlenden Teile sind:

1. Kein echter Pulsar-Bitstream.
2. Keine Quantisierung/Entquantisierung.
3. Keine vollständige Psychoakustiksteuerung.
4. Die neue Psycho-Kette ist bewusst noch eine grobe v1 und noch nicht fein getuned.
5. Die Container-/Packer-Komponenten sind noch nicht produktiv eingebunden.
6. Das System rendert aktuell direkt nach WAV, nicht in einen finalen Bitstrom.

## Nächste Schritte

1. Die Blockwechsel- und Planner-Kosten weiter vereinfachen und stärker auf zeitliche Änderung fokussieren.
2. Die Psycho-Thresholds und Allokationsgewichte als zentrale Tuning-Regler ausbauen.
3. Die Allokation enger an eine einfache Quantisierung koppeln.
4. Den Bitstream-Pfad aktiv an den Planungs- und Renderpfad anschließen.
5. Später Psychoakustik, Blockwahl und Bit-Allokation gemeinsam optimieren.
