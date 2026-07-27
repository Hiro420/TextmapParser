namespace TextmapParser;

internal sealed class ExprMaker
{
	private int _nextId = 1;

	private readonly Dictionary<(ulong Value, int Bits), ConstExpr> _consts =
		new Dictionary<(ulong, int), ConstExpr>();

	private readonly Dictionary<(DataSlot Slot, int Bits), SlotExpr> _slots =
		new Dictionary<(DataSlot, int), SlotExpr>();

	private readonly Dictionary<string, BadExpr> _bad =
		new Dictionary<string, BadExpr>(StringComparer.Ordinal);

	private readonly Dictionary<(UnaryOp Op, int Value, int Bits, int InputBits), UnaryExpr> _unaryCache =
		new Dictionary<(UnaryOp, int, int, int), UnaryExpr>();

	private readonly Dictionary<(BinaryOp Op, int Left, int Right, int Bits), BinaryExpr> _binaryCache =
		new Dictionary<(BinaryOp, int, int, int), BinaryExpr>();

	private readonly Dictionary<(int Value, int Offset, int Bits), SliceExpr> _sliceCache =
		new Dictionary<(int, int, int), SliceExpr>();

	private readonly Dictionary<(int Original, int Inserted, int Offset, int PutBits, int Bits), MergeExpr> _mergeCache =
		new Dictionary<(int, int, int, int, int), MergeExpr>();

	private readonly Dictionary<(ulong IP, int Order, int Bits, int Addr), LoadExpr> _loadCache =
		new Dictionary<(ulong, int, int, int), LoadExpr>();

	private readonly Dictionary<(int Index, ulong IP, int Bits), CallExpr> _callCache =
		new Dictionary<(int, ulong, int), CallExpr>();

	public ConstExpr Const(ulong value, int bits)
	{
		value = BitOps.Truncate(value, bits);
		var key = (value, bits);
		if (!_consts.TryGetValue(key, out var expr))
		{
			expr = new ConstExpr(_nextId++, value, bits);
			_consts.Add(key, expr);
		}
		return expr;
	}

	public SlotExpr Slot(DataSlot slot, int bits)
	{
		var key = (slot, bits);
		if (!_slots.TryGetValue(key, out var expr))
		{
			expr = new SlotExpr(_nextId++, slot, bits);
			_slots.Add(key, expr);
		}
		return expr;
	}

	public BadExpr Bad(string why, int bits)
	{
		string key = bits + ":" + why;
		if (!_bad.TryGetValue(key, out var expr))
		{
			expr = new BadExpr(_nextId++, why, bits);
			_bad.Add(key, expr);
		}
		return expr;
	}

	public LoadExpr Load(ulong ip, int order, int bits, Expr addr)
	{
		var key = (ip, order, bits, addr.Id);
		if (!_loadCache.TryGetValue(key, out var expr))
		{
			expr = new LoadExpr(_nextId++, ip, order, bits, addr);
			_loadCache.Add(key, expr);
		}
		return expr;
	}

	public CallExpr Call(int index, ulong ip, int bits)
	{
		var key = (index, ip, bits);
		if (!_callCache.TryGetValue(key, out var expr))
		{
			expr = new CallExpr(_nextId++, index, ip, bits);
			_callCache.Add(key, expr);
		}
		return expr;
	}

	public Expr Cast(Expr value, int bits, bool signExtend = false)
	{
		if (value.Bits == bits)
			return value;
		if (bits < value.Bits)
			return Extract(value, 0, bits);

		var op = signExtend ? UnaryOp.SignExtend : UnaryOp.ZeroExtend;
		var key = (op, value.Id, bits, value.Bits);
		if (!_unaryCache.TryGetValue(key, out var expr))
		{
			expr = new UnaryExpr(_nextId++, op, value, bits, value.Bits);
			_unaryCache.Add(key, expr);
		}
		return expr;
	}

	public Expr Unary(UnaryOp op, Expr value, int bits)
	{
		value = Cast(value, bits);
		if (value is ConstExpr constant)
		{
			ulong result;
			switch (op)
			{
				case UnaryOp.Not:
					result = ~constant.Value;
					break;
				case UnaryOp.Neg:
					result = unchecked(0UL - constant.Value);
					break;
				case UnaryOp.ByteSwap:
					result = BitOps.ByteSwap(constant.Value, bits);
					break;
				default:
					result = constant.Value;
					break;
			}
			return Const(result, bits);
		}

		var key = (op, value.Id, bits, bits);
		if (!_unaryCache.TryGetValue(key, out var expr))
		{
			expr = new UnaryExpr(_nextId++, op, value, bits, bits);
			_unaryCache.Add(key, expr);
		}
		return expr;
	}

	public Expr Binary(
		BinaryOp op,
		Expr left,
		Expr right,
		int bits)
	{
		left = Cast(left, bits);
		right = Cast(right, bits);

		if (op == BinaryOp.Add ||
			op == BinaryOp.Xor ||
			op == BinaryOp.And ||
			op == BinaryOp.Or ||
			op == BinaryOp.Multiply)
		{
			if (left.Id > right.Id)
			{
				var temp = left;
				left = right;
				right = temp;
			}
		}

		if (left is ConstExpr lc && right is ConstExpr rc)
		{
			var temporary = new BinaryExpr(-1, op, lc, rc, bits);
			return Const(temporary.Get(new EvalBag()), bits);
		}

		if (right is ConstExpr zero && zero.Value == 0)
		{
			if (op == BinaryOp.Add ||
				op == BinaryOp.Subtract ||
				op == BinaryOp.Xor ||
				op == BinaryOp.Or ||
				op == BinaryOp.ShiftLeft ||
				op == BinaryOp.ShiftRight ||
				op == BinaryOp.ArithmeticShiftRight ||
				op == BinaryOp.RotateLeft ||
				op == BinaryOp.RotateRight)
			{
				return left;
			}

			if (op == BinaryOp.And || op == BinaryOp.Multiply)
				return Const(0, bits);
		}

		if (right is ConstExpr one && one.Value == 1 &&
			op == BinaryOp.Multiply)
		{
			return left;
		}

		if (left.Id == right.Id && op == BinaryOp.Xor)
			return Const(0, bits);

		var key = (op, left.Id, right.Id, bits);
		if (!_binaryCache.TryGetValue(key, out var expr))
		{
			expr = new BinaryExpr(_nextId++, op, left, right, bits);
			_binaryCache.Add(key, expr);
		}
		return expr;
	}

	public Expr Extract(Expr value, int shift, int bits)
	{
		if (shift == 0 && bits == value.Bits)
			return value;

		if (value is ConstExpr constant)
			return Const(constant.Value >> shift, bits);

		if (value is SliceExpr nested)
			return Extract(nested.Value, nested.Shift + shift, bits);

		if (value is MergeExpr inserted &&
			shift == inserted.Shift &&
			bits == inserted.PutBits)
		{
			return Cast(inserted.Inserted, bits);
		}

		var key = (value.Id, shift, bits);
		if (!_sliceCache.TryGetValue(key, out var expr))
		{
			expr = new SliceExpr(_nextId++, value, shift, bits);
			_sliceCache.Add(key, expr);
		}
		return expr;
	}

	public Expr Insert(
		Expr original,
		Expr inserted,
		int shift,
		int putBits,
		int bits)
	{
		original = Cast(original, bits);
		inserted = Cast(inserted, putBits);

		if (shift == 0 && putBits == bits)
			return Cast(inserted, bits);

		var key = (original.Id, inserted.Id, shift, putBits, bits);
		if (!_mergeCache.TryGetValue(key, out var expr))
		{
			expr = new MergeExpr(
				_nextId++,
				original,
				inserted,
				shift,
				putBits,
				bits);
			_mergeCache.Add(key, expr);
		}
		return expr;
	}

	public PhiExpr Phi(int bits, string name) =>
		new PhiExpr(_nextId++, bits, name);
}
