namespace TextmapParser;

public sealed class NativeModule
{
	private NativeModule(byte[] data, PeImage image)
	{
		Data = data;
		Image = image;
	}

	public byte[] Data { get; }
	public PeImage Image { get; }

	public static NativeModule Open(string path) =>
		new(File.ReadAllBytes(path), PeImage.Open(path));

	public MethodLoc Find(uint rva)
	{
		foreach (PeSection section in Image.Sections)
		{
			ulong start = section.Rva;
			ulong end = start + Math.Max(section.VirtualSize, section.RawSize);
			if (rva < start || rva >= end)
				continue;

			uint fileOffset = checked(section.RawOffset + (rva - section.Rva));
			return new MethodLoc(rva, fileOffset, Image.ImageBase + rva);
		}

		throw new InvalidDataException($"No PE section contains RVA 0x{rva:X}.");
	}
}
