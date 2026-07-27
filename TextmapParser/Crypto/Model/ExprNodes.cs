using System.Text;

namespace TextmapParser;

internal sealed class ConstExpr : Expr
{
	public ConstExpr(int id, ulong value, int bits) : base(id, bits)
	{
		Value = BitOps.Truncate(value, bits);
	}

	public ulong Value { get; }

	internal override ulong GetCore(EvalBag bag) => Value;

	internal override void Write(StringBuilder text, HashSet<int> seen)
	{
		text.Append("0x");
		text.Append(Value.ToString("X"));
		text.Append(':');
		text.Append(Bits);
	}
}

internal sealed class SlotExpr : Expr
{
	public SlotExpr(int id, DataSlot slot, int bits) : base(id, bits)
	{
		Slot = slot;
	}

	public DataSlot Slot { get; }

	internal override ulong GetCore(EvalBag bag) => bag[Slot];

	internal override void Write(StringBuilder text, HashSet<int> seen)
	{
		text.Append(Slot);
		if (Bits != 64)
		{
			text.Append(':');
			text.Append(Bits);
		}
	}
}

internal sealed class BadExpr : Expr
{
	public BadExpr(int id, string why, int bits) : base(id, bits)
	{
		Why = why;
	}

	public string Why { get; }

	internal override ulong GetCore(EvalBag bag) =>
		throw new PlanException("Cannot evaluate unresolved expr: " + Why);

	internal override void Write(StringBuilder text, HashSet<int> seen)
	{
		text.Append("unknown(");
		text.Append(Why);
		text.Append(')');
	}
}

internal sealed class LoadExpr : Expr
{
	public LoadExpr(int id, ulong ip, int order, int bits, Expr addr)
		: base(id, bits)
	{
		IP = ip;
		Order = order;
		Addr = addr;
	}

	public ulong IP { get; }
	public int Order { get; }
	public Expr Addr { get; }

	internal override ulong GetCore(EvalBag bag) =>
		throw new PlanException($"Unbound native memory read at 0x{IP:X} ({Bits} bits)");

	internal override void Write(StringBuilder text, HashSet<int> seen)
	{
		text.Append("load");
		text.Append(Bits);
		text.Append("@0x");
		text.Append(IP.ToString("X"));
		text.Append('#');
		text.Append(Order);
	}
}

internal sealed class CallExpr : Expr
{
	public CallExpr(int id, int callIndex, ulong ip, int bits) : base(id, bits)
	{
		CallId = callIndex;
		IP = ip;
	}

	public int CallId { get; }
	public ulong IP { get; }

	internal override ulong GetCore(EvalBag bag) =>
		throw new PlanException($"Unbound native call result at 0x{IP:X}");

	internal override void Write(StringBuilder text, HashSet<int> seen)
	{
		text.Append("call_result@0x");
		text.Append(IP.ToString("X"));
	}
}

internal enum UnaryOp
{
	Not,
	Neg,
	ZeroExtend,
	SignExtend,
	ByteSwap,
}

internal sealed class UnaryExpr : Expr
{
	public UnaryExpr(
		int id,
		UnaryOp op,
		Expr value,
		int bits,
		int inputBits)
		: base(id, bits)
	{
		Op = op;
		Value = value;
		InputBits = inputBits;
	}

	public UnaryOp Op { get; }
	public Expr Value { get; }
	public int InputBits { get; }

	internal override ulong GetCore(EvalBag bag)
	{
		ulong value = Value.Get(bag);
		switch (Op)
		{
			case UnaryOp.Not:
				return ~value;
			case UnaryOp.Neg:
				return unchecked(0UL - value);
			case UnaryOp.ZeroExtend:
				return value & BitOps.Mask(InputBits);
			case UnaryOp.SignExtend:
				return BitOps.SignExtend(value, InputBits);
			case UnaryOp.ByteSwap:
				return BitOps.ByteSwap(value, Bits);
			default:
				throw new InvalidOperationException();
		}
	}

	internal override void Write(StringBuilder text, HashSet<int> seen)
	{
		if (!seen.Add(Id))
		{
			text.Append("<cycle>");
			return;
		}

		text.Append(Op);
		text.Append('(');
		Value.Write(text, seen);
		text.Append(')');
		seen.Remove(Id);
	}
}

internal enum BinaryOp
{
	Add,
	Subtract,
	Xor,
	And,
	Or,
	Multiply,
	ShiftLeft,
	ShiftRight,
	ArithmeticShiftRight,
	RotateLeft,
	RotateRight,
}

internal sealed class BinaryExpr : Expr
{
	public BinaryExpr(
		int id,
		BinaryOp op,
		Expr left,
		Expr right,
		int bits)
		: base(id, bits)
	{
		Op = op;
		Left = left;
		Right = right;
	}

	public BinaryOp Op { get; }
	public Expr Left { get; }
	public Expr Right { get; }

	internal override ulong GetCore(EvalBag bag)
	{
		ulong left = Left.Get(bag);
		ulong right = Right.Get(bag);
		int count = (int)right;

		switch (Op)
		{
			case BinaryOp.Add:
				return unchecked(left + right);
			case BinaryOp.Subtract:
				return unchecked(left - right);
			case BinaryOp.Xor:
				return left ^ right;
			case BinaryOp.And:
				return left & right;
			case BinaryOp.Or:
				return left | right;
			case BinaryOp.Multiply:
				return unchecked(left * right);
			case BinaryOp.ShiftLeft:
				return left << (count & (Bits - 1));
			case BinaryOp.ShiftRight:
				return left >> (count & (Bits - 1));
			case BinaryOp.ArithmeticShiftRight:
				return BitOps.ArithmeticShiftRight(left, count, Bits);
			case BinaryOp.RotateLeft:
				return BitOps.RotateLeft(left, count, Bits);
			case BinaryOp.RotateRight:
				return BitOps.RotateRight(left, count, Bits);
			default:
				throw new InvalidOperationException();
		}
	}

	internal override void Write(StringBuilder text, HashSet<int> seen)
	{
		if (!seen.Add(Id))
		{
			text.Append("<cycle>");
			return;
		}

		text.Append('(');
		Left.Write(text, seen);
		text.Append(' ');
		text.Append(Op);
		text.Append(' ');
		Right.Write(text, seen);
		text.Append(')');
		seen.Remove(Id);
	}
}

internal sealed class SliceExpr : Expr
{
	public SliceExpr(int id, Expr value, int shift, int bits)
		: base(id, bits)
	{
		Value = value;
		Shift = shift;
	}

	public Expr Value { get; }
	public int Shift { get; }

	internal override ulong GetCore(EvalBag bag) =>
		Value.Get(bag) >> Shift;

	internal override void Write(StringBuilder text, HashSet<int> seen)
	{
		if (!seen.Add(Id))
		{
			text.Append("<cycle>");
			return;
		}

		text.Append("extract(");
		Value.Write(text, seen);
		text.Append(',');
		text.Append(Shift);
		text.Append(',');
		text.Append(Bits);
		text.Append(')');
		seen.Remove(Id);
	}
}

internal sealed class MergeExpr : Expr
{
	public MergeExpr(
		int id,
		Expr original,
		Expr inserted,
		int shift,
		int putBits,
		int bits)
		: base(id, bits)
	{
		Original = original;
		Inserted = inserted;
		Shift = shift;
		PutBits = putBits;
	}

	public Expr Original { get; }
	public Expr Inserted { get; }
	public int Shift { get; }
	public int PutBits { get; }

	internal override ulong GetCore(EvalBag bag)
	{
		ulong mask = BitOps.Mask(PutBits) << Shift;
		ulong original = Original.Get(bag);
		ulong inserted = (Inserted.Get(bag) & BitOps.Mask(PutBits)) << Shift;
		return (original & ~mask) | inserted;
	}

	internal override void Write(StringBuilder text, HashSet<int> seen)
	{
		if (!seen.Add(Id))
		{
			text.Append("<cycle>");
			return;
		}

		text.Append("insert(");
		Original.Write(text, seen);
		text.Append(',');
		Inserted.Write(text, seen);
		text.Append(',');
		text.Append(Shift);
		text.Append(',');
		text.Append(PutBits);
		text.Append(')');
		seen.Remove(Id);
	}
}

internal sealed class PhiExpr : Expr
{
	private readonly List<Expr> _choices = new List<Expr>();
	private Dictionary<int, Expr>? _choicesBySource;

	public PhiExpr(int id, int bits, string name) : base(id, bits)
	{
		Name = name;
	}

	public string Name { get; }
	public IReadOnlyList<Expr> Choices => _choices;

	public bool AddChoice(Expr expr)
	{
		if (expr.Id == Id)
			return false;
		if (_choices.Any(x => x.Id == expr.Id))
			return false;
		_choices.Add(expr);
		return true;
	}

	public bool SetChoice(
		IEnumerable<KeyValuePair<int, Expr>> choice)
	{
		var next = new Dictionary<int, Expr>();
		foreach (KeyValuePair<int, Expr> pair in choice)
		{
			if (pair.Value.Id == Id)
				continue;
			next[pair.Key] = pair.Value;
		}

		bool changed = _choicesBySource == null ||
			_choicesBySource.Count != next.Count;

		if (!changed && _choicesBySource != null)
		{
			foreach (KeyValuePair<int, Expr> pair in next)
			{
				if (!_choicesBySource.TryGetValue(pair.Key, out Expr? old) ||
					old.Id != pair.Value.Id)
				{
					changed = true;
					break;
				}
			}
		}

		if (!changed)
			return false;

		_choicesBySource = next;
		_choices.Clear();

		foreach (Expr expr in next
			.OrderBy(x => x.Key)
			.Select(x => x.Value)
			.GroupBy(x => x.Id)
			.Select(x => x.First()))
		{
			_choices.Add(expr);
		}

		return true;
	}

	internal override ulong GetCore(EvalBag bag) =>
		throw new PlanException("Unresolved control-flow phi: " + Name);

	internal override void Write(StringBuilder text, HashSet<int> seen)
	{
		if (!seen.Add(Id))
		{
			text.Append(Name);
			return;
		}

		text.Append(Name);
		text.Append('{');
		for (int i = 0; i < _choices.Count; i++)
		{
			if (i != 0)
				text.Append(", ");
			_choices[i].Write(text, seen);
		}
		text.Append('}');
		seen.Remove(Id);
	}
}
