using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;

public static class PulsarTransformEngine
{
	private static readonly ConcurrentDictionary<int, float[]> WindowCache = new();
	private static readonly ConcurrentDictionary<int, float[]> MdctKernelCache = new();
	private static readonly ConcurrentDictionary<int, float[]> ImdctKernelCache = new();

	static PulsarTransformEngine()
	{
		PulsarBlockLadder.Validate();
	}

	public static float[] Process(float[] input, PulsarPlanner planner)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(planner);

		var framePlans = BuildFramePlans(input, planner);
		var renderedPaths = RenderRequiredPaths(input, framePlans);

		return ApplyPulsarSwitching(renderedPaths, framePlans, input.Length);
	}

	public static (float[] Output, List<PulsarFramePlan> Plans) ProcessWithPlans(float[] input, PulsarPlanner planner)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(planner);

		var framePlans = BuildFramePlans(input, planner);
		var renderedPaths = RenderRequiredPaths(input, framePlans);

		return (ApplyPulsarSwitching(renderedPaths, framePlans, input.Length), framePlans);
	}

	public static float[] ProcessLegacy(float[] input, int blockSize = PulsarBlockLadder.DefaultBlockSize)
	{
		ArgumentNullException.ThrowIfNull(input);

		if (!PulsarBlockLadder.IsValidBlockSize(blockSize))
		{
			throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "Legacy block size must be a valid ladder step.");
		}

		return RenderStationaryPath(input, blockSize);
	}

public static float[] ProcessLegacyPlanner(float[] input, PulsarPlanner planner, Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(planner);

        var framePlans = BuildFramePlans(input, planner, progress);
		var renderedPaths = RenderRequiredPaths(input, framePlans);

		return ApplyPlannerOnlySwitching(renderedPaths, framePlans, input.Length);
	}

	private static float[] ApplyPlannerOnlySwitching(
		Dictionary<int, float[]> renderedPaths,
		IReadOnlyList<PulsarFramePlan> framePlans,
		int sampleCount)
	{
		float[] output = new float[sampleCount];

		for (int planIndex = 0; planIndex < framePlans.Count; planIndex++)
		{
			PulsarFramePlan plan = framePlans[planIndex];
			int segmentStart = planIndex * PulsarBlockLadder.ControlHopSize;
			int segmentEnd = Math.Min(segmentStart + PulsarBlockLadder.ControlHopSize, sampleCount);
			float[] currentPath = renderedPaths[plan.BlockSize];

			for (int sampleIndex = segmentStart; sampleIndex < segmentEnd; sampleIndex++)
			{
				output[sampleIndex] = currentPath[sampleIndex];
			}
		}

		return output;
	}

	private static Dictionary<int, float[]> RenderRequiredPaths(float[] input, IReadOnlyList<PulsarFramePlan> framePlans)
	{
		var requiredBlockSizes = new HashSet<int>();

		foreach (PulsarFramePlan plan in framePlans)
		{
			requiredBlockSizes.Add(plan.BlockSize);
		}

		var renderedPaths = new ConcurrentDictionary<int, float[]>();

		Parallel.ForEach(requiredBlockSizes, blockSize =>
		{
			renderedPaths[blockSize] = RenderStationaryPath(input, blockSize);
		});

		return new Dictionary<int, float[]>(renderedPaths);
	}

	public static void ApplyWindow(float[] data)
{
	ArgumentNullException.ThrowIfNull(data);
	ApplyWindow(data, BuildSymmetricSineWindow(data.Length));
}

public static void ApplyWindow(float[] data, float[] window)
	{
		ArgumentNullException.ThrowIfNull(data);
		ArgumentNullException.ThrowIfNull(window);
		if (data.Length != window.Length)
		{
			throw new ArgumentException("Window length must match the target buffer length.", nameof(window));
		}

		for (int i = 0; i < data.Length; i++)
		{
			data[i] *= window[i];
		}
	}

	public static float[] Mdct(float[] input)
	{
		int n2 = input.Length;
		int n = n2 / 2;
		float[] output = new float[n];
		Mdct(input, output);
		return output;
	}

	public static void Mdct(float[] input, float[] output)
	{
		int n2 = input.Length;
		int n = n2 / 2;
		float[] kernel = GetMdctKernel(n2);

		Parallel.For(0, n, k =>
		{
			double sum = 0;
			int kernelOffset = k * n2;
			for (int i = 0; i < n2; i++)
			{
				sum += input[i] * kernel[kernelOffset + i];
			}

			output[k] = (float)sum;
		});
	}

	public static float[] Imdct(float[] input)
	{
		int n = input.Length;
		int n2 = n * 2;
		float[] output = new float[n2];
		Imdct(input, output);
		return output;
	}

	public static void Imdct(float[] input, float[] output)
	{
		int n = input.Length;
		int n2 = n * 2;
		float[] kernel = GetImdctKernel(n2);

		Parallel.For(0, n2, i =>
		{
			double sum = 0;
			int kernelOffset = i * n;
			for (int k = 0; k < n; k++)
			{
				sum += input[k] * kernel[kernelOffset + k];
			}

			output[i] = (float)(sum * 2.0 / n);
		});
	}

	private static List<PulsarFramePlan> BuildFramePlans(float[] input, PulsarPlanner planner, Action<int, int>? progress = null)
	{
		return planner.PlanSong(input, progress);
	}

	private static float[] RenderStationaryPath(float[] input, int blockSize)
	{
		int hopSize = PulsarBlockLadder.GetHopSize(blockSize);
		float[] paddedInput = new float[input.Length + (hopSize * 2)];
		Array.Copy(input, 0, paddedInput, hopSize, input.Length);

		float[] output = new float[paddedInput.Length + hopSize];
		float[] overlap = new float[hopSize];
		float[] window = GetWindow(blockSize);
		float[] frame = new float[blockSize];
		float[] spectrum = new float[blockSize / 2];
		float[] reconstructed = new float[blockSize];

		for (int offset = 0; offset < paddedInput.Length; offset += hopSize)
		{
			int available = Math.Min(blockSize, paddedInput.Length - offset);
			Array.Clear(frame, 0, blockSize);
			Array.Copy(paddedInput, offset, frame, 0, available);
			ApplyWindow(frame, window);

			Mdct(frame, spectrum);
			Imdct(spectrum, reconstructed);
			ApplyWindow(reconstructed, window);

			for (int sampleIndex = 0; sampleIndex < hopSize; sampleIndex++)
			{
				int outputIndex = offset + sampleIndex;
				if (outputIndex < output.Length)
				{
					output[outputIndex] = reconstructed[sampleIndex] + overlap[sampleIndex];
				}

				overlap[sampleIndex] = reconstructed[sampleIndex + hopSize];
			}
		}

		int tailOffset = Math.Min(paddedInput.Length, output.Length - hopSize);
		Array.Copy(overlap, 0, output, tailOffset, hopSize);
		float[] trimmed = new float[input.Length];
		Array.Copy(output, hopSize, trimmed, 0, input.Length);
		return trimmed;
	}

	private static float[] ApplyPulsarSwitching(
		Dictionary<int, float[]> renderedPaths,
		IReadOnlyList<PulsarFramePlan> framePlans,
		int sampleCount)
	{
		float[] output = new float[sampleCount];
		int previousBlockSize = framePlans.Count > 0 ? framePlans[0].BlockSize : PulsarBlockLadder.DefaultBlockSize;

		for (int planIndex = 0; planIndex < framePlans.Count; planIndex++)
		{
			PulsarFramePlan plan = framePlans[planIndex];
			int segmentStart = planIndex * PulsarBlockLadder.ControlHopSize;
			int segmentEnd = Math.Min(segmentStart + PulsarBlockLadder.ControlHopSize, sampleCount);
			float[] currentPath = renderedPaths[plan.BlockSize];
			float[] previousPath = renderedPaths[previousBlockSize];

			if (planIndex == 0 || plan.BlockSize == previousBlockSize)
			{
				for (int sampleIndex = segmentStart; sampleIndex < segmentEnd; sampleIndex++)
				{
					output[sampleIndex] = currentPath[sampleIndex];
				}
			}
			else
			{
				BlendTransitionSegment(output, segmentStart, segmentEnd, renderedPaths, previousBlockSize, plan.BlockSize);
			}

			previousBlockSize = plan.BlockSize;
		}

		return output;
	}

	private static float[] BuildSymmetricSineWindow(int length)
	{
		float[] window = new float[length];
		for (int i = 0; i < length; i++)
		{
			window[i] = (float)Math.Sin(Math.PI * (i + 0.5) / length);
		}

		return window;
	}

	private static void BlendTransitionSegment(
		float[] output,
		int segmentStart,
		int segmentEnd,
		Dictionary<int, float[]> renderedPaths,
		int previousBlockSize,
		int currentBlockSize)
	{
		float[] prevPath = renderedPaths[previousBlockSize];
		float[] curPath = renderedPaths[currentBlockSize];
		int segmentLength = segmentEnd - segmentStart;
		int transitionLength = Math.Min(segmentLength * 3, output.Length - segmentStart);
		if (transitionLength <= 0)
		{
			return;
		}

		for (int j = 0; j < transitionLength; j++)
		{
			int sampleIndex = segmentStart + j;
			if (sampleIndex >= output.Length)
			{
				break;
			}

			float t = transitionLength <= 1 ? 1.0f : (float)j / (transitionLength - 1);
			t = t * t * (3f - 2f * t);
			float fadeOut = 1f - t;
			float fadeIn = t;
			output[sampleIndex] = (prevPath[sampleIndex] * fadeOut) + (curPath[sampleIndex] * fadeIn);
		}
	}

	private static float[] GetWindow(int length)
	{
		return WindowCache.GetOrAdd(length, BuildSymmetricSineWindow);
	}

	private static float[] GetMdctKernel(int n2)
	{
		return MdctKernelCache.GetOrAdd(n2, BuildMdctKernel);
	}

	private static float[] GetImdctKernel(int n2)
	{
		return ImdctKernelCache.GetOrAdd(n2, BuildImdctKernel);
	}

	private static float[] BuildMdctKernel(int n2)
	{
		int n = n2 / 2;
		double constant = Math.PI / n;
		float[] kernel = new float[n * n2];

		for (int k = 0; k < n; k++)
		{
			int offset = k * n2;
			for (int i = 0; i < n2; i++)
			{
				kernel[offset + i] = (float)Math.Cos(constant * (i + 0.5 + n / 2.0) * (k + 0.5));
			}
		}

		return kernel;
	}

	private static float[] BuildImdctKernel(int n2)
	{
		int n = n2 / 2;
		double constant = Math.PI / n;
		float[] kernel = new float[n2 * n];

		for (int i = 0; i < n2; i++)
		{
			int offset = i * n;
			for (int k = 0; k < n; k++)
			{
				kernel[offset + k] = (float)Math.Cos(constant * (i + 0.5 + n / 2.0) * (k + 0.5));
			}
		}

		return kernel;
	}
}

public sealed class PulsarEncodedFrame
{
	public required int PreviousBlockSize { get; init; }
	public required int BlockSize { get; init; }
	public required PulsarSwitchDirection Direction { get; init; }
	public required PulsarTransientLevel TransientLevel { get; init; }
	public required double AttackRatio { get; init; }
	public required double PeakDeltaDb { get; init; }
	public required List<float[]> Blocks { get; init; }

	public int TotalBandCount
	{
		get
		{
			int bandCount = 0;
			foreach (var block in Blocks)
			{
				bandCount += block.Length;
			}

			return bandCount;
		}
	}
}
