using System;
using System.Collections.Generic;
using Pulsar.Psycho;

public static class PulsarSuperframeArchiveCodec
{
	private static readonly object DebugTraceLock = new();
	private static readonly List<string> EncoderDebugTrace = [];
	private static readonly List<string> DecoderDebugTrace = [];
	private const ushort PcmEntropyFlag = 0x0001;
	private const ushort SpectralEntropyFlag = 0x0002;
	private const ushort MidSideStereoFlag = 0x0004;
	private const int SuperframeSize = 2048;
	private const int SuperframeSegmentCount = SuperframeSize / PulsarBlockLadder.ControlHopSize;
	private const int PsychoFftSize = 2048;
	private const int BandCount = 21;
	private const int JointStereoThresholdKbps = 160;
	private const int ScaleQuantLevels = 2048;
	private const int StepQuantLevels = 2048;
	private const int GammaQuantLevels = 64;
	private const int ExplicitScaleBits = 11;
	private const int ExplicitStepBits = 11;
	private const int ExplicitGammaBits = 6;
	private const int DeltaScaleBits = 7;
	private const int DeltaStepBits = 7;
	private const int DeltaGammaBits = 4;
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
		Zero = 0,
		Explicit = 1,
		DeltaPrevious = 2,
		ReusePrevious = 3,
		DeltaStereo = 4,
		ReuseStereo = 5,
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
		public required int SegmentStart { get; init; }
		public required int SampleStart { get; init; }
		public required byte PatternId { get; init; }
		public required PulsarLargeBlockState LargeBlockState { get; init; }
		public required ArchiveBlockPlan[] Blocks { get; init; }
	}

	private sealed class ArchiveBlockPlan
	{
		public required int BlockSize { get; init; }
		public required int SegmentOffset { get; init; }
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
		public required int SegmentOffset { get; init; }
		public required int ActiveBandCount { get; init; }
		public required EncodedBand[] Bands { get; init; }
	}

	private sealed class EncodedChannelSuperframe
	{
		public required EncodedChannelBlock[] Blocks { get; init; }
	}

	private sealed class SideInfoSection
	{
		public required SpectralBandMode Mode { get; init; }
		public required int StartBand { get; init; }
		public required int BandCount { get; init; }
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
		ArgumentNullException.ThrowIfNull(interleavedSamples);
		if (channels <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(channels));
		}

		int frameCount = interleavedSamples.Length / channels;
		int superframeSampleCount = 2048;
		int superframeCount = (frameCount + superframeSampleCount - 1) / superframeSampleCount;

		var container = new PulsarContainerFile();
		container.Header.Version = 1;
		container.Header.Flags = PcmEntropyFlag;
		container.Header.SampleRate = sampleRate;
		container.Header.ChannelCount = channels;
		container.Header.TotalSamples = frameCount;

		for (int superframeIndex = 0; superframeIndex < superframeCount; superframeIndex++)
		{
			int frameStart = superframeIndex * superframeSampleCount;
			int frameLength = Math.Min(superframeSampleCount, frameCount - frameStart);
			byte[] payload = EncodePcmSuperframePayload(interleavedSamples, channels, frameStart, frameLength);
			byte[] sideInfo = BuildPcmSideInfo(frameLength);

			container.Superframes.Add(new PulsarSuperframePacket
			{
				Index = superframeIndex,
				PatternId = 0,
				LargeBlockState = PulsarLargeBlockState.Standalone,
				SideInfo = sideInfo,
				EntropyPayload = payload,
			});
		}

		return new PulsarPacker().Pack(container);
}

public static byte[] EncodeSpectralArchive(float[] interleavedSamples, int sampleRate, int channels, int targetKbps, int? quality = null)
{
	ArgumentNullException.ThrowIfNull(interleavedSamples);
	if (channels <= 0)
	{
		throw new ArgumentOutOfRangeException(nameof(channels));
	}

	ClearDebugTrace();

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
	var allocator = new PulsarAllocator(new PulsarAllocationConfig
	{
		Quality = effectiveQuality,
		SampleRate = sampleRate,
		HopSize = PulsarBlockLadder.ControlHopSize,
	});

	foreach (SpectralSuperframePlan superframe in plan.Superframes)
	{
		var encoder = new PulsarRangeEncoder();
		EncodedChannelSuperframe[] encodedChannels = BuildEncodedSuperframe(plan, superframe, previousStates, allocator);
		byte[] sideInfo = BuildSpectralSideInfo(encodedChannels);

		for (int channel = 0; channel < encodedChannels.Length; channel++)
		{
			EncodeChannelSuperframeLevels(encoder, superframe.Index, channel, encodedChannels[channel]);
			previousStates[channel] = BuildParameterState(encodedChannels[channel]);
		}

		container.Superframes.Add(new PulsarSuperframePacket
		{
			Index = superframe.Index,
			PatternId = superframe.PatternId,
			LargeBlockState = superframe.LargeBlockState,
			SideInfo = sideInfo,
			EntropyPayload = encoder.Finish(),
		});
	}

	return new PulsarPacker().Pack(container);
}

public static (float[] Samples, int SampleRate, int Channels) RenderSpectralPcm(float[] interleavedSamples, int sampleRate, int channels, int targetKbps, int? quality = null)
	{
		ArgumentNullException.ThrowIfNull(interleavedSamples);
		if (channels <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(channels));
		}

		ClearDebugTrace();

		int effectiveQuality = ResolveArchiveQuality(quality, targetKbps);
		SpectralArchivePlan plan = AnalyzeSpectralArchive(interleavedSamples, sampleRate, channels, targetKbps, effectiveQuality);
		var allBlocks = new List<(int SuperframeIndex, int BlockSize, int SegmentOffset, PulsarQuantizedSpectrum Spectrum)>[plan.Channels];
		for (int ch = 0; ch < plan.Channels; ch++)
		{
			allBlocks[ch] = new List<(int, int, int, PulsarQuantizedSpectrum)>();
		}

		var previousStates = new ChannelParameterState[plan.Channels];
		var allocator = new PulsarAllocator(new PulsarAllocationConfig
		{
			Quality = effectiveQuality,
			SampleRate = sampleRate,
			HopSize = PulsarBlockLadder.ControlHopSize,
		});
		foreach (SpectralSuperframePlan superframe in plan.Superframes)
		{
			EncodedChannelSuperframe[] encodedChannels = BuildEncodedSuperframe(plan, superframe, previousStates, allocator);
			for (int channel = 0; channel < encodedChannels.Length; channel++)
			{
				EncodedChannelSuperframe encodedChannel = encodedChannels[channel];
				for (int blockIndex = 0; blockIndex < encodedChannel.Blocks.Length; blockIndex++)
				{
					EncodedChannelBlock block = encodedChannel.Blocks[blockIndex];
					allBlocks[channel].Add((superframe.Index, block.BlockSize, block.SegmentOffset, BuildQuantizedSpectrum(block)));
				}

				previousStates[channel] = BuildParameterState(encodedChannel);
			}
		}

		var channelBuffers = new float[plan.Channels][];
		for (int ch = 0; ch < plan.Channels; ch++)
		{
			channelBuffers[ch] = ReconstructChannel(allBlocks[ch], plan.OriginalFrameCount);
		}

		if (plan.UseMidSideStereo && plan.Channels == 2)
		{
			for (int i = 0; i < plan.OriginalFrameCount; i++)
			{
				float mid = channelBuffers[0][i];
				float side = channelBuffers[1][i];
				channelBuffers[0][i] = mid + side;
				channelBuffers[1][i] = mid - side;
			}
		}

		float[] output = new float[plan.OriginalFrameCount * plan.Channels];
		for (int frame = 0; frame < plan.OriginalFrameCount; frame++)
		{
			for (int ch = 0; ch < plan.Channels; ch++)
			{
				output[(frame * plan.Channels) + ch] = channelBuffers[ch][frame];
			}
		}

		return (output, plan.SampleRate, plan.Channels);
	}

	public static (float[] Samples, int SampleRate, int Channels) DecodePcmArchive(byte[] payload)
	{
		ArgumentNullException.ThrowIfNull(payload);

		PulsarContainerFile container = new PulsarPacker().Unpack(payload);
		if ((container.Header.Flags & PcmEntropyFlag) == 0)
		{
			throw new InvalidOperationException("PULSAR archive does not contain PCM entropy payloads.");
		}

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
		ArgumentNullException.ThrowIfNull(payload);

		PulsarContainerFile container = new PulsarPacker().Unpack(payload);
		if ((container.Header.Flags & SpectralEntropyFlag) != 0)
		{
			return DecodeSpectralArchive(container);
		}

		if ((container.Header.Flags & PcmEntropyFlag) != 0)
		{
			return DecodePcmArchive(payload);
		}

		throw new InvalidOperationException("Unknown PULSAR archive payload type.");
	}

private static SpectralArchivePlan AnalyzeSpectralArchive(float[] interleavedSamples, int sampleRate, int channels, int targetKbps, int quality)
	{
		int frameCount = interleavedSamples.Length / channels;
		float[][] channelBuffers = Deinterleave(interleavedSamples, channels);
		bool useMidSideStereo = channels == 2 && targetKbps <= JointStereoThresholdKbps;
		if (useMidSideStereo)
		{
			ApplyMidSideStereo(channelBuffers[0], channelBuffers[1]);
		}

		var psychoByChannel = new IReadOnlyList<PulsarPsychoResult>[channels];
		float[] sharedPlannerSamples = channelBuffers[0];
		var sharedPlanner = new PulsarPlanner();
		List<PulsarFramePlan> sharedFramePlans = sharedPlanner.PlanSong(sharedPlannerSamples);
		List<SpectralSuperframePlan> superframes = BuildSuperframePlans(sharedFramePlans, frameCount);

		for (int channel = 0; channel < channels; channel++)
		{
			var psycho = new PulsarPsycho(new PulsarPsychoSettings
			{
				SampleRate = sampleRate,
				FftSize = PsychoFftSize,
				HopSize = PulsarBlockLadder.ControlHopSize,
			});

			psychoByChannel[channel] = psycho.AnalyzeSong(channelBuffers[channel]);
		}

		(double totalPerceptualEntropy, double averagePerceptualEntropy) = ComputeGlobalPerceptualEntropyStats(psychoByChannel);
		double totalSongBits = Math.Max(
			256.0,
			targetKbps * 1000.0 * (frameCount / (double)Math.Max(1, sampleRate)));
		int baseGlobalGain = ComputeTrueVbrBaseGlobalGain(quality, targetKbps, totalPerceptualEntropy, totalSongBits);

		var channelPlans = new SpectralChannelPlan[channels];
		double totalBitsPerControlFrame = Math.Max(
			32.0,
			targetKbps * 1000.0 * (PulsarBlockLadder.ControlHopSize / (double)sampleRate));
		int[][][] channelBandAllocations = AllocateSpectralBandBitsGlobal(psychoByChannel, totalBitsPerControlFrame, useMidSideStereo);

		for (int channel = 0; channel < channels; channel++)
		{
			channelPlans[channel] = new SpectralChannelPlan
			{
				Samples = channelBuffers[channel],
				PsychoFrames = psychoByChannel[channel],
				Allocations = BuildSpectralAllocations(channelBandAllocations[channel]),
			};
		}

		return new SpectralArchivePlan
		{
			SampleRate = sampleRate,
			Channels = channels,
			TargetKbps = targetKbps,
			UseMidSideStereo = useMidSideStereo,
			OriginalFrameCount = frameCount,
			TotalPerceptualEntropy = totalPerceptualEntropy,
			AveragePerceptualEntropy = averagePerceptualEntropy,
			TotalSongBits = totalSongBits,
			BaseGlobalGain = baseGlobalGain,
			SharedFramePlans = sharedFramePlans,
			Superframes = superframes,
			ChannelPlans = channelPlans,
		};
	}

private static EncodedChannelSuperframe[] BuildEncodedSuperframe(SpectralArchivePlan plan, SpectralSuperframePlan superframe, ChannelParameterState[] previousStates, PulsarAllocator allocator)
{
	PulsarQuantizedSpectrum[][] allQuantized = new PulsarQuantizedSpectrum[plan.Channels][];
	int minGain = int.MaxValue;
	int maxGain = int.MinValue;
	double gainSum = 0.0;
	int gainCount = 0;
	for (int ch = 0; ch < plan.Channels; ch++)
	{
		allQuantized[ch] = new PulsarQuantizedSpectrum[superframe.Blocks.Length];
	}

	for (int channel = 0; channel < plan.Channels; channel++)
	{
		SpectralChannelPlan channelPlan = plan.ChannelPlans[channel];
		for (int blockIndex = 0; blockIndex < superframe.Blocks.Length; blockIndex++)
		{
			ArchiveBlockPlan block = superframe.Blocks[blockIndex];
			int blockStartSegment = superframe.SegmentStart + block.SegmentOffset;
			int[] bandBits = AverageBandBits(channelPlan.Allocations, blockStartSegment, block.BlockSize);
			int[] quantizerBandBits = BuildQuantizerBandBits(bandBits);
			PulsarPsychoResult psycho = SelectPsychoFrame(channelPlan.PsychoFrames, blockStartSegment, block.BlockSize);
			float[] spectrum = AnalyzeBlockSpectrum(
				channelPlan.Samples,
				superframe.SampleStart + (block.SegmentOffset * PulsarBlockLadder.ControlHopSize),
				block.BlockSize);
			int blockGlobalGain = ComputeTrueVbrBlockGain(plan, psycho, bandBits);
			PulsarQuantizedSpectrum quantized = PulsarQuantizer.QuantizeSpectrumDetailed(spectrum, quantizerBandBits, psycho, blockGlobalGain);
			allQuantized[channel][blockIndex] = quantized;
			minGain = Math.Min(minGain, blockGlobalGain);
			maxGain = Math.Max(maxGain, blockGlobalGain);
			gainSum += blockGlobalGain;
			gainCount++;
		}
	}

		EncodedChannelSuperframe[] channels = BuildEncodedChannelsFromQuantized(superframe, allQuantized, previousStates);
		var estimator = new PulsarAllocator();
		int estimatedBits = 64
			+ EstimateSpectralSideInfoBits(channels)
			+ EstimateChannelPayloadBits(channels, estimator);
		double averageGain = gainCount > 0 ? gainSum / gainCount : plan.BaseGlobalGain;
		Console.WriteLine(
			$"SF {superframe.Index}: TrueVBR EstBits {estimatedBits}, BaseGain {plan.BaseGlobalGain}, BlockGain {minGain}..{maxGain}, AvgGain {averageGain:F1}");

		return channels;
	}

	private static (double TotalPerceptualEntropy, double AveragePerceptualEntropy) ComputeGlobalPerceptualEntropyStats(
		IReadOnlyList<PulsarPsychoResult>[] psychoByChannel)
	{
		double totalPerceptualEntropy = 0.0;
		int frameCount = 0;

		for (int channel = 0; channel < psychoByChannel.Length; channel++)
		{
			foreach (PulsarPsychoResult frame in psychoByChannel[channel])
			{
				totalPerceptualEntropy += Math.Max(1.0f, frame.PerceptualEntropy);
				frameCount++;
			}
		}

		double averagePerceptualEntropy = frameCount > 0
			? totalPerceptualEntropy / frameCount
			: 1.0;
		return (totalPerceptualEntropy, averagePerceptualEntropy);
	}

	private static int ResolveArchiveQuality(int? explicitQuality, int targetKbps)
	{
		if (explicitQuality.HasValue)
		{
			return Math.Clamp(explicitQuality.Value, 0, 9);
		}

		int[] nominalKbps = [320, 288, 256, 224, 192, 160, 128, 112, 96, 80];
		int bestQuality = 0;
		int bestDistance = int.MaxValue;
		for (int i = 0; i < nominalKbps.Length; i++)
		{
			int distance = Math.Abs(nominalKbps[i] - targetKbps);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				bestQuality = i;
			}
		}

		return bestQuality;
	}

	private static int ComputeTrueVbrBaseGlobalGain(int quality, int targetKbps, double totalPerceptualEntropy, double totalSongBits)
	{
		double qualityAnchor = quality switch
		{
			<= 0 => 10.0,
			1 => 24.0,
			2 => 40.0,
			3 => 70.0,
			4 => 100.0,
			5 => 140.0,
			6 => 190.0,
			7 => 245.0,
			8 => 305.0,
			_ => 360.0,
		};

		double bitrateAnchor = targetKbps switch
		{
			>= 512 => 8.0,
			>= 384 => 18.0,
			>= 320 => 26.0,
			>= 256 => 40.0,
			>= 192 => 70.0,
			>= 160 => 92.0,
			>= 128 => 120.0,
			>= 96 => 165.0,
			_ => 220.0,
		};

		if (totalPerceptualEntropy <= 1e-9 || totalSongBits <= 1e-9)
		{
			return (int)Math.Round((qualityAnchor * 0.7) + (bitrateAnchor * 0.3));
		}

		double pressure = totalPerceptualEntropy / totalSongBits;
		double pressureTrim = Math.Log(Math.Max(0.25, pressure), 2.0) * 6.0;
		double blendedAnchor = (qualityAnchor * 0.7) + (bitrateAnchor * 0.3);
		return Math.Clamp((int)Math.Round(blendedAnchor + pressureTrim), 0, 400);
	}

	private static int ComputeTrueVbrBlockGain(SpectralArchivePlan plan, PulsarPsychoResult psycho, int[] bandBits)
	{
		double localPeRatio = psycho.PerceptualEntropy / Math.Max(1.0, plan.AveragePerceptualEntropy);
		double peModulation = Math.Clamp(localPeRatio - 1.0, -0.75, 2.5);
		double smrRelief = Math.Clamp(psycho.AveragePositiveSmr / 20.0f, 0.0f, 1.5f);
		double transientRelief = Math.Clamp(psycho.TransientScore, 0.0f, 1.25f);
		double maskingRelief = Math.Clamp(psycho.MaskingPressure / 24.0f, 0.0f, 1.0f);
		double allocatedBits = 0.0;
		for (int i = 0; i < bandBits.Length; i++)
		{
			allocatedBits += Math.Max(0, bandBits[i]);
		}

		double meanBandBits = allocatedBits / Math.Max(1, bandBits.Length);
		double allocationRelief = Math.Clamp((meanBandBits - 12.0) / 20.0, -0.5, 1.0);
		int frameGain = plan.BaseGlobalGain - (int)Math.Round(peModulation * 120.0);
		frameGain -= (int)Math.Round((smrRelief * 18.0) + (transientRelief * 24.0) + (maskingRelief * 12.0) + (allocationRelief * 10.0));

		if (psycho.TotalEnergyDb <= -60.0f)
		{
			frameGain += 150;
		}
		else if (psycho.TotalEnergyDb <= -48.0f)
		{
			frameGain += 90;
		}
		else if (psycho.TotalEnergyDb <= -36.0f)
		{
			frameGain += 36;
		}

		return Math.Clamp(frameGain, 0, 450);
	}

	private static int[] BuildQuantizerBandBits(int[] bandBits)
	{
		var quantizerBandBits = new int[bandBits.Length];
		Array.Fill(quantizerBandBits, 256);
		return quantizerBandBits;
	}

	private static EncodedChannelSuperframe[] BuildEncodedChannelsFromQuantized(
		SpectralSuperframePlan superframe,
		PulsarQuantizedSpectrum[][] allQuantized,
		ChannelParameterState[] previousStates)
	{
		var channels = new EncodedChannelSuperframe[allQuantized.Length];

		for (int channel = 0; channel < allQuantized.Length; channel++)
		{
			var encodedBlocks = new List<EncodedChannelBlock>(superframe.Blocks.Length);
			ChannelParameterState? previous = previousStates[channel];

			for (int blockIndex = 0; blockIndex < superframe.Blocks.Length; blockIndex++)
			{
				ArchiveBlockPlan block = superframe.Blocks[blockIndex];
				PulsarQuantizedSpectrum quantized = allQuantized[channel][blockIndex];
				ChannelParameterState? stereoReference = channel > 0 && blockIndex < channels[0].Blocks.Length
					? BuildParameterState(channels[0].Blocks[blockIndex])
					: null;

				EncodedChannelBlock encodedBlock = ConvertQuantizedBlock(quantized, block.BlockSize, block.SegmentOffset, previous, stereoReference);
				encodedBlocks.Add(encodedBlock);
				previous = BuildParameterState(encodedBlock);
			}

			channels[channel] = new EncodedChannelSuperframe
			{
				Blocks = encodedBlocks.ToArray(),
			};
		}

		return channels;
	}

	private static EncodedChannelBlock ConvertQuantizedBlock(
		PulsarQuantizedSpectrum quantized,
		int blockSize,
		int segmentOffset,
		ChannelParameterState? previous,
		ChannelParameterState? stereoReference)
	{
		var bands = new EncodedBand[quantized.Bands.Length];
		for (int bandIndex = 0; bandIndex < quantized.Bands.Length; bandIndex++)
		{
			PulsarQuantizedBand band = quantized.Bands[bandIndex];
			int[] levels = (int[])band.Levels.Clone();
			if (IsAllZero(levels))
			{
				bands[bandIndex] = new EncodedBand
				{
					Mode = SpectralBandMode.Zero,
					ScaleQ = 0,
					StepQ = 0,
					GammaQ = 0,
					ScaleDelta = 0,
					StepDelta = 0,
					GammaDelta = 0,
					Levels = levels,
				};
				continue;
			}

			uint scaleQ = QuantizeLogValue(band.Scale, MinScale, MaxScale, ScaleQuantLevels);
			uint stepQ = QuantizeLogValue(band.NormalizedStep, MinNormalizedStep, MaxNormalizedStep, StepQuantLevels);
			uint gammaQ = QuantizeLinearValue(band.CompandGamma, MinGamma, MaxGamma, GammaQuantLevels);
			(BandParameterState? state, SpectralBandMode reuseMode) = GetBestReference(previous, stereoReference, bandIndex, scaleQ, stepQ, gammaQ);

			if (state is not null)
			{
				if (scaleQ == state.ScaleQ && stepQ == state.StepQ && gammaQ == state.GammaQ)
				{
					bands[bandIndex] = new EncodedBand
					{
						Mode = reuseMode,
						ScaleQ = scaleQ,
						StepQ = stepQ,
						GammaQ = gammaQ,
						ScaleDelta = 0,
						StepDelta = 0,
						GammaDelta = 0,
						Levels = levels,
					};
					continue;
				}

				if (CanDeltaEncode(scaleQ, state.ScaleQ, DeltaScaleLimit) &&
					CanDeltaEncode(stepQ, state.StepQ, DeltaStepLimit) &&
					CanDeltaEncode(gammaQ, state.GammaQ, DeltaGammaLimit))
				{
					bands[bandIndex] = new EncodedBand
					{
						Mode = reuseMode == SpectralBandMode.ReuseStereo ? SpectralBandMode.DeltaStereo : SpectralBandMode.DeltaPrevious,
						ScaleQ = scaleQ,
						StepQ = stepQ,
						GammaQ = gammaQ,
						ScaleDelta = unchecked((int)scaleQ - (int)state.ScaleQ),
						StepDelta = unchecked((int)stepQ - (int)state.StepQ),
						GammaDelta = unchecked((int)gammaQ - (int)state.GammaQ),
						Levels = levels,
					};
					continue;
				}
			}

			bands[bandIndex] = new EncodedBand
			{
				Mode = SpectralBandMode.Explicit,
				ScaleQ = scaleQ,
				StepQ = stepQ,
				GammaQ = gammaQ,
				ScaleDelta = 0,
				StepDelta = 0,
				GammaDelta = 0,
				Levels = levels,
			};
		}

		int activeBandCount = ComputeActiveBandCount(bands);

		return new EncodedChannelBlock
		{
			BlockSize = blockSize,
			SegmentOffset = segmentOffset,
			ActiveBandCount = activeBandCount,
			Bands = bands,
		};
	}

	private static PulsarQuantizedSpectrum BuildQuantizedSpectrum(EncodedChannelBlock block)
	{
		var bands = new PulsarQuantizedBand[BandCount];
		int runningStart = 0;
		for (int bandIndex = 0; bandIndex < BandCount; bandIndex++)
		{
			EncodedBand band = block.Bands[bandIndex];
			int width = band.Levels.Length;
			bands[bandIndex] = new PulsarQuantizedBand
			{
				Start = runningStart,
				Width = width,
				Bits = band.Mode == SpectralBandMode.Zero ? 0 : 1,
				Scale = DequantizeLogValue(band.ScaleQ, MinScale, MaxScale, ScaleQuantLevels),
				NormalizedStep = DequantizeLogValue(band.StepQ, MinNormalizedStep, MaxNormalizedStep, StepQuantLevels),
				CompandGamma = DequantizeLinearValue(band.GammaQ, MinGamma, MaxGamma, GammaQuantLevels),
				Levels = (int[])band.Levels.Clone(),
			};
			runningStart += width;
		}

		return new PulsarQuantizedSpectrum
		{
			SpectrumLength = block.BlockSize / 2,
			Bands = bands,
		};
	}

	private static byte[] BuildSpectralSideInfo(EncodedChannelSuperframe[] encodedChannels)
	{
		var writer = new PulsarBitstreamWriter();
		writer.WriteByte((byte)encodedChannels.Length);

		for (int channel = 0; channel < encodedChannels.Length; channel++)
		{
			EncodedChannelSuperframe encodedChannel = encodedChannels[channel];
			writer.WriteByte((byte)encodedChannel.Blocks.Length);

			foreach (EncodedChannelBlock block in encodedChannel.Blocks)
			{
				writer.WriteByte((byte)PulsarBlockLadder.GetIndex(block.BlockSize));
				writer.WriteByte((byte)block.SegmentOffset);
				writer.WriteByte((byte)Math.Clamp(block.ActiveBandCount, 0, BandCount));

				for (int bandIndex = 0; bandIndex < block.ActiveBandCount; bandIndex++)
				{
					EncodedBand band = block.Bands[bandIndex];
					writer.WriteByte((byte)band.Mode);
					writer.WriteUInt16((ushort)band.Levels.Length);
					WriteRawBandParameters(writer, band);
				}
			}
		}

		return writer.ToArray();
	}

	// ICDF for the linear RLE event: 0=ZERO_RUN, 1=VALUE.
	// Biased toward zero-runs since the outer loop pushes most coefficients to zero.
	private static readonly byte[] LevelEventIcdf = [176, 0];

	private static void EncodeChannelSuperframeLevels(PulsarRangeEncoder encoder, int superframeIndex, int channelIndex, EncodedChannelSuperframe channel)
	{
		for (int blockIndex = 0; blockIndex < channel.Blocks.Length; blockIndex++)
		{
			EncodedChannelBlock block = channel.Blocks[blockIndex];
			for (int bandIndex = 0; bandIndex < block.ActiveBandCount; bandIndex++)
			{
				EncodedBand band = block.Bands[bandIndex];
				if (band.Mode == SpectralBandMode.Zero)
				{
					continue;
				}

				// Linear RLE scan, strictly clamped to the current band.
				// A ZERO_RUN is never allowed to cross a band boundary because
				// each band has its own parameter state and decoder cursor.
				int cursor = 0;
				int width = band.Levels.Length;

				while (cursor < width)
				{
					if (band.Levels[cursor] == 0)
					{
						int remainingInBand = width - cursor;
						int zeroCount = 0;
						while (zeroCount < remainingInBand && band.Levels[cursor + zeroCount] == 0)
						{
							zeroCount++;
						}

						if (zeroCount <= 0)
						{
							throw new InvalidOperationException("ZERO_RUN encoder encountered a non-positive run length.");
						}

						int clampedRunLength = Math.Min(zeroCount, remainingInBand);
						TraceEncodeDebug($"[ENC] SF {superframeIndex}, CH {channelIndex}, BLK {blockIndex}, BAND {bandIndex}, Cursor {cursor}: ZERO_RUN, Len {clampedRunLength}");
						encoder.EncodeIcdfSymbol(0, LevelEventIcdf, 8);
						EncodeZeroRun(encoder, clampedRunLength);
						cursor += clampedRunLength;
					}
					else
					{
						int magnitude = Math.Abs(band.Levels[cursor]);
						bool sign = band.Levels[cursor] < 0;
						TraceEncodeDebug($"[ENC] SF {superframeIndex}, CH {channelIndex}, BLK {blockIndex}, BAND {bandIndex}, Cursor {cursor}: VALUE, Mag {magnitude}, Sign {sign}");
						encoder.EncodeIcdfSymbol(1, LevelEventIcdf, 8);
						EncodeMagnitudeValue(encoder, magnitude);
						encoder.EncodeBit(sign);
						cursor++;
					}
				}
			}
		}
	}

	private static (PulsarQuantizedSpectrum Spectrum, ChannelParameterState State) DecodeQuantizedChannel(
		PulsarBitstreamReader sideInfoReader,
		PulsarRangeDecoder decoder,
		int spectrumLength,
		int sampleRate,
		int activeBandCount,
		int superframeIndex,
		int channelIndex,
		int blockIndex)
	{
		var bands = new PulsarQuantizedBand[BandCount];
		var states = new BandParameterState[BandCount];
		int runningStart = 0;

		for (int bandIndex = 0; bandIndex < activeBandCount; bandIndex++)
		{
			SpectralBandMode bandMode = (SpectralBandMode)sideInfoReader.ReadByte();
			int width = sideInfoReader.ReadUInt16();
			if (width < 0 || runningStart + width > spectrumLength)
			{
				throw new InvalidOperationException($"Invalid band width {width} at band {bandIndex}; runningStart={runningStart}, spectrumLength={spectrumLength}.");
			}

			int start = runningStart;
			int end = start + width;
			(uint scaleQ, uint stepQ, uint gammaQ) = ReadRawBandParameters(sideInfoReader);
			float scale = DequantizeLogValue(scaleQ, MinScale, MaxScale, ScaleQuantLevels);
			float normalizedStep = DequantizeLogValue(stepQ, MinNormalizedStep, MaxNormalizedStep, StepQuantLevels);
			float gamma = DequantizeLinearValue(gammaQ, MinGamma, MaxGamma, GammaQuantLevels);

			int[] levels = new int[width];
			int cursor = 0;
			if (bandMode != SpectralBandMode.Zero)
			{
				while (cursor < width)
				{
					int eventType = decoder.DecodeIcdfSymbol(LevelEventIcdf, 8);
					if (eventType == 0)
					{
						int runLength = DecodeZeroRun(decoder);
						if (cursor + runLength > width)
						{
							DumpRecentTraces("desync_trace_enc.txt", "desync_trace_dec.txt", int.MaxValue);
							throw new InvalidOperationException($"DESYNC in SF {superframeIndex}, CH {channelIndex}, BLK {blockIndex}, BAND {bandIndex}: Cursor {cursor}, Width {width}, got ZERO_RUN length {runLength}.");
						}

						TraceDecodeDebug($"[DEC] SF {superframeIndex}, CH {channelIndex}, BLK {blockIndex}, BAND {bandIndex}, Cursor {cursor}: ZERO_RUN, Len {runLength}");
						cursor += runLength;
					}
					else
					{
						if (cursor >= width)
						{
							DumpRecentTraces("desync_trace_enc.txt", "desync_trace_dec.txt", int.MaxValue);
							throw new InvalidOperationException("VALUE symbol exceeded band width during spectral decode.");
						}

						int magnitude = DecodeMagnitudeValue(decoder);
						bool negative = decoder.DecodeBit();
						TraceDecodeDebug($"[DEC] SF {superframeIndex}, CH {channelIndex}, BLK {blockIndex}, BAND {bandIndex}, Cursor {cursor}: VALUE, Mag {magnitude}, Sign {negative}");
						levels[cursor] = negative ? -magnitude : magnitude;
						cursor++;
					}
				}
			}

			bands[bandIndex] = new PulsarQuantizedBand
			{
				Start = start,
				Width = width,
				Bits = 1,
				Scale = scale,
				NormalizedStep = normalizedStep,
				CompandGamma = gamma,
				Levels = levels,
			};
			states[bandIndex] = new BandParameterState { ScaleQ = scaleQ, StepQ = stepQ, GammaQ = gammaQ };
			runningStart = end;
		}

		for (int fillBand = activeBandCount; fillBand < BandCount; fillBand++)
		{
			int start = runningStart;
			int remainingBands = BandCount - fillBand;
			int remainingWidth = Math.Max(0, spectrumLength - runningStart);
			int width = remainingBands > 0 ? remainingWidth / remainingBands : remainingWidth;
			if (fillBand == BandCount - 1)
			{
				width = remainingWidth;
			}
			int end = start + width;
			bands[fillBand] = new PulsarQuantizedBand
			{
				Start = start,
				Width = width,
				Bits = 0,
				Scale = 1.0f,
				NormalizedStep = 1.0f,
				CompandGamma = 1.0f,
				Levels = new int[width],
			};
			states[fillBand] = new BandParameterState { ScaleQ = 0, StepQ = 0, GammaQ = 0 };
			runningStart = end;
		}

		if (activeBandCount > BandCount)
		{
			throw new InvalidOperationException($"Spectral sideinfo activeBandCount {activeBandCount} exceeds {BandCount}.");
		}

		return (
			new PulsarQuantizedSpectrum
			{
				SpectrumLength = spectrumLength,
				Bands = bands,
			},
			new ChannelParameterState
			{
				Bands = states,
			});
	}

	private static (float[] Samples, int SampleRate, int Channels) DecodeSpectralArchive(PulsarContainerFile container)
	{
		int channels = container.Header.ChannelCount;
		int sampleRate = container.Header.SampleRate;
		int totalFrames = checked((int)container.Header.TotalSamples);
		bool useMidSide = (container.Header.Flags & MidSideStereoFlag) != 0;

		// Decode all superframes into per-channel block lists first.
		var allBlocks = new List<(int SuperframeIndex, int BlockSize, int SegmentOffset, PulsarQuantizedSpectrum Spectrum)>[channels];
		for (int ch = 0; ch < channels; ch++)
		{
			allBlocks[ch] = new List<(int, int, int, PulsarQuantizedSpectrum)>();
		}

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
					if (activeBandCount < 0 || activeBandCount > BandCount)
					{
						throw new InvalidOperationException($"Invalid activeBandCount {activeBandCount} in spectral sideinfo.");
					}

					int blockSize = PulsarBlockLadder.Steps[blockSizeIndex];
					int spectrumLength = blockSize / 2;

					(PulsarQuantizedSpectrum spectrum, ChannelParameterState state) =
						DecodeQuantizedChannel(sideInfoReader, decoder, spectrumLength, sampleRate, activeBandCount, superframe.Index, ch, b);

					allBlocks[ch].Add((superframe.Index, blockSize, segmentOffset, spectrum));
					channelBlockStates[ch].Add(state);
					previous = state;
				}

				previousStates[ch] = previous!;
			}
		}

		// Reconstruct each channel independently using the same overlap-add as the encoder.
		var channelBuffers = new float[channels][];
		for (int ch = 0; ch < channels; ch++)
		{
			channelBuffers[ch] = ReconstructChannel(allBlocks[ch], totalFrames);
		}

		// Inverse Mid/Side stereo.
		if (useMidSide && channels == 2)
		{
			for (int i = 0; i < totalFrames; i++)
			{
				float mid = channelBuffers[0][i];
				float side = channelBuffers[1][i];
				channelBuffers[0][i] = mid + side;
				channelBuffers[1][i] = mid - side;
			}
		}

		// Interleave.
		float[] output = new float[totalFrames * channels];
		for (int frame = 0; frame < totalFrames; frame++)
		{
			for (int ch = 0; ch < channels; ch++)
			{
				output[(frame * channels) + ch] = channelBuffers[ch][frame];
			}
		}

		return (output, sampleRate, channels);
	}

	private static float[] ReconstructChannel(
		List<(int SuperframeIndex, int BlockSize, int SegmentOffset, PulsarQuantizedSpectrum Spectrum)> blocks,
		int totalFrames)
	{
		if (blocks.Count == 0)
		{
			return new float[totalFrames];
		}

		int maxHopSize = PulsarBlockLadder.GetHopSize(PulsarBlockLadder.MaxBlockSize);
		float[] output = new float[totalFrames + maxHopSize];

		foreach (var (superframeIndex, blockSize, segmentOffset, spectrum) in blocks)
		{
			int hopSize = blockSize / 2;
			int sampleStart = (superframeIndex * SuperframeSize) + (segmentOffset * PulsarBlockLadder.ControlHopSize);
			int writeStart = sampleStart - hopSize;

			float[] mdctCoeffs = new float[blockSize / 2];
			PulsarQuantizer.DequantizeSpectrum(mdctCoeffs, spectrum);

			float[] reconstructed = new float[blockSize];
			PulsarTransformEngine.Imdct(mdctCoeffs, reconstructed);
			PulsarTransformEngine.ApplyWindow(reconstructed, BuildSymmetricSineWindow(blockSize));

			for (int i = 0; i < blockSize; i++)
			{
				int outIdx = writeStart + i;
				if (outIdx >= 0 && outIdx < output.Length)
				{
					output[outIdx] += reconstructed[i];
				}
			}
		}

		float[] trimmed = new float[totalFrames];
		Array.Copy(output, 0, trimmed, 0, Math.Min(trimmed.Length, output.Length));
		return trimmed;
	}

	private static int[][][] AllocateSpectralBandBitsGlobal(IReadOnlyList<PulsarPsychoResult>[] psychoByChannel, double totalBitsPerFrame, bool useMidSideStereo)
	{
		int channels = psychoByChannel.Length;
		int frameCount = channels == 0 ? 0 : psychoByChannel[0].Count;
		var allocations = new int[channels][][];
		for (int channel = 0; channel < channels; channel++)
		{
			allocations[channel] = new int[frameCount][];
		}

		double averageBitsPerFrame = totalBitsPerFrame;
		double reservoirCapacity = averageBitsPerFrame * 3.0;
		double reservoir = reservoirCapacity * 0.20;
		double averageDemand = 0.0;
		int demandCount = 0;

		for (int channel = 0; channel < channels; channel++)
		{
			for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
			{
				averageDemand += ComputeChannelFrameDemand(psychoByChannel[channel][frameIndex], channel, useMidSideStereo);
				demandCount++;
			}
		}

		averageDemand = demandCount > 0 ? averageDemand / demandCount : 1.0;

		for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
		{
			double frameDemand = 0.0;
			var channelDemands = new double[channels];
			for (int channel = 0; channel < channels; channel++)
			{
				double demand = ComputeChannelFrameDemand(psychoByChannel[channel][frameIndex], channel, useMidSideStereo);
				channelDemands[channel] = demand;
				frameDemand += demand;
			}

			double normalizedFrameDemand = channels > 0 ? frameDemand / channels : 1.0;
			double pressure = Math.Clamp(normalizedFrameDemand / Math.Max(0.05, averageDemand), 0.55, 1.75);
			double targetBits = averageBitsPerFrame * pressure;

			if (pressure > 1.0)
			{
				double spend = Math.Min(reservoir, (pressure - 1.0) * averageBitsPerFrame * 0.85);
				targetBits += spend;
				reservoir -= spend;
			}
			else
			{
				double save = Math.Min(reservoirCapacity - reservoir, (1.0 - pressure) * averageBitsPerFrame * 0.65);
				targetBits -= save;
				reservoir += save;
			}

			int frameBits = Math.Max(256, (int)Math.Round(targetBits * 0.92));
			double totalChannelDemand = 0.0;
			for (int channel = 0; channel < channels; channel++)
			{
				totalChannelDemand += Math.Max(0.05, channelDemands[channel]);
			}

			for (int channel = 0; channel < channels; channel++)
			{
				int channelBits = Math.Max(
					48,
					(int)Math.Round(frameBits * (Math.Max(0.05, channelDemands[channel]) / Math.Max(0.05, totalChannelDemand))));
				allocations[channel][frameIndex] = AllocateBandBitsForFrame(psychoByChannel[channel][frameIndex], channelBits);
			}
		}

		return allocations;
	}

	private static List<PulsarFrameAllocation> BuildSpectralAllocations(int[][] frameBandBits)
	{
		var allocations = new List<PulsarFrameAllocation>(frameBandBits.Length);
		for (int frameIndex = 0; frameIndex < frameBandBits.Length; frameIndex++)
		{
			int[] bandBits = frameBandBits[frameIndex];
			int targetBits = 0;
			for (int i = 0; i < bandBits.Length; i++)
			{
				targetBits += Math.Max(0, bandBits[i]);
			}

			allocations.Add(new PulsarFrameAllocation
			{
				TargetBits = targetBits,
				MetadataBits = 0,
				BlockBits = targetBits,
				MetadataRatio = 0.0,
				BandBits = bandBits,
				ComplexityWeight = 1.0,
			});
		}

		return allocations;
	}

	private static double ComputeChannelFrameDemand(PulsarPsychoResult frame, int channelIndex, bool useMidSideStereo)
	{
		double pe = Math.Max(0.25f, frame.PerceptualEntropy / 350.0f);
		double smr = 1.0 + Math.Clamp(frame.AveragePositiveSmr / 22.0f, 0.0f, 1.0f) * 0.30;
		double transient = 1.0 + Math.Clamp(frame.TransientScore, 0.0f, 1.5f) * 0.25;
		double bass = 1.0 + Math.Clamp(frame.BassProtection / 22.0f, 0.0f, 1.0f) * 0.18;
		double tonal = 1.0 + Math.Clamp(frame.TonalProtection, 0.0f, 1.0f) * 0.10;
		double stereoBias = 1.0;

		if (useMidSideStereo)
		{
			if (channelIndex == 0)
			{
				stereoBias = 1.18 + Math.Clamp(frame.BassProtection / 28.0f, 0.0f, 0.18);
			}
			else
			{
				stereoBias = 0.52 + Math.Clamp(frame.TransientScore, 0.0f, 0.25f);
			}
		}

		return Math.Clamp(((pe * 0.60) + (smr * 0.15) + (transient * 0.15) + (bass * 0.07) + (tonal * 0.03)) * stereoBias, 0.20, 2.50);
	}

	private static int[] AllocateBandBitsForFrame(PulsarPsychoResult frame, int blockBits)
	{
		int bandCount = frame.SfbBandCenters.Length;
		int[] bits = new int[bandCount];
		double[] weights = new double[bandCount];
		double totalWeight = 0.0;

		for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
		{
			float centerHz = frame.SfbBandCenters[bandIndex];
			float positiveSmr = MathF.Max(0.0f, frame.SmrDb[bandIndex]);
			float tonality = Math.Clamp(frame.Tonality[bandIndex], 0.0f, 1.0f);
			float maskingHeadroom = MathF.Max(0.0f, frame.SfbBandEnergiesDb[bandIndex] - frame.MaskingThresholdDb[bandIndex]);

			double lowBias = centerHz <= 80.0f ? 1.55 : centerHz <= 240.0f ? 1.30 : centerHz <= 1200.0f ? 1.10 : 1.0;
			double transientBias = centerHz >= 3500.0f ? 1.0 + (Math.Clamp(frame.TransientScore, 0.0f, 1.0f) * 0.20) : 1.0;
			double weight = (0.18 + (positiveSmr * 0.09) + (maskingHeadroom * 0.045) + (tonality * 0.20)) * lowBias * transientBias;
			weights[bandIndex] = Math.Max(0.05, weight);
			totalWeight += weights[bandIndex];
		}

		int assigned = 0;
		for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
		{
			int share = (int)Math.Floor(blockBits * (weights[bandIndex] / totalWeight));
			bits[bandIndex] = share;
			assigned += share;
		}

		while (assigned < blockBits)
		{
			int bestBand = 0;
			double bestScore = double.NegativeInfinity;
			for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
			{
				double score = weights[bandIndex] / (bits[bandIndex] + 1.0);
				if (score > bestScore)
				{
					bestScore = score;
					bestBand = bandIndex;
				}
			}

			bits[bestBand]++;
			assigned++;
		}

		return bits;
	}

	private static float[] PadForTdac(float[] input)
	{
		int hopSize = SuperframeSize / 2;
		var padded = new float[input.Length + (hopSize * 2)];
		Array.Copy(input, 0, padded, hopSize, input.Length);
		return padded;
	}

	private static float[][] Deinterleave(float[] interleavedSamples, int channels)
	{
		int frameCount = interleavedSamples.Length / channels;
		var channelBuffers = new float[channels][];
		for (int channel = 0; channel < channels; channel++)
		{
			channelBuffers[channel] = new float[frameCount];
		}

		for (int frame = 0; frame < frameCount; frame++)
		{
			for (int channel = 0; channel < channels; channel++)
			{
				channelBuffers[channel][frame] = interleavedSamples[(frame * channels) + channel];
			}
		}

		return channelBuffers;
	}

	private static float[] GetWindow()
	{
		return BuildSymmetricSineWindow(SuperframeSize);
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

	private static uint QuantizeLinearValue(float value, float minValue, float maxValue, int totalLevels)
	{
		float clamped = Math.Clamp(value, minValue, maxValue);
		float normalized = (clamped - minValue) / Math.Max(1e-12f, maxValue - minValue);
		return (uint)Math.Clamp((int)MathF.Round(normalized * (totalLevels - 1)), 0, totalLevels - 1);
	}

	private static float DequantizeLinearValue(uint value, float minValue, float maxValue, int totalLevels)
	{
		float normalized = value / (float)Math.Max(1, totalLevels - 1);
		return minValue + ((maxValue - minValue) * normalized);
	}

	private static uint QuantizeLogValue(float value, float minValue, float maxValue, int totalLevels)
	{
		float clamped = Math.Clamp(value, minValue, maxValue);
		float minLog = MathF.Log2(minValue);
		float maxLog = MathF.Log2(maxValue);
		float normalized = (MathF.Log2(clamped) - minLog) / Math.Max(1e-12f, maxLog - minLog);
		return (uint)Math.Clamp((int)MathF.Round(normalized * (totalLevels - 1)), 0, totalLevels - 1);
	}

	private static float DequantizeLogValue(uint value, float minValue, float maxValue, int totalLevels)
	{
		float normalized = value / (float)Math.Max(1, totalLevels - 1);
		float minLog = MathF.Log2(minValue);
		float maxLog = MathF.Log2(maxValue);
		return MathF.Pow(2.0f, minLog + ((maxLog - minLog) * normalized));
	}

	private static byte[] BuildPcmSideInfo(int frameLength)
	{
		var writer = new PulsarBitstreamWriter();
		writer.WriteUInt16((ushort)frameLength);
		return writer.ToArray();
	}

	private static int ReadPcmSideInfo(byte[] sideInfo)
	{
		var reader = new PulsarBitstreamReader(sideInfo);
		return reader.ReadUInt16();
	}

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
		if (zigZag < 16)
		{
			encoder.EncodeBinaryValue(0, 2);
			encoder.EncodeBits(zigZag, 4);
		}
		else if (zigZag < 128)
		{
			encoder.EncodeBinaryValue(1, 2);
			encoder.EncodeBits(zigZag, 7);
		}
		else if (zigZag < 1024)
		{
			encoder.EncodeBinaryValue(2, 2);
			encoder.EncodeBits(zigZag, 10);
		}
		else
		{
			encoder.EncodeBinaryValue(3, 2);
			encoder.EncodeBits(zigZag, 17);
		}
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

	private static int FloatToPcm16(float sample)
	{
		float clamped = Math.Clamp(sample, -1.0f, 0.9999695f);
		return (int)MathF.Round(clamped * 32767.0f);
	}

	private static uint ZigZagEncode(int value)
	{
		return (uint)((value << 1) ^ (value >> 31));
	}

	private static int ZigZagDecode(uint value)
	{
		return (int)((value >> 1) ^ (uint)-(int)(value & 1));
	}

	private static void ClearDebugTrace()
	{
		lock (DebugTraceLock)
		{
			EncoderDebugTrace.Clear();
			DecoderDebugTrace.Clear();
		}
	}

	private static void TraceEncodeDebug(string line)
	{
		AppendTrace(EncoderDebugTrace, line);
	}

	private static void TraceDecodeDebug(string line)
	{
		AppendTrace(DecoderDebugTrace, line);
	}

	private static void AppendTrace(List<string> trace, string line)
	{
		lock (DebugTraceLock)
		{
			trace.Add(line);
			if (trace.Count > 500000)
			{
				trace.RemoveRange(0, trace.Count - 500000);
			}
		}
	}

	private static void DumpRecentTraces(string encoderPath, string decoderPath, int maxLines)
	{
		string[] encoderSnapshot;
		string[] decoderSnapshot;
		lock (DebugTraceLock)
		{
			encoderSnapshot = SnapshotTrace(EncoderDebugTrace, maxLines);
			decoderSnapshot = SnapshotTrace(DecoderDebugTrace, maxLines);
		}

		File.WriteAllLines(encoderPath, encoderSnapshot);
		File.WriteAllLines(decoderPath, decoderSnapshot);
	}

	private static string[] SnapshotTrace(List<string> trace, int maxLines)
	{
		int count = Math.Min(maxLines, trace.Count);
		return count > 0
			? trace.GetRange(trace.Count - count, count).ToArray()
			: ["<empty trace>"];
	}

	private static void EncodeZeroRun(PulsarRangeEncoder encoder, int zeroRun)
	{
		if (zeroRun <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(zeroRun));
		}

		if (zeroRun == 1)
		{
			encoder.EncodeIcdfSymbol(0, ZeroRunIcdf, 8);
		}
		else if (zeroRun <= 3)
		{
			encoder.EncodeIcdfSymbol(1, ZeroRunIcdf, 8);
			encoder.EncodeBits((uint)(zeroRun - 2), 1);
		}
		else if (zeroRun <= 7)
		{
			encoder.EncodeIcdfSymbol(2, ZeroRunIcdf, 8);
			encoder.EncodeBits((uint)(zeroRun - 4), 2);
		}
		else if (zeroRun <= 15)
		{
			encoder.EncodeIcdfSymbol(3, ZeroRunIcdf, 8);
			encoder.EncodeBits((uint)(zeroRun - 8), 3);
		}
		else
		{
			encoder.EncodeIcdfSymbol(4, ZeroRunIcdf, 8);
			encoder.EncodeUInt((uint)(zeroRun - 16), 1u << 16);
		}
	}

	private static int DecodeZeroRun(PulsarRangeDecoder decoder)
	{
		int symbol = decoder.DecodeIcdfSymbol(ZeroRunIcdf, 8);
		return symbol switch
		{
			0 => 1,
			1 => 2 + (int)decoder.DecodeBits(1),
			2 => 4 + (int)decoder.DecodeBits(2),
			3 => 8 + (int)decoder.DecodeBits(3),
			4 => 16 + (int)decoder.DecodeUInt(1u << 16),
			_ => throw new InvalidOperationException($"Invalid ZERO_RUN symbol {symbol}."),
		};
	}

	private static void EncodeMagnitudeValue(PulsarRangeEncoder encoder, int magnitude)
	{
		if (magnitude <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(magnitude));
		}

		if (magnitude == 1)
		{
			encoder.EncodeIcdfSymbol(0, MagnitudeIcdf, 8);
		}
		else if (magnitude == 2)
		{
			encoder.EncodeIcdfSymbol(1, MagnitudeIcdf, 8);
		}
		else if (magnitude <= 4)
		{
			encoder.EncodeIcdfSymbol(2, MagnitudeIcdf, 8);
			encoder.EncodeBits((uint)(magnitude - 3), 1);
		}
		else if (magnitude <= 8)
		{
			encoder.EncodeIcdfSymbol(3, MagnitudeIcdf, 8);
			encoder.EncodeBits((uint)(magnitude - 5), 2);
		}
		else if (magnitude <= 16)
		{
			encoder.EncodeIcdfSymbol(4, MagnitudeIcdf, 8);
			encoder.EncodeBits((uint)(magnitude - 9), 3);
		}
		else
		{
			encoder.EncodeIcdfSymbol(5, MagnitudeIcdf, 8);
			encoder.EncodeUInt((uint)(magnitude - 17), 1u << 16);
		}
	}

	private static int DecodeMagnitudeValue(PulsarRangeDecoder decoder)
	{
		int symbol = decoder.DecodeIcdfSymbol(MagnitudeIcdf, 8);
		return symbol switch
		{
			0 => 1,
			1 => 2,
			2 => 3 + (int)decoder.DecodeBits(1),
			3 => 5 + (int)decoder.DecodeBits(2),
			4 => 9 + (int)decoder.DecodeBits(3),
			_ => 17 + (int)decoder.DecodeUInt(1u << 16),
		};
	}


	private static bool IsAllZero(int[] levels)
	{
		for (int i = 0; i < levels.Length; i++)
		{
			if (levels[i] != 0)
			{
				return false;
			}
		}

		return true;
	}

	private static void ApplyMidSideStereo(float[] left, float[] right)
	{
		if (left.Length != right.Length)
		{
			throw new InvalidOperationException("Mid/Side stereo requires matching channel lengths.");
		}

		for (int i = 0; i < left.Length; i++)
		{
			float l = left[i];
			float r = right[i];
			left[i] = 0.5f * (l + r);
			right[i] = 0.5f * (l - r);
		}
	}

	private static List<SpectralSuperframePlan> BuildSuperframePlans(IReadOnlyList<PulsarFramePlan> framePlans, int sampleCount)
	{
		var superframes = new List<SpectralSuperframePlan>((framePlans.Count + SuperframeSegmentCount - 1) / SuperframeSegmentCount);

		for (int superframeIndex = 0; superframeIndex * SuperframeSegmentCount < framePlans.Count; superframeIndex++)
		{
			int segmentStart = superframeIndex * SuperframeSegmentCount;
			int[] localStates = new int[SuperframeSegmentCount];

			for (int i = 0; i < SuperframeSegmentCount; i++)
			{
				int globalSegment = Math.Min(segmentStart + i, framePlans.Count - 1);
				localStates[i] = framePlans[globalSegment].BlockSize;
			}

			bool allLarge = true;
			int firstState = localStates[0];
			for (int i = 0; i < localStates.Length; i++)
			{
				if (localStates[i] != firstState || localStates[i] <= SuperframeSize)
				{
					allLarge = false;
					break;
				}
			}

			var blocks = new List<ArchiveBlockPlan>();
			byte patternId;
			PulsarLargeBlockState largeBlockState;

			if (allLarge)
			{
				patternId = (byte)PulsarSuperframePatternCatalog.ResolvePatternId([SuperframeSize, SuperframeSize]);
				largeBlockState = GetLargeBlockState(firstState);
				if (ShouldEmitLargeBlock(superframeIndex, firstState))
				{
					blocks.Add(new ArchiveBlockPlan
					{
						BlockSize = firstState,
						SegmentOffset = 0,
					});
				}
			}
			else
			{
				largeBlockState = PulsarLargeBlockState.Standalone;
				var patternBlocks = new List<int>();

				for (int segmentOffset = 0; segmentOffset < SuperframeSegmentCount;)
				{
					int blockSize = Math.Min(localStates[segmentOffset], SuperframeSize);
					if (blockSize > SuperframeSize || !PulsarBlockLadder.IsValidBlockSize(blockSize))
					{
						blockSize = PulsarBlockLadder.MinBlockSize;
					}

					int fullUnits = Math.Max(1, blockSize / PulsarBlockLadder.ControlHopSize);
					int hopUnits = Math.Max(1, fullUnits / 2);

					// Check contiguity only for segments within this superframe.
					int checkEnd = Math.Min(segmentOffset + fullUnits, SuperframeSegmentCount);
					bool contiguous = true;
					for (int unit = 0; contiguous && unit < checkEnd - segmentOffset; unit++)
					{
						if (Math.Min(localStates[segmentOffset + unit], SuperframeSize) != blockSize)
						{
							contiguous = false;
						}
					}

					if (!contiguous)
					{
						blockSize = PulsarBlockLadder.MinBlockSize;
						hopUnits = 1;
					}

					patternBlocks.Add(blockSize);
					blocks.Add(new ArchiveBlockPlan
					{
						BlockSize = blockSize,
						SegmentOffset = segmentOffset,
					});
					segmentOffset += hopUnits;
				}

				patternId = 0; // Pattern ID is metadata only; decoder reads block layout from sideInfo.
			}

			superframes.Add(new SpectralSuperframePlan
			{
				Index = superframeIndex,
				SegmentStart = segmentStart,
				SampleStart = superframeIndex * SuperframeSize,
				PatternId = patternId,
				LargeBlockState = largeBlockState,
				Blocks = blocks.ToArray(),
			});
		}

		return superframes;
	}

	private static int ComputeActiveBandCount(EncodedBand[] bands)
	{
		for (int bandIndex = bands.Length - 1; bandIndex >= 0; bandIndex--)
		{
			EncodedBand band = bands[bandIndex];
			if (band.Mode != SpectralBandMode.Zero || !IsAllZero(band.Levels))
			{
				return bandIndex + 1;
			}
		}

		return 0;
	}

	private static void WriteRawBandParameters(PulsarBitstreamWriter writer, EncodedBand band)
	{
		writer.WriteUInt16((ushort)band.ScaleQ);
		writer.WriteUInt16((ushort)band.StepQ);
		writer.WriteUInt16((ushort)band.GammaQ);
	}

	private static (uint ScaleQ, uint StepQ, uint GammaQ) ReadRawBandParameters(PulsarBitstreamReader reader)
	{
		return (reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16());
	}

	private static int EstimateSpectralSideInfoBits(EncodedChannelSuperframe[] encodedChannels)
	{
		int bits = 8;

		foreach (EncodedChannelSuperframe channel in encodedChannels)
		{
			bits += 8;
			foreach (EncodedChannelBlock block in channel.Blocks)
			{
				bits += 8;
				bits += 8;
				bits += 8;
				bits += block.ActiveBandCount * 48;
			}
		}

		return bits;
	}


	private static int EstimateChannelPayloadBits(EncodedChannelSuperframe[] encodedChannels, PulsarAllocator allocator)
	{
		int bits = 0;
		foreach (EncodedChannelSuperframe channel in encodedChannels)
		{
			foreach (EncodedChannelBlock block in channel.Blocks)
			{
				foreach (EncodedBand band in block.Bands)
				{
					if (band.Mode == SpectralBandMode.Zero)
					{
						continue;
					}

					bits += allocator.EstimateEntropyBits(band.Levels);
				}
			}
		}

		return bits;
	}

	private static ChannelParameterState BuildParameterState(EncodedChannelSuperframe channel)
	{
		if (channel.Blocks.Length == 0)
		{
			return new ChannelParameterState
			{
				Bands = Array.Empty<BandParameterState>(),
			};
		}

		return BuildParameterState(channel.Blocks[^1]);
	}

	private static ChannelParameterState BuildParameterState(EncodedChannelBlock block)
	{
		var states = new BandParameterState[block.Bands.Length];
		for (int bandIndex = 0; bandIndex < block.Bands.Length; bandIndex++)
		{
			EncodedBand band = block.Bands[bandIndex];
			states[bandIndex] = new BandParameterState
			{
				ScaleQ = band.ScaleQ,
				StepQ = band.StepQ,
				GammaQ = band.GammaQ,
			};
		}

		return new ChannelParameterState
		{
			Bands = states,
		};
	}

	private static int[] AverageBandBits(IReadOnlyList<PulsarFrameAllocation> allocations, int startSegment, int blockSize)
	{
		int units = Math.Max(1, blockSize / PulsarBlockLadder.ControlHopSize);
		int clampedStart = Math.Clamp(startSegment, 0, allocations.Count - 1);
		int clampedEnd = Math.Clamp(startSegment + units - 1, clampedStart, allocations.Count - 1);
		int bandCount = allocations[clampedStart].BandBits.Length;
		var averaged = new int[bandCount];
		int frameCount = (clampedEnd - clampedStart) + 1;

		for (int segment = clampedStart; segment <= clampedEnd; segment++)
		{
			int[] sourceBits = allocations[segment].BandBits;
			for (int band = 0; band < bandCount; band++)
			{
				averaged[band] += sourceBits[Math.Min(band, sourceBits.Length - 1)];
			}
		}

		return averaged;
	}

	private static PulsarPsychoResult SelectPsychoFrame(IReadOnlyList<PulsarPsychoResult> psychoFrames, int startSegment, int blockSize)
	{
		int units = Math.Max(1, blockSize / PulsarBlockLadder.ControlHopSize);
		int centerIndex = Math.Clamp(startSegment + (units / 2), 0, psychoFrames.Count - 1);
		return psychoFrames[centerIndex];
	}

	private static float[] AnalyzeBlockSpectrum(float[] samples, int sampleStart, int blockSize)
	{
		int hopSize = PulsarBlockLadder.GetHopSize(blockSize);
		float[] frame = new float[blockSize];
		int sourceStart = sampleStart - hopSize;

		for (int i = 0; i < blockSize; i++)
		{
			int sourceIndex = sourceStart + i;
			frame[i] = sourceIndex >= 0 && sourceIndex < samples.Length ? samples[sourceIndex] : 0.0f;
		}

		float[] window = BuildSymmetricSineWindow(blockSize);
		PulsarTransformEngine.ApplyWindow(frame, window);
		float[] spectrum = new float[blockSize / 2];
		PulsarTransformEngine.Mdct(frame, spectrum);
		return spectrum;
	}

	private static float[] BuildSymmetricSineWindow(int length)
	{
		var window = new float[length];
		for (int i = 0; i < length; i++)
		{
			window[i] = (float)Math.Sin(Math.PI * (i + 0.5) / length);
		}

		return window;
	}

	private static bool ShouldEmitLargeBlock(int superframeIndex, int blockSize)
	{
		int hopSize = PulsarBlockLadder.GetHopSize(blockSize);
		return ((superframeIndex * SuperframeSize) % hopSize) == 0;
	}

	private static PulsarLargeBlockState GetLargeBlockState(int blockSize)
	{
		return blockSize switch
		{
			4096 => PulsarLargeBlockState.PartOf4096,
			8192 => PulsarLargeBlockState.PartOf8192,
			16384 => PulsarLargeBlockState.PartOf16384,
			_ => PulsarLargeBlockState.Standalone,
		};
	}

	private static (BandParameterState? State, SpectralBandMode ReuseMode) GetBestReference(ChannelParameterState? previous, ChannelParameterState? stereoReference, int bandIndex, uint scaleQ, uint stepQ, uint gammaQ)
	{
		if (stereoReference is not null && bandIndex < stereoReference.Bands.Length)
		{
			BandParameterState stereo = stereoReference.Bands[bandIndex];
			if (scaleQ == stereo.ScaleQ && stepQ == stereo.StepQ && gammaQ == stereo.GammaQ)
			{
				return (stereo, SpectralBandMode.ReuseStereo);
			}

			if (CanDeltaEncode(scaleQ, stereo.ScaleQ, DeltaScaleLimit) &&
				CanDeltaEncode(stepQ, stereo.StepQ, DeltaStepLimit) &&
				CanDeltaEncode(gammaQ, stereo.GammaQ, DeltaGammaLimit))
			{
				return (stereo, SpectralBandMode.ReuseStereo);
			}
		}

		if (previous is not null && bandIndex < previous.Bands.Length)
		{
			BandParameterState prior = previous.Bands[bandIndex];
			if (scaleQ == prior.ScaleQ && stepQ == prior.StepQ && gammaQ == prior.GammaQ)
			{
				return (prior, SpectralBandMode.ReusePrevious);
			}

			if (CanDeltaEncode(scaleQ, prior.ScaleQ, DeltaScaleLimit) &&
				CanDeltaEncode(stepQ, prior.StepQ, DeltaStepLimit) &&
				CanDeltaEncode(gammaQ, prior.GammaQ, DeltaGammaLimit))
			{
				return (prior, SpectralBandMode.ReusePrevious);
			}
		}

		return (null, SpectralBandMode.Explicit);
	}

	private static bool CanDeltaEncode(uint value, uint reference, int limit)
	{
		int delta = unchecked((int)value - (int)reference);
		return Math.Abs(delta) <= limit;
	}

	private static uint EncodeSignedToUnsigned(int value, int bitCount)
	{
		int limit = (1 << (bitCount - 1)) - 1;
		if (value < -limit || value > limit)
		{
			throw new ArgumentOutOfRangeException(nameof(value), $"Signed delta {value} does not fit in {bitCount} bits.");
		}

		int mask = (1 << bitCount) - 1;
		return (uint)(value & mask);
	}

	private static int DecodeUnsignedToSigned(uint value, int bitCount)
	{
		int signBit = 1 << (bitCount - 1);
		int mask = (1 << bitCount) - 1;
		int signed = (int)(value & (uint)mask);
		return (signed & signBit) != 0 ? signed - (1 << bitCount) : signed;
	}

	private static (uint ScaleQ, uint StepQ, uint GammaQ) ResolveReferencedParameters(ChannelParameterState? state, int bandIndex, string referenceName)
	{
		if (state is null || bandIndex >= state.Bands.Length)
		{
			throw new InvalidOperationException($"Spectral archive referenced missing {referenceName} band parameters.");
		}

		BandParameterState band = state.Bands[bandIndex];
		return (band.ScaleQ, band.StepQ, band.GammaQ);
	}

	private static uint ApplyDelta(uint baseValue, int delta, int totalLevels)
	{
		return (uint)Math.Clamp(unchecked((int)baseValue + delta), 0, totalLevels - 1);
	}
}
