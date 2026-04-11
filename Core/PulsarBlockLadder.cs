using System;

public static class PulsarBlockLadder
{
	public static readonly int[] Steps = [256, 512, 1024, 2048, 4096, 8192, 16384];

	public const int MinBlockSize = 256;
	public const int DefaultBlockSize = 2048;
	public const int MaxBlockSize = 16384;
	public const int AnalysisFrameSize = MaxBlockSize;
	public const int ControlHopSize = 256;

	public static void Validate()
	{
		if (Steps.Length == 0)
		{
			throw new InvalidOperationException("Pulsar block ladder must contain at least one block size.");
		}

		for (int index = 0; index < Steps.Length; index++)
		{
			int blockSize = Steps[index];
			if (!IsPowerOfTwo(blockSize))
			{
				throw new InvalidOperationException($"Block size {blockSize} must be a power of two.");
			}

			if (index > 0 && blockSize <= Steps[index - 1])
			{
				throw new InvalidOperationException("Pulsar block ladder must be strictly ascending.");
			}
		}

		if (Steps[0] != MinBlockSize || Steps[^1] != MaxBlockSize)
		{
			throw new InvalidOperationException("Min/max block size constants must match the ladder.");
		}

		if (DefaultBlockSize != Steps[GetIndex(DefaultBlockSize)])
		{
			throw new InvalidOperationException("Default block size must be part of the ladder.");
		}

		if (AnalysisFrameSize != MaxBlockSize)
		{
			throw new InvalidOperationException("Analysis frame size must match the largest block size.");
		}

		int smallestHopSize = GetHopSize(MinBlockSize);
		if (ControlHopSize < smallestHopSize || ControlHopSize % smallestHopSize != 0)
		{
			throw new InvalidOperationException("Control hop size must be a multiple of the smallest block hop.");
		}
	}

	public static bool IsValidBlockSize(int blockSize)
	{
		return Array.IndexOf(Steps, blockSize) >= 0;
	}

	public static int GetHopSize(int blockSize)
	{
		EnsureValid(blockSize);
		return blockSize / 2;
	}

	public static int GetIndex(int blockSize)
	{
		int index = Array.IndexOf(Steps, blockSize);
		if (index < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "Block size is not part of the Pulsar ladder.");
		}

		return index;
	}

	public static int MoveToward(int currentBlockSize, int targetBlockSize)
	{
		EnsureValid(currentBlockSize);
		EnsureValid(targetBlockSize);

		int currentIndex = GetIndex(currentBlockSize);
		int targetIndex = GetIndex(targetBlockSize);
		if (currentIndex == targetIndex)
		{
			return currentBlockSize;
		}

		return currentIndex < targetIndex
			? Steps[currentIndex + 1]
			: Steps[currentIndex - 1];
	}

	public static int MoveUp(int blockSize)
	{
		int index = GetIndex(blockSize);
		return index < Steps.Length - 1 ? Steps[index + 1] : blockSize;
	}

	public static int MoveDown(int blockSize)
	{
		int index = GetIndex(blockSize);
		return index > 0 ? Steps[index - 1] : blockSize;
	}

	public static int GetSmallestBlockSize() => Steps[0];

	public static int GetLargestBlockSize() => Steps[^1];

	public static int GetSecondSmallestBlockSize() => Steps[Math.Min(1, Steps.Length - 1)];

	public static int GetSecondLargestBlockSize() => Steps[Math.Max(0, Steps.Length - 2)];

	private static void EnsureValid(int blockSize)
	{
		if (!IsValidBlockSize(blockSize))
		{
			throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "Block size is not part of the Pulsar ladder.");
		}
	}

	private static bool IsPowerOfTwo(int value)
	{
		return value > 0 && (value & (value - 1)) == 0;
	}
}
