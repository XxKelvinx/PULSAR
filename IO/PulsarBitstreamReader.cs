using System;
using System.Text;

public sealed class PulsarBitstreamReader
{
	private readonly byte[] _buffer;
	private int _byteOffset;
	private int _bitOffset;

	public PulsarBitstreamReader(byte[] buffer)
	{
		_buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
	}

	public byte ReadByte()
	{
		AlignToByte();
		EnsureAvailable(1);
		return _buffer[_byteOffset++];
	}

	public ushort ReadUInt16()
	{
		AlignToByte();
		EnsureAvailable(sizeof(ushort));
		ushort value = BitConverter.ToUInt16(_buffer, _byteOffset);
		_byteOffset += sizeof(ushort);
		return value;
	}

	public uint ReadUInt32()
	{
		AlignToByte();
		EnsureAvailable(sizeof(uint));
		uint value = BitConverter.ToUInt32(_buffer, _byteOffset);
		_byteOffset += sizeof(uint);
		return value;
	}

	public ulong ReadUInt64()
	{
		AlignToByte();
		EnsureAvailable(sizeof(ulong));
		ulong value = BitConverter.ToUInt64(_buffer, _byteOffset);
		_byteOffset += sizeof(ulong);
		return value;
	}

	public int ReadInt32()
	{
		AlignToByte();
		EnsureAvailable(sizeof(int));
		int value = BitConverter.ToInt32(_buffer, _byteOffset);
		_byteOffset += sizeof(int);
		return value;
	}

	public byte[] ReadBytes(int length)
	{
		if (length < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(length));
		}

		AlignToByte();
		EnsureAvailable(length);
		byte[] result = new byte[length];
		Buffer.BlockCopy(_buffer, _byteOffset, result, 0, length);
		_byteOffset += length;
		return result;
	}

	public string ReadAscii(int length)
	{
		return Encoding.ASCII.GetString(ReadBytes(length));
	}

	public uint ReadBits(int bitCount)
	{
		if (bitCount < 0 || bitCount > 32)
		{
			throw new ArgumentOutOfRangeException(nameof(bitCount));
		}

		uint value = 0;
		for (int i = 0; i < bitCount; i++)
		{
			if (_byteOffset >= _buffer.Length)
			{
				throw new InvalidOperationException("Unexpected end of bitstream.");
			}

			int bit = (_buffer[_byteOffset] >> (7 - _bitOffset)) & 1;
			value = (value << 1) | (uint)bit;
			_bitOffset++;
			if (_bitOffset == 8)
			{
				_bitOffset = 0;
				_byteOffset++;
			}
		}

		return value;
	}

	private void AlignToByte()
	{
		if (_bitOffset == 0)
		{
			return;
		}

		_bitOffset = 0;
		_byteOffset++;
	}

	private void EnsureAvailable(int byteCount)
	{
		if (_byteOffset + byteCount > _buffer.Length)
		{
			throw new InvalidOperationException("Unexpected end of bitstream.");
		}
	}
}
