using System;

public sealed class PulsarPacker
{
	private const string Magic = "PLSR";
	private const int HeaderSizeBytes = 32;

	public byte[] Pack(PulsarContainerFile container)
	{
		ArgumentNullException.ThrowIfNull(container);

		if (container.Superframes.Count == 0)
		{
			throw new InvalidOperationException("PulsarPacker v1 expects superframes. Add PulsarSuperframePacket entries before packing.");
		}

		ValidateContainer(container);

		var bitstream = new PulsarBitstreamWriter();
		WriteHeader(bitstream, container.Header, container.Superframes.Count);

		foreach (PulsarSuperframePacket superframe in container.Superframes)
		{
			WriteSuperframe(bitstream, superframe);
		}

		return bitstream.ToArray();
	}

	public PulsarContainerFile Unpack(byte[] payload)
	{
		ArgumentNullException.ThrowIfNull(payload);

		var bitstream = new PulsarBitstreamReader(payload);
		PulsarContainerFile container = ReadHeader(bitstream, out int superframeCount);

		for (int i = 0; i < superframeCount; i++)
		{
			container.Superframes.Add(ReadSuperframe(bitstream, i));
		}

		return container;
	}

	private static void ValidateContainer(PulsarContainerFile container)
	{
		if (container.Header.Version is < 0 or > ushort.MaxValue)
		{
			throw new ArgumentOutOfRangeException(nameof(container.Header.Version), "Version must fit in 16 bits.");
		}

		if (container.Header.ChannelCount is <= 0 or > ushort.MaxValue)
		{
			throw new ArgumentOutOfRangeException(nameof(container.Header.ChannelCount), "Channel count must fit in 16 bits.");
		}

		if (container.Header.SampleRate <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(container.Header.SampleRate), "Sample rate must be positive.");
		}

		if (container.Header.TotalSamples < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(container.Header.TotalSamples), "Total samples must be non-negative.");
		}

		foreach (PulsarSuperframePacket superframe in container.Superframes)
		{
			ValidateSuperframe(superframe);
		}
	}

	private static void ValidateSuperframe(PulsarSuperframePacket superframe)
	{
		ArgumentNullException.ThrowIfNull(superframe);

		if (superframe.PatternId >= PulsarSuperframePatternCatalog.Count)
		{
			throw new ArgumentOutOfRangeException(nameof(superframe.PatternId), $"Pattern ID must be smaller than {PulsarSuperframePatternCatalog.Count}.");
		}

		if (superframe.SideInfo.Length > ushort.MaxValue)
		{
			throw new ArgumentOutOfRangeException(nameof(superframe.SideInfo), "SideInfo length must fit in 16 bits.");
		}
	}

	private static void WriteHeader(PulsarBitstreamWriter bitstream, PulsarContainerHeader header, int superframeCount)
	{
		bitstream.WriteAscii(Magic);
		bitstream.WriteUInt16((ushort)header.Version);
		bitstream.WriteUInt16(header.Flags);
		bitstream.WriteUInt32((uint)header.SampleRate);
		bitstream.WriteUInt16((ushort)header.ChannelCount);
		bitstream.WriteUInt16(header.BandwidthLimitHz);
		bitstream.WriteUInt64((ulong)header.TotalSamples);
		bitstream.WriteUInt32((uint)superframeCount);
		bitstream.WriteUInt32(header.SeekTableOffsetBytes);
	}

	private static PulsarContainerFile ReadHeader(PulsarBitstreamReader bitstream, out int superframeCount)
	{
		string magic = bitstream.ReadAscii(4);
		if (!string.Equals(magic, Magic, StringComparison.Ordinal))
		{
			throw new InvalidOperationException("Invalid PULSAR magic.");
		}

		var container = new PulsarContainerFile();
		container.Header.Version = bitstream.ReadUInt16();
		container.Header.Flags = bitstream.ReadUInt16();
		container.Header.SampleRate = checked((int)bitstream.ReadUInt32());
		container.Header.ChannelCount = bitstream.ReadUInt16();
		container.Header.BandwidthLimitHz = bitstream.ReadUInt16();
		container.Header.TotalSamples = checked((long)bitstream.ReadUInt64());
		superframeCount = checked((int)bitstream.ReadUInt32());
		container.Header.SeekTableOffsetBytes = bitstream.ReadUInt32();
		return container;
	}

	private static void WriteSuperframe(PulsarBitstreamWriter bitstream, PulsarSuperframePacket superframe)
	{
		bitstream.WriteBits(superframe.PatternId, 6);
		bitstream.WriteBits((uint)superframe.LargeBlockState, 2);
		bitstream.WriteUInt16((ushort)superframe.SideInfo.Length);
		bitstream.WriteUInt32((uint)superframe.EntropyPayload.Length);
		bitstream.WriteBytes(superframe.SideInfo);
		bitstream.WriteBytes(superframe.EntropyPayload);
	}

	private static PulsarSuperframePacket ReadSuperframe(PulsarBitstreamReader bitstream, int index)
	{
		byte patternId = (byte)bitstream.ReadBits(6);
		PulsarLargeBlockState largeBlockState = (PulsarLargeBlockState)bitstream.ReadBits(2);
		ushort sideInfoLength = bitstream.ReadUInt16();
		uint payloadLength = bitstream.ReadUInt32();

		if (patternId >= PulsarSuperframePatternCatalog.Count)
		{
			throw new InvalidOperationException($"Invalid superframe pattern ID {patternId}.");
		}

		return new PulsarSuperframePacket
		{
			Index = index,
			PatternId = patternId,
			LargeBlockState = largeBlockState,
			SideInfo = bitstream.ReadBytes(sideInfoLength),
			EntropyPayload = bitstream.ReadBytes(checked((int)payloadLength)),
		};
	}
}
