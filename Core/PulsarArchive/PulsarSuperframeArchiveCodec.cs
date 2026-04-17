using System;
using System.Collections.Generic;
using Pulsar.Psycho;

public static class PulsarSuperframeArchiveCodec
{
    private static readonly object DebugTraceLock = new();
    private static readonly List<string> EncoderDebugTrace = [];
    private static readonly List<string> DecoderDebugTrace = [];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Size, int PrevSize, int NextSize), float[]> WindowCache = new();

    
    private const int ArchiveFormatVersion = 7;
    private const ushort PcmEntropyFlag = 0x0001;
    private const ushort SpectralEntropyFlag = 0x0002;
    private const ushort MidSideStereoFlag = 0x0004;
    private const int SuperframeSize = 2048;
    private const int SuperframeSegmentCount = SuperframeSize / PulsarBlockLadder.ControlHopSize;
    private const int ArchiveOffsetQuantum = PulsarBlockLadder.MinBlockSize / 2;
    private const int PsychoFftSize = 2048;
    private const int BandCount = 25;
    private const int JointStereoThresholdKbps = 512;
    private const float DefaultBandwidthLimitHz = 19000.0f;
    
    private const int ScaleQuantLevels = 4096;
    private const int StepQuantLevels = 4096;
    private const int GammaQuantLevels = 256;
    private const int ExplicitScaleBits = 12;
    private const int ExplicitStepBits = 12;
    private const int ExplicitGammaBits = 8;
    private const int DeltaScaleBits = 7;
    private const int DeltaStepBits = 7;
    private const int DeltaGammaBits = 6;
    private const int DeltaScaleLimit = (1 << (DeltaScaleBits - 1)) - 1;
    private const int DeltaStepLimit = (1 << (DeltaStepBits - 1)) - 1;
    private const int DeltaGammaLimit = (1 << (DeltaGammaBits - 1)) - 1;
    
    private static readonly byte[] ZeroRunIcdf = [192, 144, 80, 24, 0];
    private static readonly byte[] MagnitudeIcdf = [176, 120, 72, 36, 12, 0];
    private static readonly byte[] SideInfoDeltaIcdf = [80, 36, 12, 3, 0];

    private enum EntropyPayloadKind : byte
    {
        Pcm = 0,
        Spectral = 1,
    }
    
    private const float MinGamma = 0.55f;
    private const float MaxGamma = 0.98f;
    private const float MinScale = 1e-12f;
    private const float MaxScale = 65536.0f;
    private const float MinNormalizedStep = 1e-9f;
    private const float MaxNormalizedStep = 32.0f;

    private enum SpectralBandMode : byte
    {
        Zero = 0,
        Explicit = 1,
    }

    private sealed class SpectralArchivePlan
    {
        public required int SampleRate { get; init; }
        public required int Channels { get; init; }
        public required bool UseMidSideStereo { get; init; }
        public required int OriginalFrameCount { get; init; }
        public required List<SpectralSuperframePlan> Superframes { get; init; }
        public required SpectralChannelPlan[] ChannelPlans { get; init; }
    }

    private sealed class SpectralChannelPlan
    {
        public required float[] Samples { get; init; }
        public required IReadOnlyList<PulsarPsychoResult> PsychoFrames { get; init; }
        public required List<PulsarFrameAllocation> Allocations { get; init; }
    }

    private sealed class SpectralSuperframePlan
    {
        public required int Index { get; init; }
        public required int SampleStart { get; init; }
        public required byte PatternId { get; init; }
        public required PulsarLargeBlockState LargeBlockState { get; init; }
        public required ArchiveBlockPlan[] Blocks { get; init; }
    }

    private sealed class ArchiveBlockPlan
    {
        public required int BlockSize { get; init; }
        public required int PreviousBlockSize { get; init; }
        public required int NextBlockSize { get; init; }
        public required int AnalysisSegmentIndex { get; init; }
        public required int StartOffsetUnits { get; init; }
    }

    private sealed class EncodedBand
    {
        public required SpectralBandMode Mode { get; init; }
        public required uint EnergyQ { get; init; }
        public required uint ScaleQ { get; init; }
        public required int PulseCount { get; init; }
        public required int SpreadDecision { get; init; }
        public required uint StepQ { get; init; }
        public required uint GammaQ { get; init; }
        public required int ScaleDelta { get; init; }
        public required int StepDelta { get; init; }
        public required int GammaDelta { get; init; }
        public required int[] Levels { get; init; }
    }

    private sealed class EncodedChannelBlock
    {
        public required int BlockSize { get; init; }
        public required int StartOffsetUnits { get; init; }
        public required int ActiveBandCount { get; init; }
        public required int FramePulseBudget { get; init; }
        /// <summary>Total frame bit budget in integer bits. Used by V7 deterministic allocation.</summary>
        public required int FrameBitBudget { get; init; }
        public required EncodedBand[] Bands { get; init; }
        /// <summary>Raw MDCT coefficients for the v6 recursive PVQ encoder. Null on decode path.</summary>
        public float[]? SpectrumData { get; init; }
        /// <summary>Reconstructed energyQs after Opus coarse+fine round-trip, for V7 deterministic allocation. Set by BuildSpectralSideInfo.</summary>
        public uint[]? ReconstructedEnergyQs { get; set; }
    }

    private sealed class EncodedChannelSuperframe
    {
        public required EncodedChannelBlock[] Blocks { get; init; }
    }

    public static byte[] EncodePcmArchive(float[] interleavedSamples, int sampleRate, int channels)
    {
        int frameCount = interleavedSamples.Length / channels;
        int superframeCount = (frameCount + SuperframeSize - 1) / SuperframeSize;

        var container = new PulsarContainerFile();
        container.Header.Version = ArchiveFormatVersion;
        container.Header.Flags = PcmEntropyFlag;
        container.Header.SampleRate = sampleRate;
        container.Header.ChannelCount = channels;
        container.Header.TotalSamples = frameCount;

        for (int superframeIndex = 0; superframeIndex < superframeCount; superframeIndex++)
        {
            int frameStart = superframeIndex * SuperframeSize;
            int frameLength = Math.Min(SuperframeSize, frameCount - frameStart);
            byte[] payload = WrapEntropyPayload(EntropyPayloadKind.Pcm, EncodePcmSuperframePayload(interleavedSamples, channels, frameStart, frameLength));
            byte[] sideInfo = BuildPcmSideInfo(frameLength);

            container.Superframes.Add(new PulsarSuperframePacket
            {
                Index = superframeIndex, PatternId = 0, LargeBlockState = PulsarLargeBlockState.Standalone,
                SideInfo = sideInfo, EntropyPayload = payload,
            });
        }

        return new PulsarPacker().Pack(container);
    }

    public static byte[] EncodeSpectralArchive(float[] interleavedSamples, int sampleRate, int channels, int targetKbps, int? quality = null)
    {
        int effectiveQuality = ResolveArchiveQuality(quality, targetKbps);
        var allocator = new PulsarAllocator(new PulsarAllocationConfig { Quality = effectiveQuality, SampleRate = sampleRate, HopSize = PulsarBlockLadder.ControlHopSize, ChannelCount = channels });
        SpectralArchivePlan plan = AnalyzeSpectralArchive(interleavedSamples, sampleRate, channels, targetKbps, allocator);
        var container = new PulsarContainerFile();
        container.Header.Version = ArchiveFormatVersion;
        bool useMidSideStereo = channels == 2 && targetKbps <= JointStereoThresholdKbps;
        container.Header.Flags = (ushort)(SpectralEntropyFlag | (useMidSideStereo ? MidSideStereoFlag : 0));
        container.Header.SampleRate = plan.SampleRate;
        container.Header.ChannelCount = plan.Channels;
        container.Header.TotalSamples = plan.OriginalFrameCount;
        container.Header.BandwidthLimitHz = PulsarQualityProfile.GetBandwidthLimitHz(effectiveQuality);

        foreach (SpectralSuperframePlan superframe in plan.Superframes)
        {
            EncodedChannelSuperframe[] encodedChannels = BuildEncodedSuperframe(plan, superframe, allocator);
            byte[] sideInfo = BuildSpectralSideInfo(encodedChannels);

            container.Superframes.Add(new PulsarSuperframePacket
            {
                Index = superframe.Index, PatternId = superframe.PatternId, LargeBlockState = superframe.LargeBlockState,
                SideInfo = sideInfo, EntropyPayload = WrapEntropyPayload(EntropyPayloadKind.Spectral, EncodeSpectralPayloadV7(encodedChannels, plan.SampleRate, container.Header.BandwidthLimitHz)),
            });
        }

        return new PulsarPacker().Pack(container);
    }

    public static (float[] Samples, int SampleRate, int Channels) RenderSpectralPcm(float[] interleavedSamples, int sampleRate, int channels, int targetKbps, int? quality = null)
    {
        int effectiveQuality = ResolveArchiveQuality(quality, targetKbps);
        var allocator = new PulsarAllocator(new PulsarAllocationConfig { Quality = effectiveQuality, SampleRate = sampleRate, HopSize = PulsarBlockLadder.ControlHopSize, ChannelCount = channels });
        SpectralArchivePlan plan = AnalyzeSpectralArchive(interleavedSamples, sampleRate, channels, targetKbps, allocator);
        var allBlocks = new List<(int SampleStart, int BlockSize, PulsarQuantizedSpectrum Spectrum)>[plan.Channels];
        for (int ch = 0; ch < plan.Channels; ch++) allBlocks[ch] = new();

        foreach (SpectralSuperframePlan superframe in plan.Superframes)
        {
            EncodedChannelSuperframe[] encodedChannels = BuildEncodedSuperframe(plan, superframe, allocator);
            for (int channel = 0; channel < encodedChannels.Length; channel++)
            {
                EncodedChannelSuperframe encodedChannel = encodedChannels[channel];
                for (int blockIndex = 0; blockIndex < encodedChannel.Blocks.Length; blockIndex++)
                {
                    EncodedChannelBlock block = encodedChannel.Blocks[blockIndex];
                    int sampleStart = superframe.SampleStart + (block.StartOffsetUnits * ArchiveOffsetQuantum);
                    allBlocks[channel].Add((sampleStart, block.BlockSize, BuildQuantizedSpectrum(block)));
                }
            }
        }

        var channelBuffers = new float[plan.Channels][];
        for (int ch = 0; ch < plan.Channels; ch++) channelBuffers[ch] = ReconstructChannel(allBlocks[ch], plan.OriginalFrameCount);

        if (plan.UseMidSideStereo && plan.Channels == 2) ApplyInverseMidSide(channelBuffers[0], channelBuffers[1], plan.OriginalFrameCount);

        float[] output = new float[plan.OriginalFrameCount * plan.Channels];
        for (int frame = 0; frame < plan.OriginalFrameCount; frame++)
            for (int ch = 0; ch < plan.Channels; ch++)
                output[(frame * plan.Channels) + ch] = channelBuffers[ch][frame];

        return (output, plan.SampleRate, plan.Channels);
    }

    public static (float[] Samples, int SampleRate, int Channels) DecodePcmArchive(PulsarContainerFile container)
    {
        int channels = container.Header.ChannelCount;
        int totalFrames = checked((int)container.Header.TotalSamples);
        float[] samples = new float[totalFrames * channels];
        int writeFrame = 0;
        bool hasEntropyEnvelope = container.Header.Version >= 5;

        foreach (PulsarSuperframePacket superframe in container.Superframes)
        {
            int frameLength = ReadPcmSideInfo(superframe.SideInfo);
            byte[] entropyPayload = UnwrapEntropyPayload(superframe.EntropyPayload, EntropyPayloadKind.Pcm, hasEntropyEnvelope);
            DecodePcmSuperframePayload(entropyPayload, samples, channels, writeFrame, frameLength);
            writeFrame += frameLength;
        }

        return (samples, container.Header.SampleRate, channels);
    }

    public static (float[] Samples, int SampleRate, int Channels) DecodeArchive(byte[] payload)
    {
        PulsarContainerFile container = new PulsarPacker().Unpack(payload);
        if ((container.Header.Flags & SpectralEntropyFlag) != 0) return DecodeSpectralArchive(container);
        if ((container.Header.Flags & PcmEntropyFlag) != 0) return DecodePcmArchive(container);
        throw new InvalidOperationException("Unknown PULSAR archive payload type.");
    }

    private static SpectralArchivePlan AnalyzeSpectralArchive(float[] interleavedSamples, int sampleRate, int channels, int targetKbps, PulsarAllocator allocator)
    {
        int frameCount = interleavedSamples.Length / channels;
        float[][] channelBuffers = Deinterleave(interleavedSamples, channels);
        bool useMidSideStereo = channels == 2 && targetKbps <= JointStereoThresholdKbps;
        if (useMidSideStereo) ApplyMidSideStereo(channelBuffers[0], channelBuffers[1]);

        var psychoByChannel = new IReadOnlyList<PulsarPsychoResult>[channels];
        var sharedPlanner = new PulsarPlanner();
        List<PulsarFramePlan> sharedRenderPlans = sharedPlanner.PlanLegacyRenderSong(channelBuffers[0]);
        IReadOnlyList<PulsarFramePlan> sharedSegmentPlans = sharedPlanner.LastPlan;
        List<SpectralSuperframePlan> superframes = BuildSuperframePlans(sharedRenderPlans, frameCount);

        for (int channel = 0; channel < channels; channel++)
        {
            var psycho = new PulsarPsycho(new PulsarPsychoSettings { SampleRate = sampleRate, FftSize = PsychoFftSize, HopSize = PulsarBlockLadder.ControlHopSize });
            psychoByChannel[channel] = psycho.AnalyzeSong(channelBuffers[channel]);
        }

        var channelPlans = new SpectralChannelPlan[channels];
        for (int channel = 0; channel < channels; channel++)
        {
            channelPlans[channel] = new SpectralChannelPlan
            {
                Samples = channelBuffers[channel],
                PsychoFrames = psychoByChannel[channel],
                Allocations = allocator.AllocateSong(sharedSegmentPlans, psychoByChannel[channel]),
            };
        }

        return new SpectralArchivePlan
        {
            SampleRate = sampleRate, Channels = channels, UseMidSideStereo = useMidSideStereo,
            OriginalFrameCount = frameCount,
            Superframes = superframes, ChannelPlans = channelPlans,
        };
    }

    private static EncodedChannelSuperframe[] BuildEncodedSuperframe(SpectralArchivePlan plan, SpectralSuperframePlan superframe, PulsarAllocator allocator)
    {
        PulsarQuantizedSpectrum[][] allQuantized = new PulsarQuantizedSpectrum[plan.Channels][];
        float[][][] allSpectra = new float[plan.Channels][][];
        int[][] allBitBudgets = new int[plan.Channels][];
        for (int ch = 0; ch < plan.Channels; ch++)
        {
            allQuantized[ch] = new PulsarQuantizedSpectrum[superframe.Blocks.Length];
            allSpectra[ch] = new float[superframe.Blocks.Length][];
            allBitBudgets[ch] = new int[superframe.Blocks.Length];
        }

        for (int channel = 0; channel < plan.Channels; channel++)
        {
            SpectralChannelPlan channelPlan = plan.ChannelPlans[channel];
            for (int blockIndex = 0; blockIndex < superframe.Blocks.Length; blockIndex++)
            {
                ArchiveBlockPlan block = superframe.Blocks[blockIndex];
                int blockSampleStart = superframe.SampleStart + (block.StartOffsetUnits * ArchiveOffsetQuantum);
                (int pulseBudget, int bitBudget, PulsarPsychoResult psycho) = BuildArchiveQuantizationContext(
                    blockSampleStart,
                    block.BlockSize,
                    channelPlan.Allocations,
                    channelPlan.PsychoFrames);
                float[] spectrum = AnalyzeBlockSpectrum(
                    channelPlan.Samples,
                    blockSampleStart,
                    block.BlockSize,
                    block.PreviousBlockSize,
                    block.NextBlockSize);

                int[] bandOffsets = BuildBandOffsets(block.BlockSize / 2, plan.SampleRate, BandCount);
                allQuantized[channel][blockIndex] = allocator.QuantizeFrameVbr(
                    spectrum,
                    pulseBudget,
                    psycho,
                    0,
                    bandOffsets).QuantizedSpectrum;
                allSpectra[channel][blockIndex] = spectrum;
                allBitBudgets[channel][blockIndex] = bitBudget;
            }
        }

        return BuildEncodedChannelsFromQuantized(superframe, allQuantized, allBitBudgets, allSpectra);
    }

    private static int ResolveArchiveQuality(int? explicitQuality, int targetKbps)
    {
        if (explicitQuality.HasValue) return PulsarQualityProfile.ClampQuality(explicitQuality.Value);
        return PulsarQualityProfile.ResolveQualityFromTargetKbps(targetKbps);
    }

    private static EncodedChannelSuperframe[] BuildEncodedChannelsFromQuantized(SpectralSuperframePlan superframe, PulsarQuantizedSpectrum[][] allQuantized, int[][] allBitBudgets, float[][]?[]? allSpectra = null)
    {
        var channels = new EncodedChannelSuperframe[allQuantized.Length];
        for (int channel = 0; channel < allQuantized.Length; channel++)
        {
            var encodedBlocks = new List<EncodedChannelBlock>(superframe.Blocks.Length);

            for (int blockIndex = 0; blockIndex < superframe.Blocks.Length; blockIndex++)
            {
                ArchiveBlockPlan block = superframe.Blocks[blockIndex];
                float[]? specData = allSpectra?[channel]?[blockIndex];
                int frameBitBudget = allBitBudgets[channel][blockIndex];
                EncodedChannelBlock encodedBlock = ConvertQuantizedBlock(allQuantized[channel][blockIndex], block.BlockSize, block.StartOffsetUnits, frameBitBudget, specData);
                encodedBlocks.Add(encodedBlock);
            }
            channels[channel] = new EncodedChannelSuperframe { Blocks = encodedBlocks.ToArray() };
        }
        return channels;
    }

    private static (int PulseBudget, int BitBudget, PulsarPsychoResult Psycho) BuildArchiveQuantizationContext(
        int blockSampleStart,
        int blockSize,
        IReadOnlyList<PulsarFrameAllocation> allocations,
        IReadOnlyList<PulsarPsychoResult> psychoFrames)
    {
        if (allocations.Count == 0)
        {
            throw new InvalidOperationException("Spectral archive quantization requires precomputed frame allocations.");
        }

        if (psychoFrames.Count == 0)
        {
            throw new InvalidOperationException("Spectral archive quantization requires psycho frames.");
        }

        int controlHop = PulsarBlockLadder.ControlHopSize;
        int startSegment = Math.Clamp(blockSampleStart / controlHop, 0, allocations.Count - 1);
        int endSegment = Math.Clamp((int)Math.Ceiling((blockSampleStart + blockSize) / (double)controlHop) - 1, startSegment, allocations.Count - 1);
        int centerSegment = Math.Clamp((blockSampleStart + (blockSize / 2)) / controlHop, 0, psychoFrames.Count - 1);

        int pulseBudget = 0;
        int bitBudget = 0;

        for (int segment = startSegment; segment <= endSegment; segment++)
        {
            pulseBudget += allocations[segment].PulseBudget;
            bitBudget += allocations[segment].BitBudget;
        }

        pulseBudget = Math.Max(1, pulseBudget);
        bitBudget = Math.Max(32, bitBudget);


        return (pulseBudget, bitBudget, psychoFrames[centerSegment]);
    }

    private static EncodedChannelBlock ConvertQuantizedBlock(PulsarQuantizedSpectrum quantized, int blockSize, int startOffsetUnits, int frameBitBudget, float[]? spectrumData = null)
    {
        var bands = new EncodedBand[quantized.Bands.Length];
        int framePulseBudget = 0;
        for (int bandIndex = 0; bandIndex < quantized.Bands.Length; bandIndex++)
        {
            PulsarQuantizedBand band = quantized.Bands[bandIndex];
            int[] levels = (int[])band.Levels.Clone();
            if (IsAllZero(levels))
            {
                bands[bandIndex] = new EncodedBand { Mode = SpectralBandMode.Zero, EnergyQ = 0, ScaleQ = 0, PulseCount = 0, SpreadDecision = band.SpreadDecision, StepQ = 0, GammaQ = 0, ScaleDelta = 0, StepDelta = 0, GammaDelta = 0, Levels = levels };
                continue;
            }

            uint scaleQ = band.ScaleQ;
            uint stepQ = 0;
            uint gammaQ = 0;
                bands[bandIndex] = new EncodedBand { Mode = SpectralBandMode.Explicit, EnergyQ = band.EnergyQ, ScaleQ = scaleQ, PulseCount = band.PulseCount, SpreadDecision = band.SpreadDecision, StepQ = stepQ, GammaQ = gammaQ, ScaleDelta = 0, StepDelta = 0, GammaDelta = 0, Levels = levels };
            framePulseBudget += band.PulseCount;
        }

        // V7: all bands are potentially active; activity determined by AllocateBandBitsQ3 at encode/decode time
        int activeBandCount = bands.Length;
        return new EncodedChannelBlock { BlockSize = blockSize, StartOffsetUnits = startOffsetUnits, ActiveBandCount = activeBandCount, FramePulseBudget = Math.Max(1, framePulseBudget), FrameBitBudget = Math.Max(32, frameBitBudget), Bands = bands, SpectrumData = spectrumData };
    }

    private static PulsarQuantizedSpectrum BuildQuantizedSpectrum(EncodedChannelBlock block)
    {
        var bands = new PulsarQuantizedBand[BandCount];
        int runningStart = 0;
        float[] quantizedEnergyDb = new float[BandCount];
        for (int bandIndex = 0; bandIndex < BandCount; bandIndex++)
        {
            EncodedBand band = block.Bands[bandIndex];
            quantizedEnergyDb[bandIndex] = 20.0f * MathF.Log10(Math.Max(1e-12f, DequantizeLogValue(band.EnergyQ, MinScale, MaxScale, ScaleQuantLevels)));
        }

        for (int bandIndex = 0; bandIndex < BandCount; bandIndex++)
        {
            EncodedBand band = block.Bands[bandIndex];
            int width = band.Levels.Length;
            int pulseCount = band.Mode == SpectralBandMode.Zero ? 0 : band.PulseCount;
            float pulseNorm = ComputePulseNorm(band.Levels);
            float bandNorm = DequantizeLogValue(band.EnergyQ, MinScale, MaxScale, ScaleQuantLevels);
            // Derive scale from bandNorm / pulseNorm instead of transmitting ScaleQ.
            float derivedScale = pulseNorm > 1e-12f ? bandNorm / pulseNorm : 0.0f;
            bands[bandIndex] = new PulsarQuantizedBand {
                Start = runningStart, Width = width, Bits = pulseCount,
                EnergyDb = quantizedEnergyDb[bandIndex],
                EnergyQ = band.EnergyQ,
                ScaleQ = 0,
                Scale = derivedScale,
                RequestedPulseCount = pulseCount,
                PulseCount = pulseCount,
                PulseCap = width > 0 ? Math.Max(1, width * 24) : 0,
                BandNorm = bandNorm,
                PulseNorm = pulseNorm,
                SpreadDecision = band.SpreadDecision,
                Levels = (int[])band.Levels.Clone()
            };
            runningStart += width;
        }
        return new PulsarQuantizedSpectrum { SpectrumLength = block.BlockSize / 2, Bands = bands };
    }

    private static int ComputePulseCount(int[] levels)
    {
        int pulseCount = 0;
        for (int i = 0; i < levels.Length; i++)
        {
            pulseCount += Math.Abs(levels[i]);
        }

        return pulseCount;
    }

    private static float ComputePulseNorm(int[] levels)
    {
        double energy = 0.0;
        for (int i = 0; i < levels.Length; i++)
        {
            energy += levels[i] * levels[i];
        }

        return MathF.Sqrt((float)Math.Max(1e-12, energy));
    }

    // Fine-energy bits per band, adaptive by frequency:
    // LF (<4kHz): 3 bits, Mid (4-12kHz): 4 bits, HF (≥12kHz): 5 bits.
    // Narrow HF bands benefit more from energy precision since PVQ denorm multiplies by it.
    private static int GetFineEnergyBits(float bandCenterHz)
    {
        if (bandCenterHz >= 12000f) return 5;
        if (bandCenterHz >= 4000f) return 4;
        return 3;
    }
    private static readonly float Log2MinScale = MathF.Log2(MinScale);
    private static readonly float Log2MaxScale = MathF.Log2(MaxScale);
    private static readonly float Log2Span = Log2MaxScale - Log2MinScale;

    private static float EnergyQToLog2(uint energyQ)
    {
        float t = Math.Clamp((int)energyQ, 0, ScaleQuantLevels - 1) / (float)(ScaleQuantLevels - 1);
        return Log2MinScale + t * Log2Span;
    }

    private static uint Log2ToEnergyQ(float log2Scale)
    {
        float t = (log2Scale - Log2MinScale) / Math.Max(1e-12f, Log2Span);
        int q = (int)MathF.Round(t * (ScaleQuantLevels - 1));
        return (uint)Math.Clamp(q, 0, ScaleQuantLevels - 1);
    }

    private static byte[] BuildSpectralSideInfo(EncodedChannelSuperframe[] encodedChannels)
    {
        var encoder = new PulsarRangeEncoder();
        encoder.EncodeUInt((uint)Math.Clamp(encodedChannels.Length, 0, 15), 16);

        foreach (EncodedChannelSuperframe channel in encodedChannels)
        {
            encoder.EncodeUInt((uint)Math.Clamp(channel.Blocks.Length, 0, 255), 256);
            var energyState = new CeltEnergy.State(CeltEnergy.MaxBands);
            foreach (EncodedChannelBlock block in channel.Blocks)
            {
                encoder.EncodeUInt((uint)Math.Clamp(PulsarBlockLadder.GetIndex(block.BlockSize), 0, PulsarBlockLadder.Steps.Length - 1), (uint)PulsarBlockLadder.Steps.Length);
                encoder.EncodeUInt((uint)Math.Clamp(block.StartOffsetUnits, 0, 255), 256);
                encoder.EncodeUInt((uint)Math.Clamp(block.ActiveBandCount, 0, BandCount), BandCount + 1u);

                int active = block.ActiveBandCount;

                // V7: write frame bit budget (16-bit, 0–65535).
                encoder.EncodeUInt((uint)Math.Clamp(block.FrameBitBudget, 0, 65535), 65536);

                // Pass 1 (V7): coarse energy FIRST (so decoder can compute bit allocation).
                // Compute band energy from raw spectrum (not old quantizer's EnergyQ which is 0 for skipped bands).
                int specLen = block.BlockSize / 2;
                (_, int[] bandOffsets, _) = BuildOpusBandLayout(specLen);
                Span<float> eBands = stackalloc float[CeltEnergy.MaxBands];
                for (int i = 0; i < active; i++)
                {
                    int bStart = bandOffsets[i];
                    int bEnd = (i + 1 < bandOffsets.Length) ? bandOffsets[i + 1] : specLen;
                    float sum = 0;
                    if (block.SpectrumData != null)
                    {
                        for (int j = bStart; j < bEnd && j < block.SpectrumData.Length; j++)
                            sum += block.SpectrumData[j] * block.SpectrumData[j];
                    }
                    float bandNorm = MathF.Sqrt(sum);
                    float log2Scale = bandNorm > 1e-12f ? MathF.Log2(bandNorm) : -9.0f;
                    eBands[i] = Math.Clamp(log2Scale, -9.0f, 16.0f);
                }

                Span<float> error = stackalloc float[CeltEnergy.MaxBands];
                int budgetBits = encoder.TellBits + 8192;
                CeltEnergy.QuantCoarse(encoder, 0, active, eBands, energyState, error, budgetBits, forceIntra: false);

                // Pass 2: fine energy.
                Span<int> extraQuant = stackalloc int[CeltEnergy.MaxBands];
                {
                    // Approximate hzPerBin for this block (44100 assumed for side-info)
                    float sideHzPerBin = 44100.0f / block.BlockSize;
                    for (int i = 0; i < active; i++)
                    {
                        float bcHz = (bandOffsets[i] + bandOffsets[i + 1]) * 0.5f * sideHzPerBin;
                        extraQuant[i] = GetFineEnergyBits(bcHz);
                    }
                }
                CeltEnergy.QuantFine(encoder, 0, active, energyState, error, extraQuant);

                // V7: reconstruct energy from energyState so encoder and decoder agree exactly.
                Span<float> reconLogAmp = stackalloc float[CeltEnergy.MaxBands];
                CeltEnergy.ToLogAmplitude(energyState, 0, active, reconLogAmp);
                var reconEnergyQs = new uint[active];
                for (int i = 0; i < active; i++)
                    reconEnergyQs[i] = Log2ToEnergyQ(reconLogAmp[i]);
                block.ReconstructedEnergyQs = reconEnergyQs;

                // Pass 3 (V7): per-band spread decisions for all active bands.
                // No mode bits — band activity is derived from bitsQ3 allocation.
                for (int bandIndex = 0; bandIndex < active; bandIndex++)
                {
                    encoder.EncodeUInt((uint)Math.Clamp(block.Bands[bandIndex].SpreadDecision, 0, 3), 4);
                }
            }
        }
        return encoder.Finish();
    }

    private static void EncodeQuantizedParameter(PulsarRangeEncoder encoder, uint value, ref int previousValue)
    {
        int currentValue = Math.Clamp((int)value, 0, ScaleQuantLevels - 1);
        int delta = currentValue - previousValue;
        int absDelta = Math.Abs(delta);

        if (absDelta == 0)
        {
            encoder.EncodeIcdfSymbol(0, SideInfoDeltaIcdf, 8);
        }
        else if (absDelta <= 3)
        {
            encoder.EncodeIcdfSymbol(1, SideInfoDeltaIcdf, 8);
            encoder.EncodeBit(delta < 0);
            encoder.EncodeBits((uint)(absDelta - 1), 2);
        }
        else if (absDelta <= 15)
        {
            encoder.EncodeIcdfSymbol(2, SideInfoDeltaIcdf, 8);
            encoder.EncodeBit(delta < 0);
            encoder.EncodeBits((uint)(absDelta - 4), 4);
        }
        else if (absDelta <= 63)
        {
            encoder.EncodeIcdfSymbol(3, SideInfoDeltaIcdf, 8);
            encoder.EncodeBit(delta < 0);
            encoder.EncodeBits((uint)(absDelta - 16), 6);
        }
        else
        {
            encoder.EncodeIcdfSymbol(4, SideInfoDeltaIcdf, 8);
            encoder.EncodeBits((uint)currentValue, 12);
        }

        previousValue = currentValue;
    }

    private static readonly byte[] LevelEventIcdf = [176, 0];

    private static byte[] EncodeSuperframeLevels(EncodedChannelSuperframe[] encodedChannels)
    {
        var encoder = new PulsarRangeEncoder();
        foreach (EncodedChannelSuperframe channel in encodedChannels)
        {
            foreach (EncodedChannelBlock block in channel.Blocks)
            {
                for (int bandIndex = 0; bandIndex < block.ActiveBandCount; bandIndex++)
                {
                    EncodedBand band = block.Bands[bandIndex];
                    if (band.Mode == SpectralBandMode.Zero) continue;
                    EncodeBandLevels(encoder, band.Levels, Math.Max(0, band.PulseCount));
                }
            }
        }
        return encoder.Finish();
    }

    /// <summary>
    /// V7 entropy payload: encodes spectrum using Opus-style recursive PVQ band split.
    /// Per-band bit budgets are computed deterministically from (totalFrameBits, energyDb, bandWidths)
    /// via CeltAllocation.AllocateBandBitsQ3 — no per-band pulse counts in side-info.
    /// </summary>
    private static byte[] EncodeSpectralPayloadV7(EncodedChannelSuperframe[] encodedChannels, int sampleRate, float bandwidthLimitHz)
    {
        var encoder = new PulsarRangeEncoder();
        foreach (EncodedChannelSuperframe channel in encodedChannels)
        {
            foreach (EncodedChannelBlock block in channel.Blocks)
            {
                int specLen = block.BlockSize / 2;
                // Use Opus-compatible band layout: base eBands + LM for proper cache alignment.
                (int LM, int[] bandOffsets, int[] baseBandWidths) = BuildOpusBandLayout(specLen);
                int[] bandWidths = new int[BandCount];
                for (int i = 0; i < BandCount; i++)
                    bandWidths[i] = bandOffsets[i + 1] - bandOffsets[i];
                var mode = CeltMode.CreateFromBandWidths(baseBandWidths, maxLm: LM);

                // Bandwidth limiting: only allocate bits to bands with audible content (≤20kHz)
                // Matches Opus's bandwidth parameter. At high sample rates, upper bands are ultrasonic.
                float hzPerBin = (float)sampleRate / block.BlockSize; // MDCT: freq = bin * sr / blockSize
                int endBand = BandCount;
                for (int i = 0; i < BandCount; i++)
                {
                    if (bandOffsets[i] * hzPerBin > bandwidthLimitHz)
                    {
                        endBand = i;
                        break;
                    }
                }

                // Use reconstructed energy from BuildSpectralSideInfo (Opus coarse+fine round-trip)
                uint[] reconEnergyQs = block.ReconstructedEnergyQs ?? new uint[block.ActiveBandCount];

                // Compute band energies in dB for adaptive allocation
                float[] bandEnergyDb = new float[block.ActiveBandCount];
                for (int i = 0; i < block.ActiveBandCount; i++)
                {
                    float lin = DequantizeLogValue(
                        i < reconEnergyQs.Length ? reconEnergyQs[i] : 0,
                        MinScale, MaxScale, ScaleQuantLevels);
                    bandEnergyDb[i] = 20.0f * MathF.Log10(Math.Max(1e-12f, lin));
                }

                int totalBitsQ3 = block.FrameBitBudget << CeltRate.BitRes;
                int allocTrim = CeltAllocation.ComputeAllocTrim(bandEnergyDb, block.ActiveBandCount, endBand, LM);
                int[] dynBoosts = CeltAllocation.ComputeDynallocBoosts(bandEnergyDb, block.ActiveBandCount, endBand, totalBitsQ3);

                // Deterministic per-band bit allocation with adaptive trim and dynalloc boosts
                (int[] bitsQ3, int[] fineBits) = CeltAllocation.ComputeAllocationWithBoosts(mode, totalBitsQ3, LM, allocTrim, dynBoosts, endBand: endBand);

                // (Post-allocation spectral tilt removed — trust CeltAllocation's calibrated tables.)




                float[] X = block.SpectrumData != null ? (float[])block.SpectrumData.Clone() : new float[specLen];

                // Hard zero MDCT bins above 19 kHz — the LPF bandwidth limit
                {
                    float hzPerBinLocal = (float)sampleRate / block.BlockSize;
                    int cutoffBin = (int)(bandwidthLimitHz / hzPerBinLocal);
                    for (int i = cutoffBin; i < specLen; i++)
                        X[i] = 0;
                }

                float[] lowband = new float[specLen];
                float[] lowbandOut = new float[specLen];
                float[] scratch = new float[specLen];
                uint seed = 0;

                // --- TF analysis: determine per-band temporal folding ---
                // In PULSAR's variable-block scheme, "transient" = short block (LM ≤ 2, i.e. ≤1024).
                bool isTransient = block.BlockSize <= 1024;
                int[] tfRes = new int[block.ActiveBandCount];
                int tfSelect = 0;
                if (LM > 0 && block.ActiveBandCount > 0)
                {
                    // Normalize spectrum for TF analysis (unit-energy per band, like Opus celt_norm)
                    float[] Xnorm = (float[])X.Clone();
                    for (int bi = 0; bi < block.ActiveBandCount; bi++)
                    {
                        int s = bandOffsets[bi];
                        int n = bandOffsets[bi + 1] - s;
                        float e = 0;
                        for (int j = 0; j < n; j++) e += Xnorm[s + j] * Xnorm[s + j];
                        e = MathF.Sqrt(e);
                        if (e > 1e-12f) { float inv = 1f / e; for (int j = 0; j < n; j++) Xnorm[s + j] *= inv; }
                    }
                    int effectiveBytes = Math.Max(1, block.FrameBitBudget / 8);
                    int lambda = Math.Max(80, 20480 / effectiveBytes + 2);
                    tfSelect = CeltTf.TfAnalysis(bandOffsets, Math.Min(block.ActiveBandCount, endBand),
                        isTransient, tfRes, lambda, Xnorm, specLen, LM, 0f, null);
                }
                // Budget for TF bits: use a generous limit (total frame budget in bits)
                int tfBudgetBits = block.FrameBitBudget * 8;
                CeltTf.TfEncode(0, Math.Min(block.ActiveBandCount, endBand), isTransient, tfRes, LM, tfSelect, encoder, tfBudgetBits);

                for (int bandIndex = 0; bandIndex < block.ActiveBandCount; bandIndex++)
                {
                    int start = bandOffsets[bandIndex];
                    int N = bandOffsets[bandIndex + 1] - start;
                    if (N <= 0) continue;

                    int b = bandIndex < bitsQ3.Length ? bitsQ3[bandIndex] : 0;
                    if (b <= 0) continue;

                    float bandNorm = DequantizeLogValue(
                        bandIndex < reconEnergyQs.Length ? reconEnergyQs[bandIndex] : 0,
                        MinScale, MaxScale, ScaleQuantLevels);

                    // Normalize the band slice to unit energy before PVQ search (like Opus celt_norm).
                    Span<float> bandX = X.AsSpan(start, N);
                    float bandEnergy = 0;
                    for (int i = 0; i < N; i++) bandEnergy += bandX[i] * bandX[i];
                    bandEnergy = MathF.Sqrt(bandEnergy);
                    if (bandEnergy > 1e-12f)
                    {
                        float invE = 1.0f / bandEnergy;
                        for (int i = 0; i < N; i++) bandX[i] *= invE;
                    }

                    int spread = bandIndex < block.Bands.Length ? block.Bands[bandIndex].SpreadDecision : 0;
                    var ctx = new CeltBand.BandCtx
                    {
                        Mode = mode,
                        BandIndex = bandIndex,
                        Spread = spread,
                        TfChange = bandIndex < tfRes.Length ? tfRes[bandIndex] : 0,
                        RemainingBits = b + 128,
                        Seed = seed,
                        AvoidSplitNoise = true,
                    };

                    CeltBand.QuantBandMonoEncode(
                        ref ctx, encoder,
                        X.AsSpan(start, N), N, b, 1,
                        lowband.AsSpan(0, N), LM,
                        lowbandOut.AsSpan(0, N), bandNorm,
                        scratch.AsSpan(0, N), 1);

                    int lbLen = Math.Min(N, lowband.Length);
                    Array.Copy(lowbandOut, 0, lowband, 0, lbLen);
                    seed = ctx.Seed;
                }

                // Fine energy encoding: use extra bits to refine per-band energy
                // This mirrors Opus's quant_fine_energy() — encode fractional energy
                // with fineBits[j] extra bits per band.
                for (int bandIndex = 0; bandIndex < block.ActiveBandCount; bandIndex++)
                {
                    if (bandIndex >= fineBits.Length || fineBits[bandIndex] <= 0) continue;
                    int fb = fineBits[bandIndex];
                    // Encode a uniform value in [0, 1<<fb) representing the sub-bit energy
                    // For now, encode 0 (neutral) — the energy is already quantized in side-info.
                    // The real Opus uses the error between true and coarse-quantized energy.
                    float trueEnergy = 0;
                    if (block.SpectrumData != null)
                    {
                        int s = bandOffsets[bandIndex];
                        int e = bandOffsets[bandIndex + 1];
                        float sum = 0;
                        for (int i = s; i < e && i < block.SpectrumData.Length; i++)
                            sum += block.SpectrumData[i] * block.SpectrumData[i];
                        trueEnergy = MathF.Sqrt(sum);
                    }
                    // Quantize the energy error as a fraction in [0, 1<<fb)
                    float reconEnergy = bandIndex < reconEnergyQs.Length
                        ? DequantizeLogValue(reconEnergyQs[bandIndex], MinScale, MaxScale, ScaleQuantLevels)
                        : 1e-12f;
                    // Error in log2 domain, range [-0.5, 0.5] mapped to [0, 1<<fb)
                    float logError = trueEnergy > 1e-12f && reconEnergy > 1e-12f
                        ? MathF.Log2(trueEnergy / reconEnergy) : 0;
                    logError = Math.Clamp(logError, -0.5f, 0.5f);
                    uint fineVal = (uint)Math.Clamp((int)((logError + 0.5f) * (1 << fb)), 0, (1 << fb) - 1);
                    encoder.EncodeUInt(fineVal, (uint)(1 << fb));
                }
            }
        }
        return encoder.Finish();
    }

    private static byte[] WrapEntropyPayload(EntropyPayloadKind kind, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var writer = new PulsarBitstreamWriter();
        writer.WriteAscii("PEN2");
        writer.WriteByte((byte)kind);
        writer.WriteUInt32((uint)payload.Length);
        writer.WriteBytes(payload);
        return writer.ToArray();
    }

    private static byte[] UnwrapEntropyPayload(byte[] payload, EntropyPayloadKind expectedKind, bool hasEnvelope)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!hasEnvelope)
        {
            return payload;
        }

        if (payload.Length < 9)
        {
            throw new InvalidOperationException("Entropy payload envelope is truncated.");
        }

        var reader = new PulsarBitstreamReader(payload);
        string magic = reader.ReadAscii(4);
        if (!string.Equals(magic, "PEN2", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid entropy payload magic.");
        }

        EntropyPayloadKind actualKind = (EntropyPayloadKind)reader.ReadByte();
        if (actualKind != expectedKind)
        {
            throw new InvalidOperationException($"Entropy payload kind mismatch. Expected {expectedKind}, found {actualKind}.");
        }

        int declaredLength = checked((int)reader.ReadUInt32());
        if (declaredLength != payload.Length - 9)
        {
            throw new InvalidOperationException("Entropy payload length mismatch.");
        }

        return reader.ReadBytes(declaredLength);
    }

    private static void EncodeBandLevels(PulsarRangeEncoder encoder, int[] levels, int pulseCount)
    {
        ArgumentNullException.ThrowIfNull(levels);

        if (TryEncodeCombinatorialLevels(encoder, levels, pulseCount))
        {
            return;
        }

        EncodeLegacyBandLevels(encoder, levels);
    }

    private static int[] DecodeBandLevels(PulsarRangeDecoder decoder, int width, int pulseCount)
    {
        ArgumentNullException.ThrowIfNull(decoder);

        bool combinatorialMode = decoder.DecodeBit();
        if (combinatorialMode)
        {
            return DecodeCombinatorialLevels(decoder, width, pulseCount);
        }

        return DecodeLegacyBandLevels(decoder, width);
    }

    private static bool TryEncodeCombinatorialLevels(PulsarRangeEncoder encoder, int[] levels, int pulseCount)
    {
        if (levels.Length == 0)
        {
            encoder.EncodeBit(false);
            return false;
        }

        if (pulseCount <= 0 || !CeltRate.FitsIn32(levels.Length, pulseCount))
        {
            encoder.EncodeBit(false);
            return false;
        }

        if (!CeltCwrs.TryEncode(levels, pulseCount, out uint rank, out uint total))
        {
            encoder.EncodeBit(false);
            return false;
        }

        encoder.EncodeBit(true);
        encoder.EncodeUInt(rank, total);
        return true;
    }

    private static int[] DecodeCombinatorialLevels(PulsarRangeDecoder decoder, int width, int pulseCount)
    {
        if (width <= 0)
        {
            return Array.Empty<int>();
        }

        uint total = CeltCwrs.Count(width, pulseCount);
        if (total == 0)
        {
            throw new InvalidOperationException("Combinatorial PVQ index exceeded the supported range.");
        }

        uint rank = decoder.DecodeUInt(total);
        return CeltCwrs.Decode(width, pulseCount, rank);
    }

    private static void EncodeLegacyBandLevels(PulsarRangeEncoder encoder, int[] levels)
    {
        int cursor = 0;
        while (cursor < levels.Length)
        {
            if (levels[cursor] == 0)
            {
                int runStart = cursor;
                while (cursor < levels.Length && levels[cursor] == 0) cursor++;
                encoder.EncodeBit(false);
                EncodeZeroRun(encoder, cursor - runStart);
            }
            else
            {
                encoder.EncodeBit(true);
                int magnitude = Math.Abs(levels[cursor]);
                EncodeMagnitudeValue(encoder, magnitude);
                encoder.EncodeBit(levels[cursor] < 0);
                cursor++;
            }
        }
    }

    private static int[] DecodeLegacyBandLevels(PulsarRangeDecoder decoder, int width)
    {
        int[] levels = new int[width];
        int cursor = 0;
        while (cursor < width)
        {
            bool isNonZero = decoder.DecodeBit();
            if (!isNonZero)
            {
                int runLength = DecodeZeroRun(decoder);
                runLength = Math.Min(runLength, width - cursor);
                cursor += runLength;
            }
            else
            {
                int magnitude = DecodeMagnitudeValue(decoder);
                bool negative = decoder.DecodeBit();
                levels[cursor] = negative ? -magnitude : magnitude;
                cursor++;
            }
        }

        return levels;
    }

    private static void EncodeChannelSuperframeLevels(PulsarRangeEncoder encoder, int superframeIndex, int channelIndex, EncodedChannelSuperframe channel)
    {
        _ = superframeIndex;
        _ = channelIndex;
        foreach (EncodedChannelBlock block in channel.Blocks)
        {
            for (int bandIndex = 0; bandIndex < block.ActiveBandCount; bandIndex++)
            {
                EncodedBand band = block.Bands[bandIndex];
                if (band.Mode == SpectralBandMode.Zero) continue;
                EncodeBandLevels(encoder, band.Levels, Math.Max(0, band.PulseCount));
            }
        }
    }

    private static (PulsarQuantizedSpectrum Spectrum, object? State) DecodeQuantizedChannel(
        PulsarRangeDecoder sideInfoDecoder,
        PulsarRangeDecoder decoder,
        int spectrumLength,
        int[] trueBandOffsets,
        int activeBandCount,
        CeltEnergy.State energyState)
    {
        var bands = new EncodedBand[BandCount];
        var bandModes = new SpectralBandMode[activeBandCount];
        var pulseCounts = new int[activeBandCount];
        var energyQs = new uint[activeBandCount];
        var scaleQs = new uint[activeBandCount];
        var spreadDecisions = new int[activeBandCount];

        // Pass 1 mirror: modes + pulse count + spread per active band.
        for (int bandIndex = 0; bandIndex < activeBandCount; bandIndex++)
        {
            SpectralBandMode bandMode = sideInfoDecoder.DecodeBit(falseFrequency: 64, trueFrequency: 192)
                ? SpectralBandMode.Explicit
                : SpectralBandMode.Zero;
            bandModes[bandIndex] = bandMode;
            if (bandMode == SpectralBandMode.Zero)
            {
                pulseCounts[bandIndex] = 0;
                spreadDecisions[bandIndex] = 0;
                continue;
            }

            pulseCounts[bandIndex] = (int)sideInfoDecoder.DecodeUInt(4096);
            spreadDecisions[bandIndex] = (int)sideInfoDecoder.DecodeUInt(4);
            scaleQs[bandIndex] = 0; // derived at dequant time from bandNorm / pulseNorm
        }

        // Pass 2 + 3 mirrors: coarse + fine energy.
        int budgetBits = sideInfoDecoder.TellBits + 8192;
        CeltEnergy.UnquantCoarse(sideInfoDecoder, 0, activeBandCount, energyState, budgetBits);
        Span<int> extraQuant = stackalloc int[CeltEnergy.MaxBands];
        // Legacy decoder: approximate band center from index ratio (no sampleRate available)
        for (int i = 0; i < activeBandCount; i++)
        {
            float approxHz = (float)i / Math.Max(1, activeBandCount) * 22050f;
            extraQuant[i] = GetFineEnergyBits(approxHz);
        }
        CeltEnergy.UnquantFine(sideInfoDecoder, 0, activeBandCount, energyState, extraQuant);

        // Reconstruct bandNorm from log2 state and back-convert to EnergyQ.
        Span<float> logAmp = stackalloc float[CeltEnergy.MaxBands];
        CeltEnergy.ToLogAmplitude(energyState, 0, activeBandCount, logAmp);
        for (int i = 0; i < activeBandCount; i++)
            energyQs[i] = Log2ToEnergyQ(logAmp[i]);

        for (int bandIndex = 0; bandIndex < activeBandCount; bandIndex++)
        {
            int start = trueBandOffsets[bandIndex];
            int end = trueBandOffsets[bandIndex + 1];
            int trueWidth = end - start;
            int[] levels = new int[trueWidth];

            if (bandModes[bandIndex] == SpectralBandMode.Explicit)
            {
                int[] decodedLevels = DecodeBandLevels(decoder, trueWidth, Math.Max(0, pulseCounts[bandIndex]));
                int copyLength = Math.Min(decodedLevels.Length, trueWidth);
                for (int cursor = 0; cursor < copyLength; cursor++)
                {
                    levels[cursor] = decodedLevels[cursor];
                }
            }

            bands[bandIndex] = new EncodedBand
            {
                Mode = bandModes[bandIndex],
                EnergyQ = energyQs[bandIndex],
                ScaleQ = scaleQs[bandIndex],
                PulseCount = pulseCounts[bandIndex],
                SpreadDecision = spreadDecisions[bandIndex],
                StepQ = 0,
                GammaQ = 0,
                ScaleDelta = 0,
                StepDelta = 0,
                GammaDelta = 0,
                Levels = levels,
            };
        }

        for (int fillBand = activeBandCount; fillBand < BandCount; fillBand++)
        {
            int width = trueBandOffsets[fillBand + 1] - trueBandOffsets[fillBand];
            bands[fillBand] = new EncodedBand
            {
                Mode = SpectralBandMode.Zero,
                EnergyQ = 0,
                ScaleQ = 0,
                PulseCount = 0,
                SpreadDecision = 0,
                StepQ = 0,
                GammaQ = 0,
                ScaleDelta = 0,
                StepDelta = 0,
                GammaDelta = 0,
                Levels = new int[width],
            };
        }

        var encodedBlock = new EncodedChannelBlock
        {
            BlockSize = spectrumLength * 2,
            StartOffsetUnits = 0,
            ActiveBandCount = activeBandCount,
            FramePulseBudget = 0,
            FrameBitBudget = 0,
            Bands = bands,
        };

        return (BuildQuantizedSpectrum(encodedBlock), null);
    }

    /// <summary>
    /// V6 decode: reads the recursive PVQ bitstream via CeltBand.QuantBandMonoDecode
    /// and returns float[] MDCT coefficients directly (no int[] levels intermediate).
    /// Side-info is still decoded from the same range coder as v5.
    /// </summary>
    /// <summary>
    /// V7 decoder: reads frame bit budget from side-info, computes per-band allocation
    /// deterministically from (totalBits, energyDb, bandWidths), then decodes via QuantBandMonoDecode.
    /// </summary>
    private static float[] DecodeQuantizedChannelV7(
        PulsarRangeDecoder sideInfoDecoder,
        PulsarRangeDecoder decoder,
        int spectrumLength,
        int[] trueBandOffsets,
        int activeBandCount,
        CeltEnergy.State energyState,
        int sampleRate,
        float bandwidthLimitHz)
    {
        // V7: read frame bit budget (16-bit)
        int frameBitBudget = (int)sideInfoDecoder.DecodeUInt(65536);

        // Pass 1 (V7): coarse + fine energy FIRST (matches new side-info order).
        int budgetBits = sideInfoDecoder.TellBits + 8192;
        CeltEnergy.UnquantCoarse(sideInfoDecoder, 0, activeBandCount, energyState, budgetBits);
        Span<int> extraQuant = stackalloc int[CeltEnergy.MaxBands];
        {
            // Use band layout to compute adaptive fine energy bits (must match encoder)
            (_, int[] febOffsets, _) = BuildOpusBandLayout(spectrumLength);
            float febHzPerBin = (float)sampleRate / (2 * spectrumLength);
            for (int i = 0; i < activeBandCount; i++)
            {
                float bcHz = (febOffsets[i] + febOffsets[i + 1]) * 0.5f * febHzPerBin;
                extraQuant[i] = GetFineEnergyBits(bcHz);
            }
        }
        CeltEnergy.UnquantFine(sideInfoDecoder, 0, activeBandCount, energyState, extraQuant);

        Span<float> logAmp = stackalloc float[CeltEnergy.MaxBands];
        CeltEnergy.ToLogAmplitude(energyState, 0, activeBandCount, logAmp);
        var energyQs = new uint[activeBandCount];
        for (int i = 0; i < activeBandCount; i++)
            energyQs[i] = Log2ToEnergyQ(logAmp[i]);

        // Pass 2: per-band spread decisions (for all active bands, no mode bits).
        var spreadDecisions = new int[activeBandCount];
        for (int bandIndex = 0; bandIndex < activeBandCount; bandIndex++)
            spreadDecisions[bandIndex] = (int)sideInfoDecoder.DecodeUInt(4);

        // Build CeltMode and deterministic per-band bit allocation (Opus-compatible layout).
        (int LM, int[] opusBandOffsets, int[] baseBandWidths) = BuildOpusBandLayout(spectrumLength);
        int[] bandWidths = new int[BandCount];
        for (int i = 0; i < BandCount; i++)
            bandWidths[i] = opusBandOffsets[i + 1] - opusBandOffsets[i];
        var mode = CeltMode.CreateFromBandWidths(baseBandWidths, maxLm: LM);

        // Bandwidth limiting: match encoder (only allocate to audible bands ≤20kHz)
        float hzPerBin = (float)sampleRate / (2 * spectrumLength);
        int endBand = BandCount;
        for (int i = 0; i < BandCount; i++)
        {
            if (opusBandOffsets[i] * hzPerBin > bandwidthLimitHz)
            {
                endBand = i;
                break;
            }
        }

        // Compute band energies in dB for adaptive allocation (matches encoder)
        float[] bandEnergyDb = new float[activeBandCount];
        for (int i = 0; i < activeBandCount; i++)
        {
            float lin = DequantizeLogValue(energyQs[i], MinScale, MaxScale, ScaleQuantLevels);
            bandEnergyDb[i] = 20.0f * MathF.Log10(Math.Max(1e-12f, lin));
        }

        int totalBitsQ3 = frameBitBudget << CeltRate.BitRes;
        int allocTrim = CeltAllocation.ComputeAllocTrim(bandEnergyDb, activeBandCount, endBand, LM);
        int[] dynBoosts = CeltAllocation.ComputeDynallocBoosts(bandEnergyDb, activeBandCount, endBand, totalBitsQ3);

        // Deterministic per-band bit allocation with adaptive trim and dynalloc boosts
        (int[] bitsQ3, int[] fineBits) = CeltAllocation.ComputeAllocationWithBoosts(mode, totalBitsQ3, LM, allocTrim, dynBoosts, endBand: endBand);

        // (Post-allocation spectral tilt removed — trust CeltAllocation's calibrated tables.)

        // Decode bands — band activity from bitsQ3, not mode bits
        float[] mdctCoeffs = new float[spectrumLength];
        float[] lowband = new float[spectrumLength];
        float[] lowbandOut = new float[spectrumLength];
        float[] scratch = new float[spectrumLength];
        uint seed = 0;

        // --- TF decode: read per-band temporal folding from bitstream ---
        bool isTransient = (spectrumLength * 2) <= 1024;
        int[] tfRes = new int[activeBandCount];
        if (LM > 0 && activeBandCount > 0)
        {
            int tfBudgetBits = frameBitBudget * 8;
            CeltTf.TfDecode(0, Math.Min(activeBandCount, endBand), isTransient, tfRes, LM, decoder, tfBudgetBits);
        }

        for (int bandIndex = 0; bandIndex < activeBandCount; bandIndex++)
        {
            int start = opusBandOffsets[bandIndex];
            int N = opusBandOffsets[bandIndex + 1] - start;
            if (N <= 0) continue;

            int b = bandIndex < bitsQ3.Length ? bitsQ3[bandIndex] : 0;
            if (b <= 0) continue;

            float bandNorm = DequantizeLogValue(energyQs[bandIndex], MinScale, MaxScale, ScaleQuantLevels);

            var ctx = new CeltBand.BandCtx
            {
                Mode = mode,
                BandIndex = bandIndex,
                Spread = spreadDecisions[bandIndex],
                TfChange = bandIndex < tfRes.Length ? tfRes[bandIndex] : 0,
                RemainingBits = b + 128,
                Seed = seed,
                AvoidSplitNoise = true,
            };

            Span<float> X = mdctCoeffs.AsSpan(start, N);
            CeltBand.QuantBandMonoDecode(
                ref ctx, decoder,
                X, N, b, 1,
                lowband.AsSpan(0, N), LM,
                lowbandOut.AsSpan(0, N), bandNorm,
                scratch.AsSpan(0, N), 1);

            Array.Copy(lowbandOut, 0, lowband, 0, N);
            seed = ctx.Seed;
        }

        // Fine energy decoding: refine per-band energy with extra bits
        for (int bandIndex = 0; bandIndex < activeBandCount; bandIndex++)
        {
            if (bandIndex >= fineBits.Length || fineBits[bandIndex] <= 0) continue;
            int fb = fineBits[bandIndex];
            uint fineVal = decoder.DecodeUInt((uint)(1 << fb));
            // Convert back to log2 error: [0, 1<<fb) → [-0.5, 0.5]
            float logError = ((float)fineVal / (1 << fb)) - 0.5f;
            // Apply energy correction to the decoded band
            float correction = MathF.Pow(2.0f, logError);
            int s = opusBandOffsets[bandIndex];
            int e = opusBandOffsets[bandIndex + 1];
            for (int i = s; i < e && i < mdctCoeffs.Length; i++)
                mdctCoeffs[i] *= correction;
        }

        return mdctCoeffs;
    }

    private static int DecodeQuantizedParameter(PulsarRangeDecoder decoder, ref int previousValue)
    {
        int symbol = decoder.DecodeIcdfSymbol(SideInfoDeltaIcdf, 8);
        int value;
        if (symbol == 0)
        {
            value = previousValue;
        }
        else if (symbol == 1)
        {
            int sign = decoder.DecodeBit() ? -1 : 1;
            value = previousValue + (sign * ((int)decoder.DecodeBits(2) + 1));
        }
        else if (symbol == 2)
        {
            int sign = decoder.DecodeBit() ? -1 : 1;
            value = previousValue + (sign * ((int)decoder.DecodeBits(4) + 4));
        }
        else if (symbol == 3)
        {
            int sign = decoder.DecodeBit() ? -1 : 1;
            value = previousValue + (sign * ((int)decoder.DecodeBits(6) + 16));
        }
        else
        {
            value = (int)decoder.DecodeBits(12);
        }

        previousValue = Math.Clamp(value, 0, ScaleQuantLevels - 1);
        return previousValue;
    }

    private static (PulsarQuantizedSpectrum Spectrum, object? State) DecodeQuantizedChannel(
        PulsarBitstreamReader sideInfoReader,
        PulsarRangeDecoder decoder,
        int spectrumLength,
        int[] trueBandOffsets,
        int activeBandCount,
        int archiveVersion)
    {
        var bands = new EncodedBand[BandCount];
        bool packedBandHeaders = archiveVersion >= 4;
        bool compactBandHeaders = archiveVersion >= 3;

        if (!compactBandHeaders)
        {
            _ = sideInfoReader.ReadUInt16();
        }

        var bandModes = new SpectralBandMode[activeBandCount];
        var pulseCounts = new int[activeBandCount];
        var energyQs = new uint[activeBandCount];
        var scaleQs = new uint[activeBandCount];
        var stepQs = new uint[activeBandCount];
        var gammaQs = new uint[activeBandCount];
        var spreadDecisions = new int[activeBandCount];

        for (int bandIndex = 0; bandIndex < activeBandCount; bandIndex++)
        {
            SpectralBandMode bandMode;
            int pulseCount;
            int spreadDecision;

            if (packedBandHeaders)
            {
                bandMode = sideInfoReader.ReadBits(1) == 0 ? SpectralBandMode.Zero : SpectralBandMode.Explicit;
                pulseCount = (int)sideInfoReader.ReadBits(12);
                spreadDecision = (int)sideInfoReader.ReadBits(2);
                if (bandMode == SpectralBandMode.Explicit)
                {
                    energyQs[bandIndex] = sideInfoReader.ReadBits(12);
                    scaleQs[bandIndex] = sideInfoReader.ReadBits(12);
                }
            }
            else if (compactBandHeaders)
            {
                bandMode = (SpectralBandMode)sideInfoReader.ReadByte();
                pulseCount = sideInfoReader.ReadUInt16();
                spreadDecision = sideInfoReader.ReadByte();
                if (bandMode == SpectralBandMode.Explicit)
                {
                    energyQs[bandIndex] = sideInfoReader.ReadUInt16();
                    scaleQs[bandIndex] = sideInfoReader.ReadUInt16();
                }
            }
            else
            {
                bandMode = (SpectralBandMode)sideInfoReader.ReadByte();
                int encodedWidth = sideInfoReader.ReadUInt16();
                pulseCount = sideInfoReader.ReadUInt16();
                spreadDecision = sideInfoReader.ReadByte();
                if (bandMode == SpectralBandMode.Explicit)
                {
                    (energyQs[bandIndex], scaleQs[bandIndex], stepQs[bandIndex], gammaQs[bandIndex]) = ReadRawBandParameters(sideInfoReader);
                }
                pulseCounts[bandIndex] = pulseCount;
                bandModes[bandIndex] = bandMode;
                spreadDecisions[bandIndex] = spreadDecision;
                continue;
            }

            if (bandMode != SpectralBandMode.Zero && bandMode != SpectralBandMode.Explicit)
            {
                throw new InvalidOperationException($"Unsupported spectral band mode {bandMode}.");
            }

            bandModes[bandIndex] = bandMode;
            pulseCounts[bandIndex] = pulseCount;
            spreadDecisions[bandIndex] = spreadDecision;
        }

        for (int bandIndex = 0; bandIndex < activeBandCount; bandIndex++)
        {
            int start = trueBandOffsets[bandIndex];
            int end = trueBandOffsets[bandIndex + 1];
            int trueWidth = end - start;

            int[] levels = new int[trueWidth];
            if (bandModes[bandIndex] == SpectralBandMode.Explicit)
            {
                int[] decodedLevels = DecodeBandLevels(decoder, trueWidth, Math.Max(0, pulseCounts[bandIndex]));
                int copyLength = Math.Min(decodedLevels.Length, trueWidth);
                for (int cursor = 0; cursor < copyLength; cursor++)
                {
                    levels[cursor] = decodedLevels[cursor];
                }
            }

            bands[bandIndex] = new EncodedBand
            {
                Mode = bandModes[bandIndex],
                EnergyQ = energyQs[bandIndex],
                ScaleQ = scaleQs[bandIndex],
                PulseCount = pulseCounts[bandIndex],
                SpreadDecision = spreadDecisions[bandIndex],
                StepQ = stepQs[bandIndex],
                GammaQ = gammaQs[bandIndex],
                ScaleDelta = 0,
                StepDelta = 0,
                GammaDelta = 0,
                Levels = levels,
            };
        }

        for (int fillBand = activeBandCount; fillBand < BandCount; fillBand++)
        {
            int width = trueBandOffsets[fillBand + 1] - trueBandOffsets[fillBand];

            bands[fillBand] = new EncodedBand
            {
                Mode = SpectralBandMode.Zero,
                EnergyQ = 0,
                ScaleQ = 0,
                PulseCount = 0,
                SpreadDecision = 0,
                StepQ = 0,
                GammaQ = 0,
                ScaleDelta = 0,
                StepDelta = 0,
                GammaDelta = 0,
                Levels = new int[width],
            };
        }

        var encodedBlock = new EncodedChannelBlock
        {
            BlockSize = spectrumLength * 2,
            StartOffsetUnits = 0,
            ActiveBandCount = activeBandCount,
            FramePulseBudget = 0,
            FrameBitBudget = 0,
            Bands = bands,
        };

        return (BuildQuantizedSpectrum(encodedBlock), null);
    }

    private static (float[] Samples, int SampleRate, int Channels) DecodeSpectralArchive(PulsarContainerFile container)
    {
        int sampleRate = container.Header.SampleRate;
        int channels = container.Header.ChannelCount;
        int totalFrames = checked((int)container.Header.TotalSamples);
        bool useMidSide = (container.Header.Flags & MidSideStereoFlag) != 0;
        bool hasEntropyEnvelope = container.Header.Version >= 5;
        bool isV6 = container.Header.Version >= 6; // V7 uses same decode flow with deterministic bit alloc

        if (isV6)
        {
            // V6 path: QuantBandMonoDecode → float[] coefficients directly
            var allBlocksV6 = new List<(int SampleStart, int BlockSize, float[] MdctCoeffs)>[channels];
            for (int ch = 0; ch < channels; ch++) allBlocksV6[ch] = new();

            foreach (PulsarSuperframePacket superframe in container.Superframes)
            {
                byte[] entropyPayload = UnwrapEntropyPayload(superframe.EntropyPayload, EntropyPayloadKind.Spectral, hasEntropyEnvelope);
                var decoder = new PulsarRangeDecoder(entropyPayload);
                var sideInfoDecoder = new PulsarRangeDecoder(superframe.SideInfo);
                int channelCount = (int)sideInfoDecoder.DecodeUInt(16);

                for (int ch = 0; ch < channelCount; ch++)
                {
                    int blockCount = (int)sideInfoDecoder.DecodeUInt(256);
                    var energyState = new CeltEnergy.State(CeltEnergy.MaxBands);

                    for (int b = 0; b < blockCount; b++)
                    {
                        int blockSizeIndex = (int)sideInfoDecoder.DecodeUInt((uint)PulsarBlockLadder.Steps.Length);
                        int segmentOffset = (int)sideInfoDecoder.DecodeUInt(256);
                        int activeBandCount = (int)sideInfoDecoder.DecodeUInt(BandCount + 1u);
                        int blockSize = PulsarBlockLadder.Steps[blockSizeIndex];
                        int spectrumLength = blockSize / 2;
                        int[] trueBandOffsets = BuildBandOffsets(spectrumLength, sampleRate, BandCount);

                        float[] mdctCoeffs = DecodeQuantizedChannelV7(
                            sideInfoDecoder, decoder, spectrumLength, trueBandOffsets, activeBandCount, energyState, sampleRate,
                            container.Header.BandwidthLimitHz > 0 ? container.Header.BandwidthLimitHz : DefaultBandwidthLimitHz);

                        int sampleStart = (superframe.Index * SuperframeSize) + (segmentOffset * ArchiveOffsetQuantum);
                        allBlocksV6[ch].Add((sampleStart, blockSize, mdctCoeffs));
                    }
                }
            }

            var channelBuffers = new float[channels][];
            for (int ch = 0; ch < channels; ch++) channelBuffers[ch] = ReconstructChannelFromCoeffs(allBlocksV6[ch], totalFrames);

            if (useMidSide && channels == 2) ApplyInverseMidSide(channelBuffers[0], channelBuffers[1], totalFrames);

            float[] output = new float[totalFrames * channels];
            for (int frame = 0; frame < totalFrames; frame++)
                for (int ch = 0; ch < channels; ch++)
                    output[(frame * channels) + ch] = channelBuffers[ch][frame];

            return (output, sampleRate, channels);
        }

        // Legacy v3-v5 path
        var allBlocks = new List<(int SampleStart, int BlockSize, PulsarQuantizedSpectrum Spectrum)>[channels];
        for (int ch = 0; ch < channels; ch++) allBlocks[ch] = new();

        foreach (PulsarSuperframePacket superframe in container.Superframes)
        {
            byte[] entropyPayload = UnwrapEntropyPayload(superframe.EntropyPayload, EntropyPayloadKind.Spectral, hasEntropyEnvelope);
            var decoder = new PulsarRangeDecoder(entropyPayload);

            PulsarBitstreamReader? sideInfoReader = container.Header.Version >= 5 ? null : new PulsarBitstreamReader(superframe.SideInfo);
            PulsarRangeDecoder? sideInfoDecoder = container.Header.Version >= 5 ? new PulsarRangeDecoder(superframe.SideInfo) : null;
            int channelCount = sideInfoDecoder is not null ? (int)sideInfoDecoder.DecodeUInt(16) : sideInfoReader!.ReadByte();

            for (int ch = 0; ch < channelCount; ch++)
            {
                int blockCount = sideInfoDecoder is not null ? (int)sideInfoDecoder.DecodeUInt(256) : sideInfoReader!.ReadByte();
                var energyState = new CeltEnergy.State(CeltEnergy.MaxBands);

                for (int b = 0; b < blockCount; b++)
                {
                    int blockSizeIndex = sideInfoDecoder is not null ? (int)sideInfoDecoder.DecodeUInt((uint)PulsarBlockLadder.Steps.Length) : sideInfoReader!.ReadByte();
                    int segmentOffset = sideInfoDecoder is not null ? (int)sideInfoDecoder.DecodeUInt(256) : sideInfoReader!.ReadByte();
                    int activeBandCount = sideInfoDecoder is not null ? (int)sideInfoDecoder.DecodeUInt(BandCount + 1u) : sideInfoReader!.ReadByte();
                    int blockSize = PulsarBlockLadder.Steps[blockSizeIndex];
                    int spectrumLength = blockSize / 2;
                    int[] trueBandOffsets = BuildBandOffsets(spectrumLength, sampleRate, BandCount);

                    (PulsarQuantizedSpectrum spectrum, _) = sideInfoDecoder is not null
                        ? DecodeQuantizedChannel(sideInfoDecoder, decoder, spectrumLength, trueBandOffsets, activeBandCount, energyState)
                        : DecodeQuantizedChannel(sideInfoReader!, decoder, spectrumLength, trueBandOffsets, activeBandCount, container.Header.Version);

                    int sampleStart = (superframe.Index * SuperframeSize) + (segmentOffset * ArchiveOffsetQuantum);
                    allBlocks[ch].Add((sampleStart, blockSize, spectrum));
                }
            }
        }

        var channelBuffersLegacy = new float[channels][];
        for (int ch = 0; ch < channels; ch++) channelBuffersLegacy[ch] = ReconstructChannel(allBlocks[ch], totalFrames);

        if (useMidSide && channels == 2) ApplyInverseMidSide(channelBuffersLegacy[0], channelBuffersLegacy[1], totalFrames);

        float[] outputLegacy = new float[totalFrames * channels];
        for (int frame = 0; frame < totalFrames; frame++)
            for (int ch = 0; ch < channels; ch++)
                outputLegacy[(frame * channels) + ch] = channelBuffersLegacy[ch][frame];

        return (outputLegacy, sampleRate, channels);
    }

    private static float[] ReconstructChannel(List<(int SampleStart, int BlockSize, PulsarQuantizedSpectrum Spectrum)> blocks, int totalFrames)
    {
        if (blocks.Count == 0) return new float[totalFrames];
        int maxHopSize = PulsarBlockLadder.GetHopSize(PulsarBlockLadder.MaxBlockSize);
        float[] output = new float[totalFrames + maxHopSize];

        for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            (int sampleStart, int blockSize, PulsarQuantizedSpectrum spectrum) = blocks[blockIndex];
            int previousBlockSize = blockIndex == 0 ? blockSize : blocks[blockIndex - 1].BlockSize;
            int nextBlockSize = blockIndex == blocks.Count - 1 ? blockSize : blocks[blockIndex + 1].BlockSize;
            ComputeAsymmetricOverlap(blockSize, previousBlockSize, nextBlockSize, out int leftOverlap, out _);
            int writeStart = sampleStart - leftOverlap;

            float[] mdctCoeffs = new float[blockSize / 2];
            PulsarQuantizer.DequantizeSpectrum(mdctCoeffs, spectrum);

            float[] reconstructed = new float[blockSize];
            PulsarTransformEngine.Imdct(mdctCoeffs, reconstructed);
            PulsarTransformEngine.ApplyWindow(reconstructed, GetCachedWindow(blockSize, previousBlockSize, nextBlockSize));

            for (int i = 0; i < blockSize; i++)
            {
                int outIdx = writeStart + i;
                if (outIdx >= 0 && outIdx < output.Length) output[outIdx] += reconstructed[i];
            }
        }

        float[] trimmed = new float[totalFrames];
        Array.Copy(output, 0, trimmed, 0, Math.Min(trimmed.Length, output.Length));
        return trimmed;
    }

    /// <summary>
    /// V6 reconstruction: takes pre-decoded float[] MDCT coefficients directly
    /// (from QuantBandMonoDecode), skipping the int[] levels → DequantizeSpectrum step.
    /// </summary>
    private static float[] ReconstructChannelFromCoeffs(List<(int SampleStart, int BlockSize, float[] MdctCoeffs)> blocks, int totalFrames)
    {
        if (blocks.Count == 0) return new float[totalFrames];
        int maxHopSize = PulsarBlockLadder.GetHopSize(PulsarBlockLadder.MaxBlockSize);
        float[] output = new float[totalFrames + maxHopSize];

        for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            (int sampleStart, int blockSize, float[] mdctCoeffs) = blocks[blockIndex];
            int previousBlockSize = blockIndex == 0 ? blockSize : blocks[blockIndex - 1].BlockSize;
            int nextBlockSize = blockIndex == blocks.Count - 1 ? blockSize : blocks[blockIndex + 1].BlockSize;
            ComputeAsymmetricOverlap(blockSize, previousBlockSize, nextBlockSize, out int leftOverlap, out _);
            int writeStart = sampleStart - leftOverlap;

            float[] reconstructed = new float[blockSize];
            PulsarTransformEngine.Imdct(mdctCoeffs, reconstructed);
            PulsarTransformEngine.ApplyWindow(reconstructed, GetCachedWindow(blockSize, previousBlockSize, nextBlockSize));

            for (int i = 0; i < blockSize; i++)
            {
                int outIdx = writeStart + i;
                if (outIdx >= 0 && outIdx < output.Length) output[outIdx] += reconstructed[i];
            }
        }

        float[] trimmed = new float[totalFrames];
        Array.Copy(output, 0, trimmed, 0, Math.Min(trimmed.Length, output.Length));
        return trimmed;
    }

    private static void ApplyInverseMidSide(float[] left, float[] right, int totalFrames)
    {
        for (int i = 0; i < totalFrames; i++)
        {
            float mid = left[i];
            float side = right[i];
            left[i] = mid + side;
            right[i] = mid - side;
        }
    }

    private static float[][] Deinterleave(float[] interleavedSamples, int channels)
    {
        int frameCount = interleavedSamples.Length / channels;
        var channelBuffers = new float[channels][];
        for (int channel = 0; channel < channels; channel++) channelBuffers[channel] = new float[frameCount];
        for (int frame = 0; frame < frameCount; frame++)
            for (int channel = 0; channel < channels; channel++)
                channelBuffers[channel][frame] = interleavedSamples[(frame * channels) + channel];
        return channelBuffers;
    }

    private static float[] GetCachedWindow(int length, int previousBlockSize, int nextBlockSize) =>
        WindowCache.GetOrAdd((length, previousBlockSize, nextBlockSize), key => BuildAsymmetricWindow(key.Size, key.PrevSize, key.NextSize));

    private static float[] BuildAsymmetricWindow(int length, int previousBlockSize, int nextBlockSize)
    {
        ComputeAsymmetricOverlap(length, previousBlockSize, nextBlockSize, out int leftOverlap, out int rightOverlap);
        var window = new float[length];
        int flatEnd = length - rightOverlap;

        for (int i = 0; i < leftOverlap && i < length; i++)
        {
            window[i] = (float)Math.Sin(Math.PI * (i + 0.5f) / (2.0f * leftOverlap));
        }

        for (int i = leftOverlap; i < flatEnd; i++)
        {
            window[i] = 1.0f;
        }

        for (int i = 0; i < rightOverlap && flatEnd + i < length; i++)
        {
            window[flatEnd + i] = (float)Math.Cos(Math.PI * (i + 0.5f) / (2.0f * rightOverlap));
        }

        return window;
    }

    private static void ComputeAsymmetricOverlap(int currentBlockSize, int previousBlockSize, int nextBlockSize, out int leftOverlap, out int rightOverlap)
    {
        leftOverlap = Math.Min(previousBlockSize / 2, currentBlockSize / 2);
        rightOverlap = Math.Min(currentBlockSize / 2, nextBlockSize / 2);
    }

    private static List<SpectralSuperframePlan> BuildSuperframePlans(IReadOnlyList<PulsarFramePlan> framePlans, int sampleCount)
    {
        var superframes = new List<SpectralSuperframePlan>();
        int minimumSuperframeCount = Math.Max(1, (sampleCount + SuperframeSize - 1) / SuperframeSize);
        var superframeBlocks = new List<List<ArchiveBlockPlan>>();

        int sampleStart = 0;
        foreach (PulsarFramePlan framePlan in framePlans)
        {
            int superframeIndex = Math.Max(0, sampleStart / SuperframeSize);
            while (superframeBlocks.Count <= superframeIndex)
            {
                superframeBlocks.Add(new List<ArchiveBlockPlan>());
            }

            int superframeSampleStart = superframeIndex * SuperframeSize;
            int localOffsetSamples = sampleStart - superframeSampleStart;
            int startOffsetUnits = localOffsetSamples / ArchiveOffsetQuantum;
            superframeBlocks[superframeIndex].Add(new ArchiveBlockPlan
            {
                BlockSize = framePlan.BlockSize,
                PreviousBlockSize = framePlan.PreviousBlockSize,
                NextBlockSize = framePlan.NextBlockSize,
                AnalysisSegmentIndex = framePlan.SegmentIndex,
                StartOffsetUnits = startOffsetUnits,
            });

            ComputeAsymmetricOverlap(framePlan.BlockSize, framePlan.PreviousBlockSize, framePlan.NextBlockSize, out _, out int rightOverlap);
            sampleStart += framePlan.BlockSize - rightOverlap;
        }

        int superframeCount = Math.Max(minimumSuperframeCount, superframeBlocks.Count);
        for (int i = 0; i < superframeCount; i++)
        {
            superframes.Add(new SpectralSuperframePlan
            {
                Index = i,
                SampleStart = i * SuperframeSize,
                PatternId = 0,
                LargeBlockState = PulsarLargeBlockState.Standalone,
                Blocks = i < superframeBlocks.Count ? superframeBlocks[i].ToArray() : Array.Empty<ArchiveBlockPlan>(),
            });
        }
        return superframes;
    }

    private static int ComputeActiveBandCount(EncodedBand[] bands)
    {
        for (int bandIndex = bands.Length - 1; bandIndex >= 0; bandIndex--)
            if (bands[bandIndex].Mode != SpectralBandMode.Zero || !IsAllZero(bands[bandIndex].Levels)) return bandIndex + 1;
        return 0;
    }

    private static void WriteRawBandParameters(PulsarBitstreamWriter writer, EncodedBand band)
    {
        writer.WriteUInt16((ushort)band.EnergyQ);
        writer.WriteUInt16((ushort)band.ScaleQ);
        writer.WriteUInt16((ushort)band.StepQ);
        writer.WriteUInt16((ushort)band.GammaQ);
    }

    private static (uint EnergyQ, uint ScaleQ, uint StepQ, uint GammaQ) ReadRawBandParameters(PulsarBitstreamReader reader) => (reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16());

    private static float[] AnalyzeBlockSpectrum(float[] samples, int sampleStart, int blockSize, int previousBlockSize, int nextBlockSize)
    {
        ComputeAsymmetricOverlap(blockSize, previousBlockSize, nextBlockSize, out int leftOverlap, out _);
        float[] frame = new float[blockSize];
        int sourceStart = sampleStart - leftOverlap;
        
        for (int i = 0; i < blockSize; i++)
        {
            int sourceIndex = sourceStart + i;
            frame[i] = sourceIndex >= 0 && sourceIndex < samples.Length ? samples[sourceIndex] : 0.0f;
        }
        
        PulsarTransformEngine.ApplyWindow(frame, GetCachedWindow(blockSize, previousBlockSize, nextBlockSize));
        float[] spectrum = new float[blockSize / 2];
        PulsarTransformEngine.Mdct(frame, spectrum);
        return spectrum;
    }
    
    private static int[] BuildBandOffsets(int mdctBins, int sampleRate, int bandCount)
    {
        float nyquist = sampleRate * 0.5f;
        float maxBark = HzToBark(nyquist);
        var offsets = new int[bandCount + 1];
        offsets[0] = 0;

        for (int i = 1; i < bandCount; i++)
        {
            float bark = maxBark * i / bandCount;
            float hz = BarkToHz(bark);
            int bin = (int)MathF.Round((hz / nyquist) * (mdctBins - 1));
            offsets[i] = Math.Clamp(bin, offsets[i - 1] + 1, mdctBins - (bandCount - i));
        }

        offsets[bandCount] = mdctBins;
        return offsets;
    }

    /// <summary>
    /// Opus eband5ms table: 21 critical bands for 5ms (base) frame.
    /// Reference: celt/modes.c in libopus.
    /// </summary>
    private static readonly int[] OpusEBand5ms =
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 34, 40, 48, 60, 66, 72, 78, 87, 94, 100 };

    /// <summary>
    /// Compute LM (log2 of temporal resolution ratio) for the given spectrum length.
    /// Also returns the base eBands (for the shortest "virtual" block) and the actual
    /// band offsets for the full spectrum.  The cache should be built from baseBandWidths
    /// with maxLm=LM, and the PVQ encoder/decoder called with this LM value.
    /// This mirrors Opus's approach where eBands are stored for the base frame and
    /// N = M * eBands_diff at LM level, guaranteeing that the leaf N after LM+1 splits
    /// matches the cache's level-0 entry exactly.
    /// </summary>
    private static (int LM, int[] BandOffsets, int[] BaseBandWidths) BuildOpusBandLayout(int specLen)
    {
        int LM = 0;
        while ((specLen >> (LM + 1)) > 128) LM++;

        int M = 1 << LM;
        int baseSpecLen = specLen / M;

        // Scale Opus eBands from 0..100 to 0..baseSpecLen
        int nbBands = OpusEBand5ms.Length - 1; // 21
        var baseEBands = new short[nbBands + 1];
        for (int i = 0; i <= nbBands; i++)
            baseEBands[i] = (short)Math.Clamp(
                (int)Math.Round((double)OpusEBand5ms[i] * baseSpecLen / OpusEBand5ms[nbBands]),
                i > 0 ? baseEBands[i - 1] + 1 : 0,
                baseSpecLen);
        baseEBands[nbBands] = (short)baseSpecLen;

        // Ensure all band widths are even (or 1) so the recursive PVQ split
        // never truncates coefficients via N >>= 1.  Opus's native eBands have
        // this property; our rescaling can introduce odd widths.
        for (int i = 0; i < nbBands; i++)
        {
            int w = baseEBands[i + 1] - baseEBands[i];
            if (w > 1 && (w & 1) != 0)
            {
                // Widen by 1 — shift this band's upper edge up.
                // Cascade: all subsequent edges shift up by 1.
                for (int j = i + 1; j < nbBands; j++)
                    baseEBands[j]++;
                // If last edge exceeded baseSpecLen, pull it back and widen
                // the previous band(s) instead — but normally baseSpecLen has
                // enough headroom.
                if (baseEBands[nbBands - 1] >= baseSpecLen)
                    baseEBands[nbBands - 1] = (short)(baseSpecLen - 1);
            }
        }
        baseEBands[nbBands] = (short)baseSpecLen;

        // Actual band offsets = M * baseEBands
        var bandOffsets = new int[nbBands + 1];
        for (int i = 0; i <= nbBands; i++)
            bandOffsets[i] = Math.Min(M * baseEBands[i], specLen);
        bandOffsets[nbBands] = specLen;

        // Base band widths for cache computation
        var baseBandWidths = new int[nbBands];
        for (int i = 0; i < nbBands; i++)
            baseBandWidths[i] = Math.Max(1, baseEBands[i + 1] - baseEBands[i]);

        return (LM, bandOffsets, baseBandWidths);
    }

    private static float HzToBark(float hz)
    {
        return 13.0f * MathF.Atan(0.00076f * hz) + 3.5f * MathF.Atan(MathF.Pow(hz / 7500.0f, 2.0f));
    }

    private static float BarkToHz(float bark)
    {
        float low = 0.0f;
        float high = 24000.0f;

        for (int i = 0; i < 20; i++)
        {
            float mid = 0.5f * (low + high);
            float value = HzToBark(mid);
            if (value < bark)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return 0.5f * (low + high);
    }

    private static uint QuantizeLinearValue(float value, float minValue, float maxValue, int totalLevels) => (uint)Math.Clamp((int)MathF.Round(((Math.Clamp(value, minValue, maxValue) - minValue) / Math.Max(1e-12f, maxValue - minValue)) * (totalLevels - 1)), 0, totalLevels - 1);
    private static float DequantizeLinearValue(uint value, float minValue, float maxValue, int totalLevels) => minValue + ((maxValue - minValue) * (value / (float)Math.Max(1, totalLevels - 1)));
    private static uint QuantizeLogValue(float value, float minValue, float maxValue, int totalLevels) => (uint)Math.Clamp((int)MathF.Round(((MathF.Log2(Math.Clamp(value, minValue, maxValue)) - MathF.Log2(minValue)) / Math.Max(1e-12f, MathF.Log2(maxValue) - MathF.Log2(minValue))) * (totalLevels - 1)), 0, totalLevels - 1);
    private static float DequantizeLogValue(uint value, float minValue, float maxValue, int totalLevels) => MathF.Pow(2.0f, MathF.Log2(minValue) + ((MathF.Log2(maxValue) - MathF.Log2(minValue)) * (value / (float)Math.Max(1, totalLevels - 1))));

    private static byte[] BuildPcmSideInfo(int frameLength) { var writer = new PulsarBitstreamWriter(); writer.WriteUInt16((ushort)frameLength); return writer.ToArray(); }
    private static int ReadPcmSideInfo(byte[] sideInfo) => new PulsarBitstreamReader(sideInfo).ReadUInt16();
    
    // --- RESTORED PCM PAYLOAD METHODS ---
    private static byte[] EncodePcmSuperframePayload(float[] interleavedSamples, int channels, int frameStart, int frameLength)
    {
        var encoder = new PulsarRangeEncoder();
        int[] previousSamples = new int[channels];

        for (int frame = 0; frame < frameLength; frame++)
        {
            int sampleFrameIndex = frameStart + frame;
            for (int channel = 0; channel < channels; channel++)
            {
                float sample = interleavedSamples[(sampleFrameIndex * channels) + channel];
                int quantized = FloatToPcm16(sample);
                int delta = quantized - previousSamples[channel];
                uint zigZag = ZigZagEncode(delta);
                EncodeDelta(encoder, zigZag);
                previousSamples[channel] = quantized;
            }
        }
        return encoder.Finish();
    }

    private static void DecodePcmSuperframePayload(byte[] payload, float[] destination, int channels, int frameStart, int frameLength)
    {
        var decoder = new PulsarRangeDecoder(payload);
        int[] previousSamples = new int[channels];

        for (int frame = 0; frame < frameLength; frame++)
        {
            int sampleFrameIndex = frameStart + frame;
            for (int channel = 0; channel < channels; channel++)
            {
                uint zigZag = DecodeDelta(decoder);
                int delta = ZigZagDecode(zigZag);
                int quantized = previousSamples[channel] + delta;
                quantized = Math.Clamp(quantized, short.MinValue, short.MaxValue);
                destination[(sampleFrameIndex * channels) + channel] = quantized / 32768f;
                previousSamples[channel] = quantized;
            }
        }
    }

    private static void EncodeDelta(PulsarRangeEncoder encoder, uint zigZag)
    {
        if (zigZag < 16) { encoder.EncodeBinaryValue(0, 2); encoder.EncodeBits(zigZag, 4); }
        else if (zigZag < 128) { encoder.EncodeBinaryValue(1, 2); encoder.EncodeBits(zigZag, 7); }
        else if (zigZag < 1024) { encoder.EncodeBinaryValue(2, 2); encoder.EncodeBits(zigZag, 10); }
        else { encoder.EncodeBinaryValue(3, 2); encoder.EncodeBits(zigZag, 17); }
    }

    private static uint DecodeDelta(PulsarRangeDecoder decoder)
    {
        uint category = decoder.DecodeBinary(2);
        return category switch
        {
            0 => decoder.DecodeBits(4),
            1 => decoder.DecodeBits(7),
            2 => decoder.DecodeBits(10),
            _ => decoder.DecodeBits(17),
        };
    }
    // ------------------------------------

    private static void EncodeZeroRun(PulsarRangeEncoder encoder, int zeroRun) { if (zeroRun == 1) encoder.EncodeIcdfSymbol(0, ZeroRunIcdf, 8); else if (zeroRun <= 3) { encoder.EncodeIcdfSymbol(1, ZeroRunIcdf, 8); encoder.EncodeBits((uint)(zeroRun - 2), 1); } else if (zeroRun <= 7) { encoder.EncodeIcdfSymbol(2, ZeroRunIcdf, 8); encoder.EncodeBits((uint)(zeroRun - 4), 2); } else if (zeroRun <= 15) { encoder.EncodeIcdfSymbol(3, ZeroRunIcdf, 8); encoder.EncodeBits((uint)(zeroRun - 8), 3); } else { encoder.EncodeIcdfSymbol(4, ZeroRunIcdf, 8); encoder.EncodeUInt((uint)(zeroRun - 16), 1u << 16); } }
    private static int DecodeZeroRun(PulsarRangeDecoder decoder) { int symbol = decoder.DecodeIcdfSymbol(ZeroRunIcdf, 8); return symbol switch { 0 => 1, 1 => 2 + (int)decoder.DecodeBits(1), 2 => 4 + (int)decoder.DecodeBits(2), 3 => 8 + (int)decoder.DecodeBits(3), 4 => 16 + (int)decoder.DecodeUInt(1u << 16), _ => throw new InvalidOperationException($"Invalid ZERO_RUN symbol {symbol}."), }; }
    private static void EncodeMagnitudeValue(PulsarRangeEncoder encoder, int magnitude) { if (magnitude == 1) encoder.EncodeIcdfSymbol(0, MagnitudeIcdf, 8); else if (magnitude == 2) encoder.EncodeIcdfSymbol(1, MagnitudeIcdf, 8); else if (magnitude <= 4) { encoder.EncodeIcdfSymbol(2, MagnitudeIcdf, 8); encoder.EncodeBits((uint)(magnitude - 3), 1); } else if (magnitude <= 8) { encoder.EncodeIcdfSymbol(3, MagnitudeIcdf, 8); encoder.EncodeBits((uint)(magnitude - 5), 2); } else if (magnitude <= 16) { encoder.EncodeIcdfSymbol(4, MagnitudeIcdf, 8); encoder.EncodeBits((uint)(magnitude - 9), 3); } else { encoder.EncodeIcdfSymbol(5, MagnitudeIcdf, 8); encoder.EncodeUInt((uint)(magnitude - 17), 1u << 16); } }
    private static int DecodeMagnitudeValue(PulsarRangeDecoder decoder) { int symbol = decoder.DecodeIcdfSymbol(MagnitudeIcdf, 8); return symbol switch { 0 => 1, 1 => 2, 2 => 3 + (int)decoder.DecodeBits(1), 3 => 5 + (int)decoder.DecodeBits(2), 4 => 9 + (int)decoder.DecodeBits(3), _ => 17 + (int)decoder.DecodeUInt(1u << 16), }; }
    private static bool IsAllZero(int[] levels) { for (int i = 0; i < levels.Length; i++) if (levels[i] != 0) return false; return true; }
    private static void ApplyMidSideStereo(float[] left, float[] right) { for (int i = 0; i < left.Length; i++) { float l = left[i], r = right[i]; left[i] = 0.5f * (l + r); right[i] = 0.5f * (l - r); } }
    private static int FloatToPcm16(float sample) => (int)MathF.Round(Math.Clamp(sample, -1.0f, 0.9999695f) * 32767.0f);
    private static uint ZigZagEncode(int value) => (uint)((value << 1) ^ (value >> 31));
    private static int ZigZagDecode(uint value) => (int)((value >> 1) ^ (uint)-(int)(value & 1));
    private static void ClearDebugTrace() { }
}
