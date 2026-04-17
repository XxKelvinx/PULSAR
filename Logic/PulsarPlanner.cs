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
    public required int NextBlockSize { get; init; }
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

public static class PulsarPlannerOutputFormat
{
    public const int SchemaVersion = 2;

    public const string AnalysisCsvHeader = "segment_index,time_s,transient_level,attack_ratio,peak_delta_db,attack_index,energy_modulation,crest_factor,low_band_ratio,high_band_ratio,sustained_high_band_ratio,desired_ladder_position,clue_strength,pre_echo_risk,spectral_flux,sub_bass,bass,low_mid,mid,high_mid,presence,brilliance,centroid,flatness";
    public const string PlanCsvHeader = "segment_index,time_s,previous_block_size,block_size,next_block_size,target_block_size,direction,transient_level,pre_echo_risk,spectral_flux,clue_strength,desired_ladder_position";
    public const string RenderCsvHeader = "frame_index,start_time_s,segment_index,previous_block_size,block_size,next_block_size,target_block_size,direction,transient_level,pre_echo_risk,spectral_flux,clue_strength";
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
    public double ContextSplitPenalty { get; init; } = 0.16;
    public double ContextBlock256Penalty { get; init; } = 0.40;
    public double ContextBlock512Penalty { get; init; } = 0.10;
    public double ContextUnjustifiedSmallBlockPenalty { get; init; } = 1.10;
    public double ContextOverfragmentPenalty { get; init; } = 0.55;
    public double ContextPreferred2048Bonus { get; init; } = -0.18;
    public double ContextAttack256Bonus { get; init; } = -0.75;
    public double ContextAttack512Bonus { get; init; } = -0.22;
    public double ContextAttackMissPenalty { get; init; } = 1.15;
    public double LargeBlock4096PromotionThreshold { get; init; } = 3.75;
    public double LargeBlock8192PromotionThreshold { get; init; } = 4.55;
    public double LargeBlock16384PromotionThreshold { get; init; } = 5.40;
    public int Min4096BlockRun { get; init; } = 2;
    public int Min8192BlockRun { get; init; } = 2;
    public int Min16384BlockRun { get; init; } = 2;
    public double LargeBlockEntryPenalty { get; init; } = 0.18;
    public double LargeBlockExtensionBonus { get; init; } = 0.08;
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

internal readonly record struct LargeBlockRunDecision(
    int State,
    int ContextCount,
    double Gain);

public sealed class PulsarPlanner
{
    private const int LegacyRenderQuantum = PulsarBlockLadder.MinBlockSize / 2;

    // ── Full block ladder: 4 steps from 256 to 2048 ──
    private static readonly int[] BlockSteps = { 256, 512, 1024, 2048 };
    private static readonly int StateCount = BlockSteps.Length;
    private static readonly int DefaultStateIndex = Array.IndexOf(BlockSteps, PulsarBlockLadder.DefaultBlockSize);
    private static readonly int ContextSegmentCount = PulsarBlockLadder.DefaultBlockSize / PulsarBlockLadder.ControlHopSize;
    private static readonly int[] ContextEligibleStates = { 0, 1, 2, 3 };
    private static readonly int[] LargeBlockPromotionStates = Array.Empty<int>();
    private static readonly double[] TransientStateBias = { -0.58, -0.24, 0.08, 0.42 };
    private static readonly double[] BassStateBias = { 0.60, 0.28, 0.04, -0.24 };
    private static readonly double[] CalmStateBias = { 0.18, 0.08, 0.00, -0.09, -0.16, -0.22, -0.26 };

    private readonly PulsarPlannerSettings _settings;
    private readonly PulsarTransientDetector _transientDetector = new();

    public IReadOnlyList<PulsarTransientAnalysis> LastAnalyses { get; private set; } = Array.Empty<PulsarTransientAnalysis>();
    public IReadOnlyList<PulsarFramePlan> LastPlan { get; private set; } = Array.Empty<PulsarFramePlan>();
    public IReadOnlyList<PulsarFramePlan> LastLegacyRenderPlan { get; private set; } = Array.Empty<PulsarFramePlan>();

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

    // —— Planner tuning knobs: transient sensitivity / recovery / large-block regain ——
    private const double OpusTransientSoftThreshold = 0.24;
    private const double OpusTransientHardThreshold = 0.60;
    private const double OpusTransientOnsetCenter = 0.38;
    private const double OpusTransientOnsetRange = 0.38;
    private const double TransientFluxCenter = 0.12;
    private const double TransientFluxRange = 0.34;
    private const double TransientRatioCenter = 6.0;
    private const double TransientRatioRange = 7.0;
    private const double TransientModulationCenter = 0.09;
    private const double TransientModulationRange = 0.24;
    private const double TransientCrestCenter = 1.8;
    private const double TransientCrestRange = 1.8;
    private const double TransientBrightnessCenter = 0.015;
    private const double TransientBrightnessRange = 0.10;
    private const double OpusWeakTransientPenalty = 0.10;
    private const double OpusEarlyFluxBonus = 0.18;
    private const double OpusEarlyFluxTrigger = 0.48;

    private const double ShortBurstTrigger = 0.74;
    private const double ShortBurst256Trigger = 0.90;
    private const double ShortBurstPreEchoTrigger = 0.42;
    private const double ShortBurstFluxTrigger = 0.22;
    private const double ShortBurstAttackRatioTrigger = 9.0;

    private const double LateBurstTrigger = 0.62;
    private const double LateBurstAttackRatioTrigger = 8.5;
    private const double LateBurstFluxTrigger = 0.14;
    private const double LateBurstPreEchoTrigger = 0.30;

    private const double RecoveryStopTransientNeed = 0.34;
    private const double RecoveryBass2048Threshold = 0.64;
    private const double RecoveryBass4096Threshold = 0.88;

    private const double GapRecoveryTransientCeiling = 0.20;
    private const double GapRecoveryFluxCeiling = 0.14;
    private const double GapRecoveryPreEchoCeiling = 0.16;
    private const double GapRecovery2048BassThreshold = 0.60;
    private const double GapRecovery4096BassThreshold = 0.86;

    private const double ClampTransientFloor = 0.30;
    private const double Clamp2048BassThreshold = 0.82;
    private const double Clamp2048TransientCeiling = 0.24;

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
            LastLegacyRenderPlan = Array.Empty<PulsarFramePlan>();
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

    public List<PulsarFramePlan> PlanLegacyRenderSong(float[] input, Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        List<PulsarFramePlan> segmentPlans = PlanSong(input, progress);
        List<PulsarFramePlan> renderPlans = BuildLegacyRenderSchedule(segmentPlans, LastAnalyses, input.Length);
        LastLegacyRenderPlan = renderPlans;
        return renderPlans;
    }

    private static List<PulsarFramePlan> BuildLegacyRenderSchedule(
        IReadOnlyList<PulsarFramePlan> segmentPlans,
        IReadOnlyList<PulsarTransientAnalysis> analyses,
        int sampleCount)
    {
        if (segmentPlans.Count == 0 || sampleCount <= 0)
        {
            return new List<PulsarFramePlan>();
        }

        int totalQuanta = Math.Max(1, (sampleCount + LegacyRenderQuantum - 1) / LegacyRenderQuantum);
        int initialHopQuanta = Math.Max(1, (segmentPlans[0].BlockSize / 2) / LegacyRenderQuantum);
        int targetCoverageQuanta = totalQuanta + initialHopQuanta;
        int quantaPerSegment = Math.Max(1, PulsarBlockLadder.ControlHopSize / LegacyRenderQuantum);
        int timelineLength = Math.Max(targetCoverageQuanta, segmentPlans.Count * quantaPerSegment);
        int[] desiredBlockSizes = new int[timelineLength];

        for (int segmentIndex = 0; segmentIndex < segmentPlans.Count; segmentIndex++)
        {
            int blockSize = segmentPlans[segmentIndex].BlockSize;
            int quantumStart = segmentIndex * quantaPerSegment;
            for (int quantumOffset = 0; quantumOffset < quantaPerSegment && quantumStart + quantumOffset < desiredBlockSizes.Length; quantumOffset++)
            {
                desiredBlockSizes[quantumStart + quantumOffset] = blockSize;
            }
        }

        int lastDesiredBlockSize = segmentPlans[^1].BlockSize;
        for (int quantumIndex = 0; quantumIndex < desiredBlockSizes.Length; quantumIndex++)
        {
            if (desiredBlockSizes[quantumIndex] == 0)
            {
                desiredBlockSizes[quantumIndex] = lastDesiredBlockSize;
            }
        }

        var frameStartQuanta = new List<int>();
        var frameSizes = new List<int>();

        int cursorQuantum = 0;
        int? pendingBlockSize = null;
        while (cursorQuantum < targetCoverageQuanta)
        {
            int currentBlockSize = pendingBlockSize ?? desiredBlockSizes[Math.Min(cursorQuantum, desiredBlockSizes.Length - 1)];
            int runEndQuantum = cursorQuantum;
            while (runEndQuantum < desiredBlockSizes.Length && desiredBlockSizes[runEndQuantum] == currentBlockSize)
            {
                runEndQuantum++;
            }

            int remainingRunQuanta = Math.Max(1, runEndQuantum - cursorQuantum);
            int constantHopQuanta = GetLegacyRenderHopQuanta(currentBlockSize, currentBlockSize);
            int upcomingBlockSize = runEndQuantum < desiredBlockSizes.Length ? desiredBlockSizes[runEndQuantum] : currentBlockSize;
            bool approachingBoundary = remainingRunQuanta <= constantHopQuanta;
            int targetHopQuanta = approachingBoundary ? remainingRunQuanta : constantHopQuanta;
            int targetNextBlockSize = approachingBoundary ? upcomingBlockSize : currentBlockSize;

            bool isFinalFrame = cursorQuantum + targetHopQuanta >= targetCoverageQuanta;
            int nextBlockSize = isFinalFrame
                ? currentBlockSize
                : ChooseLegacyRenderNextBlockSize(currentBlockSize, targetNextBlockSize, targetHopQuanta);

            frameStartQuanta.Add(cursorQuantum);
            frameSizes.Add(currentBlockSize);

            int hopQuanta = GetLegacyRenderHopQuanta(currentBlockSize, nextBlockSize);
            cursorQuantum += hopQuanta;
            pendingBlockSize = nextBlockSize;
        }

        var renderPlans = new List<PulsarFramePlan>(frameSizes.Count);
        for (int frameIndex = 0; frameIndex < frameSizes.Count; frameIndex++)
        {
            int startQuantum = frameStartQuanta[frameIndex];
            int analysisSegmentIndex = Math.Clamp((startQuantum * LegacyRenderQuantum) / PulsarBlockLadder.ControlHopSize, 0, segmentPlans.Count - 1);
            PulsarFramePlan sourcePlan = segmentPlans[analysisSegmentIndex];
            PulsarTransientAnalysis analysis = analyses.Count > 0
                ? analyses[Math.Clamp(analysisSegmentIndex, 0, analyses.Count - 1)]
                : new PulsarTransientAnalysis
                {
                    SegmentIndex = analysisSegmentIndex,
                    Level = PulsarTransientLevel.None,
                    AttackRatio = 0.0,
                    PeakDeltaDb = 0.0,
                    AttackIndex = 0,
                    EnergyModulation = 0.0,
                    CrestFactor = 0.0,
                    LowBandRatio = 0.0,
                    HighBandRatio = 0.0,
                    SustainedHighBandRatio = 0.0,
                    DesiredLadderPosition = 0.0,
                    ClueStrength = 0.0,
                    Spectral = new SpectralProfile(),
                    PreEchoRisk = 0.0,
                    SpectralFlux = 0.0,
                };

            int currentBlockSize = frameSizes[frameIndex];
            int previousBlockSize = frameIndex == 0 ? currentBlockSize : frameSizes[frameIndex - 1];
            int nextBlockSize = frameIndex == frameSizes.Count - 1 ? currentBlockSize : frameSizes[frameIndex + 1];
            int previousStateIndex = Array.IndexOf(BlockSteps, previousBlockSize);
            int currentStateIndex = Array.IndexOf(BlockSteps, currentBlockSize);

            renderPlans.Add(new PulsarFramePlan
            {
                SegmentIndex = analysisSegmentIndex,
                PreviousBlockSize = previousBlockSize,
                BlockSize = currentBlockSize,
                NextBlockSize = nextBlockSize,
                TargetBlockSize = sourcePlan.TargetBlockSize,
                Direction = DirFromIndex(DirFromDelta(currentStateIndex - previousStateIndex)),
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
                PathCost = sourcePlan.PathCost,
                Spectral = analysis.Spectral,
                PreEchoRisk = analysis.PreEchoRisk,
                SpectralFlux = analysis.SpectralFlux,
            });
        }

        return renderPlans;
    }

    private static int ChooseLegacyRenderNextBlockSize(int currentBlockSize, int targetNextBlockSize, int targetHopQuanta)
    {
        int currentStateIndex = Array.IndexOf(BlockSteps, currentBlockSize);
        int targetNextStateIndex = Array.IndexOf(BlockSteps, targetNextBlockSize);
        int bestBlockSize = currentBlockSize;
        double bestScore = double.PositiveInfinity;

        foreach (int candidateBlockSize in BlockSteps)
        {
            int hopQuanta = GetLegacyRenderHopQuanta(currentBlockSize, candidateBlockSize);
            int candidateStateIndex = Array.IndexOf(BlockSteps, candidateBlockSize);
            double score = (Math.Abs(hopQuanta - targetHopQuanta) * 8.0)
                + (Math.Abs(candidateStateIndex - targetNextStateIndex) * 2.5)
                + (Math.Abs(candidateStateIndex - currentStateIndex) * 0.35);

            if (score < bestScore)
            {
                bestScore = score;
                bestBlockSize = candidateBlockSize;
            }
        }

        return bestBlockSize;
    }

    private static int GetLegacyRenderHopQuanta(int currentBlockSize, int nextBlockSize)
    {
        int rightOverlap = Math.Min(currentBlockSize / 2, nextBlockSize / 2);
        int hopSize = currentBlockSize - rightOverlap;
        return Math.Max(1, hopSize / LegacyRenderQuantum);
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
        var level = ClassifyTransientLevel(
            isTransient,
            weakTransient,
            tfEstimate,
            attackRatio,
            attackIndex,
            energyModulation,
            crestFactor,
            highBandRatio,
            sustainedHighBandRatio,
            spectral,
            spectralFlux);

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
            var fastLevel = ClassifyTransientLevel(
                isTransient,
                weakTransient,
                tfEstimate,
                attackRatio,
                attackIndex,
                energyModulation,
                crestFactor,
                highBandRatio,
                highBandRatio,
                new SpectralProfile(),
                0.0);
            return new SegmentAnalysisData(
                fastLevel,
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
        var level = ClassifyTransientLevel(
            isTransient,
            weakTransient,
            tfEstimate,
            attackRatio,
            attackIndex,
            energyModulation,
            crestFactor,
            highBandRatio,
            highBandRatio,
            spectral,
            0.0);

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

                // Also detect upcoming spectral flux spikes (pre-echo precursor)
                if (future.SpectralFlux > 0.3 && future.Level == PulsarTransientLevel.None)
                {
                    double distanceFade = 1.0 / ahead;
                    maxRisk = Math.Max(maxRisk, distanceFade * future.SpectralFlux * 0.4);
                }
            }

            // Also consider high spectral flux as pre-echo indicator
            var current = analyses[i];
            if (current.SpectralFlux > 0.4 && current.Level == PulsarTransientLevel.None)
            {
                maxRisk = Math.Max(maxRisk, current.SpectralFlux * 0.6);
            }

            // Energy modulation rising sharply also signals pre-echo risk
            if (i > 0 && current.EnergyModulation > 0.35)
            {
                double prevMod = analyses[i - 1].EnergyModulation;
                double modDelta = current.EnergyModulation - prevMod;
                if (modDelta > 0.15)
                {
                    maxRisk = Math.Max(maxRisk, Math.Clamp(modDelta * 1.2, 0, 0.8));
                }
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
        double classWeight = level switch
        {
            PulsarTransientLevel.Hard => 1.0,
            PulsarTransientLevel.Soft => 0.55,
            _ => 0.0,
        };
        double attackLead = attackIndex switch
        {
            <= 0 => 1.0,
            1 => 0.92,
            2 => 0.80,
            3 => 0.64,
            4 => 0.48,
            _ => 0.30,
        };

        double fluxNeed = Math.Clamp((spectralFlux - 0.18) / 0.44, 0.0, 1.0);
        double ratioNeed = Math.Clamp((attackRatio - 3.2) / 8.8, 0.0, 1.0);
        double crestNeed = Math.Clamp((crestFactor - 1.55) / 2.00, 0.0, 1.0);
        double brightnessNeed = Math.Clamp((highBandRatio - 0.018) / 0.13, 0.0, 1.0);
        double transientNeed = (0.34 * classWeight * attackLead)
            + (0.24 * fluxNeed)
            + (0.18 * ratioNeed)
            + (0.10 * crestNeed)
            + (0.14 * brightnessNeed);
        transientNeed = Math.Clamp(transientNeed, 0.0, 1.0);

        double bassNeed = Math.Clamp(
            (spectral.SubBass * 1.45)
            + (spectral.Bass * 1.15)
            + (spectral.LowMid * 0.40),
            0.0,
            1.45);
        double tonalNeed = Math.Clamp((0.68 - spectral.Flatness) / 0.68, 0.0, 1.0);
        double calmNeed = Math.Clamp((0.28 - energyModulation) / 0.28, 0.0, 1.0);
        double darkNeed = Math.Clamp((0.055 - highBandRatio) / 0.055, 0.0, 1.0);
        double stableNeed = Math.Clamp(
            (0.46 * Math.Clamp(bassNeed, 0.0, 1.0))
            + (0.22 * tonalNeed)
            + (0.18 * calmNeed)
            + (0.14 * darkNeed),
            0.0,
            1.0);

        double sustainedBrightnessPenalty = Math.Clamp((sustainedHighBandRatio - 0.030) / 0.16, 0.0, 1.0);
        double basePosition = 3.0
            + (stableNeed * 2.75)
            - (transientNeed * 3.45)
            - (sustainedBrightnessPenalty * 0.60);

        if (transientNeed >= 0.88)
        {
            basePosition = Math.Min(basePosition, attackIndex <= 1 ? 0.15 : 0.85);
        }
        else if (transientNeed >= 0.72)
        {
            basePosition = Math.Min(basePosition, attackIndex <= 2 ? 1.05 : 1.70);
        }
        else if (transientNeed >= 0.54)
        {
            basePosition = Math.Min(basePosition, 2.25);
        }

        if (stableNeed >= 0.92 && transientNeed <= 0.18)
        {
            basePosition = Math.Max(basePosition, 5.55);
        }
        else if (stableNeed >= 0.76 && transientNeed <= 0.28)
        {
            basePosition = Math.Max(basePosition, 4.20);
        }
        else if (stableNeed >= 0.56 && transientNeed <= 0.36)
        {
            basePosition = Math.Max(basePosition, 3.10);
        }

        if (spectralFlux <= 0.08 && energyModulation <= 0.10 && bassNeed >= 0.78)
        {
            basePosition = Math.Max(basePosition, 5.10);
        }

        // Promote to largest blocks when the signal is very calm and tonal (high frequency resolution benefits)
        if (spectralFlux <= 0.05 && energyModulation <= 0.06 && tonalNeed >= 0.65 && transientNeed <= 0.10)
        {
            basePosition = Math.Max(basePosition, 5.80);
        }

        // Pull down from large blocks when pre-echo risk is elevated via flux
        if (spectralFlux >= 0.25 && transientNeed >= 0.30)
        {
            basePosition = Math.Min(basePosition, 3.5 - (transientNeed * 2.0));
        }

        return Math.Clamp(basePosition, 0.0, StateCount - 1.0);
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

        ApplyPreEchoSwitching(stateSequence, analyses);
        ApplyTransientBurstSwitching(stateSequence, analyses);
        ApplyLateAttackShortBursts(stateSequence, analyses);
        ApplyRecoveryPromotion(stateSequence, analyses);
        ApplyGapRecoveryPromotion(stateSequence, analyses);
        EnforceAdjacentSteps(stateSequence, DefaultStateIndex);
        SuppressIsolatedBlips(stateSequence, analyses);
        PromoteLargeBlockRuns(analyses, stateSequence);
        EnforceAdjacentSteps(stateSequence, DefaultStateIndex);
        SuppressIsolatedBlips(stateSequence, analyses);
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

        return totalCost + ComputePatternStructureCost(pattern, analyses, startSegment);
    }

    private double ComputePatternStructureCost(
        PulsarContextPattern pattern,
        IReadOnlyList<PulsarTransientAnalysis> analyses,
        int startSegment)
    {
        double cost = Math.Max(pattern.BlockStates.Length - 1, 0) * _settings.ContextSplitPenalty;

        int segmentOffset = 0;
        int smallBlockCount = 0;
        int block256Count = 0;

        foreach (int state in pattern.BlockStates)
        {
            int coveredSegments = BlockSteps[state] / PulsarBlockLadder.ControlHopSize;
            double urgency = ComputeBlockUrgency(analyses, startSegment + segmentOffset, coveredSegments);
            double desiredPosition = ComputeAverageDesiredPosition(analyses, startSegment + segmentOffset, coveredSegments);
            double attackNeed = ComputeAttackWindowNeed(analyses, startSegment + segmentOffset, coveredSegments);

            if (state == 0)
            {
                cost += _settings.ContextBlock256Penalty;
                cost += (1.0 - urgency) * _settings.ContextUnjustifiedSmallBlockPenalty;
                cost += _settings.ContextAttack256Bonus * attackNeed;
                smallBlockCount++;
                block256Count++;
            }
            else if (state == 1)
            {
                cost += _settings.ContextBlock512Penalty;
                cost += (1.0 - urgency) * _settings.ContextUnjustifiedSmallBlockPenalty * 0.22;
                cost += _settings.ContextAttack512Bonus * attackNeed;
                smallBlockCount++;
            }
            else if (state == 2 && urgency < 0.18)
            {
                cost += (0.18 - urgency) * 0.18;
            }
            else if (state == 2 && desiredPosition >= 3.0 && urgency < 0.24)
            {
                cost += (desiredPosition - 3.0) * 0.28;
            }
            else if (state == 3)
            {
                if (urgency < 0.22 && desiredPosition >= 3.0)
                {
                    double calmWeight = Math.Clamp((0.22 - urgency) / 0.22, 0.0, 1.0);
                    double desiredWeight = Math.Clamp((desiredPosition - 3.0) / 1.6, 0.0, 1.0);
                    cost += _settings.ContextPreferred2048Bonus * (0.55 + (0.45 * Math.Max(calmWeight, desiredWeight)));
                }

                if (desiredPosition >= 3.8 && urgency < 0.16)
                {
                    cost -= 0.05;
                }
            }

            if (attackNeed > 0.48 && state >= 2)
            {
                double sizePenalty = state switch
                {
                    2 => 0.55,
                    3 => 0.90,
                    _ => 1.15,
                };

                cost += _settings.ContextAttackMissPenalty * (attackNeed - 0.48) * sizePenalty;
            }

            segmentOffset += coveredSegments;
        }

        if (smallBlockCount >= 3)
        {
            cost += (smallBlockCount - 2) * _settings.ContextOverfragmentPenalty * 0.5;
        }

        if (block256Count >= 2)
        {
            cost += (block256Count - 1) * _settings.ContextOverfragmentPenalty;
        }

        return cost;
    }

    private static double ComputeLargeBlockTransientRelief(PulsarTransientAnalysis a)
    {
        double bassDominance = Math.Clamp((a.LowBandRatio - 0.92) / 0.08, 0.0, 1.0);
        double highQuietness = Math.Clamp((0.03 - a.HighBandRatio) / 0.03, 0.0, 1.0);
        double steadyModulation = Math.Clamp((0.26 - a.EnergyModulation) / 0.26, 0.0, 1.0);
        double tonalWeight = Math.Clamp((0.65 - a.Spectral.Flatness) / 0.65, 0.0, 1.0);
        double lateAttack = Math.Clamp((a.AttackIndex - 1.0) / 5.0, 0.0, 1.0);

        double relief = (0.34 * bassDominance)
            + (0.22 * highQuietness)
            + (0.20 * steadyModulation)
            + (0.14 * tonalWeight)
            + (0.10 * lateAttack);

        if (a.SustainedHighBandRatio < 0.02)
        {
            relief += 0.08;
        }

        return Math.Clamp(relief, 0.0, 1.0);
    }

    private static double ComputePerAnalysisTransientNeed(PulsarTransientAnalysis analysis)
    {
        double attackLead = analysis.AttackIndex switch
        {
            <= 0 => 1.0,
            1 => 0.92,
            2 => 0.72,
            3 => 0.44,
            4 => 0.22,
            _ => 0.10,
        };

        double levelWeight = analysis.Level switch
        {
            PulsarTransientLevel.Hard => 0.42,
            PulsarTransientLevel.Soft => 0.18,
            _ => 0.0,
        };

        double fluxNeed = Math.Clamp((analysis.SpectralFlux - 0.26) / 0.42, 0.0, 1.0);
        double preEchoNeed = Math.Clamp((analysis.PreEchoRisk - 0.18) / 0.52, 0.0, 1.0);
        double ratioNeed = Math.Clamp((analysis.AttackRatio - 6.5) / 7.5, 0.0, 1.0);
        double brightnessNeed = Math.Clamp((analysis.HighBandRatio - 0.02) / 0.14, 0.0, 1.0);
        double modulationNeed = Math.Clamp((analysis.EnergyModulation - 0.09) / 0.28, 0.0, 1.0);
        double bassShield = Math.Clamp(
            ((analysis.Spectral.SubBass * 1.35) + (analysis.Spectral.Bass * 1.05) - (analysis.HighBandRatio * 2.2)),
            0.0,
            1.0);

        double need = (0.18 * levelWeight * attackLead)
            + (0.26 * fluxNeed)
            + (0.23 * preEchoNeed)
            + (0.12 * ratioNeed)
            + (0.11 * brightnessNeed)
            + (0.16 * modulationNeed);

        need *= 1.0 - ((0.42 - (0.28 * modulationNeed)) * bassShield);

        if (analysis.PreEchoRisk >= 0.72 && analysis.AttackIndex <= 1)
        {
            need = Math.Max(need, 0.92);
        }
        else if (analysis.PreEchoRisk >= 0.55 && analysis.AttackIndex <= 2)
        {
            need = Math.Max(need, 0.78);
        }

        return Math.Clamp(need, 0.0, 1.0);
    }

    private static double ComputeBassResolutionNeed(PulsarTransientAnalysis analysis)
    {
        double bassEnergy = Math.Clamp(
            (analysis.Spectral.SubBass * 1.35)
            + (analysis.Spectral.Bass * 1.10)
            + (analysis.Spectral.LowMid * 0.40),
            0.0,
            1.4);
        double tonalWeight = Math.Clamp((0.72 - analysis.Spectral.Flatness) / 0.72, 0.0, 1.0);
        double calmWeight = Math.Clamp((0.30 - analysis.EnergyModulation) / 0.30, 0.0, 1.0);
        double darkWeight = Math.Clamp((0.06 - analysis.HighBandRatio) / 0.06, 0.0, 1.0);

        double need = (0.52 * Math.Clamp(bassEnergy, 0.0, 1.0))
            + (0.20 * tonalWeight)
            + (0.18 * calmWeight)
            + (0.10 * darkWeight);

        return Math.Clamp(need, 0.0, 1.0);
    }

    private static double ComputeAttackWindowNeed(
        IReadOnlyList<PulsarTransientAnalysis> analyses,
        int startSegment,
        int coveredSegments)
    {
        double bestNeed = 0.0;

        for (int i = 0; i < coveredSegments; i++)
        {
            var analysis = analyses[startSegment + i];

            bestNeed = Math.Max(bestNeed, ComputePerAnalysisTransientNeed(analysis));
        }

        return Math.Clamp(bestNeed, 0.0, 1.0);
    }

    private static double ComputeBlockUrgency(
        IReadOnlyList<PulsarTransientAnalysis> analyses,
        int startSegment,
        int coveredSegments)
    {
        double maxUrgency = 0.0;

        for (int i = 0; i < coveredSegments; i++)
        {
            var analysis = analyses[startSegment + i];

            double modulationUrgency = Math.Clamp((analysis.EnergyModulation - 0.10) / 0.28, 0.0, 1.0);
            double fluxUrgency = Math.Clamp((analysis.SpectralFlux - 0.10) / 0.35, 0.0, 1.0);
            double clueUrgency = Math.Clamp((analysis.ClueStrength - 0.55) / 0.40, 0.0, 1.0);
            double attackUrgency = Math.Clamp((analysis.AttackRatio - HardTransientThreshold) / 4.0, 0.0, 1.0);

            double urgency = 0.50 * modulationUrgency
                + 0.25 * fluxUrgency
                + 0.15 * clueUrgency
                + 0.10 * attackUrgency;

            if (analysis.Level == PulsarTransientLevel.Hard && analysis.AttackIndex <= 1)
            {
                urgency = Math.Max(urgency, 0.85);
            }
            else if (analysis.Level == PulsarTransientLevel.Soft && analysis.AttackIndex <= 1)
            {
                urgency = Math.Max(urgency, 0.60);
            }

            maxUrgency = Math.Max(maxUrgency, urgency);
        }

        return Math.Clamp(maxUrgency, 0.0, 1.0);
    }

    private static double ComputeAverageDesiredPosition(
        IReadOnlyList<PulsarTransientAnalysis> analyses,
        int startSegment,
        int coveredSegments)
    {
        double total = 0.0;
        for (int i = 0; i < coveredSegments; i++)
        {
            total += analyses[startSegment + i].DesiredLadderPosition;
        }

        return coveredSegments > 0 ? total / coveredSegments : 0.0;
    }

    private void PromoteLargeBlockRuns(
        IReadOnlyList<PulsarTransientAnalysis> analyses,
        int[] stateSequence)
    {
        if (stateSequence.Length < ContextSegmentCount * 2)
        {
            return;
        }

        int contextCount = stateSequence.Length / ContextSegmentCount;
        if (contextCount <= 0)
        {
            return;
        }

        var promotableContexts = new bool[contextCount];
        var contextDesired = new double[contextCount];
        var contextUrgency = new double[contextCount];
        var contextBassTone = new double[contextCount];
        var contextTransientNeed = new double[contextCount];
        var contextTransientRelief = new double[contextCount];

        for (int contextIndex = 0; contextIndex < contextCount; contextIndex++)
        {
            int startSegment = contextIndex * ContextSegmentCount;
            bool all2048 = true;

            for (int i = 0; i < ContextSegmentCount; i++)
            {
                if (stateSequence[startSegment + i] != 3)
                {
                    all2048 = false;
                    break;
                }
            }

            promotableContexts[contextIndex] = all2048;
            if (!all2048)
            {
                continue;
            }

            contextDesired[contextIndex] = ComputeAverageDesiredPosition(analyses, startSegment, ContextSegmentCount);
            contextUrgency[contextIndex] = ComputeBlockUrgency(analyses, startSegment, ContextSegmentCount);

            double bassToneTotal = 0.0;
            double reliefTotal = 0.0;
            double transientNeedMax = 0.0;
            for (int i = 0; i < ContextSegmentCount; i++)
            {
                var analysis = analyses[startSegment + i];
                var spectral = analysis.Spectral;
                bassToneTotal += spectral.SubBass + spectral.Bass + (0.6 * spectral.LowMid) + ((1.0 - spectral.Flatness) * 0.35);
                reliefTotal += ComputeLargeBlockTransientRelief(analysis);
                transientNeedMax = Math.Max(transientNeedMax, ComputePerAnalysisTransientNeed(analysis));
            }

            contextBassTone[contextIndex] = bassToneTotal / ContextSegmentCount;
            contextTransientNeed[contextIndex] = transientNeedMax;
            contextTransientRelief[contextIndex] = reliefTotal / ContextSegmentCount;
        }

        var contiguousPromotableContexts = new int[contextCount];
        for (int contextIndex = contextCount - 1; contextIndex >= 0; contextIndex--)
        {
            if (!promotableContexts[contextIndex])
            {
                contiguousPromotableContexts[contextIndex] = 0;
                continue;
            }

            contiguousPromotableContexts[contextIndex] = 1;
            if (contextIndex + 1 < contextCount)
            {
                contiguousPromotableContexts[contextIndex] += contiguousPromotableContexts[contextIndex + 1];
            }
        }

        var bestGain = new double[contextCount + 1];
        var bestDecision = new LargeBlockRunDecision[contextCount];

        for (int contextStart = contextCount - 1; contextStart >= 0; contextStart--)
        {
            bestGain[contextStart] = bestGain[contextStart + 1];

            int availableContexts = contiguousPromotableContexts[contextStart];
            if (availableContexts == 0)
            {
                continue;
            }

            foreach (int state in LargeBlockPromotionStates)
            {
                int contextsPerBlock = BlockSteps[state] / PulsarBlockLadder.DefaultBlockSize;
                int minRunBlocks = GetMinimumLargeBlockRunBlocks(state);
                int minRunContexts = contextsPerBlock * minRunBlocks;
                int maxRunContexts = GetMaximumPromotableRunContexts(state, availableContexts);
                if (contextsPerBlock <= 1 || maxRunContexts < minRunContexts)
                {
                    continue;
                }

                double desiredTotal = 0.0;
                double bassToneTotal = 0.0;
                double reliefTotal = 0.0;
                double maxUrgency = 0.0;
                double maxTransientNeed = 0.0;

                for (int runContexts = 1; runContexts <= maxRunContexts; runContexts++)
                {
                    int contextIndex = contextStart + runContexts - 1;
                    desiredTotal += contextDesired[contextIndex];
                    bassToneTotal += contextBassTone[contextIndex];
                    reliefTotal += contextTransientRelief[contextIndex];
                    maxUrgency = Math.Max(maxUrgency, contextUrgency[contextIndex]);
                    maxTransientNeed = Math.Max(maxTransientNeed, contextTransientNeed[contextIndex]);

                    if (runContexts < minRunContexts || runContexts % contextsPerBlock != 0)
                    {
                        continue;
                    }

                    int runBlocks = runContexts / contextsPerBlock;
                    double averageDesired = desiredTotal / runContexts;
                    double averageBassTone = bassToneTotal / runContexts;
                    double averageRelief = reliefTotal / runContexts;

                    if (!ShouldPromoteLargeBlock(state, averageDesired, maxUrgency, averageBassTone, maxTransientNeed, runBlocks))
                    {
                        continue;
                    }

                    double runGain = ComputeLargeBlockRunGain(
                        state,
                        averageDesired,
                        maxUrgency,
                        averageBassTone,
                        averageRelief,
                        maxTransientNeed,
                        runBlocks);
                    if (double.IsNegativeInfinity(runGain))
                    {
                        continue;
                    }

                    double totalGain = runGain + bestGain[contextStart + runContexts];
                    if (totalGain > bestGain[contextStart] + 1e-9)
                    {
                        bestGain[contextStart] = totalGain;
                        bestDecision[contextStart] = new LargeBlockRunDecision(state, runContexts, runGain);
                    }
                }
            }
        }

        for (int contextStart = 0; contextStart < contextCount;)
        {
            LargeBlockRunDecision decision = bestDecision[contextStart];
            if (decision.ContextCount <= 0 || decision.Gain <= 0.0 || bestGain[contextStart] <= bestGain[contextStart + 1] + 1e-9)
            {
                contextStart++;
                continue;
            }

            int segmentStart = contextStart * ContextSegmentCount;
            int segmentLength = decision.ContextCount * ContextSegmentCount;
            for (int i = 0; i < segmentLength; i++)
            {
                stateSequence[segmentStart + i] = decision.State;
            }

            contextStart += decision.ContextCount;
        }
    }

    private bool ShouldPromoteLargeBlock(
        int state,
        double averageDesired,
        double maxUrgency,
        double averageBassTone,
        double maxTransientNeed,
        int runBlocks)
    {
        if (runBlocks < GetMinimumLargeBlockRunBlocks(state))
        {
            return false;
        }

        return state switch
        {
            4 => averageDesired >= _settings.LargeBlock4096PromotionThreshold
                && maxUrgency <= 0.22
                && maxTransientNeed <= 0.20
                && averageBassTone >= 0.30,
            5 => averageDesired >= _settings.LargeBlock8192PromotionThreshold
                && maxUrgency <= 0.14
                && maxTransientNeed <= 0.12
                && averageBassTone >= 0.34,
            6 => averageDesired >= _settings.LargeBlock16384PromotionThreshold
                && maxUrgency <= 0.10
                && maxTransientNeed <= 0.08
                && averageBassTone >= 0.38,
            _ => false,
        };
    }

    private int GetMinimumLargeBlockRunBlocks(int state)
    {
        return state switch
        {
            4 => _settings.Min4096BlockRun,
            5 => _settings.Min8192BlockRun,
            6 => _settings.Min16384BlockRun,
            _ => 1,
        };
    }

    private static int GetMaximumPromotableRunContexts(int state, int availableContexts)
    {
        int contextsPerBlock = BlockSteps[state] / PulsarBlockLadder.DefaultBlockSize;
        return availableContexts - (availableContexts % contextsPerBlock);
    }

    private double ComputeLargeBlockRunGain(
        int state,
        double averageDesired,
        double maxUrgency,
        double averageBassTone,
        double averageRelief,
        double maxTransientNeed,
        int runBlocks)
    {
        double desiredThreshold;
        double urgencyCeiling;
        double transientCeiling;
        double bassThreshold;
        double reliefThreshold;
        double stateBias;

        switch (state)
        {
            case 4:
                desiredThreshold = _settings.LargeBlock4096PromotionThreshold;
                urgencyCeiling = 0.22;
                transientCeiling = 0.20;
                bassThreshold = 0.30;
                reliefThreshold = 0.34;
                stateBias = 0.08;
                break;
            case 5:
                desiredThreshold = _settings.LargeBlock8192PromotionThreshold;
                urgencyCeiling = 0.14;
                transientCeiling = 0.12;
                bassThreshold = 0.34;
                reliefThreshold = 0.40;
                stateBias = 0.14;
                break;
            case 6:
                desiredThreshold = _settings.LargeBlock16384PromotionThreshold;
                urgencyCeiling = 0.10;
                transientCeiling = 0.08;
                bassThreshold = 0.38;
                reliefThreshold = 0.46;
                stateBias = 0.20;
                break;
            default:
                return double.NegativeInfinity;
        }

        double desiredMargin = averageDesired - desiredThreshold;
        double urgencyMargin = urgencyCeiling - maxUrgency;
        double transientMargin = transientCeiling - maxTransientNeed;
        double bassMargin = averageBassTone - bassThreshold;
        double reliefMargin = Math.Max(averageRelief - reliefThreshold, 0.0);
        if (desiredMargin < 0.0 || urgencyMargin < 0.0 || transientMargin < 0.0 || bassMargin < 0.0)
        {
            return double.NegativeInfinity;
        }

        double extensionBonus = Math.Max(runBlocks - GetMinimumLargeBlockRunBlocks(state), 0) * _settings.LargeBlockExtensionBonus;
        return (1.65 * desiredMargin)
            + (1.10 * urgencyMargin)
            + (1.05 * transientMargin)
            + (0.85 * bassMargin)
            + (0.55 * reliefMargin)
            + stateBias
            + extensionBonus
            - _settings.LargeBlockEntryPenalty;
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
            int stateIndex = Math.Clamp(stateSequence[segmentIndex], 0, StateCount - 1);
            int blockSize = BlockSteps[stateIndex];
            int targetBlockSize = BlockSteps[(int)Math.Round(Math.Clamp(analysis.DesiredLadderPosition, 0, StateCount - 1))];
            int previousStateIndex = segmentIndex == 0 ? DefaultStateIndex : stateSequence[segmentIndex - 1];
            int directionIndex = DirFromDelta(stateIndex - previousStateIndex);

            int nextBlockSize = segmentIndex == stateSequence.Count - 1 ? blockSize : BlockSteps[Math.Clamp(stateSequence[segmentIndex + 1], 0, StateCount - 1)];

            plans.Add(new PulsarFramePlan
            {
                SegmentIndex = segmentIndex,
                PreviousBlockSize = previousBlockSize,
                BlockSize = blockSize,
                NextBlockSize = nextBlockSize,
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
            int si = Math.Clamp(stateSeq[s], 0, StateCount - 1);
            int blockSize = BlockSteps[si];
            int targetBlockSize = BlockSteps[(int)Math.Round(Math.Clamp(a.DesiredLadderPosition, 0, StateCount - 1))];
            int prevSi = s == 0 ? defIdx : stateSeq[s - 1];
            int dirIdx = DirFromDelta(si - prevSi);

            int nextBlockSize = s == segCount - 1 ? blockSize : BlockSteps[Math.Clamp(stateSeq[s + 1], 0, StateCount - 1)];

            plans.Add(new PulsarFramePlan
            {
                SegmentIndex = s,
                PreviousBlockSize = prevBlockSize,
                BlockSize = blockSize,
                NextBlockSize = nextBlockSize,
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
        double largeBlockTransientRelief = blockSize >= 2048 ? ComputeLargeBlockTransientRelief(a) : 0.0;
        double transientNeed = ComputePerAnalysisTransientNeed(a);
        double bassNeed = ComputeBassResolutionNeed(a);
        double calmNeed = Math.Clamp((0.26 - a.EnergyModulation) / 0.26, 0.0, 1.0)
            * Math.Clamp(1.0 - transientNeed, 0.0, 1.0);

        cost += (transientNeed * TransientStateBias[stateIndex])
            + (bassNeed * BassStateBias[stateIndex])
            + (calmNeed * CalmStateBias[stateIndex]);

        double continuousTransientPenalty = transientNeed * transientNeed;
        if (stateIndex > 2)
        {
            double largeStatePenalty = (stateIndex - 2) * (0.82 + (0.58 * transientNeed));
            if (blockSize >= 2048)
            {
                largeStatePenalty *= 1.0 - (0.88 * largeBlockTransientRelief);
            }

            cost += largeStatePenalty * continuousTransientPenalty;
        }

        if (stateIndex == 0 && transientNeed >= 0.90)
        {
            cost += _settings.HardTransientSmallestBlockBonus;
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

        if (blockSize >= 1024)
            return risk * _settings.PreEchoPenalty * 0.6; // 1024 still has ~23ms pre-echo window

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
            double transientNeed = ComputePerAnalysisTransientNeed(a);
            bool burstCandidate = transientNeed >= ShortBurstTrigger
                && a.AttackIndex <= 2
                && (a.PreEchoRisk >= ShortBurstPreEchoTrigger
                    || a.SpectralFlux >= ShortBurstFluxTrigger
                    || a.AttackRatio >= ShortBurstAttackRatioTrigger);

            if (!burstCandidate) continue;

            // V-shape: ramp down to 256, then back up
            // ..., 4096, 2048, 1024, 512, 256, 512, 1024, 2048, 4096, ...
            int center = i + 3;
            int depth = transientNeed >= ShortBurst256Trigger ? 0 : 1; // 256 or 512

            // Ramp down
            for (int j = 3; j >= 0 && center - (3 - j) >= 0; j--)
            {
                int idx = center - (3 - j);
                states[idx] = Math.Min(states[idx], Math.Max(depth, Math.Min(j, StateCount - 1)));
            }

            // Center
            states[center] = Math.Min(states[center], depth);

            // Ramp up
            for (int j = 1; j <= 3 && center + j < states.Length; j++)
            {
                states[center + j] = Math.Min(states[center + j], Math.Max(depth, Math.Min(j, StateCount - 1)));
            }

            i = center + 3; // skip past
        }
    }

    private static void ApplyLateAttackShortBursts(int[] states, IReadOnlyList<PulsarTransientAnalysis> analyses)
    {
        if (states.Length < 4) return;

        for (int i = 1; i < analyses.Count - 2; i++)
        {
            var a = analyses[i];
            double transientNeed = ComputePerAnalysisTransientNeed(a);
            if (transientNeed < LateBurstTrigger) continue;
            if (a.AttackIndex < 3 || a.AttackIndex > 4) continue;
            if (a.AttackRatio < LateBurstAttackRatioTrigger) continue;
            if (a.SpectralFlux < LateBurstFluxTrigger && a.PreEchoRisk < LateBurstPreEchoTrigger) continue;

            bool localPeak = a.AttackRatio >= analyses[i - 1].AttackRatio
                && a.AttackRatio >= analyses[i + 1].AttackRatio;
            if (!localPeak) continue;

            int center = Math.Min(i + 1, states.Length - 1);

            if (center - 1 >= 0)
            {
                states[center - 1] = Math.Min(states[center - 1], 1);
            }

            states[center] = 0;

            if (center + 1 < states.Length)
            {
                states[center + 1] = Math.Min(states[center + 1], 1);
            }

            i = center + 1;
        }
    }

    private static void ApplyRecoveryPromotion(int[] states, IReadOnlyList<PulsarTransientAnalysis> analyses)
    {
        if (states.Length < 4) return;

        for (int i = 1; i < states.Length - 1; i++)
        {
            if (states[i] > 1 || states[i - 1] > 1)
            {
                continue;
            }

            for (int ahead = 1; ahead <= 4 && i + ahead < states.Length; ahead++)
            {
                int idx = i + ahead;
                var analysis = analyses[idx];
                double transientNeed = ComputePerAnalysisTransientNeed(analysis);
                double bassNeed = ComputeBassResolutionNeed(analysis);

                if (transientNeed > RecoveryStopTransientNeed)
                {
                    break;
                }

                int targetState = bassNeed switch
                {
                    >= RecoveryBass4096Threshold => 4,
                    >= RecoveryBass2048Threshold => 3,
                    _ => 2,
                };

                targetState = Math.Min(targetState, ahead switch
                {
                    1 => 1,
                    2 => 2,
                    3 => 3,
                    _ => 4,
                });

                targetState = Math.Min(targetState, StateCount - 1);
                states[idx] = Math.Max(states[idx], targetState);
            }
        }
    }

    private static void ApplyGapRecoveryPromotion(int[] states, IReadOnlyList<PulsarTransientAnalysis> analyses)
    {
        if (states.Length < 6) return;

        for (int i = 1; i < states.Length - 3; i++)
        {
            if (states[i - 1] > 2 && states[i] > 2)
            {
                continue;
            }

            bool quietGap = true;
            double bassNeedMax = 0.0;

            for (int j = 0; j < 3 && i + j < states.Length; j++)
            {
                var a = analyses[i + j];
                double transientNeed = ComputePerAnalysisTransientNeed(a);
                bassNeedMax = Math.Max(bassNeedMax, ComputeBassResolutionNeed(a));

                if (transientNeed > GapRecoveryTransientCeiling
                    || a.SpectralFlux > GapRecoveryFluxCeiling
                    || a.PreEchoRisk > GapRecoveryPreEchoCeiling)
                {
                    quietGap = false;
                    break;
                }
            }

            if (!quietGap)
            {
                continue;
            }

            int targetState = bassNeedMax switch
            {
                >= GapRecovery4096BassThreshold => 3,
                >= GapRecovery2048BassThreshold => 3,
                _ => 2,
            };

            for (int j = 0; j < 3 && i + j < states.Length; j++)
            {
                states[i + j] = Math.Max(states[i + j], targetState);
            }

            i += 2;
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
                double transientNeed = ComputePerAnalysisTransientNeed(a);
                justified = transientNeed >= 0.58;
                justified |= a.SpectralFlux > 0.38;
                justified |= a.PreEchoRisk > 0.34;
            }
            else // bump up
            {
                double bassNeed = ComputeBassResolutionNeed(a);
                justified = bassNeed >= 0.55 && ComputePerAnalysisTransientNeed(a) <= 0.28;
                justified |= a.Level == PulsarTransientLevel.None && a.EnergyModulation >= BusyModulation;
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
            if (ComputePerAnalysisTransientNeed(a) >= ClampTransientFloor)
                clamped = Math.Min(clamped, TransientClampPosition(a));

            a.DesiredLadderPosition = Math.Clamp(clamped, 0, StateCount - 1.0);
        }
    }

    private static double TransientClampPosition(PulsarTransientAnalysis a)
    {
        double transientNeed = ComputePerAnalysisTransientNeed(a);
        double bassNeed = ComputeBassResolutionNeed(a);

        if (bassNeed >= Clamp2048BassThreshold && transientNeed <= Clamp2048TransientCeiling)
        {
            return a.AttackIndex >= 4 ? 4.20 : 3.10;
        }

        if (transientNeed >= 0.88)
        {
            return a.AttackIndex <= 1 ? 0.15 : 0.85;
        }

        if (transientNeed >= 0.72)
        {
            return a.AttackIndex <= 2 ? 1.05 : 1.70;
        }

        if (transientNeed >= 0.56)
        {
            return 2.25;
        }

        if (transientNeed >= 0.36)
        {
            return 3.10;
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

    private static PulsarTransientLevel ClassifyTransientLevel(
        bool isTransient,
        bool weakTransient,
        float tfEstimate,
        double attackRatio,
        int attackIndex,
        double energyModulation,
        double crestFactor,
        double highBandRatio,
        double sustainedHighBandRatio,
        SpectralProfile spectral,
        double spectralFlux)
    {
        if (!isTransient)
        {
            return PulsarTransientLevel.None;
        }

        double onsetNeed = Math.Clamp((tfEstimate - OpusTransientOnsetCenter) / OpusTransientOnsetRange, 0.0, 1.0);
        double fluxNeed = Math.Clamp((spectralFlux - TransientFluxCenter) / TransientFluxRange, 0.0, 1.0);
        double ratioNeed = Math.Clamp((attackRatio - TransientRatioCenter) / TransientRatioRange, 0.0, 1.0);
        double modulationNeed = Math.Clamp((energyModulation - TransientModulationCenter) / TransientModulationRange, 0.0, 1.0);
        double crestNeed = Math.Clamp((crestFactor - TransientCrestCenter) / TransientCrestRange, 0.0, 1.0);
        double brightnessNeed = Math.Clamp((highBandRatio - TransientBrightnessCenter) / TransientBrightnessRange, 0.0, 1.0);
        double bassShield = Math.Clamp(
            (spectral.SubBass * 1.35) + (spectral.Bass * 1.05) + (spectral.LowMid * 0.35)
            - (highBandRatio * 1.8)
            - (sustainedHighBandRatio * 1.4),
            0.0,
            1.0);
        double lateAttackPenalty = attackIndex switch
        {
            <= 1 => 0.0,
            2 => 0.08,
            3 => 0.18,
            _ => 0.28,
        };

        double score = (0.34 * onsetNeed)
            + (0.18 * fluxNeed)
            + (0.14 * ratioNeed)
            + (0.16 * modulationNeed)
            + (0.10 * crestNeed)
            + (0.08 * brightnessNeed)
            - ((0.28 - (0.18 * modulationNeed)) * bassShield)
            - lateAttackPenalty;

        if (weakTransient)
        {
            score -= OpusWeakTransientPenalty;
        }

        if (spectralFlux >= OpusEarlyFluxTrigger && attackIndex <= 1)
        {
            score += OpusEarlyFluxBonus;
        }

        if (score >= OpusTransientHardThreshold)
        {
            return PulsarTransientLevel.Hard;
        }

        if (score >= OpusTransientSoftThreshold)
        {
            return PulsarTransientLevel.Soft;
        }

        return PulsarTransientLevel.None;
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
