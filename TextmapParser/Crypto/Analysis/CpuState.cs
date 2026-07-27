using Iced.Intel;

namespace TextmapParser;

internal readonly struct MemKey : IEquatable<MemKey>
{
	public MemKey(int addressId)
	{
		AddressKey = addressId;
	}

	public int AddressKey { get; }

	public bool Equals(MemKey other) => AddressKey == other.AddressKey;
	public override bool Equals(object? obj) => obj is MemKey other && Equals(other);
	public override int GetHashCode() => AddressKey;
}

internal sealed class MemCell
{
	public MemCell(int bits, Expr value)
	{
		Bits = bits;
		Value = value;
	}

	public int Bits { get; }
	public Expr Value { get; }
}

internal sealed class CpuState
{
	private readonly ExprMaker _maker;
	private readonly Expr[] _regs = new Expr[16];
	private readonly Dictionary<MemKey, MemCell> _mem =
		new Dictionary<MemKey, MemCell>();

	public CpuState(ExprMaker maker)
	{
		_maker = maker;
		for (int i = 0; i < _regs.Length; i++)
			_regs[i] = maker.Bad("input_" + (Register.RAX + i), 64);
	}

	private CpuState(
		ExprMaker maker,
		Expr[] gpr,
		Dictionary<MemKey, MemCell> memory)
	{
		_maker = maker;
		_regs = gpr;
		_mem = memory;
	}

	public CpuState Clone() =>
		new CpuState(
			_maker,
			(Expr[])_regs.Clone(),
			new Dictionary<MemKey, MemCell>(_mem));

	public Expr GetReg64(int index) => _regs[index];
	public void SetReg64(int index, Expr value) => _regs[index] = _maker.Cast(value, 64);

	public IEnumerable<KeyValuePair<MemKey, MemCell>> Mem => _mem;

	public bool TryGetMem(MemKey key, out MemCell cell) =>
		_mem.TryGetValue(key, out cell!);

	public void SetMem(MemKey key, MemCell cell) => _mem[key] = cell;

	public Expr ReadReg(Register register)
	{
		if (!TryGetRegInfo(register, out int index, out int offset, out int bits))
			return _maker.Bad("read_" + register, Math.Max(8, register.GetSize() * 8));

		return _maker.Extract(_regs[index], offset, bits);
	}

	public void WriteReg(Register register, Expr value)
	{
		if (!TryGetRegInfo(register, out int index, out int offset, out int bits))
			return;

		value = _maker.Cast(value, bits);
		if (bits == 64 && offset == 0)
		{
			_regs[index] = value;
		}
		else if (bits == 32 && offset == 0)
		{
			_regs[index] = _maker.Cast(value, 64);
		}
		else
		{
			_regs[index] = _maker.Insert(_regs[index], value, offset, bits, 64);
		}
	}

	public Expr ReadMem(
		Expr addr,
		int bits,
		ulong ip,
		int order)
	{

		if (!ContainsPhi(addr))
		{
			var key = new MemKey(addr.Id);
			if (_mem.TryGetValue(key, out var cell))
			{
				if (cell.Bits == bits)
					return cell.Value;
				if (cell.Bits > bits)
					return _maker.Extract(cell.Value, 0, bits);
			}
		}

		return _maker.Load(ip, order, bits, addr);
	}

	public void WriteMem(Expr addr, int bits, Expr value)
	{

		if (ContainsPhi(addr))
			return;

		var key = new MemKey(addr.Id);
		_mem[key] = new MemCell(bits, _maker.Cast(value, bits));
	}

	private static bool ContainsPhi(Expr expr)
	{
		var pending = new Stack<Expr>();
		var visited = new HashSet<int>();
		pending.Push(expr);

		while (pending.Count != 0)
		{
			Expr current = pending.Pop();
			if (!visited.Add(current.Id))
				continue;

			switch (current)
			{
				case PhiExpr:
					return true;

				case UnaryExpr unary:
					pending.Push(unary.Value);
					break;

				case BinaryExpr binary:
					pending.Push(binary.Left);
					pending.Push(binary.Right);
					break;

				case SliceExpr extract:
					pending.Push(extract.Value);
					break;

				case MergeExpr insert:
					pending.Push(insert.Original);
					pending.Push(insert.Inserted);
					break;
			}
		}

		return false;
	}

	public bool SameRoots(CpuState other)
	{
		if (other == null)
			return false;

		for (int i = 0; i < _regs.Length; i++)
		{
			if (_regs[i].Id != other._regs[i].Id)
				return false;
		}

		if (_mem.Count != other._mem.Count)
			return false;

		foreach (KeyValuePair<MemKey, MemCell> pair in _mem)
		{
			if (!other._mem.TryGetValue(pair.Key, out MemCell? otherCell) ||
				pair.Value.Bits != otherCell.Bits ||
				pair.Value.Value.Id != otherCell.Value.Id)
			{
				return false;
			}
		}

		return true;
	}

	private static bool TryGetRegInfo(
		Register register,
		out int index,
		out int offset,
		out int bits)
	{
		Register full = register.GetFullRegister();
		if (full < Register.RAX || full > Register.R15)
		{
			index = offset = bits = 0;
			return false;
		}

		index = full - Register.RAX;
		bits = register.GetSize() * 8;
		offset = register >= Register.AH && register <= Register.BH ? 8 : 0;
		return bits != 0;
	}
}
