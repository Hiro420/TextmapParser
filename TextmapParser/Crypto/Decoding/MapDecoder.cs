using System.Buffers.Binary;
using System.Text;

namespace TextmapParser;

public sealed class MapDecoder : IMapDecoder
{
	private static readonly UTF8Encoding Utf8 = new UTF8Encoding(
		encoderShouldEmitUTF8Identifier: false,
		throwOnInvalidBytes: false);

	public bool Read(
		ByteCursor input,
		IDictionary<uint, string> map,
		DecodePlan plan)
	{
		if (input == null)
			throw new ArgumentNullException(nameof(input));
		if (map == null)
			throw new ArgumentNullException(nameof(map));
		if (plan == null)
			throw new ArgumentNullException(nameof(plan));

		byte[] data = input.Data ?? throw new NullReferenceException(nameof(input.Data));
		var bag = new EvalBag();

		uint rawCount = ReadU32(input, data);
		bag[DataSlot.MainCountRaw] = rawCount;
		uint count = checked((uint)plan.MainCount.Get(bag));

		int bytesLeft = data.Length - input.Pos;
		if (count > (uint)(bytesLeft / 6))
		{
			throw new InvalidDataException(
				$"Recovered primary count {count} cannot fit in the remaining {bytesLeft} data.");
		}

		for (uint i = 0; i < count; i++)
		{
			bag[DataSlot.MainIndex] = i;

			uint rawKey = ReadU32(input, data);
			bag[DataSlot.MainKeyRaw] = rawKey;
			uint key = unchecked((uint)plan.MainKey.Get(bag));

			ushort rawSize = ReadU16(input, data);
			bag[DataSlot.TextSizeRaw] = rawSize;
			ushort size = unchecked((ushort)plan.TextSize.Get(bag));

			string value;
			if (size == 0)
			{
				value = string.Empty;
			}
			else
			{
				Need(input, data, size);
				byte[] plain = ReadText(data, input.Pos, size, plan.DataBlock, bag);
				input.Pos = checked(input.Pos + size);
				value = Utf8.GetString(plain, 0, size);
			}

			map.Add(key, value);
		}

		if (plan.HasCopies)
		{
			uint rawCopyCount = ReadU32(input, data);
			bag[DataSlot.CopyCountRaw] = rawCopyCount;
			uint copyCount = checked((uint)plan.CopyCount!.Get(bag));

			int copyBytesLeft = data.Length - input.Pos;
			if (copyCount > (uint)(copyBytesLeft / 8))
			{
				throw new InvalidDataException(
					$"Recovered alias count {copyCount} cannot fit in the remaining {copyBytesLeft} data.");
			}

			for (uint i = 0; i < copyCount; i++)
			{
				bag[DataSlot.CopyIndex] = i;

				uint raw0 = ReadU32(input, data);
				uint raw1 = ReadU32(input, data);
				bag[DataSlot.CopyLeftRaw] = raw0;
				bag[DataSlot.CopyRightRaw] = raw1;

				uint toKey = unchecked((uint)plan.CopyToKey!.Get(bag));
				uint fromKey = unchecked((uint)plan.CopyFromKey!.Get(bag));

				string value = map[fromKey];
				map.Add(toKey, value);
			}
		}

		return true;
	}

	private static byte[] ReadText(
		byte[] source,
		int offset,
		int size,
		Expr dataExpr,
		EvalBag bag)
	{
		byte[] output = new byte[size];
		int readAt = offset;
		int writeAt = 0;

		while (writeAt < size)
		{
			int take = Math.Min(8, size - writeAt);
			ulong raw = ReadU64Part(source, readAt, take);
			bag[DataSlot.DataBlockRaw] = raw;
			ulong plain = dataExpr.Get(bag);

			for (int i = 0; i < take; i++)
				output[writeAt + i] = (byte)(plain >> (i * 8));

			readAt += take;
			writeAt += take;
		}

		return output;
	}

	private static uint ReadU32(ByteCursor input, byte[] data)
	{
		Need(input, data, 4);
		uint value = BinaryPrimitives.ReadUInt32LittleEndian(
			data.AsSpan(input.Pos, 4));
		input.Pos = checked(input.Pos + 4);
		return value;
	}

	private static ushort ReadU16(ByteCursor input, byte[] data)
	{
		Need(input, data, 2);
		ushort value = BinaryPrimitives.ReadUInt16LittleEndian(
			data.AsSpan(input.Pos, 2));
		input.Pos = checked(input.Pos + 2);
		return value;
	}

	private static ulong ReadU64Part(
		byte[] source,
		int offset,
		int count)
	{
		ulong value = 0;
		for (int i = 0; i < count; i++)
			value |= (ulong)source[offset + i] << (8 * i);
		return value;
	}

	private static void Need(ByteCursor input, byte[] data, int count)
	{
		if (input.CheckBounds)
			input.Check(count);

		int offset = input.Pos;
		if (count < 0 ||
			offset < 0 ||
			offset > data.Length - count)
		{
			throw new EndOfStreamException();
		}
	}
}
