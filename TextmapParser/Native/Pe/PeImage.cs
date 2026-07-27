using System.Text;

namespace TextmapParser;

public sealed class PeImage
{
	private PeImage(ulong imageBase, IReadOnlyList<PeSection> sections)
	{
		ImageBase = imageBase;
		Sections = sections;
	}

	public ulong ImageBase { get; }
	public IReadOnlyList<PeSection> Sections { get; }

	public static PeImage Open(string path)
	{
		using var stream = File.OpenRead(path);
		using var input = new System.IO.BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

		if (input.ReadUInt16() != 0x5A4D)
			throw new InvalidDataException($"'{path}' is not a PE file.");

		stream.Position = 0x3C;
		uint peOffset = input.ReadUInt32();
		stream.Position = peOffset;

		if (input.ReadUInt32() != 0x00004550)
			throw new InvalidDataException($"'{path}' has an invalid PE signature.");

		input.ReadUInt16();
		ushort sectionCount = input.ReadUInt16();
		stream.Position += 12;
		ushort optionSize = input.ReadUInt16();
		input.ReadUInt16();

		long optionStart = stream.Position;
		ushort magic = input.ReadUInt16();
		ulong imageBase = magic switch
		{
			0x10B => ReadBase32(stream, input, optionStart),
			0x20B => ReadBase64(stream, input, optionStart),
			_ => throw new InvalidDataException($"'{path}' has an unsupported optional header.")
		};

		stream.Position = optionStart + optionSize;
		var sections = new List<PeSection>(sectionCount);

		for (int i = 0; i < sectionCount; i++)
		{
			string name = Encoding.ASCII.GetString(input.ReadBytes(8)).TrimEnd('\0');
			uint virtualSize = input.ReadUInt32();
			uint rva = input.ReadUInt32();
			uint rawSize = input.ReadUInt32();
			uint rawOffset = input.ReadUInt32();
			stream.Position += 12;
			input.ReadUInt32();
			sections.Add(new PeSection(name, virtualSize, rva, rawSize, rawOffset));
		}

		return new PeImage(imageBase, sections);
	}

	private static ulong ReadBase32(Stream stream, System.IO.BinaryReader input, long start)
	{
		stream.Position = start + 28;
		return input.ReadUInt32();
	}

	private static ulong ReadBase64(Stream stream, System.IO.BinaryReader input, long start)
	{
		stream.Position = start + 24;
		return input.ReadUInt64();
	}
}
