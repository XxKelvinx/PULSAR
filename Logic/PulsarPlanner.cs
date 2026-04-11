using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Pulsar.Psycho;

// ─────────────────────────────────────────────────────────────────
//  PulsarPlanner v2 – Full-range spectral block switching
//  Block sizes: 256, 512, 1024, 2048, 4096, 8192, 16384
//  Features:
//    • Multi-band spectral energy (sub-bass, bass, low-mid, mid, hi-mid, presence, brilliance)
//    • Pre-echo lookahead detection (up to 3 segments ahead)
//    • Viterbi path optimization with direction-aware costs
//    • Adaptive run-length constraints per block size
//    • Spectral flux tracking for onset sharpness
//    • Crest factor & modulation depth integration
// ─────────────────────────────────────────────────────────────────

public enum PulsarTransientLevel
{
    None,
    Soft,
    Hard,
}

public enum PulsarSwitchDirection
{
    Hold,
    Down,
    Up,
}

public sealed class PulsarFramePlan
{
    public required int SegmentIndex { get; init; }
    public required int PreviousBlockSize { get; init; }
    public required int BlockSize { get; init; }
    public required int TargetBlockSize { get; init; }
    public required PulsarSwitchDirection Direction { get; init; }
    public required PulsarTransientLevel TransientLevel { get; init; }
    public required double AttackRatio { get; init; }
    public required double PeakDeltaDb { get; init; }
    public required int AttackIndex { get; init; }
    public required double EnergyModulation { get; init; }
    public required double CrestFactor { get; init; }
    public required double LowBandRatio { get; init; }
    public required double HighBandRatio { get; init; }
    public required double SustainedHighBandRatio { get; init; }
    public required double DesiredLadderPosition { get; init; }
    public required double ClueStrength { get; init; }
    public required double PathCost { get; init; }
    public required SpectralProfile Spectral { get; init; }
    public required double PreEchoRisk { get; init; }
    public required double SpectralFlux { get; init; }

    public int HopSize => PulsarBlockLadder.GetHopSize(BlockSize);
    public int BandCount => BlockSize / 2;
}

/// <summary>
/// Multi-band spectral energy profile for a single analysis segment.
/// Frequencies are approximate assuming 44100 Hz sample rate.
/// </summary>
public sealed class SpectralProfile
{
    /// <summary>Sub-bass energy ratio (roughly 0–60 Hz)</summary>
    public double SubBass { get; init; }
    /// <summary>Bass energy ratio (roughly 60–250 Hz)</summary>
    public double Bass { get; init; }
    /// <summary>Low-mid energy ratio (roughly 250–500 Hz)</summary>
    public double LowMid { get; init; }
    /// <summary>Mid energy ratio (roughly 500–2000 Hz)</summary>
    public double Mid { get; init; }
    /// <summary>High-mid energy ratio (roughly 2000–4000 Hz)</summary>
    public double HighMid { get; init; }
    /// <summary>Presence energy ratio (roughly 4000–8000 Hz)</summary>
    public double Presence { get; init; }
    /// <summary>Brilliance / air energy ratio (roughly 8000+ Hz)</summary>
    public double Brilliance { get; init; }
    /// <summary>Spectral centroid as normalized position [0..1]</summary>
    public double Centroid { get; init; }
    /// <summary>Spectral flatness [0..1], 1 = noise-like, 0 = tonal</summary>
    public double Flatness { get; init; }
}

public sealed class PulsarTransientAnalysis
{
    public required int SegmentIndex { get; init; }
    public required PulsarTransientLevel Level { get; init; }
    public required double AttackRatio { get; init; }
    public required double PeakDeltaDb { get; init; }
    public required int AttackIndex { get; init; }
    public required double EnergyModulation { get; init; }
    public required double CrestFactor { get; init; }
    public required double LowBandRatio { get; init; }
    public required double HighBandRatio { get; init; }
    public required double SustainedHighBandRatio { get; init; }
    public required double DesiredLadderPosition { get; set; }
    public required double ClueStrength { get; init; }
    public required SpectralProfile Spectral { get; init; }
    public required double PreEchoRisk { get; set; }
    public required double SpectralFlux { get; init; }
}

public sealed class PulsarPlannerSettings
{
    // ── Smoothing ──
    public int AnalysisSmoothingPasses { get; init; } = 5;

    // ── Viterbi cost weights ──
    public double DistancePenalty { get; init; } = 1.35;
    public double SwitchPenalty { get; init; } = 1.05;
    public double LowClueSwitchPenalty { get; init; } = 0.30;
    public double ReversalPenalty { get; init; } = 1.50;
    public double MultiStepJumpPenalty { get; init; } = 2.20;

    // ── Transient-driven costs ──
    public double HardTransientLargeBlockPenalty { get; init; } = 1.20;
    public double SoftTransientLargeBlockPenalty { get; init; } = 0.70;
    public double HardTransientSmallestBlockBonus { get; init; } = -0.40;

    // ── Calm / very calm costs ──
    public double VeryCalmSmallBlockPenalty { get; init; } = 0.65;
    public double CalmSmallBlockPenalty { get; init; } = 0.30;

    // ── Block-size-specific penalties ──
    public double Block256Penalty { get; init; } = 1.60;
    public double Block256HoldPenalty { get; init; } = 1.90;
    public double Block512Penalty { get; init; } = 0.55;
    public double Block512HoldPenalty { get; init; } = 0.70;
    public double Block1024HoldPenalty { get; init; } = 0.28;
    public double Block16384Penalty { get; init; } = 0.20;
    public double Block16384HoldPenalty { get; init; } = 0.10;

    // ── Pre-echo cost ──
    public double PreEchoPenalty { get; init; } = 2.50;
    public double PreEchoLargeBlockPenalty { get; init; } = 3.80;

    // ── Spectral costs ──
    public double BassHeavyLargeBlockPenalty { get; init; } = 0.70;
    public double BrilliantSmallBlockPenalty { get; init; } = 0.60;
    public double TonalLargeBlockBonus { get; init; } = -0.15;
    public double NoisySmallBlockBonus { get; init; } = -0.10;
    public double SpectralFluxSwitchBonus { get; init; } = -0.35;
    public double HighCentroidSmallBlockBonus { get; init; } = -0.20;

    // ── Spectral analysis tuning ──
    public int AnalysisFrameSize { get; init; } = 4096;
    public int SpectralFftSize { get; init; } = 1024;
    public bool UseFastAnalysis { get; init; } = false;

    // ── Run-length limits ──
    public int Max256Run { get; init; } = 2;
    public int Max512Run { get; init; } = 3;
    public int Max1024Run { get; init; } = 5;
    public int Max2048Run { get; init; } = 8;
    public int Max4096Run { get; init; } = 12;
    public int Max8192Run { get; init; } = 16;
    // 16384 has no run limit (default large block)

    // ── Pre-echo lookahead ──
    public int PreEchoLookahead { get; init; } = 3;
    public double PreEchoThreshold { get; init; } = 0.55;
}

internal readonly record struct SegmentAnalysisData(
    PulsarTransientLevel Level,
    double AttackRatio,
    double PeakDeltaDb,
    int AttackIndex,
    double EnergyModulation,
    double CrestFactor,
    double LowBandRatio,
    double HighBandRatio,
    double SustainedHighBandRatio,
    SpectralProfile Spectral);

internal sealed class FftPlan
{
    public required int[] BitReverse { get; init; }
    public required double[] StageMulReal { get; init; }
    public required double[] StageMulImag { get; init; }
}

internal sealed class PulsarContextPattern
{
    public required int[] SegmentStates { get; init; }
    public required int[] BlockStates { get; init; }
}

public sealed class PulsarPlanner
{
    // ── Full block ladder: 7 steps from 256 to 16384 ──
    private static readonly int[] BlockSteps = { 256, 512, 1024, 2048, 4096, 8192, 16384 };
    private static readonly int StateCount = BlockSteps.Length;
    private static readonly int DefaultStateIndex = Array.IndexOf(BlockSteps, PulsarBlockLadder.DefaultBlockSize);
    private static readonly int ContextSegmentCount = PulsarBlockLadder.DefaultBlockSize / PulsarBlockLadder.ControlHopSize;
    private static readonly int[] ContextEligibleStates = { 0, 1, 2, 3 };

    private readonly PulsarPlannerSettings _settings;
    private readonly PulsarTransientDetector _transientDetector = new();

    public IReadOnlyList<PulsarTransientAnalysis> LastAnalyses { get; private set; } = Array.Empty<PulsarTransientAnalysis>();
    public IReadOnlyList<PulsarFramePlan> LastPlan { get; private set; } = Array.Empty<PulsarFramePlan>();

    // ── Thresholds ──
    private const double HardTransientThreshold = 8.0;
    private const double SoftTransientThreshold = 4.0;
    private const double ExtremeHardAttackRatio = 10.5;
    private const double ExtremeHardModulation = 0.55;
    private const double ExtremeHardCrest = 4.0;
    private const double CalmModulation = 0.18;
    private const double VeryCalmModulation = 0.08;
    private const double BusyModulation = 0.40;
    private const double VeryBusyModulation = 0.75;
    private const double CalmCrest = 4.0;
    private const double VeryCalmCrest = 3.0;
    private const double BassHeavyRatio = 0.18;
    private const double BrightHighBand = 0.16;
    private const double VeryBrightHighBand = 0.24;

    // ── Spectral band cutoff bins (approximate for 44100 Hz, 512-sample DFT) ──
    // We compute these relative to the analysis frame length at runtime.
    private const double SubBassCutoffHz = 60.0;
    private const double BassCutoffHz = 250.0;
    private const double LowMidCutoffHz = 500.0;
    private const double MidCutoffHz = 2000.0;
    private const double HighMidCutoffHz = 4000.0;
    private const double PresenceCutoffHz = 8000.0;
    private const double AssumedSampleRate = 44100.0;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, double[]> AnalysisWindowCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, FftPlan> FftPlanCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyList<PulsarContextPattern>> ContextPatternCache = new();

    // ── Low-pass filter for legacy low-band ratio ──
    private const double LowBandFilterCoeff = 0.985;

    // ── Direction encoding ──
    private const int DirDown = 0;
    private const int DirHold = 1;
    private const int DirUp = 2;

    public PulsarPlanner(PulsarPlannerSettings? settings = null)
    {
        _settings = settings ?? new PulsarPlannerSettings();
    }

    // ─────────────────────────────────────────────────────────────
    //  Public entry point
    // ─────────────────────────────────────────────────────────────
    public List<PulsarFramePlan> PlanSong(float[] input, Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        PulsarBlockLadder.Validate();

        if (input.Length == 0)
        {
            LastAnalyses = Array.Empty<PulsarTransientAnalysis>();
            LastPlan = Array.Empty<PulsarFramePlan>();
            return new List<PulsarFramePlan>();
        }

        var analyses = AnalyzeSong(input, progress);
        ComputePreEchoRisk(analyses);
        SmoothDesiredPositions(analyses);
        var plan = RunContextPlanner(analyses);

        LastAnalyses = analyses;
        LastPlan = plan;
        return plan;
    }

    // ═════════════════════════════════════════════════════════════
    //  ANALYSIS
    // ═════════════════════════════════════════════════════════════

    private List<PulsarTransientAnalysis> AnalyzeSong(float[] input, Action<int, int>? progress)
    {
        int segmentCount = (input.Length + PulsarBlockLadder.ControlHopSize - 1) / PulsarBlockLadder.ControlHopSize;
        int reportInterval = Math.Max(1, segmentCount / 100);
        int analysisFrameSize = Math.Clamp(
            _settings.AnalysisFrameSize,
            PulsarBlockLadder.ControlHopSize,
            PulsarBlockLadder.AnalysisFrameSize);
        int fftSize = _settings.SpectralFftSize;
        int halfBins = fftSize / 2 + 1;

        float[] frame = new float[analysisFrameSize];
        double[] fftReal = new double[fftSize];
        double[] fftImag = new double[fftSize];
        double[] magnitudeBuffer = new double[halfBins];
        double[] previousMagnitudes = new double[halfBins];
        bool hasPreviousMagnitudes = false;
        double[] analysisWindow = GetAnalysisWindow(fftSize);

        var analyses = new List<PulsarTransientAnalysis>(segmentCount);

        for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            int offset = segmentIndex * PulsarBlockLadder.ControlHopSize;
            FillFrame(input, offset, frame);

            var partial = AnalyzeSegmentPartial(frame, segmentIndex, fftReal, fftImag, magnitudeBuffer, analysisWindow);
            double spectralFlux = _settings.UseFastAnalysis
                ? 0
                : hasPreviousMagnitudes ? ComputeSpectralFlux(magnitudeBuffer, previousMagnitudes) : 0;

            if (!_settings.UseFastAnalysis)
            {
                Array.Copy(magnitudeBuffer, previousMagnitudes, halfBins);
                hasPreviousMagnitudes = true;
            }

            double desiredPosition = SelectDesiredPosition(
                partial.Level,
                partial.AttackRatio,
                partial.AttackIndex,
                partial.EnergyModulation,
                partial.CrestFactor,
                partial.HighBandRatio,
                partial.SustainedHighBandRatio,
                partial.Spectral,
                spectralFlux);

            double clueStrength = ComputeClueStrength(
                partial.Level,
                partial.AttackRatio,
                partial.AttackIndex,
                partial.EnergyModulation,
                partial.CrestFactor,
                partial.SustainedHighBandRatio,
                spectralFlux);

            analyses.Add(new PulsarTransientAnalysis
            {
                SegmentIndex = segmentIndex,
                Level = partial.Level,
                AttackRatio = partial.AttackRatio,
                PeakDeltaDb = partial.PeakDeltaDb,
                AttackIndex = partial.AttackIndex,
                EnergyModulation = partial.EnergyModulation,
                CrestFactor = partial.CrestFactor,
                LowBandRatio = partial.LowBandRatio,
                HighBandRatio = partial.HighBandRatio,
                SustainedHighBandRatio = partial.SustainedHighBandRatio,
                DesiredLadderPosition = desiredPosition,
                ClueStrength = clueStrength,
                Spectral = partial.Spectral,
                PreEchoRisk = 0.0,
                SpectralFlux = spectralFlux,
            });

            if (progress != null && (segmentIndex % reportInterval == 0 || segmentIndex == segmentCount - 1))
            {
                progress(segmentIndex + 1, segmentCount);
            }
        }

        return analyses;
    }

    private PulsarTransientAnalysis AnalyzeSegment(
        float[] samples,
        int segmentIndex,
        ref double[]? previousMagnitudes,
        double[] fftReal,
        double[] fftImag,
        double[] fftMag)
    {
        // ── Opus transient detector ──
        bool isTransient = _transientDetector.TransientAnalysis(
            samples, samples.Length, 1,
            out float tfEstimate, out int tfChan,
            allowWeakTransients: true, out bool weakTransient,
            toneFreq: 0.0f, toneishness: 0.0f);

        var level = PulsarTransientLevel.None;
        if (isTransient)
            level = tfEstimate >= 0.55f ? PulsarTransientLevel.Hard : PulsarTransientLevel.Soft;

        double attackRatio = tfEstimate * 12.0;
        double peakDeltaDb = tfEstimate * 20.0;
        int attackIndex = tfEstimate switch
        {
            <= 0.20f => 0,
            <= 0.40f => 1,
            <= 0.60f => 2,
            <= 0.80f => 3,
            _ => 4,
        };

        // ── Time-domain metrics ──
        ComputeTimeDomainMetrics(samples,
            out double energyModulation, out double crestFactor,
            out double lowBandRatio, out double highBandRatio);

        // ── Multi-band spectral analysis and flux ──
        ComputeSpectralProfileAndFlux(samples, ref previousMagnitudes, fftReal, fftImag, fftMag,
            out SpectralProfile spectral, out double spectralFlux);

        double sustainedHighBandRatio = highBandRatio;

        double desiredPosition = SelectDesiredPosition(
            level, attackRatio, attackIndex,
            energyModulation, crestFactor,
            highBandRatio, sustainedHighBandRatio,
            spectral, spectralFlux);

        double clueStrength = ComputeClueStrength(
            level, attackRatio, attackIndex,
            energyModulation, crestFactor,
            sustainedHighBandRatio, spectralFlux);

        return new PulsarTransientAnalysis
        {
            SegmentIndex = segmentIndex,
            Level = level,
            AttackRatio = attackRatio,
            PeakDeltaDb = peakDeltaDb,
            AttackIndex = attackIndex,
            EnergyModulation = energyModulation,
            CrestFactor = crestFactor,
            LowBandRatio = lowBandRatio,
            HighBandRatio = highBandRatio,
            SustainedHighBandRatio = sustainedHighBandRatio,
            DesiredLadderPosition = desiredPosition,
            ClueStrength = clueStrength,
            Spectral = spectral,
            PreEchoRisk = 0.0, // filled in by ComputePreEchoRisk pass
            SpectralFlux = spectralFlux,
        };
    }

    private SegmentAnalysisData AnalyzeSegmentPartial(
        float[] samples,
        int segmentIndex,
        double[] fftReal,
        double[] fftImag,
        double[] magnitudeBuffer,
        double[] analysisWindow)
    {
        bool isTransient = _transientDetector.TransientAnalysis(
            samples, samples.Length, 1,
            out float tfEstimate, out int tfChan,
            allowWeakTransients: true, out bool weakTransient,
            toneFreq: 0.0f, toneishness: 0.0f);

        var level = PulsarTransientLevel.None;
        if (isTransient)
            level = tfEstimate >= 0.55f ? PulsarTransientLevel.Hard : PulsarTransientLevel.Soft;

        double attackRatio = tfEstimate * 12.0;
        double peakDeltaDb = tfEstimate * 20.0;
        int attackIndex = tfEstimate switch
        {
            <= 0.20f => 0,
            <= 0.40f => 1,
            <= 0.60f => 2,
            <= 0.80f => 3,
            _ => 4,
        };

        ComputeTimeDomainMetrics(samples,
            out double energyModulation, out double crestFactor,
            out double lowBandRatio, out double highBandRatio);

        if (_settings.UseFastAnalysis)
        {
            return new SegmentAnalysisData(
                level,
                attackRatio,
                peakDeltaDb,
                attackIndex,
                energyModulation,
                crestFactor,
                lowBandRatio,
                highBandRatio,
                highBandRatio, // sustainedHighBandRatio
                new SpectralProfile());
        }

        ComputeSpectralProfile(samples, fftReal, fftImag, magnitudeBuffer, analysisWindow, out SpectralProfile spectral);

        return new SegmentAnalysisData(
            level,
            attackRatio,
            peakDeltaDb,
            attackIndex,
            energyModulation,
            crestFactor,
            lowBandRatio,
            highBandRatio,
            highBandRatio, // sustainedHighBandRatio
            spectral);
    }

    private static double ComputeSpectralFlux(double[] currentMagnitudes, double[]? previousMagnitudes)
    {
        if (previousMagnitudes == null || previousMagnitudes.Length != currentMagnitudes.Length)
        {
            return 0;
        }

        double flux = 0;
        double totalMag = 0;
        int halfBins = currentMagnitudes.Length;

        for (int k = 1; k < halfBins; k++)
        {
            double m = currentMagnitudes[k];
            totalMag += m;
            double diff = m - previousMagnitudes[k];
            if (diff > 0) flux += diff;
        }

        return totalMag > 1e-15 ? flux / totalMag : 0;
    }

    private static void ComputeSpectralProfile(
        float[] samples,
        double[] fftReal,
        double[] fftImag,
        double[] magnitudeBuffer,
        double[] analysisWindow,
        out SpectralProfile spectral)
    {
        int fftSize = fftReal.Length;
        int n = Math.Min(samples.Length, fftSize);
        int halfBins = fftSize / 2 + 1;

        for (int i = 0; i < n; i++)
        {
            fftReal[i] = samples[i] * analysisWindow[i];
            fftImag[i] = 0.0;
        }

        for (int i = n; i < fftSize; i++)
        {
            fftReal[i] = 0.0;
            fftImag[i] = 0.0;
        }

        ComputeRealFft(fftReal, fftImag, fftSize);

        double totalMag = 0;
        double weightedSum = 0;
        double logProduct = 0;
        int logCount = 0;

        for (int k = 1; k < halfBins; k++)
        {
            double re = fftReal[k];
            double im = fftImag[k];
            double m = Math.Sqrt(re * re + im * im);
            magnitudeBuffer[k] = m;
            totalMag += m;
            weightedSum += k * m;
            if (m > 1e-12)
            {
                logProduct += Math.Log(m);
                logCount++;
            }
        }

        if (totalMag < 1e-15)
        {
            spectral = new SpectralProfile();
            return;
        }

        int subBassBin = Math.Max(1, (int)(SubBassCutoffHz / (AssumedSampleRate / fftSize)));
        int bassBin = Math.Max(subBassBin + 1, (int)(BassCutoffHz / (AssumedSampleRate / fftSize)));
        int lowMidBin = Math.Max(bassBin + 1, (int)(LowMidCutoffHz / (AssumedSampleRate / fftSize)));
        int midBin = Math.Max(lowMidBin + 1, (int)(MidCutoffHz / (AssumedSampleRate / fftSize)));
        int highMidBin = Math.Max(midBin + 1, (int)(HighMidCutoffHz / (AssumedSampleRate / fftSize)));
        int presenceBin = Math.Max(highMidBin + 1, (int)(PresenceCutoffHz / (AssumedSampleRate / fftSize)));

        double SumRange(int lo, int hi)
        {
            double s = 0;
            for (int k = lo; k < Math.Min(hi, halfBins); k++) s += magnitudeBuffer[k];
            return s;
        }

        double subBass = SumRange(1, subBassBin);
        double bass = SumRange(subBassBin, bassBin);
        double lowMid = SumRange(bassBin, lowMidBin);
        double mid = SumRange(lowMidBin, midBin);
        double highMid = SumRange(midBin, highMidBin);
        double presence = SumRange(highMidBin, presenceBin);
        double brilliance = SumRange(presenceBin, halfBins);

        double centroid = weightedSum / totalMag / (halfBins - 1);
        double geoMean = logCount > 0 ? Math.Exp(logProduct / logCount) : 0;
        double ariMean = totalMag / (halfBins - 1);
        double flatness = ariMean > 1e-15 ? Math.Clamp(geoMean / ariMean, 0, 1) : 0;

        spectral = new SpectralProfile
        {
            SubBass = subBass / totalMag,
            Bass = bass / totalMag,
            LowMid = lowMid / totalMag,
            Mid = mid / totalMag,
            HighMid = highMid / totalMag,
            Presence = presence / totalMag,
            Brilliance = brilliance / totalMag,
            Centroid = centroid,
            Flatness = flatness,
        };
    }

    // ── Time-domain metrics (unchanged logic) ──
    private static void ComputeTimeDomainMetrics(float[] samples,
        out double energyModulation, out double crestFactor,
        out double lowBandRatio, out double highBandRatio)
    {
        double totalEnergy = 0, maxPeak = 0, totalDiff = 0;
        double lowBandEnergy = 0, highBandEnergy = 0, lowPass = 0;
        float prev = 0;
        int n = samples.Length;

        for (int i = 0; i < n; i++)
        {
            float s = samples[i];
            double e = s * (double)s;
            totalEnergy += e;
            maxPeak = Math.Max(maxPeak, Math.Abs(s));

            lowPass = LowBandFilterCoeff * lowPass + (1.0 - LowBandFilterCoeff) * s;
            lowBandEnergy += lowPass * lowPass;

            if (i > 0)
            {
                double d = s - prev;
                highBandEnergy += d * d;
                totalDiff += Math.Abs(e - prev * (double)prev);
            }
            prev = s;
        }

        double avgEnergy = totalEnergy / Math.Max(n, 1);
        double rms = Math.Sqrt(Math.Max(avgEnergy, 0.0));
        crestFactor = rms > 0 ? maxPeak / rms : 0;
        energyModulation = avgEnergy > 0
            ? totalDiff / (avgEnergy * Math.Max(n - 1, 1))
            : 0;
        highBandRatio = avgEnergy > 0
            ? Math.Clamp(highBandEnergy / (4.0 * totalEnergy), 0, 1)
            : 0;
        lowBandRatio = Math.Clamp(1.0 - highBandRatio, 0, 1);
    }

    private static void ComputeSpectralProfileAndFlux(
        float[] samples,
        ref double[]? previousMagnitudes,
        double[] fftReal,
        double[] fftImag,
        double[] fftMag,
        out SpectralProfile spectral,
        out double spectralFlux)
    {
        int fftSize = fftReal.Length;
        int n = Math.Min(samples.Length, fftSize);
        int halfBins = fftSize / 2 + 1;

        for (int i = 0; i < n; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
            fftReal[i] = samples[i] * w;
            fftImag[i] = 0.0;
        }
        for (int i = n; i < fftSize; i++)
        {
            fftReal[i] = 0.0;
            fftImag[i] = 0.0;
        }

        ComputeRealFft(fftReal, fftImag, fftSize);

        double totalMag = 0;
        double weightedSum = 0;
        double logProduct = 0;
        int logCount = 0;

        for (int k = 1; k < halfBins; k++)
        {
            double re = fftReal[k];
            double im = fftImag[k];
            double m = Math.Sqrt(re * re + im * im);
            fftMag[k] = m;
            totalMag += m;
            weightedSum += k * m;
            if (m > 1e-12)
            {
                logProduct += Math.Log(m);
                logCount++;
            }
        }

        if (previousMagnitudes != null && previousMagnitudes.Length == halfBins)
        {
            double flux = 0;
            for (int k = 1; k < halfBins; k++)
            {
                double diff = fftMag[k] - previousMagnitudes[k];
                if (diff > 0) flux += diff;
            }
            spectralFlux = totalMag > 1e-15 ? flux / totalMag : 0;
        }
        else
        {
            spectralFlux = 0;
        }

        previousMagnitudes = new double[halfBins];
        Array.Copy(fftMag, previousMagnitudes, halfBins);

        if (totalMag < 1e-15)
        {
            spectral = new SpectralProfile();
            return;
        }

        double binHz = AssumedSampleRate / fftSize;
        int subBassBin = Math.Max(1, (int)(SubBassCutoffHz / binHz));
        int bassBin = Math.Max(subBassBin + 1, (int)(BassCutoffHz / binHz));
        int lowMidBin = Math.Max(bassBin + 1, (int)(LowMidCutoffHz / binHz));
        int midBin = Math.Max(lowMidBin + 1, (int)(MidCutoffHz / binHz));
        int highMidBin = Math.Max(midBin + 1, (int)(HighMidCutoffHz / binHz));
        int presenceBin = Math.Max(highMidBin + 1, (int)(PresenceCutoffHz / binHz));

        double SumRange(int lo, int hi)
        {
            double s = 0;
            for (int k = lo; k < Math.Min(hi, halfBins); k++) s += fftMag[k];
            return s;
        }

        double subBass = SumRange(1, subBassBin);
        double bass = SumRange(subBassBin, bassBin);
        double lowMid = SumRange(bassBin, lowMidBin);
        double mid = SumRange(lowMidBin, midBin);
        double highMid = SumRange(midBin, highMidBin);
        double presence = SumRange(highMidBin, presenceBin);
        double brilliance = SumRange(presenceBin, halfBins);

        double centroid = weightedSum / totalMag / (halfBins - 1);
        double geoMean = logCount > 0 ? Math.Exp(logProduct / logCount) : 0;
        double ariMean = totalMag / (halfBins - 1);
        double flatness = ariMean > 1e-15 ? Math.Clamp(geoMean / ariMean, 0, 1) : 0;

        spectral = new SpectralProfile
        {
            SubBass = subBass / totalMag,
            Bass = bass / totalMag,
            LowMid = lowMid / totalMag,
            Mid = mid / totalMag,
            HighMid = highMid / totalMag,
            Presence = presence / totalMag,
            Brilliance = brilliance / totalMag,
            Centroid = centroid,
            Flatness = flatness,
        };
    }

    private static double[] GetAnalysisWindow(int fftSize)
    {
        return AnalysisWindowCache.GetOrAdd(fftSize, size =>
        {
            int n = size;
            var window = new double[n];
            for (int i = 0; i < n; i++)
            {
                window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
            }
            return window;
        });
    }

    private static void ComputeRealFft(double[] real, double[] imag, int n)
    {
        FftPlan plan = GetFftPlan(n);
        int[] bitReverse = plan.BitReverse;
        for (int i = 0; i < n; i++)
        {
            int j = bitReverse[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        int stageIndex = 0;
        for (int size = 2; size <= n; size <<= 1, stageIndex++)
        {
            int halfSize = size >> 1;
            double wMulReal = plan.StageMulReal[stageIndex];
            double wMulImag = plan.StageMulImag[stageIndex];

            for (int start = 0; start < n; start += size)
            {
                double wReal = 1.0;
                double wImag = 0.0;

                for (int k = 0; k < halfSize; k++)
                {
                    int evenIndex = start + k;
                    int oddIndex = start + k + halfSize;

                    double oddReal = real[oddIndex] * wReal - imag[oddIndex] * wImag;
                    double oddImag = real[oddIndex] * wImag + imag[oddIndex] * wReal;

                    real[oddIndex] = real[evenIndex] - oddReal;
                    imag[oddIndex] = imag[evenIndex] - oddImag;
                    real[evenIndex] += oddReal;
                    imag[evenIndex] += oddImag;

                    double nextReal = wReal * wMulReal - wImag * wMulImag;
                    double nextImag = wReal * wMulImag + wImag * wMulReal;
                    wReal = nextReal;
                    wImag = nextImag;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReverseBits(int value, int bitCount)
    {
        int result = 0;
        for (int i = 0; i < bitCount; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LeadingZeroCount(int value)
    {
        return BitOperations.LeadingZeroCount((uint)value);
    }

    private static FftPlan GetFftPlan(int fftSize)
    {
        return FftPlanCache.GetOrAdd(fftSize, size =>
        {
            int levels = 31 - LeadingZeroCount(size);
            var bitReverse = new int[size];
            for (int i = 0; i < size; i++)
            {
                bitReverse[i] = ReverseBits(i, levels);
            }

            var stageMulReal = new double[levels];
            var stageMulImag = new double[levels];
            int stageIndex = 0;
            for (int currentSize = 2; currentSize <= size; currentSize <<= 1, stageIndex++)
            {
                double theta = -2.0 * Math.PI / currentSize;
                stageMulReal[stageIndex] = Math.Cos(theta);
                stageMulImag[stageIndex] = Math.Sin(theta);
            }

            return new FftPlan
            {
                BitReverse = bitReverse,
                StageMulReal = stageMulReal,
                StageMulImag = stageMulImag,
            };
        });
    }

    // ── Pre-echo risk: lookahead for upcoming transients ──
    private void ComputePreEchoRisk(List<PulsarTransientAnalysis> analyses)
    {
        int lookahead = _settings.PreEchoLookahead;

        for (int i = 0; i < analyses.Count; i++)
        {
            double maxRisk = 0;
            for (int ahead = 1; ahead <= lookahead && i + ahead < analyses.Count; ahead++)
            {
                var future = analyses[i + ahead];
                if (future.Level == PulsarTransientLevel.Hard)
                {
                    // Risk decreases with distance, increases with attack strength
                    double distanceFade = 1.0 / ahead;
                    double attackWeight = Math.Min(future.AttackRatio / ExtremeHardAttackRatio, 1.5);
                    double fluxWeight = Math.Min(future.SpectralFlux * 2.0, 1.0);
                    maxRisk = Math.Max(maxRisk, distanceFade * (0.5 * attackWeight + 0.3 * fluxWeight + 0.2));
                }
                else if (future.Level == PulsarTransientLevel.Soft)
                {
                    double distanceFade = 1.0 / ahead;
                    double attackWeight = Math.Min(future.AttackRatio / HardTransientThreshold, 1.0);
                    maxRisk = Math.Max(maxRisk, distanceFade * attackWeight * 0.5);
                }
            }

            // Also consider high spectral flux as pre-echo indicator
            var current = analyses[i];
            if (current.SpectralFlux > 0.4 && current.Level == PulsarTransientLevel.None)
            {
                maxRisk = Math.Max(maxRisk, current.SpectralFlux * 0.6);
            }

            // Rebuild with pre-echo risk filled in
            current.PreEchoRisk = Math.Clamp(maxRisk, 0, 1);
        }
    }

    // ═════════════════════════════════════════════════════════════
    //  DESIRED POSITION SELECTION
    // ═════════════════════════════════════════════════════════════

    private static double SelectDesiredPosition(
        PulsarTransientLevel level, double attackRatio, int attackIndex,
        double energyModulation, double crestFactor,
        double highBandRatio, double sustainedHighBandRatio,
        SpectralProfile spectral, double spectralFlux)
    {
        // State indices: 0=256, 1=512, 2=1024, 3=2048, 4=4096, 5=8192, 6=16384
        int maxIdx = StateCount - 1;    // 6 = 16384
        int largeIdx = maxIdx;          // 6
        int secLargeIdx = maxIdx - 1;   // 5
        int midIdx = maxIdx / 2;        // 3 = 2048
        int smIdx = 1;                  // 512

        // ── Hard transient: go small ──
        if (level == PulsarTransientLevel.Hard)
        {
            bool extreme = IsExtremeHard(attackRatio, energyModulation, crestFactor);
            double basePos = extreme ? 0.0 : smIdx;

            // With high spectral flux, even more urgency to shrink
            if (spectralFlux > 0.5)
                basePos = Math.Min(basePos, 0.5);

            return ApplyAttackLead(level, basePos, attackIndex);
        }

        // ── Soft transient ──
        if (level == PulsarTransientLevel.Soft)
        {
            double basePos = 2.0; // 1024
            if (spectralFlux > 0.4)
                basePos = 1.5;
            return ApplyAttackLead(level, basePos, attackIndex);
        }

        // ── Very busy: modulation + high flux ──
        if (energyModulation >= VeryBusyModulation)
            return smIdx;

        if (energyModulation >= BusyModulation)
        {
            // Busy but not transient: 1024–2048 range
            if (spectral.Centroid > 0.5)
                return 2.0;
            return midIdx;
        }

        // ── Spectral flux onset without transient detection ──
        if (spectralFlux > 0.6)
            return 2.5; // between 1024 and 2048

        if (spectralFlux > 0.35)
            return midIdx; // 2048

        // ── Very calm: go large ──
        if (energyModulation <= VeryCalmModulation
            && crestFactor <= VeryCalmCrest
            && attackRatio <= 1.5)
        {
            // Tonal content benefits from large blocks
            if (spectral.Flatness < 0.3)
                return largeIdx; // 16384

            // Bright content: slightly smaller
            if (sustainedHighBandRatio >= BrightHighBand || spectral.Brilliance > 0.15)
                return secLargeIdx; // 8192

            return largeIdx;
        }

        // ── Calm ──
        if (energyModulation <= CalmModulation && crestFactor <= CalmCrest && attackRatio <= 2.0)
        {
            // Bass-heavy & tonal: large block for frequency resolution
            if (spectral.SubBass + spectral.Bass > 0.5 && spectral.Flatness < 0.35)
                return secLargeIdx; // 8192

            if (highBandRatio < BrightHighBand)
                return maxIdx - 2; // 4096

            return secLargeIdx;
        }

        // ── Default: moderate activity ──
        // Use spectral centroid to choose: low centroid → bigger, high centroid → smaller
        double centroidBias = (1.0 - spectral.Centroid) * 2.0; // 0..2
        double basePosition = midIdx + centroidBias * 0.5; // 3..4 range typically

        // Noise-like signals need less frequency resolution
        if (spectral.Flatness > 0.6)
            basePosition -= 0.5;

        // Very bright: nudge down
        if (sustainedHighBandRatio >= VeryBrightHighBand)
            basePosition -= 0.35;

        return Math.Clamp(basePosition, 1.0, maxIdx);
    }

    // ═════════════════════════════════════════════════════════════
    //  VITERBI PATH OPTIMIZATION
    // ═════════════════════════════════════════════════════════════

    private List<PulsarFramePlan> RunContextPlanner(List<PulsarTransientAnalysis> analyses)
    {
        int totalSegments = analyses.Count;
        int contextCount = (totalSegments + ContextSegmentCount - 1) / ContextSegmentCount;
        if (contextCount == 0)
        {
            return new List<PulsarFramePlan>();
        }

        var candidateSets = new List<PulsarContextPattern>[contextCount];
        for (int contextIndex = 0; contextIndex < contextCount; contextIndex++)
        {
            int startSegment = contextIndex * ContextSegmentCount;
            int contextSegments = Math.Min(ContextSegmentCount, totalSegments - startSegment);
            candidateSets[contextIndex] = new List<PulsarContextPattern>(GetContextPatterns(contextSegments));
        }

        var costs = new double[contextCount][];
        var previous = new int[contextCount][];

        for (int contextIndex = 0; contextIndex < contextCount; contextIndex++)
        {
            int candidateCount = candidateSets[contextIndex].Count;
            costs[contextIndex] = new double[candidateCount];
            previous[contextIndex] = new int[candidateCount];
            Array.Fill(costs[contextIndex], double.PositiveInfinity);
            Array.Fill(previous[contextIndex], -1);
        }

        for (int candidateIndex = 0; candidateIndex < candidateSets[0].Count; candidateIndex++)
        {
            costs[0][candidateIndex] = ScorePattern(
                candidateSets[0][candidateIndex],
                analyses,
                0,
                DefaultStateIndex,
                DirHold);
        }

        for (int contextIndex = 1; contextIndex < contextCount; contextIndex++)
        {
            int startSegment = contextIndex * ContextSegmentCount;
            for (int candidateIndex = 0; candidateIndex < candidateSets[contextIndex].Count; candidateIndex++)
            {
                var currentPattern = candidateSets[contextIndex][candidateIndex];

                for (int previousIndex = 0; previousIndex < candidateSets[contextIndex - 1].Count; previousIndex++)
                {
                    double previousCost = costs[contextIndex - 1][previousIndex];
                    if (double.IsPositiveInfinity(previousCost))
                    {
                        continue;
                    }

                    var previousPattern = candidateSets[contextIndex - 1][previousIndex];
                    int previousState = previousPattern.SegmentStates[^1];
                    int previousDirection = GetPatternTerminalDirection(previousPattern);

                    double totalCost = previousCost + ScorePattern(
                        currentPattern,
                        analyses,
                        startSegment,
                        previousState,
                        previousDirection);

                    if (totalCost < costs[contextIndex][candidateIndex])
                    {
                        costs[contextIndex][candidateIndex] = totalCost;
                        previous[contextIndex][candidateIndex] = previousIndex;
                    }
                }
            }
        }

        int lastContext = contextCount - 1;
        double bestCost = double.PositiveInfinity;
        int bestCandidate = 0;
        for (int candidateIndex = 0; candidateIndex < candidateSets[lastContext].Count; candidateIndex++)
        {
            double candidateCost = costs[lastContext][candidateIndex];
            if (candidateCost < bestCost)
            {
                bestCost = candidateCost;
                bestCandidate = candidateIndex;
            }
        }

        if (double.IsPositiveInfinity(bestCost))
        {
            throw new InvalidOperationException("PulsarPlanner: no valid 2048-context pattern.");
        }

        var stateSequence = new int[totalSegments];
        for (int contextIndex = lastContext; contextIndex >= 0; contextIndex--)
        {
            var pattern = candidateSets[contextIndex][bestCandidate];
            int startSegment = contextIndex * ContextSegmentCount;
            Array.Copy(pattern.SegmentStates, 0, stateSequence, startSegment, pattern.SegmentStates.Length);

            if (contextIndex > 0)
            {
                bestCandidate = previous[contextIndex][bestCandidate];
            }
        }

        return BuildPlansFromStateSequence(analyses, stateSequence);
    }

    private double ScorePattern(
        PulsarContextPattern pattern,
        IReadOnlyList<PulsarTransientAnalysis> analyses,
        int startSegment,
        int previousState,
        int previousDirection)
    {
        double totalCost = 0.0;
        int lastState = previousState;
        int lastDirection = previousDirection;

        for (int i = 0; i < pattern.SegmentStates.Length; i++)
        {
            int state = pattern.SegmentStates[i];
            var analysis = analyses[startSegment + i];
            int delta = state - lastState;
            int direction = DirFromDelta(delta);

            totalCost += TransitionCost(analysis, lastState, state, lastDirection, direction, delta);
            totalCost += StateCost(analysis, state);
            totalCost += PersistenceCost(analysis, lastState, state);
            totalCost += PreEchoCost(analysis, state);

            lastState = state;
            lastDirection = direction;
        }

        return totalCost + Math.Max(pattern.BlockStates.Length - 1, 0) * 0.03;
    }

    private List<PulsarFramePlan> BuildPlansFromStateSequence(
        IReadOnlyList<PulsarTransientAnalysis> analyses,
        IReadOnlyList<int> stateSequence)
    {
        var plans = new List<PulsarFramePlan>(stateSequence.Count);
        int previousBlockSize = BlockSteps[Math.Clamp(DefaultStateIndex, 0, StateCount - 1)];

        for (int segmentIndex = 0; segmentIndex < stateSequence.Count; segmentIndex++)
        {
            var analysis = analyses[segmentIndex];
            int stateIndex = stateSequence[segmentIndex];
            int blockSize = BlockSteps[stateIndex];
            int targetBlockSize = BlockSteps[(int)Math.Round(Math.Clamp(analysis.DesiredLadderPosition, 0, StateCount - 1))];
            int previousStateIndex = segmentIndex == 0 ? DefaultStateIndex : stateSequence[segmentIndex - 1];
            int directionIndex = DirFromDelta(stateIndex - previousStateIndex);

            plans.Add(new PulsarFramePlan
            {
                SegmentIndex = segmentIndex,
                PreviousBlockSize = previousBlockSize,
                BlockSize = blockSize,
                TargetBlockSize = targetBlockSize,
                Direction = DirFromIndex(directionIndex),
                TransientLevel = analysis.Level,
                AttackRatio = analysis.AttackRatio,
                PeakDeltaDb = analysis.PeakDeltaDb,
                AttackIndex = analysis.AttackIndex,
                EnergyModulation = analysis.EnergyModulation,
                CrestFactor = analysis.CrestFactor,
                LowBandRatio = analysis.LowBandRatio,
                HighBandRatio = analysis.HighBandRatio,
                SustainedHighBandRatio = analysis.SustainedHighBandRatio,
                DesiredLadderPosition = analysis.DesiredLadderPosition,
                ClueStrength = analysis.ClueStrength,
                PathCost = 0.0,
                Spectral = analysis.Spectral,
                PreEchoRisk = analysis.PreEchoRisk,
                SpectralFlux = analysis.SpectralFlux,
            });

            previousBlockSize = blockSize;
        }

        return plans;
    }

    private static int GetPatternTerminalDirection(PulsarContextPattern pattern)
    {
        if (pattern.SegmentStates.Length <= 1)
        {
            return DirHold;
        }

        return DirFromDelta(pattern.SegmentStates[^1] - pattern.SegmentStates[^2]);
    }

    private static IReadOnlyList<PulsarContextPattern> GetContextPatterns(int segmentCount)
    {
        return ContextPatternCache.GetOrAdd(segmentCount, BuildContextPatterns);
    }

    private static IReadOnlyList<PulsarContextPattern> BuildContextPatterns(int segmentCount)
    {
        var patterns = new List<PulsarContextPattern>();
        var segmentStates = new List<int>(segmentCount);
        var blockStates = new List<int>(segmentCount);
        BuildContextPatternsRecursive(segmentCount, segmentStates, blockStates, patterns);
        return patterns;
    }

    private static void BuildContextPatternsRecursive(
        int remainingSegments,
        List<int> segmentStates,
        List<int> blockStates,
        List<PulsarContextPattern> patterns)
    {
        if (remainingSegments == 0)
        {
            patterns.Add(new PulsarContextPattern
            {
                SegmentStates = segmentStates.ToArray(),
                BlockStates = blockStates.ToArray(),
            });
            return;
        }

        foreach (int state in ContextEligibleStates)
        {
            int coveredSegments = BlockSteps[state] / PulsarBlockLadder.ControlHopSize;
            if (coveredSegments > remainingSegments)
            {
                continue;
            }

            blockStates.Add(state);
            for (int i = 0; i < coveredSegments; i++)
            {
                segmentStates.Add(state);
            }

            BuildContextPatternsRecursive(
                remainingSegments - coveredSegments,
                segmentStates,
                blockStates,
                patterns);

            segmentStates.RemoveRange(segmentStates.Count - coveredSegments, coveredSegments);
            blockStates.RemoveAt(blockStates.Count - 1);
        }
    }

    private List<PulsarFramePlan> RunViterbi(List<PulsarTransientAnalysis> analyses)
    {
        int segCount = analyses.Count;
        double posInf = double.PositiveInfinity;

        // costs[seg, state, dir]   dir: 0=Down, 1=Hold, 2=Up
        var costs = new double[segCount, StateCount, 3];
        var prev = new int[segCount, StateCount, 3];

        // Initialize
        for (int s = 0; s < segCount; s++)
            for (int st = 0; st < StateCount; st++)
                for (int d = 0; d < 3; d++)
                {
                    costs[s, st, d] = posInf;
                    prev[s, st, d] = -1;
                }

        int defIdx = Math.Clamp(DefaultStateIndex, 0, StateCount - 1);

        // Seed first segment
        AccumulateTransitions(analyses[0], defIdx, DirHold, 0.0, 0, costs, prev);

        // Forward pass
        for (int s = 1; s < segCount; s++)
        {
            var analysis = analyses[s];
            for (int pst = 0; pst < StateCount; pst++)
                for (int pd = 0; pd < 3; pd++)
                {
                    double pc = costs[s - 1, pst, pd];
                    if (double.IsPositiveInfinity(pc)) continue;
                    AccumulateTransitions(analysis, pst, pd, pc, s, costs, prev);
                }
        }

        // Find best terminal state
        double bestCost = posInf;
        int bestSt = defIdx, bestDir = DirHold;
        for (int st = 0; st < StateCount; st++)
            for (int d = 0; d < 3; d++)
            {
                double c = costs[segCount - 1, st, d];
                if (c < bestCost) { bestCost = c; bestSt = st; bestDir = d; }
            }

        if (double.IsPositiveInfinity(bestCost))
            throw new InvalidOperationException("PulsarPlanner: no valid terminal state.");

        // Backtrack
        var stateSeq = new int[segCount];
        var dirSeq = new int[segCount];
        for (int s = segCount - 1; s >= 0; s--)
        {
            stateSeq[s] = bestSt;
            dirSeq[s] = bestDir;
            int enc = prev[s, bestSt, bestDir];
            if (s == 0 || enc < 0) break;
            bestSt = enc / 3;
            bestDir = enc % 3;
        }

        // ── Post-processing passes ──
        ApplyRunLengthConstraints(stateSeq, analyses);
        ApplyPreEchoSwitching(stateSeq, analyses);
        ApplyTransientBurstSwitching(stateSeq, analyses);
        EnforceAdjacentSteps(stateSeq, defIdx);
        SuppressIsolatedBlips(stateSeq, analyses);

        // ── Build output plans ──
        var plans = new List<PulsarFramePlan>(segCount);
        int prevBlockSize = BlockSteps[defIdx];

        for (int s = 0; s < segCount; s++)
        {
            var a = analyses[s];
            int si = stateSeq[s];
            int blockSize = BlockSteps[si];
            int targetBlockSize = BlockSteps[(int)Math.Round(Math.Clamp(a.DesiredLadderPosition, 0, StateCount - 1))];
            int prevSi = s == 0 ? defIdx : stateSeq[s - 1];
            int dirIdx = DirFromDelta(si - prevSi);

            plans.Add(new PulsarFramePlan
            {
                SegmentIndex = s,
                PreviousBlockSize = prevBlockSize,
                BlockSize = blockSize,
                TargetBlockSize = targetBlockSize,
                Direction = DirFromIndex(dirIdx),
                TransientLevel = a.Level,
                AttackRatio = a.AttackRatio,
                PeakDeltaDb = a.PeakDeltaDb,
                AttackIndex = a.AttackIndex,
                EnergyModulation = a.EnergyModulation,
                CrestFactor = a.CrestFactor,
                LowBandRatio = a.LowBandRatio,
                HighBandRatio = a.HighBandRatio,
                SustainedHighBandRatio = a.SustainedHighBandRatio,
                DesiredLadderPosition = a.DesiredLadderPosition,
                ClueStrength = a.ClueStrength,
                PathCost = BestCostAtState(costs, s, si),
                Spectral = a.Spectral,
                PreEchoRisk = a.PreEchoRisk,
                SpectralFlux = a.SpectralFlux,
            });

            prevBlockSize = blockSize;
        }

        return plans;
    }

    private void AccumulateTransitions(
        PulsarTransientAnalysis analysis,
        int prevSt, int prevDir, double prevCost,
        int seg, double[,,] costs, int[,,] prev)
    {
        // Allow transitions up to ±2 steps for faster response to transients
        int minSt = Math.Max(0, prevSt - 2);
        int maxSt = Math.Min(StateCount - 1, prevSt + 2);

        for (int st = minSt; st <= maxSt; st++)
        {
            int delta = st - prevSt;
            int dir = DirFromDelta(delta);

            double cost = prevCost
                + TransitionCost(analysis, prevSt, st, prevDir, dir, delta)
                + StateCost(analysis, st)
                + PersistenceCost(analysis, prevSt, st)
                + PreEchoCost(analysis, st);

            if (cost < costs[seg, st, dir])
            {
                costs[seg, st, dir] = cost;
                prev[seg, st, dir] = prevSt * 3 + prevDir;
            }
        }
    }

    // ── Transition cost ──
    private double TransitionCost(
        PulsarTransientAnalysis a,
        int prevSt, int curSt, int prevDir, int curDir, int delta)
    {
        double cost = 0;

        if (delta != 0)
        {
            double clueFactor = 1.0 - Math.Min(a.ClueStrength, 1.0);
            cost += _settings.SwitchPenalty + _settings.LowClueSwitchPenalty * clueFactor;

            // Multi-step jump penalty
            int absDelta = Math.Abs(delta);
            if (absDelta > 1)
                cost += _settings.MultiStepJumpPenalty * (absDelta - 1);

            // Bonus for switching when spectral flux is high (onset detected)
            if (a.SpectralFlux > 0.3 && delta < 0) // switching down on onset
                cost += _settings.SpectralFluxSwitchBonus; // negative = bonus
        }

        // Reversal penalty
        if (prevDir != DirHold && curDir != DirHold && prevDir != curDir)
            cost += _settings.ReversalPenalty;

        return cost;
    }

    // ── State cost: how appropriate is this block size for the current signal? ──
    private double StateCost(PulsarTransientAnalysis a, int stateIndex)
    {
        double distance = Math.Abs(stateIndex - a.DesiredLadderPosition);
        double cost = distance * _settings.DistancePenalty * Math.Max(a.ClueStrength, 0.35);

        int blockSize = BlockSteps[stateIndex];

        // ── Transient penalties for large blocks ──
        if (a.Level == PulsarTransientLevel.Hard && stateIndex > 0)
        {
            cost += stateIndex * _settings.HardTransientLargeBlockPenalty;
            // Bonus for smallest block on extreme transients
            if (stateIndex == 0 && IsExtremeHard(a.AttackRatio, a.EnergyModulation, a.CrestFactor))
                cost += _settings.HardTransientSmallestBlockBonus;
        }
        else if (a.Level == PulsarTransientLevel.Soft && stateIndex > 1)
        {
            cost += (stateIndex - 1) * _settings.SoftTransientLargeBlockPenalty;
        }

        // ── Calm penalties for small blocks ──
        if (a.EnergyModulation <= VeryCalmModulation && stateIndex < StateCount - 1)
        {
            cost += (StateCount - 1 - stateIndex) * _settings.VeryCalmSmallBlockPenalty;
        }
        else if (a.EnergyModulation <= CalmModulation && stateIndex < StateCount - 2)
        {
            cost += (StateCount - 2 - stateIndex) * _settings.CalmSmallBlockPenalty;
        }

        // ── Block-size-specific costs for non-transient ──
        if (a.Level == PulsarTransientLevel.None)
        {
            if (blockSize == 256)
                cost += _settings.Block256Penalty;
            else if (blockSize == 512)
                cost += _settings.Block512Penalty * 0.5;
            else if (blockSize == 16384)
            {
                // 16384 is great for tonal, bad for anything active
                if (a.EnergyModulation > CalmModulation)
                    cost += _settings.Block16384Penalty * 2.0;
            }
        }

        // ── Spectral-aware costs ──
        double bassWeight = a.Spectral.SubBass + a.Spectral.Bass;
        double highWeight = a.Spectral.Presence + a.Spectral.Brilliance;

        // Bass-heavy content in small blocks: bad frequency resolution
        if (bassWeight > 0.5 && blockSize <= 512)
            cost += _settings.BassHeavyLargeBlockPenalty * bassWeight;

        // Brilliant content in very large blocks: pre-echo risk
        if (highWeight > 0.2 && blockSize >= 8192)
            cost += _settings.BrilliantSmallBlockPenalty * highWeight;

        // Tonal content: bonus for large blocks
        if (a.Spectral.Flatness < 0.3 && blockSize >= 4096)
            cost += _settings.TonalLargeBlockBonus * (1.0 - a.Spectral.Flatness);

        // Noisy content: bonus for smaller blocks
        if (a.Spectral.Flatness > 0.6 && blockSize <= 2048)
            cost += _settings.NoisySmallBlockBonus * a.Spectral.Flatness;

        // High centroid (bright signal) → smaller blocks handle better
        if (a.Spectral.Centroid > 0.5 && blockSize <= 2048)
            cost += _settings.HighCentroidSmallBlockBonus * a.Spectral.Centroid;

        return cost;
    }

    // ── Persistence cost: penalty for holding the same block size ──
    private double PersistenceCost(PulsarTransientAnalysis a, int prevSt, int curSt)
    {
        if (prevSt != curSt) return 0;

        int blockSize = BlockSteps[curSt];
        return blockSize switch
        {
            256 => a.Level switch
            {
                PulsarTransientLevel.Hard when IsExtremeHard(a.AttackRatio, a.EnergyModulation, a.CrestFactor) => 0.18,
                PulsarTransientLevel.Hard => _settings.Block256HoldPenalty * 0.85,
                _ => _settings.Block256HoldPenalty,
            },
            512 => a.Level switch
            {
                PulsarTransientLevel.Hard => 0.12,
                PulsarTransientLevel.Soft => 0.22,
                _ => _settings.Block512HoldPenalty,
            },
            1024 => a.Level == PulsarTransientLevel.None ? _settings.Block1024HoldPenalty : 0,
            16384 => a.Level == PulsarTransientLevel.None ? _settings.Block16384HoldPenalty : 1.5,
            _ => 0,
        };
    }

    // ── Pre-echo cost: penalize large blocks when a transient is approaching ──
    private double PreEchoCost(PulsarTransientAnalysis a, int stateIndex)
    {
        if (a.PreEchoRisk <= 0.1) return 0;

        int blockSize = BlockSteps[stateIndex];
        double risk = a.PreEchoRisk;

        // Larger blocks have worse pre-echo artifacts
        if (blockSize >= 4096)
            return risk * _settings.PreEchoLargeBlockPenalty * (blockSize / 4096.0);

        if (blockSize >= 2048)
            return risk * _settings.PreEchoPenalty;

        // Small blocks: reduce pre-echo penalty (they handle it better)
        if (blockSize <= 512)
            return risk * _settings.PreEchoPenalty * -0.2; // bonus for being small

        return 0;
    }

    // ═════════════════════════════════════════════════════════════
    //  POST-PROCESSING
    // ═════════════════════════════════════════════════════════════

    // ── Run-length constraints per block size ──
    private void ApplyRunLengthConstraints(int[] states, IReadOnlyList<PulsarTransientAnalysis> analyses)
    {
        ApplyRunLimit(states, analyses, 0, _settings.Max256Run, 1);   // 256 → 512
        ApplyRunLimit(states, analyses, 1, _settings.Max512Run, 2);   // 512 → 1024
        ApplyRunLimit(states, analyses, 2, _settings.Max1024Run, 3);  // 1024 → 2048
        ApplyRunLimit(states, analyses, 3, _settings.Max2048Run, 4);  // 2048 → 4096
        ApplyRunLimit(states, analyses, 4, _settings.Max4096Run, 5);  // 4096 → 8192
        ApplyRunLimit(states, analyses, 5, _settings.Max8192Run, 6);  // 8192 → 16384
    }

    private static void ApplyRunLimit(int[] states, IReadOnlyList<PulsarTransientAnalysis> analyses,
        int targetState, int maxRun, int fallbackState)
    {
        if (fallbackState >= StateCount) fallbackState = StateCount - 1;
        int i = 0;
        while (i < states.Length)
        {
            if (states[i] != targetState) { i++; continue; }

            int start = i;
            while (i < states.Length && states[i] == targetState) i++;
            int runLen = i - start;

            // Allow longer runs if transient is active
            int effectiveMax = maxRun;
            for (int j = start; j < i; j++)
            {
                if (analyses[j].Level == PulsarTransientLevel.Hard)
                    effectiveMax = Math.Max(effectiveMax, maxRun + 2);
                else if (analyses[j].Level == PulsarTransientLevel.Soft)
                    effectiveMax = Math.Max(effectiveMax, maxRun + 1);
            }

            for (int j = start + effectiveMax; j < i; j++)
                states[j] = fallbackState;
        }
    }

    // ── Pre-echo switching: force small blocks before detected transients ──
    private void ApplyPreEchoSwitching(int[] states, IReadOnlyList<PulsarTransientAnalysis> analyses)
    {
        double threshold = _settings.PreEchoThreshold;

        for (int i = 0; i < analyses.Count; i++)
        {
            if (analyses[i].PreEchoRisk < threshold) continue;
            if (states[i] <= 3) continue; // already 2048 or smaller

            // Find how far ahead the transient is
            int transientDist = 0;
            for (int ahead = 1; ahead <= _settings.PreEchoLookahead && i + ahead < analyses.Count; ahead++)
            {
                if (analyses[i + ahead].Level != PulsarTransientLevel.None)
                {
                    transientDist = ahead;
                    break;
                }
            }

            if (transientDist == 0) continue;

            // Ramp down to smaller blocks leading up to transient
            // e.g., for dist=3: current → 4096, next → 2048, next → 1024
            int targetState = Math.Max(states[i] - transientDist, 2); // at least 1024
            for (int j = 0; j < transientDist && i + j < states.Length; j++)
            {
                int rampState = states[i] - (j + 1);
                if (rampState >= targetState)
                    states[i + j] = Math.Max(rampState, 0);
            }
        }
    }

    // ── Transient burst: V-shape switching for extreme transients ──
    private void ApplyTransientBurstSwitching(int[] states, IReadOnlyList<PulsarTransientAnalysis> analyses)
    {
        if (states.Length < 7) return;

        for (int i = 0; i < analyses.Count - 6; i++)
        {
            var a = analyses[i + 3];

            bool extremeHard = a.Level == PulsarTransientLevel.Hard
                && a.AttackIndex <= 1
                && a.AttackRatio >= 8.0
                && a.SpectralFlux > 0.3;

            if (!extremeHard) continue;

            // V-shape: ramp down to 256, then back up
            // ..., 4096, 2048, 1024, 512, 256, 512, 1024, 2048, 4096, ...
            int center = i + 3;
            int depth = 0; // 256

            // Ramp down
            for (int j = 3; j >= 0 && center - (3 - j) >= 0; j--)
            {
                int idx = center - (3 - j);
                states[idx] = Math.Min(j, StateCount - 1);
            }

            // Center
            states[center] = depth;

            // Ramp up
            for (int j = 1; j <= 3 && center + j < states.Length; j++)
            {
                states[center + j] = Math.Min(j, StateCount - 1);
            }

            i = center + 3; // skip past
        }
    }

    // ── Enforce max ±1 step between adjacent segments ──
    private static void EnforceAdjacentSteps(int[] states, int defaultState)
    {
        if (states.Length == 0) return;
        int prev = defaultState;
        for (int i = 0; i < states.Length; i++)
        {
            int delta = states[i] - prev;
            if (delta > 1) states[i] = prev + 1;
            else if (delta < -1) states[i] = prev - 1;
            prev = states[i];
        }
    }

    // ── Suppress single-segment "blips" that aren't justified ──
    private static void SuppressIsolatedBlips(int[] states, IReadOnlyList<PulsarTransientAnalysis> analyses)
    {
        if (states.Length < 3) return;
        for (int i = 1; i < states.Length - 1; i++)
        {
            int p = states[i - 1], c = states[i], n = states[i + 1];
            if (p != n || Math.Abs(c - p) != 1) continue;

            var a = analyses[i];
            bool justified = false;

            if (c < p) // dip down
            {
                justified = a.Level == PulsarTransientLevel.Hard && a.AttackIndex <= 1;
                justified |= a.Level == PulsarTransientLevel.Soft && a.AttackIndex <= 0;
                justified |= a.SpectralFlux > 0.5;
            }
            else // bump up
            {
                justified = a.Level == PulsarTransientLevel.None && a.EnergyModulation >= BusyModulation;
                justified |= a.Level == PulsarTransientLevel.Hard && a.AttackIndex <= 0;
            }

            if (!justified)
                states[i] = p;
        }
    }

    // ═════════════════════════════════════════════════════════════
    //  SMOOTHING & CLAMPING
    // ═════════════════════════════════════════════════════════════

    private void SmoothDesiredPositions(List<PulsarTransientAnalysis> analyses)
    {
        if (analyses.Count <= 1) return;

        double[] desired = new double[analyses.Count];
        for (int i = 0; i < analyses.Count; i++)
            desired[i] = analyses[i].DesiredLadderPosition;

        // Gaussian-ish smoothing
        for (int pass = 0; pass < _settings.AnalysisSmoothingPasses; pass++)
        {
            double[] smoothed = new double[desired.Length];
            for (int i = 0; i < desired.Length; i++)
            {
                double sum = desired[i] * 0.5;
                double weight = 0.5;
                if (i > 0) { sum += desired[i - 1] * 0.25; weight += 0.25; }
                if (i < desired.Length - 1) { sum += desired[i + 1] * 0.25; weight += 0.25; }
                smoothed[i] = sum / weight;
            }
            desired = smoothed;
        }

        // Clamp transients
        for (int i = 0; i < analyses.Count; i++)
        {
            var a = analyses[i];
            double clamped = desired[i];
            if (a.Level != PulsarTransientLevel.None)
                clamped = Math.Min(clamped, TransientClampPosition(a));

            a.DesiredLadderPosition = Math.Clamp(clamped, 0, StateCount - 1.0);
        }
    }

    private static double TransientClampPosition(PulsarTransientAnalysis a)
    {
        if (a.Level == PulsarTransientLevel.Hard)
        {
            return a.AttackIndex switch
            {
                <= 0 => 0.15,
                1 => 1.10,
                2 => 2.10,
                3 => 3.10,
                4 => 4.10,
                _ => 5.10,
            };
        }

        if (a.Level == PulsarTransientLevel.Soft)
        {
            return a.AttackIndex switch
            {
                <= 1 => 1.10,
                2 => 2.10,
                3 => 3.10,
                _ => 4.10,
            };
        }

        return StateCount - 1.0;
    }

    // ═════════════════════════════════════════════════════════════
    //  HELPERS
    // ═════════════════════════════════════════════════════════════

    private static double ComputeClueStrength(
        PulsarTransientLevel level, double attackRatio, int attackIndex,
        double energyModulation, double crestFactor,
        double sustainedHighBandRatio, double spectralFlux)
    {
        double attackWeight = GetAttackLeadWeight(level, attackIndex);
        double s = 0.35;
        s += Math.Min(Math.Max(attackRatio - 1.0, 0.0) / 8.0, 0.30) * attackWeight;
        s += Math.Min(energyModulation, 1.0) * 0.30;
        s += Math.Min(crestFactor / 8.0, 1.0) * 0.10;
        s += Math.Min(sustainedHighBandRatio / 0.4, 1.0) * 0.08;
        s += Math.Min(spectralFlux, 1.0) * 0.12; // spectral flux contributes to confidence

        if (level == PulsarTransientLevel.Hard)
            s += 0.20 * attackWeight;
        else if (level == PulsarTransientLevel.Soft)
            s += 0.10 * attackWeight;

        return Math.Clamp(s, 0.35, 1.30);
    }

    private static double ApplyAttackLead(PulsarTransientLevel level, double pos, int attackIndex)
    {
        if (attackIndex < 0) return pos;

        if (level == PulsarTransientLevel.Hard)
        {
            return attackIndex switch
            {
                0 => pos,
                1 => Math.Max(pos, 1.0),
                2 => Math.Max(pos, 2.0),
                3 => Math.Max(pos, 3.0),
                4 => Math.Max(pos, 4.0),
                _ => Math.Max(pos, 5.0),
            };
        }

        if (level == PulsarTransientLevel.Soft)
        {
            return attackIndex switch
            {
                <= 1 => pos,
                2 => Math.Max(pos, 2.0),
                3 => Math.Max(pos, 3.0),
                _ => Math.Max(pos, 4.0),
            };
        }

        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsExtremeHard(double attackRatio, double modulation, double crest)
    {
        return attackRatio >= ExtremeHardAttackRatio
            || (attackRatio >= HardTransientThreshold && modulation >= ExtremeHardModulation)
            || (attackRatio >= HardTransientThreshold && crest >= ExtremeHardCrest);
    }

    private static double GetAttackLeadWeight(PulsarTransientLevel level, int attackIndex)
    {
        if (level == PulsarTransientLevel.None || attackIndex < 0) return 1.0;
        return attackIndex switch
        {
            0 => 1.0,
            1 => 0.85,
            2 => 0.60,
            3 => 0.40,
            4 => 0.28,
            _ => 0.18,
        };
    }

    private static double BestCostAtState(double[,,] costs, int seg, int state)
    {
        double best = double.PositiveInfinity;
        for (int d = 0; d < 3; d++)
            best = Math.Min(best, costs[seg, state, d]);
        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DirFromDelta(int delta) => delta < 0 ? DirDown : delta > 0 ? DirUp : DirHold;

    private static PulsarSwitchDirection DirFromIndex(int dir) => dir switch
    {
        DirDown => PulsarSwitchDirection.Down,
        DirUp => PulsarSwitchDirection.Up,
        _ => PulsarSwitchDirection.Hold,
    };

    private static void FillFrame(float[] input, int offset, float[] frame)
    {
        Array.Clear(frame, 0, frame.Length);
        if (offset < input.Length)
        {
            int available = Math.Min(frame.Length, input.Length - offset);
            Array.Copy(input, offset, frame, 0, available);
        }
    }

    private static int NextPowerOfTwo(int v)
    {
        v--;
        v |= v >> 1; v |= v >> 2; v |= v >> 4;
        v |= v >> 8; v |= v >> 16;
        return Math.Max(v + 1, 2);
    }

    // ── Clone helpers to replace immutable fields ──
    private static PulsarTransientAnalysis CloneWithPreEchoRisk(PulsarTransientAnalysis a, double preEchoRisk) =>
        new()
        {
            SegmentIndex = a.SegmentIndex, Level = a.Level, AttackRatio = a.AttackRatio,
            PeakDeltaDb = a.PeakDeltaDb, AttackIndex = a.AttackIndex,
            EnergyModulation = a.EnergyModulation, CrestFactor = a.CrestFactor,
            LowBandRatio = a.LowBandRatio, HighBandRatio = a.HighBandRatio,
            SustainedHighBandRatio = a.SustainedHighBandRatio,
            DesiredLadderPosition = a.DesiredLadderPosition, ClueStrength = a.ClueStrength,
            Spectral = a.Spectral, PreEchoRisk = preEchoRisk, SpectralFlux = a.SpectralFlux,
        };

    private static PulsarTransientAnalysis CloneWithDesiredPosition(PulsarTransientAnalysis a, double desiredPos) =>
        new()
        {
            SegmentIndex = a.SegmentIndex, Level = a.Level, AttackRatio = a.AttackRatio,
            PeakDeltaDb = a.PeakDeltaDb, AttackIndex = a.AttackIndex,
            EnergyModulation = a.EnergyModulation, CrestFactor = a.CrestFactor,
            LowBandRatio = a.LowBandRatio, HighBandRatio = a.HighBandRatio,
            SustainedHighBandRatio = a.SustainedHighBandRatio,
            DesiredLadderPosition = desiredPos, ClueStrength = a.ClueStrength,
            Spectral = a.Spectral, PreEchoRisk = a.PreEchoRisk, SpectralFlux = a.SpectralFlux,
        };
}
