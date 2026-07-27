using Iced.Intel;

namespace TextmapParser;

public sealed class CodeReader
{
	public IReadOnlyList<Instruction> Read(NativeModule module, uint rva, bool trace = false)
	{
		MethodLoc spot = module.Find(rva);
		var input = new ByteArrayCodeReader(module.Data)
		{
			Position = checked((int)spot.FileOffset)
		};
		var decoder = Decoder.Create(IntPtr.Size * 8, input);
		decoder.IP = spot.Address;
		var code = new List<Instruction>();

		while (true)
		{
			Instruction ins = decoder.Decode();
			code.Add(ins);

			if (trace)
				Console.WriteLine($"{ins.IP:X} | {ins}");

			if (ins.Mnemonic == Mnemonic.Ret)
				return code;
		}
	}
}
