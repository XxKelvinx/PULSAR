using System;
using System.Collections.Generic;
using System.Text;

public sealed class PulsarBitstreamWriter
{
	private readonly List<byte> _buffer = new();
	private byte _bitAccumulator;
	private int _bitCount;

	public void WriteAscii(string value)
	{
		ArgumentNullException.ThrowIfNull(value);
		FlushBits();
		_buffer.AddRange(Encoding.ASCII.GetBytes(value));
	}

	public void WriteInt32(int value)
	{
		FlushBits();
		_buffer.AddRange(BitConverter.GetBytes(value));
	}

	public void WriteByte(byte value)
	{
		FlushBits();
		_buffer.Add(value);
	}

	public void WriteUInt16(ushort value)
	{
		FlushBits();
		_buffer.AddRange(BitConverter.GetBytes(value));
	}

	public void WriteUInt32(uint value)
	{
		FlushBits();
		_buffer.AddRange(BitConverter.GetBytes(value));
	}

	public void WriteUInt64(ulong value)
	{
		FlushBits();
		_buffer.AddRange(BitConverter.GetBytes(value));
	}

	public void WriteSingle(float value)
	{
		FlushBits();
		_buffer.AddRange(BitConverter.GetBytes(value));
	}

	public void WriteBytes(byte[] value)
	{
		ArgumentNullException.ThrowIfNull(value);
		FlushBits();
		_buffer.AddRange(value);
	}

	public void WriteBits(uint value, int bitCount)
	{
		if (bitCount < 0 || bitCount > 32)
		{
			throw new ArgumentOutOfRangeException(nameof(bitCount));
		}

		for (int bitIndex = bitCount - 1; bitIndex >= 0; bitIndex--)
		{
			int bit = (int)((value >> bitIndex) & 1u);
			_bitAccumulator = (byte)((_bitAccumulator << 1) | bit);
			_bitCount++;

			if (_bitCount == 8)
			{
				_buffer.Add(_bitAccumulator);
				_bitAccumulator = 0;
				_bitCount = 0;
			}
		}
	}

	public byte[] ToArray()
	{
		FlushBits();
		return _buffer.ToArray();
	}

	private void FlushBits()
	{
		if (_bitCount == 0)
		{
			return;
		}

		_bitAccumulator = (byte)(_bitAccumulator << (8 - _bitCount));
		_buffer.Add(_bitAccumulator);
		_bitAccumulator = 0;
		_bitCount = 0;
	}
}
