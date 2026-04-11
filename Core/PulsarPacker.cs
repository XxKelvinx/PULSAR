using System;
using System.Collections.Generic;

public sealed class PulsarPacker
{
	public byte[] Pack(PulsarContainerFile container)
	{
		ArgumentNullException.ThrowIfNull(container);

		var bitstream = new PulsarBitstreamWriter();
		WriteHeader(bitstream, container.Header);
		bitstream.WriteInt32(container.Frames.Count);

		foreach (var frame in container.Frames)
		{
			PrepareFrameHeader(frame);
			bitstream.WriteInt32(frame.Index);
			WriteGlobalFrameHeader(bitstream, frame.GlobalHeader);
			bitstream.WriteInt32(frame.PreviousBlockSize);
			bitstream.WriteInt32(frame.BlockSize);
			bitstream.WriteInt32((int)frame.SwitchDirection);
			bitstream.WriteInt32((int)frame.TransientLevel);
			bitstream.WriteBytes(frame.SidePayload);
			bitstream.WriteBytes(BuildBlockPayload(frame));
		}

		return bitstream.ToArray();
	}

	private static void WriteHeader(PulsarBitstreamWriter bitstream, PulsarContainerHeader header)
	{
		bitstream.WriteAscii("PLSR");
		bitstream.WriteInt32(header.Version);
		bitstream.WriteInt32(header.SampleRate);
		bitstream.WriteInt32(header.ChannelCount);
		bitstream.WriteInt32(header.AnalysisFrameSize);
		bitstream.WriteInt32(header.ControlHopSize);
		bitstream.WriteInt32(header.MinBlockSize);
		bitstream.WriteInt32(header.MaxBlockSize);
	}

	private static void WriteGlobalFrameHeader(PulsarBitstreamWriter bitstream, PulsarFrameGlobalHeader header)
	{
		ArgumentNullException.ThrowIfNull(bitstream);
		ArgumentNullException.ThrowIfNull(header);

		if (header.SidePayloadLengthBytes is < 0 or > 4095)
		{
			throw new ArgumentOutOfRangeException(nameof(header.SidePayloadLengthBytes), "Side payload must fit in 12 bits.");
		}

		if (header.BlockPayloadLengthBytes is < 0 or > 4095)
		{
			throw new ArgumentOutOfRangeException(nameof(header.BlockPayloadLengthBytes), "Block payload must fit in 12 bits.");
		}

		if (header.FrameSizeId is < 0 or > 7)
		{
			throw new ArgumentOutOfRangeException(nameof(header.FrameSizeId), "Frame size ID must fit in 3 bits.");
		}

		uint frameHeaderByte0 = (uint)header.FrameSizeId;
		frameHeaderByte0 |= ((uint)header.FrameType & 0x3u) << 3;
		frameHeaderByte0 |= (header.EntropyCabacEnabled ? 1u : 0u) << 5;
		frameHeaderByte0 |= (header.HasHeaderExtension ? 1u : 0u) << 6;

		bitstream.WriteBits(frameHeaderByte0, 8);
		bitstream.WriteBits(header.HeaderExtensionByte, 8);
		bitstream.WriteBits((uint)header.CabacProfile, 3);
		bitstream.WriteBits((uint)header.SidePayloadLengthBytes, 12);
		bitstream.WriteBits((uint)header.BlockPayloadLengthBytes, 12);
	}

	private static void PrepareFrameHeader(PulsarFramePacket frame)
	{
		ArgumentNullException.ThrowIfNull(frame);
		byte[] blockPayload = BuildBlockPayload(frame);
		frame.GlobalHeader.SidePayloadLengthBytes = frame.SidePayload.Length;
		frame.GlobalHeader.BlockPayloadLengthBytes = blockPayload.Length;
		frame.GlobalHeader.FrameSizeId = GetFrameSizeId(frame.BlockSize);
		frame.GlobalHeader.FrameType = frame.TransientLevel switch
		{
			PulsarTransientLevel.Hard => PulsarFrameType.HardTransient,
			PulsarTransientLevel.Soft => PulsarFrameType.SoftTransient,
			_ => PulsarFrameType.Steady,
		};
		frame.GlobalHeader.EntropyCabacEnabled = true;
		frame.GlobalHeader.HasHeaderExtension = false;
		frame.GlobalHeader.HeaderExtensionByte = 0;
	}

	private static int GetFrameSizeId(int blockSize)
	{
		return PulsarBlockLadder.GetIndex(blockSize);
	}

	private static byte[] BuildBlockPayload(PulsarFramePacket frame)
	{
		var blockWriter = new PulsarBitstreamWriter();
		blockWriter.WriteInt32(frame.Blocks.Length);
		foreach (var block in frame.Blocks)
		{
			blockWriter.WriteInt32(block.Length);
			foreach (var coefficient in block)
			{
				blockWriter.WriteSingle(coefficient);
			}
		}

		return blockWriter.ToArray();
	}
}
