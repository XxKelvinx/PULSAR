using System.Collections.Generic;

public sealed class PulsarContainerFile
{
	public PulsarContainerHeader Header { get; } = new();
	public List<PulsarFramePacket> Frames { get; } = new();
}

public enum PulsarCabacProfile
{
	Standard = 0,
	AggressiveSilence = 1,
	ComplexNoise = 2,
}

public sealed class PulsarContainerHeader
{
	public int Version { get; set; } = 1;
	public int SampleRate { get; set; }
	public int ChannelCount { get; set; }
	public int AnalysisFrameSize { get; set; } = PulsarBlockLadder.AnalysisFrameSize;
	public int ControlHopSize { get; set; } = PulsarBlockLadder.ControlHopSize;
	public int MinBlockSize { get; set; } = PulsarBlockLadder.MinBlockSize;
	public int MaxBlockSize { get; set; } = PulsarBlockLadder.MaxBlockSize;
}

// Minimal 2-byte frame header for future stream compatibility.
// Byte 0: frame size ID (3 bits), frame type (2 bits), entropy mode flag (1 bit), extension flag (1 bit), reserved (1 bit).
// Byte 1: reserved extension byte for future data or metadata.
public enum PulsarFrameType
{
	Steady = 0,
	SoftTransient = 1,
	HardTransient = 2,
	Reserved = 3,
}

public sealed class PulsarFrameGlobalHeader
{
	public PulsarCabacProfile CabacProfile { get; set; } = PulsarCabacProfile.Standard;
	public int SidePayloadLengthBytes { get; set; }
	public int BlockPayloadLengthBytes { get; set; }
	public int FrameSizeId { get; set; }
	public PulsarFrameType FrameType { get; set; } = PulsarFrameType.Steady;
	public bool EntropyCabacEnabled { get; set; } = true;
	public bool HasHeaderExtension { get; set; }
	public byte HeaderExtensionByte { get; set; }
}

public sealed class PulsarFramePacket
{
	public int Index { get; set; }
	public int PreviousBlockSize { get; set; } = PulsarBlockLadder.DefaultBlockSize;
	public int BlockSize { get; set; } = PulsarBlockLadder.DefaultBlockSize;
	public int HopSize => PulsarBlockLadder.GetHopSize(BlockSize);
	public PulsarSwitchDirection SwitchDirection { get; set; } = PulsarSwitchDirection.Hold;
	public PulsarTransientLevel TransientLevel { get; set; } = PulsarTransientLevel.None;
	public PulsarFrameGlobalHeader GlobalHeader { get; set; } = new();
	public byte[] SidePayload { get; set; } = [];
	public float[][] Blocks { get; set; } = [];
}
