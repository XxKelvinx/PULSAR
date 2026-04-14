using System;
using System.Collections.Generic;
using Pulsar.Psycho;

public static class PulsarSuperframeArchiveCodec
{
    private static readonly object DebugTraceLock = new();
    private static readonly List<string> EncoderDebugTrace = [];
    private static readonly List<string> DecoderDebugTrace = [];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Size, int PrevSize, int NextSize), float[]> WindowCache = new();
    
    private const ushort PcmEntropyFlag = 0x0001;
    private const ushort SpectralEntropyFlag = 0x0002;
    private const ushort MidSideStereoFlag = 0x0004;
    private const int SuperframeSize = 2048;
    private const int SuperframeSegmentCount = SuperframeSize / PulsarBlockLadder.ControlHopSize;
    private const int ArchiveOffsetQuantum = PulsarBlockLadder.MinBlockSize / 2;
    private const int PsychoFftSize = 2048;
    private const int BandCount = 21;
    private const int JointStereoThresholdKbps = 160;
    
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
    
    private const float MinGamma = 0.55f;
    private const float MaxGamma = 0.98f;
    private const float MinScale = 1e-12f;
    private const float MaxScale = 65536.0f;
    private const float MinNormalizedStep = 1e-9f;
    private const float MaxNormalizedStep = 32.0f;

    private enum SpectralBandMode : byte
    {
        Zero = 0, Explicit = 1, DeltaPrevious = 2, ReusePrevious = 3, DeltaStereo = 4, ReuseStereo = 5,
    }

    private sealed class SpectralArchivePlan
    {
        public required int SampleRate { get; init; }
        public required int Channels { get; init; }
        public required int TargetKbps { get; init; }
        public required bool UseMidSideStereo { get; init; }
        public required int OriginalFrameCount { get; init; }
        public required double TotalPerceptualEntropy { get; init; }
        public required double AveragePerceptualEntropy { get; init; }
        public required double TotalSongBits { get; init; }
        public required int BaseGlobalGain { get; init; }
        public required List<PulsarFramePlan> SharedFramePlans { get; init; }
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
        public required uint ScaleQ { get; init; }
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
        public required EncodedBand[] Bands { get; init; }
    }

    private sealed class EncodedChannelSuperframe
    {
        public required EncodedChannelBlock[] Blocks { get; init; }
    }

    private sealed class BandParameterState
    {
        public required uint ScaleQ { get; init; }
        public required uint StepQ { get; init; }
        public required uint GammaQ { get; init; }
    }

    private sealed class ChannelParameterState
    {
        public required BandParameterState[] Bands { get; init; }
    }

    public static byte[] EncodePcmArchive(float[] interleavedSamples, int sampleRate, int channels)
    {
        int frameCount = interleavedSamples.Length / channels;
        int superframeCount = (frameCount + SuperframeSize - 1) / SuperframeSize;

        var container = new PulsarContainerFile();
        container.Header.Version = 1;
        container.Header.Flags = PcmEntropyFlag;
        container.Header.SampleRate = sampleRate;
        container.Header.ChannelCount = channels;
        container.Header.TotalSamples = frameCount;

        for (int superframeIndex = 0; superframeIndex < superframeCount; superframeIndex++)
        {
            int frameStart = superframeIndex * SuperframeSize;
            int frameLength = Math.Min(SuperframeSize, frameCount - frameStart);
            byte[] payload = EncodePcmSuperframePayload(interleavedSamples, channels, frameStart, frameLength);
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
        SpectralArchivePlan plan = AnalyzeSpectralArchive(interleavedSamples, sampleRate, channels, targetKbps, effectiveQuality);
        var container = new PulsarContainerFile();
        container.Header.Version = 1;
        bool useMidSideStereo = channels == 2 && targetKbps <= JointStereoThresholdKbps;
        container.Header.Flags = (ushort)(SpectralEntropyFlag | (useMidSideStereo ? MidSideStereoFlag : 0));
        container.Header.SampleRate = plan.SampleRate;
        container.Header.ChannelCount = plan.Channels;
        container.Header.TotalSamples = plan.OriginalFrameCount;

        var previousStates = new ChannelParameterState[plan.Channels];
        var allocator = new PulsarAllocator(new PulsarAllocationConfig { Quality = effectiveQuality, SampleRate = sampleRate, HopSize = PulsarBlockLadder.ControlHopSize });

        foreach (SpectralSuperframePlan superframe in plan.Superframes)
        {
            EncodedChannelSuperframe[] encodedChannels = BuildEncodedSuperframe(plan, superframe, previousStates, allocator);
            byte[] sideInfo = BuildSpectralSideInfo(encodedChannels);

            for (int channel = 0; channel < encodedChannels.Length; channel++)
            {
                previousStates[channel] = BuildParameterState(encodedChannels[channel]);
            }

            container.Superframes.Add(new PulsarSuperframePacket
            {
                Index = superframe.Index, PatternId = superframe.PatternId, LargeBlockState = superframe.LargeBlockState,
                SideInfo = sideInfo, EntropyPayload = Array.Empty<byte>(),
            });
        }

        return new PulsarPacker().Pack(container);
    }

    public static (float[] Samples, int SampleRate, int Channels) RenderSpectralPcm(float[] interleavedSamples, int sampleRate, int channels, int targetKbps, int? quality = null)
    {
        int effectiveQuality = ResolveArchiveQuality(quality, targetKbps);
        SpectralArchivePlan plan = AnalyzeSpectralArchive(interleavedSamples, sampleRate, channels, targetKbps, effectiveQuality);
        var allBlocks = new List<(int SampleStart, int BlockSize, PulsarQuantizedSpectrum Spectrum)>[plan.Channels];
        for (int ch = 0; ch < plan.Channels; ch++) allBlocks[ch] = new();

        var previousStates = new ChannelParameterState[plan.Channels];
        var allocator = new PulsarAllocator(new PulsarAllocationConfig { Quality = effectiveQuality, SampleRate = sampleRate, HopSize = PulsarBlockLadder.ControlHopSize });
        
        foreach (SpectralSuperframePlan superframe in plan.Superframes)
        {
            EncodedChannelSuperframe[] encodedChannels = BuildEncodedSuperframe(plan, superframe, previousStates, allocator);
            for (int channel = 0; channel < encodedChannels.Length; channel++)
            {
                EncodedChannelSuperframe encodedChannel = encodedChannels[channel];
                for (int blockIndex = 0; blockIndex < encodedChannel.Blocks.Length; blockIndex++)
                {
                    EncodedChannelBlock block = encodedChannel.Blocks[blockIndex];
                    int sampleStart = superframe.SampleStart + (block.StartOffsetUnits * ArchiveOffsetQuantum);
                    allBlocks[channel].Add((sampleStart, block.BlockSize, BuildQuantizedSpectrum(block)));
                }
                previousStates[channel] = BuildParameterState(encodedChannel);
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

    public static (float[] Samples, int SampleRate, int Channels) DecodePcmArchive(byte[] payload)
    {
        PulsarContainerFile container = new PulsarPacker().Unpack(payload);
        int channels = container.Header.ChannelCount;
        int totalFrames = checked((int)container.Header.TotalSamples);
        float[] samples = new float[totalFrames * channels];
        int writeFrame = 0;

        foreach (PulsarSuperframePacket superframe in container.Superframes)
        {
            int frameLength = ReadPcmSideInfo(superframe.SideInfo);
            DecodePcmSuperframePayload(superframe.EntropyPayload, samples, channels, writeFrame, frameLength);
            writeFrame += frameLength;
        }

        return (samples, container.Header.SampleRate, channels);
    }

    public static (float[] Samples, int SampleRate, int Channels) DecodeArchive(byte[] payload)
    {
        PulsarContainerFile container = new PulsarPacker().Unpack(payload);
        if ((container.Header.Flags & SpectralEntropyFlag) != 0) return DecodeSpectralArchive(container);
        if ((container.Header.Flags & PcmEntropyFlag) != 0) return DecodePcmArchive(payload);
        throw new InvalidOperationException("Unknown PULSAR archive payload type.");
    }

    private static SpectralArchivePlan AnalyzeSpectralArchive(float[] interleavedSamples, int sampleRate, int channels, int targetKbps, int quality)
    {
        int frameCount = interleavedSamples.Length / channels;
        float[][] channelBuffers = Deinterleave(interleavedSamples, channels);
        bool useMidSideStereo = channels == 2 && targetKbps <= JointStereoThresholdKbps;
        if (useMidSideStereo) ApplyMidSideStereo(channelBuffers[0], channelBuffers[1]);

        var psychoByChannel = new IReadOnlyList<PulsarPsychoResult>[channels];
        var sharedPlanner = new PulsarPlanner();
        List<PulsarFramePlan> sharedFramePlans = sharedPlanner.PlanLegacyRenderSong(channelBuffers[0]);
        List<SpectralSuperframePlan> superframes = BuildSuperframePlans(sharedFramePlans, frameCount);

        for (int channel = 0; channel < channels; channel++)
        {
            var psycho = new PulsarPsycho(new PulsarPsychoSettings { SampleRate = sampleRate, FftSize = PsychoFftSize, HopSize = PulsarBlockLadder.ControlHopSize });
            psychoByChannel[channel] = psycho.AnalyzeSong(channelBuffers[channel]);
        }

        (double totalPerceptualEntropy, double averagePerceptualEntropy) = ComputeGlobalPerceptualEntropyStats(psychoByChannel);
        double totalSongBits = Math.Max(256.0, targetKbps * 1000.0 * (frameCount / (double)Math.Max(1, sampleRate)));
        int baseGlobalGain = ComputeTrueVbrBaseGlobalGain(quality, targetKbps, totalPerceptualEntropy, totalSongBits);

        var channelPlans = new SpectralChannelPlan[channels];
        double totalBitsPerControlFrame = Math.Max(32.0, targetKbps * 1000.0 * (PulsarBlockLadder.ControlHopSize / (double)sampleRate));
        int[][][] channelBandAllocations = AllocateSpectralBandBitsGlobal(psychoByChannel, totalBitsPerControlFrame, useMidSideStereo);

        for (int channel = 0; channel < channels; channel++)
        {
            channelPlans[channel] = new SpectralChannelPlan { Samples = channelBuffers[channel], PsychoFrames = psychoByChannel[channel], Allocations = BuildSpectralAllocations(channelBandAllocations[channel]) };
        }

        return new SpectralArchivePlan
        {
            SampleRate = sampleRate, Channels = channels, TargetKbps = targetKbps, UseMidSideStereo = useMidSideStereo,
            OriginalFrameCount = frameCount, TotalPerceptualEntropy = totalPerceptualEntropy, AveragePerceptualEntropy = averagePerceptualEntropy,
            TotalSongBits = totalSongBits, BaseGlobalGain = baseGlobalGain, SharedFramePlans = sharedFramePlans,
            Superframes = superframes, ChannelPlans = channelPlans,
        };
    }

    private static EncodedChannelSuperframe[] BuildEncodedSuperframe(SpectralArchivePlan plan, SpectralSuperframePlan superframe, ChannelParameterState[] previousStates, PulsarAllocator allocator)
    {
        PulsarQuantizedSpectrum[][] allQuantized = new PulsarQuantizedSpectrum[plan.Channels][];
        for (int ch = 0; ch < plan.Channels; ch++) allQuantized[ch] = new PulsarQuantizedSpectrum[superframe.Blocks.Length];

        for (int channel = 0; channel < plan.Channels; channel++)
        {
            SpectralChannelPlan channelPlan = plan.ChannelPlans[channel];
            for (int blockIndex = 0; blockIndex < superframe.Blocks.Length; blockIndex++)
            {
                ArchiveBlockPlan block = superframe.Blocks[blockIndex];
                PulsarPsychoResult psycho = SelectPsychoFrame(channelPlan.PsychoFrames, block.AnalysisSegmentIndex, block.BlockSize);
                
                float[] spectrum = AnalyzeBlockSpectrum(
                    channelPlan.Samples,
                    superframe.SampleStart + (block.StartOffsetUnits * ArchiveOffsetQuantum),
                    block.BlockSize,
                    block.PreviousBlockSize,
                    block.NextBlockSize);

                double localPeRatio = psycho.PerceptualEntropy / Math.Max(1.0, plan.AveragePerceptualEntropy);
                double peModulation = Math.Clamp(localPeRatio - 1.0, -0.75, 2.5);
                int frameGain = plan.BaseGlobalGain - (int)Math.Round(peModulation * 100.0);
                if (psycho.TotalEnergyDb < -48.0f) frameGain += 120;
                frameGain = Math.Clamp(frameGain, 0, 550);

                int[] dummyBandBits = new int[BandCount];
                Array.Fill(dummyBandBits, 256);

                allQuantized[channel][blockIndex] = PulsarQuantizer.QuantizeSpectrumDetailed(spectrum, dummyBandBits, psycho, frameGain);
            }
        }

        return BuildEncodedChannelsFromQuantized(superframe, allQuantized, previousStates);
    }

    private static (double, double) ComputeGlobalPerceptualEntropyStats(IReadOnlyList<PulsarPsychoResult>[] psychoByChannel)
    {
        double totalPe = 0.0;
        int count = 0;
        foreach (var channel in psychoByChannel)
            foreach (var frame in channel) { totalPe += Math.Max(1.0f, frame.PerceptualEntropy); count++; }
        return (totalPe, count > 0 ? totalPe / count : 1.0);
    }

    private static int ResolveArchiveQuality(int? explicitQuality, int targetKbps)
    {
        if (explicitQuality.HasValue) return Math.Clamp(explicitQuality.Value, 0, 9);
        int[] nominalKbps = [320, 288, 256, 224, 192, 160, 128, 112, 96, 80];
        int best = 0, bestDist = int.MaxValue;
        for (int i = 0; i < nominalKbps.Length; i++)
        {
            int dist = Math.Abs(nominalKbps[i] - targetKbps);
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    private static int ComputeTrueVbrBaseGlobalGain(int quality, int targetKbps, double totalPerceptualEntropy, double totalSongBits)
    {
        // Quality 0 = best quality (low quantization gain), Quality 9 = worst quality (high quantization gain)
        double qAnchor = quality switch { 0 => 10.0, 1 => 30.0, 2 => 60.0, 3 => 100.0, 4 => 150.0, 5 => 200.0, 6 => 250.0, 7 => 300.0, 8 => 350.0, _ => 400.0 };
        return (int)Math.Round(qAnchor);
    }

    private static EncodedChannelSuperframe[] BuildEncodedChannelsFromQuantized(SpectralSuperframePlan superframe, PulsarQuantizedSpectrum[][] allQuantized, ChannelParameterState[] previousStates)
    {
        var channels = new EncodedChannelSuperframe[allQuantized.Length];
        for (int channel = 0; channel < allQuantized.Length; channel++)
        {
            var encodedBlocks = new List<EncodedChannelBlock>(superframe.Blocks.Length);
            ChannelParameterState? previous = previousStates[channel];

            for (int blockIndex = 0; blockIndex < superframe.Blocks.Length; blockIndex++)
            {
                ArchiveBlockPlan block = superframe.Blocks[blockIndex];
                EncodedChannelBlock encodedBlock = ConvertQuantizedBlock(allQuantized[channel][blockIndex], block.BlockSize, block.StartOffsetUnits, null, null);
                encodedBlocks.Add(encodedBlock);
                previous = BuildParameterState(encodedBlock);
            }
            channels[channel] = new EncodedChannelSuperframe { Blocks = encodedBlocks.ToArray() };
        }
        return channels;
    }

    private static EncodedChannelBlock ConvertQuantizedBlock(PulsarQuantizedSpectrum quantized, int blockSize, int startOffsetUnits, ChannelParameterState? previous, ChannelParameterState? stereoReference)
    {
        var bands = new EncodedBand[quantized.Bands.Length];
        for (int bandIndex = 0; bandIndex < quantized.Bands.Length; bandIndex++)
        {
            PulsarQuantizedBand band = quantized.Bands[bandIndex];
            int[] levels = (int[])band.Levels.Clone();
            if (IsAllZero(levels))
            {
                bands[bandIndex] = new EncodedBand { Mode = SpectralBandMode.Zero, ScaleQ = 0, StepQ = 0, GammaQ = 0, ScaleDelta = 0, StepDelta = 0, GammaDelta = 0, Levels = levels };
                continue;
            }

            uint scaleQ = band.ScaleQ;
            uint stepQ = band.StepQ;
            uint gammaQ = band.GammaQ;
            (BandParameterState? state, SpectralBandMode reuseMode) = GetBestReference(previous, stereoReference, bandIndex, scaleQ, stepQ, gammaQ);

            if (state is not null)
            {
                if (scaleQ == state.ScaleQ && stepQ == state.StepQ && gammaQ == state.GammaQ)
                {
                    bands[bandIndex] = new EncodedBand { Mode = reuseMode, ScaleQ = scaleQ, StepQ = stepQ, GammaQ = gammaQ, ScaleDelta = 0, StepDelta = 0, GammaDelta = 0, Levels = levels };
                    continue;
                }
                if (CanDeltaEncode(scaleQ, state.ScaleQ, DeltaScaleLimit) && CanDeltaEncode(stepQ, state.StepQ, DeltaStepLimit) && CanDeltaEncode(gammaQ, state.GammaQ, DeltaGammaLimit))
                {
                    bands[bandIndex] = new EncodedBand {
                        Mode = reuseMode == SpectralBandMode.ReuseStereo ? SpectralBandMode.DeltaStereo : SpectralBandMode.DeltaPrevious,
                        ScaleQ = scaleQ, StepQ = stepQ, GammaQ = gammaQ,
                        ScaleDelta = unchecked((int)scaleQ - (int)state.ScaleQ), StepDelta = unchecked((int)stepQ - (int)state.StepQ), GammaDelta = unchecked((int)gammaQ - (int)state.GammaQ),
                        Levels = levels
                    };
                    continue;
                }
            }

            bands[bandIndex] = new EncodedBand { Mode = SpectralBandMode.Explicit, ScaleQ = scaleQ, StepQ = stepQ, GammaQ = gammaQ, ScaleDelta = 0, StepDelta = 0, GammaDelta = 0, Levels = levels };
        }

        return new EncodedChannelBlock { BlockSize = blockSize, StartOffsetUnits = startOffsetUnits, ActiveBandCount = ComputeActiveBandCount(bands), Bands = bands };
    }

    private static PulsarQuantizedSpectrum BuildQuantizedSpectrum(EncodedChannelBlock block)
    {
        var bands = new PulsarQuantizedBand[BandCount];
        int runningStart = 0;
        for (int bandIndex = 0; bandIndex < BandCount; bandIndex++)
        {
            EncodedBand band = block.Bands[bandIndex];
            int width = band.Levels.Length;
            bands[bandIndex] = new PulsarQuantizedBand {
                Start = runningStart, Width = width, Bits = band.Mode == SpectralBandMode.Zero ? 0 : 1,
                ScaleQ = band.ScaleQ, StepQ = band.StepQ, GammaQ = band.GammaQ,
                Scale = DequantizeLogValue(band.ScaleQ, MinScale, MaxScale, ScaleQuantLevels),
                NormalizedStep = DequantizeLogValue(band.StepQ, MinNormalizedStep, MaxNormalizedStep, StepQuantLevels),
                CompandGamma = DequantizeLinearValue(band.GammaQ, MinGamma, MaxGamma, GammaQuantLevels), Levels = (int[])band.Levels.Clone()
            };
            runningStart += width;
        }
        return new PulsarQuantizedSpectrum { SpectrumLength = block.BlockSize / 2, Bands = bands };
    }

    private static byte[] BuildSpectralSideInfo(EncodedChannelSuperframe[] encodedChannels)
    {
        var writer = new PulsarBitstreamWriter();
        writer.WriteByte((byte)encodedChannels.Length);

        foreach (EncodedChannelSuperframe channel in encodedChannels)
        {
            writer.WriteByte((byte)channel.Blocks.Length);
            foreach (EncodedChannelBlock block in channel.Blocks)
            {
                writer.WriteByte((byte)PulsarBlockLadder.GetIndex(block.BlockSize));
                writer.WriteByte((byte)block.StartOffsetUnits);
                writer.WriteByte((byte)Math.Clamp(block.ActiveBandCount, 0, BandCount));

                for (int bandIndex = 0; bandIndex < block.ActiveBandCount; bandIndex++)
                {
                    EncodedBand band = block.Bands[bandIndex];
                    writer.WriteByte((byte)band.Mode);
                    writer.WriteUInt16((ushort)band.Levels.Length);
                    
                    if (band.Mode == SpectralBandMode.Explicit)
                    {
                        WriteRawBandParameters(writer, band);
                    }
                    else if (band.Mode == SpectralBandMode.DeltaPrevious || band.Mode == SpectralBandMode.DeltaStereo)
                    {
                        writer.WriteBits(EncodeSignedToUnsigned(band.ScaleDelta, DeltaScaleBits), DeltaScaleBits);
                        writer.WriteBits(EncodeSignedToUnsigned(band.StepDelta, DeltaStepBits), DeltaStepBits);
                        writer.WriteBits(EncodeSignedToUnsigned(band.GammaDelta, DeltaGammaBits), DeltaGammaBits);
                    }

                    if (band.Mode != SpectralBandMode.Zero)
                    {
                        for (int levelIndex = 0; levelIndex < band.Levels.Length; levelIndex++)
                        {
                            writer.WriteInt32(band.Levels[levelIndex]);
                        }
                    }
                }
            }
        }
        return writer.ToArray();
    }

    private static readonly byte[] LevelEventIcdf = [176, 0];

    private static void EncodeChannelSuperframeLevels(PulsarRangeEncoder encoder, int superframeIndex, int channelIndex, EncodedChannelSuperframe channel)
    {
        _ = encoder;
        _ = superframeIndex;
        _ = channelIndex;
        _ = channel;
    }

    private static (PulsarQuantizedSpectrum Spectrum, ChannelParameterState State) DecodeQuantizedChannel(
        PulsarBitstreamReader sideInfoReader,
        PulsarRangeDecoder decoder,
        int spectrumLength,
        int[] trueBandOffsets,
        int activeBandCount,
        ChannelParameterState? previous,
        ChannelParameterState? stereoReference)
    {
        var bands = new EncodedBand[BandCount];
        var states = new BandParameterState[BandCount];

        for (int bandIndex = 0; bandIndex < activeBandCount; bandIndex++)
        {
            SpectralBandMode bandMode = (SpectralBandMode)sideInfoReader.ReadByte();
            int encodedWidth = sideInfoReader.ReadUInt16(); 
            
            int start = trueBandOffsets[bandIndex];
            int end = trueBandOffsets[bandIndex + 1];
            int trueWidth = end - start;
            int storedWidth = encodedWidth;

            uint scaleQ = 0, stepQ = 0, gammaQ = 0;

            if (bandMode == SpectralBandMode.Explicit)
            {
                (scaleQ, stepQ, gammaQ) = ReadRawBandParameters(sideInfoReader);
            }
            else if (bandMode == SpectralBandMode.ReusePrevious && previous != null)
            {
                (scaleQ, stepQ, gammaQ) = ResolveReferencedParameters(previous, bandIndex, "previous");
            }
            else if (bandMode == SpectralBandMode.ReuseStereo && stereoReference != null)
            {
                (scaleQ, stepQ, gammaQ) = ResolveReferencedParameters(stereoReference, bandIndex, "stereo");
            }
            else if (bandMode == SpectralBandMode.DeltaPrevious && previous != null)
            {
                (uint refScale, uint refStep, uint refGamma) = ResolveReferencedParameters(previous, bandIndex, "previous");
                scaleQ = ApplyDelta(refScale, DecodeUnsignedToSigned(sideInfoReader.ReadBits(DeltaScaleBits), DeltaScaleBits), ScaleQuantLevels);
                stepQ = ApplyDelta(refStep, DecodeUnsignedToSigned(sideInfoReader.ReadBits(DeltaStepBits), DeltaStepBits), StepQuantLevels);
                gammaQ = ApplyDelta(refGamma, DecodeUnsignedToSigned(sideInfoReader.ReadBits(DeltaGammaBits), DeltaGammaBits), GammaQuantLevels);
            }
            else if (bandMode == SpectralBandMode.DeltaStereo && stereoReference != null)
            {
                (uint refScale, uint refStep, uint refGamma) = ResolveReferencedParameters(stereoReference, bandIndex, "stereo");
                scaleQ = ApplyDelta(refScale, DecodeUnsignedToSigned(sideInfoReader.ReadBits(DeltaScaleBits), DeltaScaleBits), ScaleQuantLevels);
                stepQ = ApplyDelta(refStep, DecodeUnsignedToSigned(sideInfoReader.ReadBits(DeltaStepBits), DeltaStepBits), StepQuantLevels);
                gammaQ = ApplyDelta(refGamma, DecodeUnsignedToSigned(sideInfoReader.ReadBits(DeltaGammaBits), DeltaGammaBits), GammaQuantLevels);
            }

            int[] levels = new int[trueWidth];
            if (bandMode != SpectralBandMode.Zero)
            {
                int copyLength = Math.Min(storedWidth, trueWidth);
                for (int cursor = 0; cursor < storedWidth; cursor++)
                {
                    int level = sideInfoReader.ReadInt32();
                    if (cursor < copyLength)
                    {
                        levels[cursor] = level;
                    }
                }
            }

            bands[bandIndex] = new EncodedBand
            {
                Mode = bandMode,
                ScaleQ = scaleQ,
                StepQ = stepQ,
                GammaQ = gammaQ,
                ScaleDelta = 0,
                StepDelta = 0,
                GammaDelta = 0,
                Levels = levels,
            };
            states[bandIndex] = new BandParameterState { ScaleQ = scaleQ, StepQ = stepQ, GammaQ = gammaQ };
        }

        for (int fillBand = activeBandCount; fillBand < BandCount; fillBand++)
        {
            int width = trueBandOffsets[fillBand + 1] - trueBandOffsets[fillBand];

            bands[fillBand] = new EncodedBand
            {
                Mode = SpectralBandMode.Zero,
                ScaleQ = 0,
                StepQ = 0,
                GammaQ = 0,
                ScaleDelta = 0,
                StepDelta = 0,
                GammaDelta = 0,
                Levels = new int[width],
            };
            states[fillBand] = new BandParameterState { ScaleQ = 0, StepQ = 0, GammaQ = 0 };
        }

        var encodedBlock = new EncodedChannelBlock
        {
            BlockSize = spectrumLength * 2,
            StartOffsetUnits = 0,
            ActiveBandCount = activeBandCount,
            Bands = bands,
        };

        return (BuildQuantizedSpectrum(encodedBlock), new ChannelParameterState { Bands = states });
    }

    private static (float[] Samples, int SampleRate, int Channels) DecodeSpectralArchive(PulsarContainerFile container)
    {
        int sampleRate = container.Header.SampleRate;
        int channels = container.Header.ChannelCount;
        int totalFrames = checked((int)container.Header.TotalSamples);
        bool useMidSide = (container.Header.Flags & MidSideStereoFlag) != 0;

        var allBlocks = new List<(int SampleStart, int BlockSize, PulsarQuantizedSpectrum Spectrum)>[channels];
        for (int ch = 0; ch < channels; ch++) allBlocks[ch] = new();

        var previousStates = new ChannelParameterState[channels];

        foreach (PulsarSuperframePacket superframe in container.Superframes)
        {
            var sideInfoReader = new PulsarBitstreamReader(superframe.SideInfo);
            var decoder = new PulsarRangeDecoder(superframe.EntropyPayload);

            int channelCount = sideInfoReader.ReadByte();
            var channelBlockStates = new List<ChannelParameterState>[channelCount];

            for (int ch = 0; ch < channelCount; ch++)
            {
                channelBlockStates[ch] = new List<ChannelParameterState>();
                int blockCount = sideInfoReader.ReadByte();
                ChannelParameterState? previous = previousStates[ch];

                for (int b = 0; b < blockCount; b++)
                {
                    int blockSizeIndex = sideInfoReader.ReadByte();
                    int segmentOffset = sideInfoReader.ReadByte();
                    int activeBandCount = sideInfoReader.ReadByte();
                    int blockSize = PulsarBlockLadder.Steps[blockSizeIndex];
                    int spectrumLength = blockSize / 2;
                    int[] trueBandOffsets = BuildBandOffsets(spectrumLength, sampleRate, BandCount);

                    ChannelParameterState? stereoRef = null;
                    if (ch > 0 && b < channelBlockStates[0].Count) stereoRef = channelBlockStates[0][b];

                    (PulsarQuantizedSpectrum spectrum, ChannelParameterState state) =
                        DecodeQuantizedChannel(sideInfoReader, decoder, spectrumLength, trueBandOffsets, activeBandCount, previous, stereoRef);

                    int sampleStart = (superframe.Index * SuperframeSize) + (segmentOffset * ArchiveOffsetQuantum);
                    allBlocks[ch].Add((sampleStart, blockSize, spectrum));
                    channelBlockStates[ch].Add(state);
                    previous = state;
                }
                previousStates[ch] = previous!;
            }
        }

        var channelBuffers = new float[channels][];
        for (int ch = 0; ch < channels; ch++) channelBuffers[ch] = ReconstructChannel(allBlocks[ch], totalFrames);

        if (useMidSide && channels == 2) ApplyInverseMidSide(channelBuffers[0], channelBuffers[1], totalFrames);

        float[] output = new float[totalFrames * channels];
        for (int frame = 0; frame < totalFrames; frame++)
            for (int ch = 0; ch < channels; ch++)
                output[(frame * channels) + ch] = channelBuffers[ch][frame];

        return (output, sampleRate, channels);
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

    private static int[][][] AllocateSpectralBandBitsGlobal(IReadOnlyList<PulsarPsychoResult>[] psychoByChannel, double totalBitsPerFrame, bool useMidSideStereo)
    {
        int channels = psychoByChannel.Length;
        int frameCount = channels == 0 ? 0 : psychoByChannel[0].Count;
        var allocations = new int[channels][][];
        for (int channel = 0; channel < channels; channel++) allocations[channel] = new int[frameCount][];

        for (int channel = 0; channel < channels; channel++)
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                allocations[channel][frameIndex] = AllocateBandBitsForFrame(psychoByChannel[channel][frameIndex], 256);

        return allocations;
    }

    private static List<PulsarFrameAllocation> BuildSpectralAllocations(int[][] frameBandBits)
    {
        var allocations = new List<PulsarFrameAllocation>(frameBandBits.Length);
        for (int frameIndex = 0; frameIndex < frameBandBits.Length; frameIndex++)
        {
            allocations.Add(new PulsarFrameAllocation { TargetBits = 0, MetadataBits = 0, BlockBits = 0, MetadataRatio = 0, BandBits = frameBandBits[frameIndex], ComplexityWeight = 1 });
        }
        return allocations;
    }

    private static int[] AllocateBandBitsForFrame(PulsarPsychoResult frame, int blockBits) => new int[frame.SfbBandCenters.Length];

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
        writer.WriteUInt16((ushort)band.ScaleQ);
        writer.WriteUInt16((ushort)band.StepQ);
        writer.WriteUInt16((ushort)band.GammaQ);
    }

    private static (uint ScaleQ, uint StepQ, uint GammaQ) ReadRawBandParameters(PulsarBitstreamReader reader) => (reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16());

    private static ChannelParameterState BuildParameterState(EncodedChannelSuperframe channel) => channel.Blocks.Length == 0 ? new ChannelParameterState { Bands = Array.Empty<BandParameterState>() } : BuildParameterState(channel.Blocks[^1]);
    private static ChannelParameterState BuildParameterState(EncodedChannelBlock block)
    {
        var states = new BandParameterState[block.Bands.Length];
        for (int i = 0; i < block.Bands.Length; i++) states[i] = new BandParameterState { ScaleQ = block.Bands[i].ScaleQ, StepQ = block.Bands[i].StepQ, GammaQ = block.Bands[i].GammaQ };
        return new ChannelParameterState { Bands = states };
    }

    private static PulsarPsychoResult SelectPsychoFrame(IReadOnlyList<PulsarPsychoResult> psychoFrames, int startSegment, int blockSize) => psychoFrames[Math.Clamp(startSegment + (Math.Max(1, blockSize / PulsarBlockLadder.ControlHopSize) / 2), 0, psychoFrames.Count - 1)];
    
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
    
    private static (BandParameterState? State, SpectralBandMode ReuseMode) GetBestReference(ChannelParameterState? previous, ChannelParameterState? stereoReference, int bandIndex, uint scaleQ, uint stepQ, uint gammaQ)
    {
        if (stereoReference is not null && bandIndex < stereoReference.Bands.Length)
        {
            BandParameterState stereo = stereoReference.Bands[bandIndex];
            if (scaleQ == stereo.ScaleQ && stepQ == stereo.StepQ && gammaQ == stereo.GammaQ) return (stereo, SpectralBandMode.ReuseStereo);
            if (CanDeltaEncode(scaleQ, stereo.ScaleQ, DeltaScaleLimit) && CanDeltaEncode(stepQ, stereo.StepQ, DeltaStepLimit) && CanDeltaEncode(gammaQ, stereo.GammaQ, DeltaGammaLimit)) return (stereo, SpectralBandMode.ReuseStereo);
        }
        if (previous is not null && bandIndex < previous.Bands.Length)
        {
            BandParameterState prior = previous.Bands[bandIndex];
            if (scaleQ == prior.ScaleQ && stepQ == prior.StepQ && gammaQ == prior.GammaQ) return (prior, SpectralBandMode.ReusePrevious);
            if (CanDeltaEncode(scaleQ, prior.ScaleQ, DeltaScaleLimit) && CanDeltaEncode(stepQ, prior.StepQ, DeltaStepLimit) && CanDeltaEncode(gammaQ, prior.GammaQ, DeltaGammaLimit)) return (prior, SpectralBandMode.ReusePrevious);
        }
        return (null, SpectralBandMode.Explicit);
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

    private static bool CanDeltaEncode(uint value, uint reference, int limit) => Math.Abs(unchecked((int)value - (int)reference)) <= limit;
    private static uint EncodeSignedToUnsigned(int value, int bitCount) => (uint)(value & ((1 << bitCount) - 1));
    private static int DecodeUnsignedToSigned(uint value, int bitCount) { int signBit = 1 << (bitCount - 1); int signed = (int)(value & ((1 << bitCount) - 1)); return (signed & signBit) != 0 ? signed - (1 << bitCount) : signed; }
    private static (uint ScaleQ, uint StepQ, uint GammaQ) ResolveReferencedParameters(ChannelParameterState? state, int bandIndex, string referenceName) => (state.Bands[bandIndex].ScaleQ, state.Bands[bandIndex].StepQ, state.Bands[bandIndex].GammaQ);
    private static uint ApplyDelta(uint baseValue, int delta, int totalLevels) => (uint)Math.Clamp(unchecked((int)baseValue + delta), 0, totalLevels - 1);
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