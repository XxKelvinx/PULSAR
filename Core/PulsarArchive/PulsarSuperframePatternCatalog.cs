/// <summary>
/// Registry of valid superframe packing patterns.
/// PatternId is stored as a 6-bit field (0..63) in the PLSR bitstream.
/// Currently only pattern 0 (default block-ladder layout) is defined.
/// Future patterns may encode M/S stereo, TNS or alternative block topologies.
/// </summary>
public static class PulsarSuperframePatternCatalog
{
    /// <summary>Number of registered patterns. PatternId must be &lt; Count.</summary>
    public const int Count = 1;

    /// <summary>Maximum number of patterns the 6-bit field can represent.</summary>
    public const int MaxPatterns = 64; // 2^6
}
