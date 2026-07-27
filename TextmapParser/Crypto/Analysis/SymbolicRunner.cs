using Iced.Intel;

namespace TextmapParser;

internal sealed class SymbolicRunner
{
	private const int RunLimit = 250_000;

	private readonly List<Instruction> _code;
	private readonly Dictionary<ulong, int> _indexByIp;
	private readonly List<int>[] _next;
	private readonly List<int>[] _prev;
	private readonly CpuState?[] _inputs;
	private readonly Dictionary<(int From, int To), CpuState> _edges =
		new Dictionary<(int, int), CpuState>();
	private readonly Queue<int> _work = new Queue<int>();
	private readonly bool[] _waiting;
	private readonly ExprMaker _maker = new ExprMaker();
	private readonly CpuState _start;

	private readonly Dictionary<(int To, int Location), PhiExpr> _joinPhis =
		new Dictionary<(int, int), PhiExpr>();

	private readonly Dictionary<(int Event, int Slot), PhiExpr> _eventPhis =
		new Dictionary<(int, int), PhiExpr>();

	private readonly Dictionary<int, CallHit> _callHits = new Dictionary<int, CallHit>();
	private readonly Dictionary<int, CompareHit> _compareHits = new Dictionary<int, CompareHit>();
	private readonly Dictionary<int, StoreHit> _storeHits = new Dictionary<int, StoreHit>();

	private bool _savingEvents;

	public SymbolicRunner(IReadOnlyList<Instruction> code)
	{
		if (code == null)
			throw new ArgumentNullException(nameof(code));
		if (code.Count == 0)
			throw new PlanException("The ins list is empty.");

		_code = code.OrderBy(x => x.IP).ToList();
		_indexByIp = new Dictionary<ulong, int>(_code.Count);
		for (int i = 0; i < _code.Count; i++)
			_indexByIp[_code[i].IP] = i;

		_next = BuildNext();
		_prev = BuildPrev(_next);
		_inputs = new CpuState?[_code.Count];
		_waiting = new bool[_code.Count];
		_start = new CpuState(_maker);
	}

	public RunResult Run()
	{
		_inputs[0] = _start.Clone();
		QueueUp(0);

		int processed = 0;
		var runs = new int[_code.Count];
		while (_work.Count != 0)
		{
			if (++processed > RunLimit)
			{
				int hotIndex = 0;
				for (int i = 1; i < runs.Length; i++)
				{
					if (runs[i] > runs[hotIndex])
						hotIndex = i;
				}

				int maxMem = _inputs
					.Where(x => x != null)
					.Select(x => x!.Mem.Count())
					.DefaultIfEmpty(0)
					.Max();

				throw new PlanException(
					"Symbolic analysis did not converge after " + processed +
					" ins executions. Hottest ins: 0x" +
					_code[hotIndex].IP.ToString("X") + " (" +
					runs[hotIndex] + " executions). Edge states: " +
					_edges.Count + ". Maximum retained memory facts: " +
					maxMem + ".");
			}

			int index = _work.Dequeue();
			runs[index]++;
			_waiting[index] = false;

			CpuState input = _inputs[index]!;
			CpuState output = input.Clone();
			Execute(index, output);

			foreach (int successor in _next[index])
			{
				if (successor < 0 || successor >= _code.Count)
					continue;

				PushEdge(index, successor, output);
			}
		}

		ReplayEvents();

		return new RunResult(
			_code,
			_callHits.Values.OrderBy(x => x.Index).ToList(),
			_compareHits.Values.OrderBy(x => x.Index).ToList(),
			_storeHits.Values.OrderBy(x => x.Index).ToList(),
			_maker);
	}

	private void ReplayEvents()
	{
		_eventPhis.Clear();
		_callHits.Clear();
		_compareHits.Clear();
		_storeHits.Clear();

		_savingEvents = true;
		try
		{
			for (int index = 0; index < _code.Count; index++)
			{
				CpuState? input = _inputs[index];
				if (input == null)
					continue;

				CpuState replay = input.Clone();
				Execute(index, replay);
			}
		}
		finally
		{
			_savingEvents = false;
		}
	}

	private List<int>[] BuildNext()
	{
		var result = new List<int>[_code.Count];

		for (int i = 0; i < _code.Count; i++)
		{
			var list = new List<int>(2);
			Instruction ins = _code[i];

			switch (ins.FlowControl)
			{
				case FlowControl.ConditionalBranch:
					if (_indexByIp.TryGetValue(ins.NearBranchTarget, out int branchTo))
						list.Add(branchTo);
					if (i + 1 < _code.Count)
						list.Add(i + 1);
					break;

				case FlowControl.UnconditionalBranch:
					if (ins.Op0Kind == OpKind.NearBranch16 ||
						ins.Op0Kind == OpKind.NearBranch32 ||
						ins.Op0Kind == OpKind.NearBranch64)
					{
						if (_indexByIp.TryGetValue(ins.NearBranchTarget, out int jumpTo))
							list.Add(jumpTo);
					}
					break;

				case FlowControl.IndirectBranch:
				case FlowControl.Return:
				case FlowControl.Interrupt:
				case FlowControl.Exception:
					break;

				default:
					if (i + 1 < _code.Count)
						list.Add(i + 1);
					break;
			}

			result[i] = list;
		}

		return result;
	}

	private static List<int>[] BuildPrev(List<int>[] successors)
	{
		var predecessors = new List<int>[successors.Length];
		for (int i = 0; i < predecessors.Length; i++)
			predecessors[i] = new List<int>();

		for (int source = 0; source < successors.Length; source++)
		{
			foreach (int target in successors[source])
			{
				if (target >= 0 && target < predecessors.Length &&
					!predecessors[target].Contains(source))
				{
					predecessors[target].Add(source);
				}
			}
		}

		return predecessors;
	}

	private void QueueUp(int index)
	{
		if (_waiting[index])
			return;
		_waiting[index] = true;
		_work.Enqueue(index);
	}

	private void PushEdge(
		int fromIndex,
		int toIndex,
		CpuState output)
	{
		var edge = (fromIndex, toIndex);
		if (_edges.TryGetValue(edge, out CpuState? previous) &&
			previous.SameRoots(output))
		{
			return;
		}

		_edges[edge] = output.Clone();
		RefreshInput(toIndex);
	}

	private void RefreshInput(int toIndex)
	{
		var inputs = new List<(int Source, CpuState State)>();

		if (toIndex == 0)
			inputs.Add((int.MinValue, _start));

		foreach (int from in _prev[toIndex])
		{
			if (_edges.TryGetValue((from, toIndex), out CpuState? cpu))
				inputs.Add((from, cpu));
		}

		if (inputs.Count == 0)
			return;

		CpuState recomputed = inputs.Count == 1
			? inputs[0].State.Clone()
			: MergeInputs(toIndex, inputs);

		CpuState? current = _inputs[toIndex];
		if (current != null && current.SameRoots(recomputed))
			return;

		_inputs[toIndex] = recomputed;
		QueueUp(toIndex);
	}

	private CpuState MergeInputs(
		int toIndex,
		IReadOnlyList<(int Source, CpuState State)> inputs)
	{
		var merged = new CpuState(_maker);

		for (int registerIndex = 0; registerIndex < 16; registerIndex++)
		{
			var values = new List<KeyValuePair<int, Expr>>(inputs.Count);
			foreach ((int source, CpuState cpu) in inputs)
			{
				values.Add(new KeyValuePair<int, Expr>(
					source,
					cpu.GetReg64(registerIndex)));
			}

			merged.SetReg64(
				registerIndex,
				MergeInputExpr(toIndex, registerIndex, 64, values));
		}

		var memoryKeys = inputs[0].State.Mem
			.Select(pair => pair.Key)
			.Where(key => inputs.All(x => x.State.TryGetMem(key, out _)))
			.Distinct()
			.ToList();

		foreach (MemKey memoryKey in memoryKeys)
		{
			var cells = new List<(int Source, MemCell Cell)>(inputs.Count);
			foreach ((int source, CpuState cpu) in inputs)
			{
				if (!cpu.TryGetMem(memoryKey, out MemCell cell))
				{
					cells.Clear();
					break;
				}
				cells.Add((source, cell));
			}

			if (cells.Count != inputs.Count)
				continue;

			int bits = cells.Max(x => x.Cell.Bits);
			var values = new List<KeyValuePair<int, Expr>>(cells.Count);
			foreach ((int source, MemCell cell) in cells)
			{
				values.Add(new KeyValuePair<int, Expr>(
					source,
					_maker.Cast(cell.Value, bits)));
			}

			Expr value = MergeInputExpr(
				toIndex,
				1000 + memoryKey.AddressKey,
				bits,
				values);
			merged.SetMem(memoryKey, new MemCell(bits, value));
		}

		return merged;
	}

	private Expr MergeInputExpr(
		int toIndex,
		int location,
		int bits,
		IReadOnlyList<KeyValuePair<int, Expr>> choice)
	{
		List<KeyValuePair<int, Expr>> castInputs = choice
			.Select(pair => new KeyValuePair<int, Expr>(
				pair.Key,
				_maker.Cast(pair.Value, bits)))
			.ToList();

		List<Expr> distinct = castInputs
			.Select(x => x.Value)
			.GroupBy(x => x.Id)
			.Select(x => x.First())
			.ToList();

		var key = (toIndex, location);

		if (_joinPhis.TryGetValue(key, out PhiExpr? phi))
		{
			phi.SetChoice(castInputs);
			return phi;
		}

		if (distinct.Count == 1)
			return distinct[0];

		phi = _maker.Phi(bits, $"phi_{toIndex}_{location}");
		_joinPhis.Add(key, phi);
		phi.SetChoice(castInputs);
		return phi;
	}

	private Expr MergeEventExpr(
		int eventIndex,
		int slot,
		Expr? current,
		Expr choice)
	{
		if (current == null)
			return choice;
		if (current.Id == choice.Id)
			return current;

		var key = (eventIndex, slot);
		if (!_eventPhis.TryGetValue(key, out var phi))
		{
			phi = _maker.Phi(Math.Max(current.Bits, choice.Bits), $"event_{eventIndex}_{slot}");
			phi.AddChoice(_maker.Cast(current, phi.Bits));
			_eventPhis.Add(key, phi);
		}

		phi.AddChoice(_maker.Cast(choice, phi.Bits));
		return phi;
	}

	private void Execute(int index, CpuState cpu)
	{
		Instruction ins = _code[index];

		if (ins.FlowControl == FlowControl.Call ||
			ins.FlowControl == FlowControl.IndirectCall)
		{
			SaveCall(index, ins, cpu);
			ClearAfterCall(index, ins, cpu);
			return;
		}

		switch (ins.Mnemonic)
		{
			case Mnemonic.Mov:
				RunMove(index, ins, cpu);
				return;

			case Mnemonic.Movzx:
				RunExtend(index, ins, cpu, signExtend: false, forceSourceBits: null);
				return;

			case Mnemonic.Movsx:
				RunExtend(index, ins, cpu, signExtend: true, forceSourceBits: null);
				return;

			case Mnemonic.Movsxd:
				RunExtend(index, ins, cpu, signExtend: true, forceSourceBits: 32);
				return;

			case Mnemonic.Lea:
				if (ins.Op0Kind == OpKind.Register && ins.Op1Kind == OpKind.Memory)
				{
					Register dest = ins.GetOpRegister(0);
					cpu.WriteReg(dest, GetAddress(ins, cpu));
				}
				return;

			case Mnemonic.Add:
				RunBinary(index, ins, cpu, BinaryOp.Add);
				return;
			case Mnemonic.Sub:
				RunBinary(index, ins, cpu, BinaryOp.Subtract);
				return;
			case Mnemonic.Xor:
				RunBinary(index, ins, cpu, BinaryOp.Xor);
				return;
			case Mnemonic.And:
				RunBinary(index, ins, cpu, BinaryOp.And);
				return;
			case Mnemonic.Or:
				RunBinary(index, ins, cpu, BinaryOp.Or);
				return;

			case Mnemonic.Imul:
				RunMultiply(index, ins, cpu);
				return;

			case Mnemonic.Shl:
			case Mnemonic.Sal:
				RunBinary(index, ins, cpu, BinaryOp.ShiftLeft);
				return;
			case Mnemonic.Shr:
				RunBinary(index, ins, cpu, BinaryOp.ShiftRight);
				return;
			case Mnemonic.Sar:
				RunBinary(index, ins, cpu, BinaryOp.ArithmeticShiftRight);
				return;
			case Mnemonic.Rol:
				RunBinary(index, ins, cpu, BinaryOp.RotateLeft);
				return;
			case Mnemonic.Ror:
				RunBinary(index, ins, cpu, BinaryOp.RotateRight);
				return;

			case Mnemonic.Inc:
				RunAddOne(ins, cpu, 1);
				return;
			case Mnemonic.Dec:
				RunAddOne(ins, cpu, -1);
				return;
			case Mnemonic.Neg:
				RunUnary(ins, cpu, UnaryOp.Neg);
				return;
			case Mnemonic.Not:
				RunUnary(ins, cpu, UnaryOp.Not);
				return;
			case Mnemonic.Bswap:
				RunUnary(ins, cpu, UnaryOp.ByteSwap);
				return;

			case Mnemonic.Xchg:
				RunSwap(index, ins, cpu);
				return;

			case Mnemonic.Cmp:
				SaveCompare(index, ins, cpu);
				return;

			case Mnemonic.Push:
				RunPush(index, ins, cpu);
				return;
			case Mnemonic.Pop:
				RunPop(index, ins, cpu);
				return;

			case Mnemonic.Cdqe:
				cpu.WriteReg(
					Register.RAX,
					_maker.Cast(cpu.ReadReg(Register.EAX), 64, signExtend: true));
				return;

			case Mnemonic.Nop:
			case Mnemonic.Test:
				return;
		}

		string mnemonicName = ins.Mnemonic.ToString();
		if (mnemonicName.StartsWith("Cmov", StringComparison.Ordinal))
		{
			RunConditionalMove(index, ins, cpu);
			return;
		}

		if (mnemonicName.StartsWith("Set", StringComparison.Ordinal))
		{
			if (ins.Op0Kind == OpKind.Register)
				cpu.WriteReg(
					ins.GetOpRegister(0),
					_maker.Bad($"condition@0x{ins.IP:X}", 8));
			return;
		}

		if (ins.Op0Kind == OpKind.Register &&
			WritesFirst(ins.Mnemonic))
		{
			Register dest = ins.GetOpRegister(0);
			cpu.WriteReg(
				dest,
				_maker.Bad($"unsupported_{ins.Mnemonic}@0x{ins.IP:X}", dest.GetSize() * 8));
		}
	}

	private static bool WritesFirst(Mnemonic mnemonic)
	{
		switch (mnemonic)
		{
			case Mnemonic.Jmp:
			case Mnemonic.Call:
			case Mnemonic.Ret:
			case Mnemonic.Cmp:
			case Mnemonic.Test:
			case Mnemonic.Nop:
				return false;
			default:
				return true;
		}
	}

	private void RunMove(int index, Instruction ins, CpuState cpu)
	{
		if (ins.Op0Kind == OpKind.Register)
		{
			Register dest = ins.GetOpRegister(0);
			int bits = dest.GetSize() * 8;
			Expr source = ReadArg(index, ins, 1, cpu, bits);
			cpu.WriteReg(dest, source);
			return;
		}

		if (ins.Op0Kind == OpKind.Memory)
		{
			int bits = GetMemBits(ins, defaultBits: GetArgBits(ins, 1));
			Expr addr = GetAddress(ins, cpu);
			Expr source = ReadArg(index, ins, 1, cpu, bits);
			cpu.WriteMem(addr, bits, source);
			SaveStore(index, ins, bits, addr, source);
		}
	}

	private void RunExtend(
		int index,
		Instruction ins,
		CpuState cpu,
		bool signExtend,
		int? forceSourceBits)
	{
		if (ins.Op0Kind != OpKind.Register)
			return;

		Register dest = ins.GetOpRegister(0);
		int destinationBits = dest.GetSize() * 8;
		int inputBits = forceSourceBits ?? GetArgBits(ins, 1);
		Expr source = ReadArg(index, ins, 1, cpu, inputBits);
		cpu.WriteReg(dest, _maker.Cast(source, destinationBits, signExtend));
	}

	private void RunBinary(
		int index,
		Instruction ins,
		CpuState cpu,
		BinaryOp op)
	{
		if (ins.Op0Kind == OpKind.Register)
		{
			Register dest = ins.GetOpRegister(0);
			int bits = dest.GetSize() * 8;
			Expr left = cpu.ReadReg(dest);
			Expr right = ReadArg(index, ins, 1, cpu, bits);
			cpu.WriteReg(dest, _maker.Binary(op, left, right, bits));
			return;
		}

		if (ins.Op0Kind == OpKind.Memory)
		{
			int bits = GetMemBits(ins, defaultBits: 64);
			Expr addr = GetAddress(ins, cpu);
			Expr left = cpu.ReadMem(addr, bits, ins.IP, 0);
			Expr right = ReadArg(index, ins, 1, cpu, bits);
			Expr value = _maker.Binary(op, left, right, bits);
			cpu.WriteMem(addr, bits, value);
			SaveStore(index, ins, bits, addr, value);
		}
	}

	private void RunMultiply(int index, Instruction ins, CpuState cpu)
	{
		if (ins.Op0Kind != OpKind.Register)
			return;

		Register dest = ins.GetOpRegister(0);
		int bits = dest.GetSize() * 8;
		Expr value;

		if (ins.OpCount == 2)
		{
			value = _maker.Binary(
				BinaryOp.Multiply,
				cpu.ReadReg(dest),
				ReadArg(index, ins, 1, cpu, bits),
				bits);
		}
		else if (ins.OpCount >= 3)
		{
			value = _maker.Binary(
				BinaryOp.Multiply,
				ReadArg(index, ins, 1, cpu, bits),
				ReadArg(index, ins, 2, cpu, bits),
				bits);
		}
		else
		{
			value = _maker.Bad($"one_operand_imul@0x{ins.IP:X}", bits);
		}

		cpu.WriteReg(dest, value);
	}

	private void RunAddOne(Instruction ins, CpuState cpu, int delta)
	{
		if (ins.Op0Kind != OpKind.Register)
			return;

		Register dest = ins.GetOpRegister(0);
		int bits = dest.GetSize() * 8;
		Expr value = cpu.ReadReg(dest);
		Expr constant = _maker.Const(unchecked((ulong)delta), bits);
		cpu.WriteReg(
			dest,
			_maker.Binary(BinaryOp.Add, value, constant, bits));
	}

	private void RunUnary(
		Instruction ins,
		CpuState cpu,
		UnaryOp op)
	{
		if (ins.Op0Kind != OpKind.Register)
			return;

		Register dest = ins.GetOpRegister(0);
		int bits = dest.GetSize() * 8;
		cpu.WriteReg(dest, _maker.Unary(op, cpu.ReadReg(dest), bits));
	}

	private void RunSwap(int index, Instruction ins, CpuState cpu)
	{
		if (ins.Op0Kind != OpKind.Register || ins.Op1Kind != OpKind.Register)
			return;

		Register leftRegister = ins.GetOpRegister(0);
		Register rightRegister = ins.GetOpRegister(1);
		Expr left = cpu.ReadReg(leftRegister);
		Expr right = cpu.ReadReg(rightRegister);
		cpu.WriteReg(leftRegister, right);
		cpu.WriteReg(rightRegister, left);
	}

	private void RunConditionalMove(int index, Instruction ins, CpuState cpu)
	{
		if (ins.Op0Kind != OpKind.Register)
			return;

		Register dest = ins.GetOpRegister(0);
		int bits = dest.GetSize() * 8;
		Expr oldValue = cpu.ReadReg(dest);
		Expr newValue = ReadArg(index, ins, 1, cpu, bits);
		var phi = _maker.Phi(bits, $"cmov@0x{ins.IP:X}");
		phi.AddChoice(oldValue);
		phi.AddChoice(newValue);
		cpu.WriteReg(dest, phi);
	}

	private void RunPush(int index, Instruction ins, CpuState cpu)
	{

		Expr value = ReadArg(index, ins, 0, cpu, 64);
		Expr oldRsp = cpu.ReadReg(Register.RSP);
		Expr newRsp = _maker.Binary(
			BinaryOp.Subtract,
			oldRsp,
			_maker.Const(8, 64),
			64);
		cpu.WriteReg(Register.RSP, newRsp);
		cpu.WriteMem(newRsp, 64, value);
	}

	private void RunPop(int index, Instruction ins, CpuState cpu)
	{
		Expr rsp = cpu.ReadReg(Register.RSP);
		Expr value = cpu.ReadMem(rsp, 64, ins.IP, 0);
		if (ins.Op0Kind == OpKind.Register)
			cpu.WriteReg(ins.GetOpRegister(0), value);
		cpu.WriteReg(
			Register.RSP,
			_maker.Binary(BinaryOp.Add, rsp, _maker.Const(8, 64), 64));
	}

	private Expr ReadArg(
		int codeIndex,
		Instruction ins,
		int arg,
		CpuState cpu,
		int desiredBits)
	{
		OpKind kind = ins.GetOpKind(arg);
		switch (kind)
		{
			case OpKind.Register:
				return _maker.Cast(cpu.ReadReg(ins.GetOpRegister(arg)), desiredBits);

			case OpKind.Memory:
				{
					int bits = GetMemBits(ins, desiredBits);
					Expr addr = GetAddress(ins, cpu);
					Expr value = cpu.ReadMem(addr, bits, ins.IP, arg);
					return _maker.Cast(value, desiredBits);
				}

			case OpKind.Immediate8:
			case OpKind.Immediate8_2nd:
			case OpKind.Immediate16:
			case OpKind.Immediate32:
			case OpKind.Immediate64:
			case OpKind.Immediate8to16:
			case OpKind.Immediate8to32:
			case OpKind.Immediate8to64:
			case OpKind.Immediate32to64:
				return _maker.Const(ins.GetImmediate(arg), desiredBits);

			default:
				return _maker.Bad(
					$"operand_{kind}@0x{ins.IP:X}",
					desiredBits);
		}
	}

	private int GetArgBits(Instruction ins, int arg)
	{
		OpKind kind = ins.GetOpKind(arg);
		if (kind == OpKind.Register)
			return Math.Max(8, ins.GetOpRegister(arg).GetSize() * 8);
		if (kind == OpKind.Memory)
			return GetMemBits(ins, 64);

		switch (kind)
		{
			case OpKind.Immediate8:
			case OpKind.Immediate8_2nd:
				return 8;
			case OpKind.Immediate16:
			case OpKind.Immediate8to16:
				return 16;
			case OpKind.Immediate32:
			case OpKind.Immediate8to32:
				return 32;
			default:
				return 64;
		}
	}

	private static int GetMemBits(Instruction ins, int defaultBits)
	{
		int size = ins.MemorySize.GetSize();
		return size > 0 ? size * 8 : defaultBits;
	}

	private Expr GetAddress(Instruction ins, CpuState cpu)
	{
		if (ins.IsIPRelativeMemoryOperand)
			return _maker.Const(ins.IPRelativeMemoryAddress, 64);

		Expr addr = _maker.Const(ins.MemoryDisplacement64, 64);

		if (ins.MemoryBase != Register.None)
		{
			addr = _maker.Binary(
				BinaryOp.Add,
				addr,
				_maker.Cast(cpu.ReadReg(ins.MemoryBase), 64),
				64);
		}

		if (ins.MemoryIndex != Register.None)
		{
			Expr index = _maker.Cast(cpu.ReadReg(ins.MemoryIndex), 64);
			if (ins.MemoryIndexScale != 1)
			{
				index = _maker.Binary(
					BinaryOp.Multiply,
					index,
					_maker.Const((ulong)ins.MemoryIndexScale, 64),
					64);
			}

			addr = _maker.Binary(BinaryOp.Add, addr, index, 64);
		}

		return addr;
	}

	private void SaveCall(int index, Instruction ins, CpuState cpu)
	{
		if (!_savingEvents)
			return;

		bool indirect = ins.FlowControl == FlowControl.IndirectCall;
		var result = _maker.Call(index, ins.IP, 64);

		if (!_callHits.TryGetValue(index, out var site))
		{
			site = new CallHit(index, ins.IP, indirect, result);
			_callHits.Add(index, site);
		}

		site.Rcx = MergeEventExpr(index, 0, site.Rcx, cpu.ReadReg(Register.RCX));
		site.Rdx = MergeEventExpr(index, 1, site.Rdx, cpu.ReadReg(Register.RDX));
		site.R8 = MergeEventExpr(index, 2, site.R8, cpu.ReadReg(Register.R8));
		site.R9 = MergeEventExpr(index, 3, site.R9, cpu.ReadReg(Register.R9));
	}

	private void ClearAfterCall(int index, Instruction ins, CpuState cpu)
	{
		cpu.WriteReg(Register.RAX, _maker.Call(index, ins.IP, 64));

		Register[] volatileRegisters =
		{
			Register.RCX,
			Register.RDX,
			Register.R8,
			Register.R9,
			Register.R10,
			Register.R11,
		};

		foreach (Register register in volatileRegisters)
		{
			cpu.WriteReg(
				register,
				_maker.Bad($"call_clobber_{register}@0x{ins.IP:X}", 64));
		}
	}

	private void SaveCompare(int index, Instruction ins, CpuState cpu)
	{
		if (!_savingEvents)
			return;

		int bits = Math.Max(GetArgBits(ins, 0), GetArgBits(ins, 1));
		Expr left = ReadArg(index, ins, 0, cpu, bits);
		Expr right = ReadArg(index, ins, 1, cpu, bits);

		if (!_compareHits.TryGetValue(index, out var site))
		{
			site = new CompareHit(index, ins.IP);
			_compareHits.Add(index, site);
		}

		site.Left = MergeEventExpr(index, 10, site.Left, left);
		site.Right = MergeEventExpr(index, 11, site.Right, right);
	}

	private void SaveStore(
		int index,
		Instruction ins,
		int bits,
		Expr addr,
		Expr value)
	{
		if (!_savingEvents)
			return;

		if (!_storeHits.TryGetValue(index, out var site))
		{
			site = new StoreHit(index, ins.IP, bits);
			_storeHits.Add(index, site);
		}

		site.Value = MergeEventExpr(index, 20, site.Value, value);
		site.Addr = MergeEventExpr(index, 21, site.Addr, addr);
	}
}
