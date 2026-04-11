using System;
using System.Collections.Generic;

public sealed class PulsarAllocationConfig
{
	public int TargetKbps { get; init; } = 128;
	public int MinKbps { get; init; } = 64;
	public int MaxKbps { get; init; } = 512;
	public int SampleRate { get; init; } = 44100;
	public int HopSize { get; init; } = PulsarBlockLadder.ControlHopSize;
	public double MetadataFloorRatio { get; init; } = 0.10;
	public double MetadataCeilingRatio { get; init; } = 0.20;
	public int MinBitsPerFrame { get; init; } = 48;
	public int MaxBitsPerFrame { get; init; } = 1536;
}

public sealed class PulsarFrameAllocation
{
	public required int TargetBits { get; init; }
	public required int MetadataBits { get; init; }
	public required int BlockBits { get; init; }
	public required double MetadataRatio { get; init; }
	public required int[] BandBits { get; init; }
	public required double ComplexityWeight { get; init; }
}

public sealed class PulsarAllocator
{
	private readonly PulsarAllocationConfig _config;

	public PulsarAllocator(PulsarAllocationConfig? config = null)
	{
		_config = config ?? new PulsarAllocationConfig();
	}

	public List<PulsarFrameAllocation> AllocateSong(IReadOnlyList<PulsarFramePlan> framePlans)
	{
		ArgumentNullException.ThrowIfNull(framePlans);

		var allocations = new List<PulsarFrameAllocation>(framePlans.Count);
		if (framePlans.Count == 0)
		{
			return allocations;
		}

		double effectiveKbps = Math.Clamp(_config.TargetKbps, _config.MinKbps, _config.MaxKbps);
		double frameSeconds = _config.HopSize / (double)_config.SampleRate;
		double totalBitBudget = effectiveKbps * 1000.0 * frameSeconds * framePlans.Count;
		var weights = new double[framePlans.Count];
		double weightSum = 0.0;

		for (int index = 0; index < framePlans.Count; index++)
		{
			weights[index] = ComputeFrameComplexityWeight(framePlans[index]);
			weightSum += weights[index];
		}

		if (weightSum <= 0.0)
		{
			weightSum = framePlans.Count;
			for (int index = 0; index < framePlans.Count; index++)
			{
				weights[index] = 1.0;
			}
		}

		int targetBudget = Math.Clamp((int)Math.Round(totalBitBudget), _config.MinBitsPerFrame * framePlans.Count, _config.MaxBitsPerFrame * framePlans.Count);
		int accumulatedBits = 0;

		for (int index = 0; index < framePlans.Count; index++)
		{
			PulsarFramePlan plan = framePlans[index];
			double idealBits = totalBitBudget * weights[index] / weightSum;
			int targetBits = Math.Clamp((int)Math.Round(idealBits), _config.MinBitsPerFrame, _config.MaxBitsPerFrame);

			int metadataBits = Math.Max(16, (int)Math.Round(targetBits * ComputeMetadataRatio(plan)));
			int blockBits = Math.Max(0, targetBits - metadataBits);
			int[] bandBits = AllocateBandBits(plan, blockBits);

			allocations.Add(new PulsarFrameAllocation
			{
				TargetBits = targetBits,
				MetadataBits = metadataBits,
				BlockBits = blockBits,
				MetadataRatio = metadataBits / (double)Math.Max(1, targetBits),
				BandBits = bandBits,
				ComplexityWeight = weights[index],
			});

			accumulatedBits += targetBits;
		}

		return allocations;
	}

	private double ComputeFrameComplexityWeight(PulsarFramePlan framePlan)
	{
		double weight = 1.0;
		weight += framePlan.TransientLevel switch
		{
			PulsarTransientLevel.Hard => 0.38,
			PulsarTransientLevel.Soft => 0.18,
			_ => 0.00,
		};

		weight += Math.Clamp(framePlan.AttackRatio / 12.0, 0.0, 0.35);
		weight += Math.Clamp(framePlan.CrestFactor / 10.0, 0.0, 0.24);
		weight += Math.Clamp(framePlan.EnergyModulation / 0.90, 0.0, 0.20);
		weight += Math.Clamp(framePlan.HighBandRatio / 0.45, 0.0, 0.22);
		weight += Math.Clamp(framePlan.LowBandRatio / 0.40, 0.0, 0.14);
		weight += Math.Clamp(framePlan.SustainedHighBandRatio / 0.30, 0.0, 0.10);

		if (framePlan.BlockSize < PulsarBlockLadder.DefaultBlockSize)
		{
			weight *= 1.05;
		}
		else if (framePlan.BlockSize > PulsarBlockLadder.DefaultBlockSize)
		{
			weight *= 0.95;
		}

		return Math.Clamp(weight, 0.7, 2.6);
	}

	private double ComputeMetadataRatio(PulsarFramePlan framePlan)
	{
		double ratio = _config.MetadataFloorRatio;
		ratio += framePlan.TransientLevel switch
		{
			PulsarTransientLevel.Hard => 0.04,
			PulsarTransientLevel.Soft => 0.02,
			_ => 0.00,
		};
		ratio += framePlan.BlockSize switch
		{
			<= 512 => -0.01,
			>= 2048 => 0.02,
			_ => 0.00,
		};
		ratio += Math.Clamp(framePlan.AttackRatio / 40.0, 0.0, 0.03);
		return Math.Clamp(ratio, _config.MetadataFloorRatio, _config.MetadataCeilingRatio);
	}

	private int[] AllocateBandBits(PulsarFramePlan framePlan, int blockBits)
	{
		int bandCount = framePlan.BandCount;
		if (bandCount <= 0 || blockBits <= 0)
		{
			return Array.Empty<int>();
		}

		var bandWeights = new double[bandCount];
		double totalWeight = 0.0;
		double highRatio = framePlan.HighBandRatio;
		double lowRatio = framePlan.LowBandRatio;
		double transientFactor = framePlan.TransientLevel switch
		{
			PulsarTransientLevel.Hard => 1.0,
			PulsarTransientLevel.Soft => 0.6,
			_ => 0.2,
		};
		double dynamicFactor = Math.Clamp(framePlan.CrestFactor / 8.0 + framePlan.EnergyModulation / 2.0, 0.0, 1.0);

		for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
		{
			double position = bandCount == 1 ? 0.0 : (double)bandIndex / (bandCount - 1);
			double lowShape = 1.0 - position;
			double highShape = position;
			double bandWeight = 1.0;
			bandWeight += lowShape * lowRatio * 2.5;
			bandWeight += highShape * highRatio * 2.0;
			bandWeight += (0.5 + position) * transientFactor * 0.8;
			bandWeight += (0.65 + 0.45 * Math.Cos(Math.PI * (position - 0.5))) * dynamicFactor * 0.65;
			bandWeights[bandIndex] = Math.Max(0.05, bandWeight);
			totalWeight += bandWeights[bandIndex];
		}

		int[] bandBits = new int[bandCount];
		int assigned = 0;
		for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
		{
			bandBits[bandIndex] = (int)Math.Floor(blockBits * bandWeights[bandIndex] / totalWeight);
			assigned += bandBits[bandIndex];
		}

		int remaining = blockBits - assigned;
		while (remaining > 0)
		{
			int bestBand = 0;
			double bestScore = double.NegativeInfinity;
			for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
			{
				double score = bandWeights[bandIndex] / (bandBits[bandIndex] + 1);
				if (score > bestScore)
				{
					bestScore = score;
					bestBand = bandIndex;
				}
			}
			bandBits[bestBand]++;
			remaining--;
		}

		return bandBits;
	}

}
