using System;
using System.Collections.Generic;

public static class PulsarSuperframePatternCatalog
{
	private static readonly IReadOnlyList<int[]> Patterns = BuildPatterns();

	public static int Count => Patterns.Count;

	public static IReadOnlyList<int> GetPattern(int patternId)
	{
		if ((uint)patternId >= (uint)Patterns.Count)
		{
			throw new ArgumentOutOfRangeException(nameof(patternId));
		}

		return Patterns[patternId];
	}

	public static int ResolvePatternId(IReadOnlyList<int> blockSizes)
	{
		ArgumentNullException.ThrowIfNull(blockSizes);

		for (int patternId = 0; patternId < Patterns.Count; patternId++)
		{
			int[] pattern = Patterns[patternId];
			if (pattern.Length != blockSizes.Count)
			{
				continue;
			}

			bool match = true;
			for (int i = 0; i < pattern.Length; i++)
			{
				if (pattern[i] != blockSizes[i])
				{
					match = false;
					break;
				}
			}

			if (match)
			{
				return patternId;
			}
		}

		throw new ArgumentException("Block layout is not a valid 2048-superframe pattern.", nameof(blockSizes));
	}

	private static IReadOnlyList<int[]> BuildPatterns()
	{
		var patterns = new List<int[]>();
		var current = new List<int>(8);
		BuildPatternsRecursive(8, current, patterns);
		return patterns;
	}

	private static void BuildPatternsRecursive(int remainingUnits, List<int> current, List<int[]> patterns)
	{
		if (remainingUnits == 0)
		{
			patterns.Add([.. current]);
			return;
		}

		TryAppend(4, 2048);
		TryAppend(2, 1024);
		TryAppend(1, 512);
		TryAppend(1, 256);

		void TryAppend(int units, int blockSize)
		{
			if (units > remainingUnits)
			{
				return;
			}

			current.Add(blockSize);
			BuildPatternsRecursive(remainingUnits - units, current, patterns);
			current.RemoveAt(current.Count - 1);
		}
	}
}
