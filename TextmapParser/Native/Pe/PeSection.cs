namespace TextmapParser;

public readonly record struct PeSection(
	string Name,
	uint VirtualSize,
	uint Rva,
	uint RawSize,
	uint RawOffset);
