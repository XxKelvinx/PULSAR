using System;
using System.Collections.Generic;

public sealed class PulsarRangeEncoder
{
	private const int UIntRangeBits = 8;
	private const int BitRes = 3;
	private const ulong TopValue = 0xFFFFFFFFUL;
	private const ulong Half = 0x80000000UL;
	private const ulong FirstQuarter = 0x40000000UL;
	private const ulong ThirdQuarter = 0xC0000000UL;
	private static readonly ushort[] TellCorrection =
	[
		35733, 38967, 42495, 46340, 50535, 55109, 60097, 65535
	];

	private readonly List<byte> _bytes = new();
	private readonly List<byte> _tailBytes = new();
	private ulong _low;
	private ulong _high = TopValue;
	private int _pendingBits;
	private byte _bitAccumulator;
	private int _bitCount;
	private ulong _tailWindow;
	private int _tailBitCount;
	private bool _finished;
	private int _nbitsTotal;

	public int EstimatedRangeBytes => _bytes.Count;
	public int EstimatedTailBytes => _tailBytes.Count + ((_tailBitCount + 7) / 8);
	public int EstimatedTotalBytes => EstimatedRangeBytes + EstimatedTailBytes + (_bitCount > 0 ? 1 : 0);
	public int TellBits => _nbitsTotal - ILog((uint)Math.Max(1UL, _high - _low + 1UL));
	public uint TellFractionalBits => ComputeTellFractionalBits(_nbitsTotal, (uint)Math.Max(1UL, _high - _low + 1UL));

	public void EncodeSymbol(uint lowCount, uint highCount, uint total)
	{
		EnsureNotFinished();
		if (total == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(total));
		}

		if (lowCount >= highCount || highCount > total)
		{
			throw new ArgumentOutOfRangeException(nameof(highCount), "Counts must satisfy low < high <= total.");
		}

		ulong range = (_high - _low) + 1UL;
		_high = _low + ((range * highCount) / total) - 1UL;
		_low += (range * lowCount) / total;

		Normalize();
	}

	public void EncodeUInt(uint value, uint total)
	{
		EnsureNotFinished();
		if (value >= total)
		{
			throw new ArgumentOutOfRangeException(nameof(value));
		}

		if (total <= 1)
		{
			return;
		}

		uint totalMinusOne = total - 1;
		int totalBits = ILog(totalMinusOne);
		if (totalBits > UIntRangeBits)
		{
			int rawBitCount = totalBits - UIntRangeBits;
			uint rangeTotal = (totalMinusOne >> rawBitCount) + 1u;
			uint rangeValue = value >> rawBitCount;
			EncodeSymbol(rangeValue, rangeValue + 1u, rangeTotal);
			EncodeBits(value & ((1u << rawBitCount) - 1u), rawBitCount);
			return;
		}

		EncodeSymbol(value, value + 1u, total);
	}

	public void EncodeBinary(uint lowCount, uint highCount, int bitCount)
	{
		EnsureNotFinished();
		if (bitCount < 0 || bitCount > 30)
		{
			throw new ArgumentOutOfRangeException(nameof(bitCount));
		}

		uint total = 1u << bitCount;
		EncodeSymbol(lowCount, highCount, total);
	}

	public void EncodeBinaryValue(uint value, int bitCount)
	{
		if (bitCount < 0 || bitCount > 30)
		{
			throw new ArgumentOutOfRangeException(nameof(bitCount));
		}

		uint total = 1u << bitCount;
		if (value >= total)
		{
			throw new ArgumentOutOfRangeException(nameof(value));
		}

		EncodeBinary(value, value + 1u, bitCount);
	}

	public void EncodeBit(bool value, uint falseFrequency = 1, uint trueFrequency = 1)
	{
		EnsureNotFinished();
		uint total = checked(falseFrequency + trueFrequency);
		if (value)
		{
			EncodeSymbol(falseFrequency, total, total);
		}
		else
		{
			EncodeSymbol(0, falseFrequency, total);
		}
	}

	public void EncodeBitLogp(bool value, int logp)
	{
		EnsureNotFinished();
		if (logp < 0 || logp > 30)
		{
			throw new ArgumentOutOfRangeException(nameof(logp));
		}

		uint total = 1u << logp;
		uint trueFrequency = 1u;
		uint falseFrequency = total - trueFrequency;
		EncodeBit(value, falseFrequency, trueFrequency);
	}

	public void EncodeBits(uint value, int bitCount)
	{
		EnsureNotFinished();
		if (bitCount < 0 || bitCount > 32)
		{
			throw new ArgumentOutOfRangeException(nameof(bitCount));
		}

		if (bitCount < 32 && value >= (1u << bitCount))
		{
			throw new ArgumentOutOfRangeException(nameof(value), "Value does not fit in the requested bit count.");
		}

		if (bitCount == 0)
		{
			return;
		}

		while (_tailBitCount + bitCount > 56)
		{
			_tailBytes.Add((byte)(_tailWindow & 0xFF));
			_tailWindow >>= 8;
			_tailBitCount -= 8;
		}

		_tailWindow |= (ulong)value << _tailBitCount;
		_tailBitCount += bitCount;
		_nbitsTotal += bitCount;
	}

	public void EncodeIcdfSymbol(int symbol, IReadOnlyList<byte> icdf, int ftb)
	{
		EnsureNotFinished();
		ArgumentNullException.ThrowIfNull(icdf);
		if (symbol < 0 || symbol >= icdf.Count)
		{
			throw new ArgumentOutOfRangeException(nameof(symbol));
		}

		ValidateIcdf(icdf);
		uint total = 1u << ftb;
		uint lowCount = icdf[symbol];
		uint highCount = symbol > 0 ? icdf[symbol - 1] : total;
		EncodeSymbol(lowCount, highCount, total);
	}

	public void EncodeIcdf16Symbol(int symbol, IReadOnlyList<ushort> icdf, int ftb)
	{
		EnsureNotFinished();
		ArgumentNullException.ThrowIfNull(icdf);
		if (symbol < 0 || symbol >= icdf.Count)
		{
			throw new ArgumentOutOfRangeException(nameof(symbol));
		}

		ValidateIcdf(icdf);
		uint total = 1u << ftb;
		uint lowCount = icdf[symbol];
		uint highCount = symbol > 0 ? icdf[symbol - 1] : total;
		EncodeSymbol(lowCount, highCount, total);
	}

	// Opus CELT laplace.c port. Encodes a signed integer under a Laplace-like
	// distribution parameterised by fs (Q15 probability of 0) and decay (Q15
	// per-step decay of the two-sided tail). Symmetric-symbol update runs in
	// the 15-bit binary domain (total = 32768). Returns possibly-clamped value
	// via out param (Opus mutates *value in-place when tail saturates).
	public int LaplaceEncode(int value, uint fs, int decay)
	{
		const int LaplaceMinP = 1;
		uint fl = 0;
		int val = value;
		if (val != 0)
		{
			int s = val < 0 ? -1 : 0;
			val = (val + s) ^ s;
			fl = fs;
			fs = LaplaceGetFreq1(fs, decay);
			int i;
			for (i = 1; fs > 0 && i < val; i++)
			{
				fs *= 2;
				fl += fs + 2u * LaplaceMinP;
				fs = (uint)((fs * (long)decay) >> 15);
			}
			if (fs == 0)
			{
				int ndiMax = (int)((32768u - fl + LaplaceMinP - 1u) >> 0);
				ndiMax = (ndiMax - s) >> 1;
				int di = Math.Min(val - i, ndiMax - 1);
				fl += (uint)((2 * di + 1 + s) * LaplaceMinP);
				fs = Math.Min((uint)LaplaceMinP, 32768u - fl);
				value = (i + di + s) ^ s;
			}
			else
			{
				fs += LaplaceMinP;
				fl += fs & (uint)~s;
			}
		}
		EncodeSymbol(fl, fl + fs, 32768);
		return value;
	}

	private static uint LaplaceGetFreq1(uint fs0, int decay)
	{
		const int LaplaceMinP = 1;
		const int LaplaceNMin = 16;
		uint ft = 32768u - (uint)(LaplaceMinP * 2 * LaplaceNMin) - fs0;
		return (uint)((ft * (long)(16384 - decay)) >> 15);
	}

	public byte[] Finish()
	{
		EnsureNotFinished();
		_finished = true;
		_pendingBits++;
		if (_low < FirstQuarter)
		{
			WriteBitPlusPending(false);
		}
		else
		{
			WriteBitPlusPending(true);
		}

		FlushPartialByte();
		FlushTailBytes();

		byte[] output = new byte[_bytes.Count + _tailBytes.Count];
		for (int i = 0; i < _bytes.Count; i++)
		{
			output[i] = _bytes[i];
		}

		for (int i = 0; i < _tailBytes.Count; i++)
		{
			output[_bytes.Count + i] = _tailBytes[_tailBytes.Count - 1 - i];
		}

		return output;
	}

	public void Reset()
	{
		_bytes.Clear();
		_tailBytes.Clear();
		_low = 0;
		_high = TopValue;
		_pendingBits = 0;
		_bitAccumulator = 0;
		_bitCount = 0;
		_tailWindow = 0;
		_tailBitCount = 0;
		_finished = false;
		_nbitsTotal = 0;
	}

	private void Normalize()
	{
		while (true)
		{
			if (_high < Half)
			{
				WriteBitPlusPending(false);
			}
			else if (_low >= Half)
			{
				WriteBitPlusPending(true);
				_low -= Half;
				_high -= Half;
			}
			else if (_low >= FirstQuarter && _high < ThirdQuarter)
			{
				_pendingBits++;
				_low -= FirstQuarter;
				_high -= FirstQuarter;
			}
			else
			{
				break;
			}

			_low <<= 1;
			_high = (_high << 1) | 1UL;
			_nbitsTotal++;
		}
	}

	private void WriteBitPlusPending(bool bit)
	{
		WriteBit(bit);
		while (_pendingBits > 0)
		{
			WriteBit(!bit);
			_pendingBits--;
		}
	}

	private void WriteBit(bool bit)
	{
		_bitAccumulator = (byte)((_bitAccumulator << 1) | (bit ? 1 : 0));
		_bitCount++;
		if (_bitCount == 8)
		{
			_bytes.Add(_bitAccumulator);
			_bitAccumulator = 0;
			_bitCount = 0;
		}
	}

	private void FlushPartialByte()
	{
		if (_bitCount == 0)
		{
			return;
		}

		_bitAccumulator = (byte)(_bitAccumulator << (8 - _bitCount));
		_bytes.Add(_bitAccumulator);
		_bitAccumulator = 0;
		_bitCount = 0;
	}

	private void FlushTailBytes()
	{
		while (_tailBitCount >= 8)
		{
			_tailBytes.Add((byte)(_tailWindow & 0xFF));
			_tailWindow >>= 8;
			_tailBitCount -= 8;
		}

		if (_tailBitCount > 0)
		{
			_tailBytes.Add((byte)(_tailWindow & ((1UL << _tailBitCount) - 1UL)));
			_tailWindow = 0;
			_tailBitCount = 0;
		}
	}

	private static int ILog(uint value)
	{
		int bits = 0;
		while (value > 0)
		{
			value >>= 1;
			bits++;
		}

		return bits;
	}

	private static uint ComputeTellFractionalBits(int totalBits, uint rangeWidth)
	{
		uint nbits = (uint)Math.Max(0, totalBits) << BitRes;
		int l = ILog(Math.Max(1u, rangeWidth));
		uint r = l > 16 ? rangeWidth >> (l - 16) : rangeWidth << (16 - l);
		int bucket = Math.Clamp((int)(r >> 12) - 8, 0, TellCorrection.Length - 1);
		bucket += r > TellCorrection[bucket] ? 1 : 0;
		uint used = (uint)((l << BitRes) + bucket);
		return nbits >= used ? nbits - used : 0u;
	}

	public static void PatchInitialBits(byte[] payload, uint value, int bitCount)
	{
		ArgumentNullException.ThrowIfNull(payload);
		if (bitCount < 0 || bitCount > 8)
		{
			throw new ArgumentOutOfRangeException(nameof(bitCount));
		}

		if (bitCount == 0)
		{
			return;
		}

		if (payload.Length == 0)
		{
			throw new InvalidOperationException("Cannot patch bits in an empty payload.");
		}

		uint maxValue = (1u << bitCount) - 1u;
		if (value > maxValue)
		{
			throw new ArgumentOutOfRangeException(nameof(value));
		}

		int shift = 8 - bitCount;
		byte mask = (byte)(maxValue << shift);
		payload[0] = (byte)((payload[0] & ~mask) | ((byte)value << shift));
	}

	public static byte[] Shrink(byte[] payload, int size)
	{
		ArgumentNullException.ThrowIfNull(payload);
		if (size < 0 || size > payload.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(size));
		}

		if (size == payload.Length)
		{
			return payload;
		}

		byte[] output = new byte[size];
		Buffer.BlockCopy(payload, 0, output, 0, size);
		return output;
	}

	private void EnsureNotFinished()
	{
		if (_finished)
		{
			throw new InvalidOperationException("Range encoder is already finished. Call Reset() before reusing it.");
		}
	}

	private static void ValidateIcdf(IReadOnlyList<byte> icdf)
	{
		byte previous = byte.MaxValue;
		for (int i = 0; i < icdf.Count; i++)
		{
			byte value = icdf[i];
			if (value > previous)
			{
				throw new ArgumentException("ICDF must be monotonically non-increasing.", nameof(icdf));
			}

			previous = value;
		}

		if (icdf.Count > 0 && icdf[^1] != 0)
		{
			throw new ArgumentException("ICDF last entry must be 0.", nameof(icdf));
		}
	}

	private static void ValidateIcdf(IReadOnlyList<ushort> icdf)
	{
		ushort previous = ushort.MaxValue;
		for (int i = 0; i < icdf.Count; i++)
		{
			ushort value = icdf[i];
			if (value > previous)
			{
				throw new ArgumentException("ICDF must be monotonically non-increasing.", nameof(icdf));
			}

			previous = value;
		}

		if (icdf.Count > 0 && icdf[^1] != 0)
		{
			throw new ArgumentException("ICDF last entry must be 0.", nameof(icdf));
		}
	}
}

public sealed class PulsarRangeDecoder
{
	private const int UIntRangeBits = 8;
	private const int BitRes = 3;
	private const ulong TopValue = 0xFFFFFFFFUL;
	private const ulong Half = 0x80000000UL;
	private const ulong FirstQuarter = 0x40000000UL;
	private const ulong ThirdQuarter = 0xC0000000UL;
	private static readonly ushort[] TellCorrection =
	[
		35733, 38967, 42495, 46340, 50535, 55109, 60097, 65535
	];

	private readonly byte[] _buffer;
	private int _byteOffset;
	private int _bitOffset;
	private int _tailOffset;
	private ulong _tailWindow;
	private int _tailBitCount;
	private ulong _low;
	private ulong _high = TopValue;
	private ulong _code;
	private int _nbitsTotal;

	public int ConsumedRangeBytes => _byteOffset;
	public int ConsumedTailBytes => _tailOffset;
	public int TellBits => _nbitsTotal - ILog((uint)Math.Max(1UL, _high - _low + 1UL));
	public uint TellFractionalBits => ComputeTellFractionalBits(_nbitsTotal, (uint)Math.Max(1UL, _high - _low + 1UL));

	public PulsarRangeDecoder(byte[] buffer)
	{
		_buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
		for (int i = 0; i < 32; i++)
		{
			_code = (_code << 1) | (ReadRawBit() ? 1UL : 0UL);
		}
	}

	public uint GetTarget(uint total)
	{
		if (total == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(total));
		}

		ulong range = (_high - _low) + 1UL;
		return (uint)(((((_code - _low) + 1UL) * total) - 1UL) / range);
	}

	public void Update(uint lowCount, uint highCount, uint total)
	{
		if (total == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(total));
		}

		if (lowCount >= highCount || highCount > total)
		{
			throw new ArgumentOutOfRangeException(nameof(highCount), "Counts must satisfy low < high <= total.");
		}

		ulong range = (_high - _low) + 1UL;
		_high = _low + ((range * highCount) / total) - 1UL;
		_low += (range * lowCount) / total;

		Normalize();
	}

	public uint DecodeUInt(uint total)
	{
		if (total <= 1)
		{
			return 0;
		}

		uint totalMinusOne = total - 1;
		int totalBits = ILog(totalMinusOne);
		if (totalBits > UIntRangeBits)
		{
			int rawBitCount = totalBits - UIntRangeBits;
			uint rangeTotal = (totalMinusOne >> rawBitCount) + 1u;
			uint rangeValue = GetTarget(rangeTotal);
			Update(rangeValue, rangeValue + 1u, rangeTotal);
			uint tail = DecodeBits(rawBitCount);
			uint decodedValue = (rangeValue << rawBitCount) | tail;
			if (decodedValue > totalMinusOne)
			{
				throw new InvalidOperationException("Decoded uint exceeds declared total.");
			}

			return decodedValue;
		}

		uint decodedUniform = GetTarget(total);
		Update(decodedUniform, decodedUniform + 1u, total);
		return decodedUniform;
	}

	public uint DecodeBinary(int bitCount)
	{
		if (bitCount < 0 || bitCount > 30)
		{
			throw new ArgumentOutOfRangeException(nameof(bitCount));
		}

		uint total = 1u << bitCount;
		uint value = GetTarget(total);
		Update(value, value + 1u, total);
		return value;
	}

	public bool DecodeBit(uint falseFrequency = 1, uint trueFrequency = 1)
	{
		uint total = checked(falseFrequency + trueFrequency);
		uint target = GetTarget(total);
		bool value = target >= falseFrequency;
		if (value)
		{
			Update(falseFrequency, total, total);
		}
		else
		{
			Update(0, falseFrequency, total);
		}

		return value;
	}

	public bool DecodeBitLogp(int logp)
	{
		if (logp < 0 || logp > 30)
		{
			throw new ArgumentOutOfRangeException(nameof(logp));
		}

		uint total = 1u << logp;
		uint trueFrequency = 1u;
		uint falseFrequency = total - trueFrequency;
		return DecodeBit(falseFrequency, trueFrequency);
	}

	public uint DecodeBits(int bitCount)
	{
		if (bitCount < 0 || bitCount > 32)
		{
			throw new ArgumentOutOfRangeException(nameof(bitCount));
		}

		if (bitCount == 0)
		{
			return 0;
		}

		while (_tailBitCount < bitCount)
		{
			_tailWindow |= (ulong)ReadRawByteFromEnd() << _tailBitCount;
			_tailBitCount += 8;
		}

		uint value = (uint)(_tailWindow & ((1UL << bitCount) - 1UL));
		_tailWindow >>= bitCount;
		_tailBitCount -= bitCount;
		_nbitsTotal += bitCount;
		return value;
	}

	public int DecodeIcdfSymbol(IReadOnlyList<byte> icdf, int ftb)
	{
		ArgumentNullException.ThrowIfNull(icdf);
		ValidateIcdf(icdf);
		uint total = 1u << ftb;
		uint target = GetTarget(total);

		for (int symbol = 0; symbol < icdf.Count; symbol++)
		{
			uint lowCount = icdf[symbol];
			uint highCount = symbol > 0 ? icdf[symbol - 1] : total;
			if (target >= lowCount && target < highCount)
			{
				Update(lowCount, highCount, total);
				return symbol;
			}
		}

		throw new InvalidOperationException("Failed to decode ICDF symbol.");
	}

	public int DecodeIcdf16Symbol(IReadOnlyList<ushort> icdf, int ftb)
	{
		ArgumentNullException.ThrowIfNull(icdf);
		ValidateIcdf(icdf);
		uint total = 1u << ftb;
		uint target = GetTarget(total);

		for (int symbol = 0; symbol < icdf.Count; symbol++)
		{
			uint lowCount = icdf[symbol];
			uint highCount = symbol > 0 ? icdf[symbol - 1] : total;
			if (target >= lowCount && target < highCount)
			{
				Update(lowCount, highCount, total);
				return symbol;
			}
		}

		throw new InvalidOperationException("Failed to decode 16-bit ICDF symbol.");
	}

	public int LaplaceDecode(uint fs, int decay)
	{
		const int LaplaceMinP = 1;
		int val = 0;
		uint fm = GetTarget(32768);
		uint fl = 0;
		if (fm >= fs)
		{
			val++;
			fl = fs;
			fs = LaplaceGetFreq1Decode(fs, decay) + LaplaceMinP;
			while (fs > LaplaceMinP && fm >= fl + 2 * fs)
			{
				fs *= 2;
				fl += fs;
				fs = (uint)(((fs - 2u * LaplaceMinP) * (long)decay) >> 15);
				fs += LaplaceMinP;
				val++;
			}
			if (fs <= LaplaceMinP)
			{
				int di = (int)((fm - fl) >> 1);
				val += di;
				fl += (uint)(2 * di * LaplaceMinP);
			}
			if (fm < fl + fs)
			{
				val = -val;
			}
			else
			{
				fl += fs;
			}
		}
		uint high = Math.Min(fl + fs, 32768u);
		Update(fl, high, 32768);
		return val;
	}

	private static uint LaplaceGetFreq1Decode(uint fs0, int decay)
	{
		const int LaplaceMinP = 1;
		const int LaplaceNMin = 16;
		uint ft = 32768u - (uint)(LaplaceMinP * 2 * LaplaceNMin) - fs0;
		return (uint)((ft * (long)(16384 - decay)) >> 15);
	}

	private void Normalize()
	{
		while (true)
		{
			if (_high < Half)
			{
			}
			else if (_low >= Half)
			{
				_low -= Half;
				_high -= Half;
				_code -= Half;
			}
			else if (_low >= FirstQuarter && _high < ThirdQuarter)
			{
				_low -= FirstQuarter;
				_high -= FirstQuarter;
				_code -= FirstQuarter;
			}
			else
			{
				break;
			}

			_low <<= 1;
			_high = (_high << 1) | 1UL;
			_code = (_code << 1) | (ReadRawBit() ? 1UL : 0UL);
			_nbitsTotal++;
		}
	}

	private bool ReadRawBit()
	{
		if (_byteOffset >= _buffer.Length)
		{
			return false;
		}

		bool bit = ((_buffer[_byteOffset] >> (7 - _bitOffset)) & 1) != 0;
		_bitOffset++;
		if (_bitOffset == 8)
		{
			_bitOffset = 0;
			_byteOffset++;
		}

		return bit;
	}

	private byte ReadRawByteFromEnd()
	{
		int index = _buffer.Length - 1 - _tailOffset;
		if (index < 0)
		{
			return 0;
		}

		_tailOffset++;
		return _buffer[index];
	}

	private static int ILog(uint value)
	{
		int bits = 0;
		while (value > 0)
		{
			value >>= 1;
			bits++;
		}

		return bits;
	}

	private static uint ComputeTellFractionalBits(int totalBits, uint rangeWidth)
	{
		uint nbits = (uint)Math.Max(0, totalBits) << BitRes;
		int l = ILog(Math.Max(1u, rangeWidth));
		uint r = l > 16 ? rangeWidth >> (l - 16) : rangeWidth << (16 - l);
		int bucket = Math.Clamp((int)(r >> 12) - 8, 0, TellCorrection.Length - 1);
		bucket += r > TellCorrection[bucket] ? 1 : 0;
		uint used = (uint)((l << BitRes) + bucket);
		return nbits >= used ? nbits - used : 0u;
	}

	private static void ValidateIcdf(IReadOnlyList<byte> icdf)
	{
		byte previous = byte.MaxValue;
		for (int i = 0; i < icdf.Count; i++)
		{
			byte value = icdf[i];
			if (value > previous)
			{
				throw new ArgumentException("ICDF must be monotonically non-increasing.", nameof(icdf));
			}

			previous = value;
		}

		if (icdf.Count > 0 && icdf[^1] != 0)
		{
			throw new ArgumentException("ICDF last entry must be 0.", nameof(icdf));
		}
	}

	private static void ValidateIcdf(IReadOnlyList<ushort> icdf)
	{
		ushort previous = ushort.MaxValue;
		for (int i = 0; i < icdf.Count; i++)
		{
			ushort value = icdf[i];
			if (value > previous)
			{
				throw new ArgumentException("ICDF must be monotonically non-increasing.", nameof(icdf));
			}

			previous = value;
		}

		if (icdf.Count > 0 && icdf[^1] != 0)
		{
			throw new ArgumentException("ICDF last entry must be 0.", nameof(icdf));
		}
	}
}
