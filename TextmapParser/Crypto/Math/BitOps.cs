namespace TextmapParser;

internal static class BitOps
{
	public static ulong Mask(int bits)
	{
		if (bits <= 0 || bits > 64)
			throw new ArgumentOutOfRangeException(nameof(bits));
		return bits == 64 ? ulong.MaxValue : (1UL << bits) - 1UL;
	}

	public static ulong Truncate(ulong value, int bits) => value & Mask(bits);

	public static ulong SignExtend(ulong value, int inputBits)
	{
		value &= Mask(inputBits);
		if (inputBits == 64)
			return value;

		ulong sign = 1UL << (inputBits - 1);
		return (value ^ sign) - sign;
	}

	public static ulong RotateLeft(ulong value, int count, int bits)
	{
		ulong mask = Mask(bits);
		value &= mask;
		count &= bits - 1;
		if (count == 0)
			return value;
		return ((value << count) | (value >> (bits - count))) & mask;
	}

	public static ulong RotateRight(ulong value, int count, int bits)
	{
		ulong mask = Mask(bits);
		value &= mask;
		count &= bits - 1;
		if (count == 0)
			return value;
		return ((value >> count) | (value << (bits - count))) & mask;
	}

	public static ulong ArithmeticShiftRight(ulong value, int count, int bits)
	{
		count &= bits - 1;
		if (count == 0)
			return value & Mask(bits);

		ulong signed = SignExtend(value, bits);
		return unchecked((ulong)((long)signed >> count)) & Mask(bits);
	}

	public static ulong ByteSwap(ulong value, int bits)
	{
		int bytes = bits / 8;
		ulong result = 0;
		for (int i = 0; i < bytes; i++)
		{
			result <<= 8;
			result |= value & 0xFF;
			value >>= 8;
		}
		return result & Mask(bits);
	}
}
