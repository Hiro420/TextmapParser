using Iced.Intel;

namespace TextmapParser;

public sealed class PlanReader : IPlanReader
{
	public DecodePlan Read(List<Instruction> code) =>
		Read((IReadOnlyList<Instruction>)code);

	public DecodePlan Read(IReadOnlyList<Instruction> code)
	{
		var symbolic = new SymbolicRunner(code).Run();
		return MakePlan(symbolic);
	}

	private static DecodePlan MakePlan(RunResult run)
	{
		var notes = new List<string>();
		ExprMaker maker = run.Maker;

		var callByIndex = run.CallHits.ToDictionary(x => x.Index);

		CallHit? mainAdd = null;
		CallHit? textCall = null;
		int bestScore = int.MinValue;

		foreach (CallHit item in run.CallHits.Where(x => x.Indirect))
		{
			if (item.Rdx == null || item.R8 == null)
				continue;

			foreach (CallExpr result in ExprTools.CallHits(item.R8))
			{
				if (!callByIndex.TryGetValue(result.CallId, out CallHit? producer) ||
					producer.Indirect ||
					producer.Rdx == null)
				{
					continue;
				}

				int score = 0;
				if (ExprTools.FindLoads(item.Rdx).Any(x => x.Bits == 32))
					score += 4;
				if (ExprTools.FindLoads(producer.Rdx).Any(x => x.Bits == 16))
					score += 6;
				if (ExprTools.FindPhis(item.Rdx).Any(ExprTools.IsLoopCounter))
					score += 4;
				if (ExprTools.FindPhis(producer.Rdx).Any(ExprTools.IsLoopCounter))
					score += 2;

				if (score > bestScore)
				{
					bestScore = score;
					mainAdd = item;
					textCall = producer;
				}
			}
		}

		if (mainAdd == null || textCall == null ||
			mainAdd.Rdx == null || textCall.Rdx == null)
		{
			throw new PlanException(
				"Could not identify the primary IDictionary.Add call and its string-producing call.");
		}

		Expr mainKeyRaw = maker.Cast(mainAdd.Rdx, 32);
		Expr sizeRaw = maker.Cast(textCall.Rdx, 16);

		List<PhiExpr> mainLoops =
			ExprTools.FindPhis(mainKeyRaw)
				.Concat(ExprTools.FindPhis(sizeRaw))
				.Where(ExprTools.IsLoopCounter)
				.GroupBy(x => x.Id)
				.Select(x => x.First())
				.ToList();

		if (mainLoops.Count == 0)
			throw new PlanException("Could not identify the primary loop induction slot.");

		PhiExpr? mainLoop = null;
		CompareHit? countCheck = null;
		int bestLoopScore = int.MinValue;

		foreach (PhiExpr loop in mainLoops)
		{
			CompareHit? check = TryFindCountCheck(run, loop);
			if (check == null)
				continue;

			int score = 0;
			if (ExprTools.HasNode(mainKeyRaw, loop.Id))
				score += 8;
			if (ExprTools.HasNode(sizeRaw, loop.Id))
				score += 8;

			score -= check.Index / 100_000;

			if (score > bestLoopScore)
			{
				bestLoopScore = score;
				mainLoop = loop;
				countCheck = check;
			}
		}

		if (mainLoop == null || countCheck == null)
			throw new PlanException(
				"Could not associate the primary induction slot with its count comparison.");

		Expr mainCountRaw =
			OtherSide(countCheck, mainLoop);

		var dataStores =
			run.StoreHits
				.Where(x => x.Bits == 64 && x.Value != null && x.Addr != null)
				.Where(x => x.Index > mainAdd.Index &&
							x.Index < textCall.Index)
				.Where(x => IsInLoop(run.Code, x.Index))
				.Where(x => ExprTools.FindPhis(x.Addr!).Count != 0)
				.Where(x => ExprTools.FindLoads(x.Value!)
					.Any(load => load.Bits == 64 && ExprTools.FindPhis(load.Addr).Count != 0))
				.Where(x => mainLoops.Any(phi =>
					ExprTools.HasNode(x.Value!, phi.Id)))
				.OrderByDescending(x => ScoreDataStore(x, mainKeyRaw))
				.ThenBy(x => x.Index)
				.ToList();

		StoreHit dataStore = dataStores.FirstOrDefault()
			?? throw new PlanException(
				"Could not identify a loop-carried 64-bit payload decode store. " +
				"The item must be inside a backward loop and both its source " +
				"load addr and dest store addr must vary with a loop phi.");

		Expr dataRaw = dataStore.Value!;

		IReadOnlyList<LoadExpr> countLoads = PickLoads(
			mainCountRaw,
			32,
			Array.Empty<int>(),
			"primary count");
		IReadOnlyList<LoadExpr> keyLoads = PickLoads(
			mainKeyRaw,
			32,
			countLoads.Select(x => x.Id),
			"primary key");
		IReadOnlyList<LoadExpr> sizeLoads = PickLoads(
			sizeRaw,
			16,
			Array.Empty<int>(),
			"encoded length");
		IReadOnlyList<LoadExpr> dataLoads = PickLoads(
			dataRaw,
			64,
			Array.Empty<int>(),
			"payload block");

		var swaps = new Dictionary<int, Expr>();
		foreach (PhiExpr phi in mainLoops)
		{
			swaps[phi.Id] =
				maker.Slot(DataSlot.MainIndex, phi.Bits);
		}

		AddLoadMap(
			swaps,
			countLoads,
			maker.Slot(DataSlot.MainCountRaw, 32));
		AddLoadMap(
			swaps,
			keyLoads,
			maker.Slot(DataSlot.MainKeyRaw, 32));
		AddLoadMap(
			swaps,
			sizeLoads,
			maker.Slot(DataSlot.TextSizeRaw, 16));
		AddLoadMap(
			swaps,
			dataLoads,
			maker.Slot(DataSlot.DataBlockRaw, 64));

		Expr mainCount = maker.Cast(
			ExprTools.Swap(mainCountRaw, maker, swaps),
			32);
		Expr mainKey = maker.Cast(
			ExprTools.Swap(mainKeyRaw, maker, swaps),
			32);
		Expr textSize = maker.Cast(
			ExprTools.Swap(sizeRaw, maker, swaps),
			16);
		Expr dataBlock = maker.Cast(
			ExprTools.Swap(dataRaw, maker, swaps),
			64);

		CheckReady("primary count", mainCount);
		CheckReady("primary key", mainKey);
		CheckReady("primary length", textSize);
		CheckReady("primary payload", dataBlock);

		CheckIndex(
			"primary key",
			mainKeyRaw,
			mainKey,
			mainLoops);
		CheckIndex(
			"primary length",
			sizeRaw,
			textSize,
			mainLoops);
		CheckIndex(
			"primary payload",
			dataRaw,
			dataBlock,
			mainLoops);

		notes.Add($"Primary Add call: 0x{mainAdd.IP:X}");
		notes.Add($"String call: 0x{textCall.IP:X}");
		notes.Add($"Primary count load: {DescribeLoads(countLoads)}");
		notes.Add($"Primary key load: {DescribeLoads(keyLoads)}");
		notes.Add($"Encoded length load: {DescribeLoads(sizeLoads)}");
		notes.Add($"Payload decode store: 0x{dataStore.IP:X}");
		notes.Add($"Payload block load: {DescribeLoads(dataLoads)}");
		notes.Add(
			$"Primary loop index: {mainLoops.Count} induction phi" +
			$"{(mainLoops.Count == 1 ? string.Empty : "s")} rebound to MainIndex");
		notes.Add($"Primary count: {mainCount}");
		notes.Add($"Primary key: {mainKey}");
		notes.Add($"Primary length: {textSize}");
		notes.Add($"Primary payload: {dataBlock}");
		notes.Add(
			$"Primary key depends on index: " +
			ExprTools.HasSlot(mainKey, DataSlot.MainIndex));
		notes.Add(
			$"Primary length depends on index: " +
			ExprTools.HasSlot(textSize, DataSlot.MainIndex));
		notes.Add(
			$"Primary payload depends on index: " +
			ExprTools.HasSlot(dataBlock, DataSlot.MainIndex));

		Expr? copyCount = null;
		Expr? copyTo = null;
		Expr? copyFrom = null;

		FindCopyPass(
			run,
			callByIndex,
			mainAdd,
			mainLoop,
			swaps,
			out CallHit? copyAdd,
			out CallHit? copyLookup,
			out PhiExpr? copyLoop);

		if (copyAdd != null && copyLookup != null && copyLoop != null &&
			copyAdd.Rdx != null && copyLookup.Rdx != null)
		{
			CompareHit copyCheck = FindCountCheck(run, copyLoop);
			Expr copyCountRaw = OtherSide(copyCheck, copyLoop);

			IReadOnlyList<LoadExpr> copyCountLoads = PickLoads(
				copyCountRaw,
				32,
				swaps.Keys,
				"alias count");

			var copyCountIds = copyCountLoads.Select(x => x.Id).ToHashSet();
			var copyLoads =
				ExprTools.FindLoads(copyAdd.Rdx)
					.Concat(ExprTools.FindLoads(copyLookup.Rdx))
					.Where(x => x.Bits == 32)
					.Where(x =>
						!swaps.ContainsKey(x.Id) &&
						!copyCountIds.Contains(x.Id))
					.GroupBy(x => x.Id)
					.Select(x => x.First())
					.GroupBy(x => (x.IP, x.Order, x.Bits))
					.Select(group => group
						.OrderBy(x => x.Id)
						.ToList())
					.OrderBy(group => group[0].IP)
					.ThenBy(group => group[0].Order)
					.ToList();

			if (copyLoads.Count != 2)
			{
				string details = copyLoads.Count == 0
					? "none"
					: string.Join(", ", copyLoads.Select(DescribeLoads));

				throw new PlanException(
					$"Expected two alias-entry 32-bit load sites, " +
					$"found {copyLoads.Count}: {details}.");
			}

			swaps[copyLoop.Id] =
				maker.Slot(DataSlot.CopyIndex, copyLoop.Bits);
			AddLoadMap(
				swaps,
				copyCountLoads,
				maker.Slot(DataSlot.CopyCountRaw, 32));
			AddLoadMap(
				swaps,
				copyLoads[0],
				maker.Slot(DataSlot.CopyLeftRaw, 32));
			AddLoadMap(
				swaps,
				copyLoads[1],
				maker.Slot(DataSlot.CopyRightRaw, 32));

			copyCount = maker.Cast(
				ExprTools.Swap(copyCountRaw, maker, swaps),
				32);
			copyTo = maker.Cast(
				ExprTools.Swap(copyAdd.Rdx, maker, swaps),
				32);
			copyFrom = maker.Cast(
				ExprTools.Swap(copyLookup.Rdx, maker, swaps),
				32);

			CheckReady("alias count", copyCount);
			CheckReady("alias dest key", copyTo);
			CheckReady("alias source key", copyFrom);

			notes.Add($"Alias lookup call: 0x{copyLookup.IP:X}");
			notes.Add($"Alias Add call: 0x{copyAdd.IP:X}");
			notes.Add($"Alias count load: {DescribeLoads(copyCountLoads)}");
			notes.Add($"Alias raw load 0: {DescribeLoads(copyLoads[0])}");
			notes.Add($"Alias raw load 1: {DescribeLoads(copyLoads[1])}");
			notes.Add($"Alias count: {copyCount}");
			notes.Add($"Alias dest key: {copyTo}");
			notes.Add($"Alias source key: {copyFrom}");
		}
		else
		{
			notes.Add("No second alias/reference pass was identified.");
		}

		return new DecodePlan(
			mainCount,
			mainKey,
			textSize,
			dataBlock,
			copyCount,
			copyTo,
			copyFrom,
			notes);
	}

	private static CompareHit FindCountCheck(
		RunResult run,
		PhiExpr indexPhi)
	{
		return TryFindCountCheck(run, indexPhi)
			?? throw new PlanException("Could not identify the loop-count comparison.");
	}

	private static CompareHit? TryFindCountCheck(
		RunResult run,
		PhiExpr indexPhi)
	{
		return run.CompareHits
			.Where(x => x.Left != null && x.Right != null)
			.Where(x =>
				ExprTools.HasNode(x.Left!, indexPhi.Id) ^
				ExprTools.HasNode(x.Right!, indexPhi.Id))
			.Where(x =>
				ExprTools.FindLoads(
					ExprTools.HasNode(x.Left!, indexPhi.Id) ? x.Right! : x.Left!)
				.Any(l => l.Bits == 32))
			.OrderBy(x => x.Index)
			.FirstOrDefault();
	}

	private static Expr OtherSide(
		CompareHit compare,
		PhiExpr indexPhi)
	{
		if (compare.Left == null || compare.Right == null)
			throw new InvalidOperationException();

		return ExprTools.HasNode(compare.Left, indexPhi.Id)
			? compare.Right
			: compare.Left;
	}

	private static int ScoreDataStore(
		StoreHit store,
		Expr key)
	{
		if (store.Value == null || store.Addr == null)
			return int.MinValue;

		int score = 0;
		IReadOnlyList<LoadExpr> loads = ExprTools.FindLoads(store.Value);
		IReadOnlyList<LoadExpr> loopLoads = loads
			.Where(x => x.Bits == 64 && ExprTools.FindPhis(x.Addr).Count != 0)
			.ToList();

		if (loopLoads.Count == 1)
			score += 100;
		else
			score -= Math.Abs(loopLoads.Count - 1) * 25;

		if (ExprTools.FindPhis(store.Addr).Count != 0)
			score += 50;

		if (store.Value is BinaryExpr binary &&
			(binary.Op == BinaryOp.Add ||
			 binary.Op == BinaryOp.Subtract ||
			 binary.Op == BinaryOp.Xor))
		{
			bool leftHasBlock = ExprTools.FindLoads(binary.Left)
				.Any(x => x.Bits == 64 && ExprTools.FindPhis(x.Addr).Count != 0);
			bool rightHasBlock = ExprTools.FindLoads(binary.Right)
				.Any(x => x.Bits == 64 && ExprTools.FindPhis(x.Addr).Count != 0);
			if (leftHasBlock ^ rightHasBlock)
				score += 40;
		}

		var keyLoads = ExprTools.FindLoads(key).Select(x => x.Id).ToHashSet();
		score += loads.Count(x => keyLoads.Contains(x.Id)) * 3;
		score -= ExprTools.FindPhis(store.Value)
			.Count(x => !ExprTools.IsLoopCounter(x)) * 2;
		return score;
	}

	private static bool IsInLoop(
		IReadOnlyList<Instruction> code,
		int codeIndex)
	{
		if ((uint)codeIndex >= (uint)code.Count)
			return false;

		var ipToIndex = new Dictionary<ulong, int>(code.Count);
		for (int i = 0; i < code.Count; i++)
			ipToIndex[code[i].IP] = i;

		for (int branchIndex = codeIndex + 1;
			 branchIndex < code.Count;
			 branchIndex++)
		{
			Instruction branch = code[branchIndex];
			if (branch.FlowControl != FlowControl.ConditionalBranch &&
				branch.FlowControl != FlowControl.UnconditionalBranch)
			{
				continue;
			}

			if (!ipToIndex.TryGetValue(branch.NearBranchTarget, out int toIndex))
				continue;

			if (toIndex <= codeIndex && codeIndex <= branchIndex)
				return true;
		}

		return false;
	}

	private static IReadOnlyList<LoadExpr> PickLoads(
		Expr expr,
		int bits,
		IEnumerable<int> excludedIds,
		string name)
	{
		var excluded = excludedIds.ToHashSet();

		var variants = ExprTools.FindLoads(expr)
			.Where(x => x.Bits == bits && !excluded.Contains(x.Id))
			.GroupBy(x => x.Id)
			.Select(x => x.First())
			.OrderBy(x => x.IP)
			.ThenBy(x => x.Order)
			.ThenBy(x => x.Id)
			.ToList();

		var sites = variants
			.GroupBy(x => (x.IP, x.Order, x.Bits))
			.Select(group => group.ToList())
			.OrderBy(group => group[0].IP)
			.ThenBy(group => group[0].Order)
			.ToList();

		if (sites.Count == 1)
			return sites[0];

		var allVariants = sites.SelectMany(x => x).ToList();
		if (sites.Count > 1 &&
			ExprTools.AreBranchLoads(
				expr,
				allVariants.Select(x => x.Id)))
		{
			return allVariants;
		}

		string details = sites.Count == 0
			? "none"
			: string.Join(
				", ",
				sites.Select(site =>
					$"0x{site[0].IP:X}#{site[0].Order} " +
					$"({site.Count} symbolic variant{(site.Count == 1 ? string.Empty : "s")})"));

		throw new PlanException(
			$"Expected one serialized {bits}-bit load site " +
			$"(or proven control-flow alternatives) for {name}, " +
			$"found {sites.Count}: {details}.");
	}

	private static string DescribeLoads(
		IEnumerable<LoadExpr> loads)
	{
		var list = loads
			.GroupBy(x => x.Id)
			.Select(x => x.First())
			.OrderBy(x => x.IP)
			.ThenBy(x => x.Order)
			.ThenBy(x => x.Id)
			.ToList();

		if (list.Count == 0)
			return "none";

		LoadExpr first = list[0];
		int siteCount = list
			.Select(x => (x.IP, x.Order, x.Bits))
			.Distinct()
			.Count();

		return $"0x{first.IP:X}#{first.Order}/{first.Bits} " +
			   $"({siteCount} physical site" +
			   $"{(siteCount == 1 ? string.Empty : "s")}, " +
			   $"{list.Count} symbolic variant" +
			   $"{(list.Count == 1 ? string.Empty : "s")})";
	}

	private static void AddLoadMap(
		IDictionary<int, Expr> swaps,
		IEnumerable<LoadExpr> loads,
		Expr slot)
	{
		foreach (LoadExpr load in loads)
			swaps[load.Id] = slot;
	}

	private static void FindCopyPass(
		RunResult run,
		IReadOnlyDictionary<int, CallHit> callByIndex,
		CallHit mainAdd,
		PhiExpr mainLoop,
		IReadOnlyDictionary<int, Expr> mainSwaps,
		out CallHit? copyAdd,
		out CallHit? copyLookup,
		out PhiExpr? copyLoop)
	{
		copyAdd = null;
		copyLookup = null;
		copyLoop = null;
		int bestScore = int.MinValue;

		foreach (CallHit item in run.CallHits.Where(x => x.Indirect && x.Index != mainAdd.Index))
		{
			if (item.Rdx == null || item.R8 == null)
				continue;

			foreach (CallExpr result in ExprTools.CallHits(item.R8))
			{
				if (!callByIndex.TryGetValue(result.CallId, out CallHit? producer) ||
					!producer.Indirect ||
					producer.Rdx == null)
				{
					continue;
				}

				var phis = ExprTools.FindPhis(item.Rdx)
					.Concat(ExprTools.FindPhis(producer.Rdx))
					.Where(ExprTools.IsLoopCounter)
					.Where(x => !mainSwaps.ContainsKey(x.Id))
					.GroupBy(x => x.Id)
					.Select(x => x.First())
					.ToList();

				foreach (PhiExpr phi in phis)
				{
					int score = 0;
					if (ExprTools.HasNode(item.Rdx, phi.Id))
						score += 4;
					if (ExprTools.HasNode(producer.Rdx, phi.Id))
						score += 4;

					int loadCount = ExprTools.FindLoads(item.Rdx)
						.Concat(ExprTools.FindLoads(producer.Rdx))
						.Where(x =>
							x.Bits == 32 &&
							!mainSwaps.ContainsKey(x.Id))
						.GroupBy(x => (x.IP, x.Order, x.Bits))
						.Count();

					if (loadCount == 2)
						score += 8;

					if (score > bestScore)
					{
						bestScore = score;
						copyAdd = item;
						copyLookup = producer;
						copyLoop = phi;
					}
				}
			}
		}
	}

	private static void CheckIndex(
		string name,
		Expr nativeExpr,
		Expr cleanExpr,
		IReadOnlyCollection<PhiExpr> loops)
	{
		bool nativeUsesIndex = loops.Any(phi =>
			ExprTools.HasNode(nativeExpr, phi.Id));

		if (nativeUsesIndex &&
			!ExprTools.HasSlot(
				cleanExpr,
				DataSlot.MainIndex))
		{
			string phiSummary = string.Join(", ", loops.Select(phi =>
				$"{phi.Name}#{phi.Id}[{phi.Choices.Count}]"));
			throw new PlanException(
				$"The native {name} expr depends on the primary loop index, " +
				"but the recovered plan does not. Refusing to emit an i=0-only plan. " +
				$"Index phis: {phiSummary}. " +
				$"Native: {ExprText.Format(nativeExpr, 256, 4096)}. " +
				$"Recovered: {ExprText.Format(cleanExpr, 256, 4096)}.");
		}
	}

	private static void CheckReady(string name, Expr expr)
	{
		if (ExprTools.IsBad(expr))
			throw new PlanException(
				$"The {name} expr still contains unresolved native cpu: {expr}");
	}
}
