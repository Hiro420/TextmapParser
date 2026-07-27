using Iced.Intel;

namespace TextmapParser;

public interface IPlanReader
{
	DecodePlan Read(IReadOnlyList<Instruction> code);
}
