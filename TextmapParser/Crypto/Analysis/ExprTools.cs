namespace TextmapParser;

internal static class ExprTools
{
    public static IReadOnlyList<LoadExpr> FindLoads(Expr expr)
    {
        var result = new List<LoadExpr>();
        Visit(expr, new HashSet<int>(), node =>
        {
            if (node is LoadExpr load)
                result.Add(load);
        }, walkLoadAddr: false);
        return result;
    }

    public static IReadOnlyList<CallExpr> CallHits(Expr expr)
    {
        var result = new List<CallExpr>();
        Visit(expr, new HashSet<int>(), node =>
        {
            if (node is CallExpr call)
                result.Add(call);
        }, walkLoadAddr: false);
        return result;
    }

    public static IReadOnlyList<PhiExpr> FindPhis(Expr expr)
    {
        var result = new List<PhiExpr>();
        Visit(expr, new HashSet<int>(), node =>
        {
            if (node is PhiExpr phi)
                result.Add(phi);
        }, walkLoadAddr: false);
        return result;
    }

    public static bool HasNode(Expr expr, int nodeId)
    {
        bool found = false;
        Visit(expr, new HashSet<int>(), node =>
        {
            if (node.Id == nodeId)
                found = true;
        }, walkLoadAddr: false);
        return found;
    }

    public static bool IsBad(Expr expr)
    {
        bool unresolved = false;
        Visit(expr, new HashSet<int>(), node =>
        {
            if (node is BadExpr ||
                node is LoadExpr ||
                node is CallExpr ||
                node is PhiExpr)
            {
                unresolved = true;
            }
        }, walkLoadAddr: false);
        return unresolved;
    }

    public static bool AreBranchLoads(
        Expr expr,
        IEnumerable<int> loadIds)
    {
        var wanted = loadIds.ToHashSet();
        if (wanted.Count < 2)
            return false;

        bool sawPhiSeparation = false;
        bool combinedByValueOperation = false;
        var seen = new HashSet<int>();
        var memo = new Dictionary<int, HashSet<int>>();

        HashSet<int> Visit(Expr node)
        {
            if (memo.TryGetValue(node.Id, out HashSet<int>? cached))
                return new HashSet<int>(cached);

            if (!seen.Add(node.Id))
                return new HashSet<int>();

            HashSet<int> result;
            switch (node)
            {
                case LoadExpr load:
                    result = wanted.Contains(load.Id)
                        ? new HashSet<int> { load.Id }
                        : new HashSet<int>();
                    break;

                case UnaryExpr unary:
                    result = Visit(unary.Value);
                    break;

                case SliceExpr extract:
                    result = Visit(extract.Value);
                    break;

                case BinaryExpr binary:
                    {
                        HashSet<int> left = Visit(binary.Left);
                        HashSet<int> right = Visit(binary.Right);
                        if (left.Count != 0 && right.Count != 0 &&
                            left.Concat(right).Distinct().Count() > 1)
                        {
                            combinedByValueOperation = true;
                        }

                        left.UnionWith(right);
                        result = left;
                        break;
                    }

                case MergeExpr insert:
                    {
                        HashSet<int> original = Visit(insert.Original);
                        HashSet<int> inserted = Visit(insert.Inserted);
                        if (original.Count != 0 && inserted.Count != 0 &&
                            original.Concat(inserted).Distinct().Count() > 1)
                        {
                            combinedByValueOperation = true;
                        }

                        original.UnionWith(inserted);
                        result = original;
                        break;
                    }

                case PhiExpr phi:
                    {
                        result = new HashSet<int>();
                        int nonEmptyIncoming = 0;
                        foreach (Expr choice in phi.Choices)
                        {
                            HashSet<int> branch = Visit(choice);
                            if (branch.Count != 0)
                                nonEmptyIncoming++;
                            result.UnionWith(branch);
                        }

                        if (nonEmptyIncoming > 1 && result.Count > 1)
                            sawPhiSeparation = true;
                        break;
                    }

                default:
                    result = new HashSet<int>();
                    break;
            }

            seen.Remove(node.Id);
            memo[node.Id] = new HashSet<int>(result);
            return result;
        }

        HashSet<int> found = Visit(expr);
        return !combinedByValueOperation &&
               sawPhiSeparation &&
               wanted.All(found.Contains);
    }

    public static bool HasSlot(
        Expr expr,
        DataSlot slot)
    {
        bool found = false;
        Visit(expr, new HashSet<int>(), node =>
        {
            if (node is SlotExpr value && value.Slot == slot)
                found = true;
        }, walkLoadAddr: false);
        return found;
    }

    public static Expr Swap(
        Expr expr,
        ExprMaker maker,
        IReadOnlyDictionary<int, Expr> swaps)
    {
        var memo = new Dictionary<int, Expr>();
        var seen = new HashSet<int>();
        int budget = 500_000;

        return SwapCore(
            expr,
            maker,
            swaps,
            memo,
            seen,
            ref budget);
    }

    public static bool IsLoopCounter(PhiExpr phi)
    {
        bool hasZero = phi.Choices.Any(IsZero);
        bool hasStep = phi.Choices.Any(x => IsStepFromPhi(x, phi.Id));
        return hasZero && hasStep;
    }

    private static bool IsZero(Expr expr)
    {
        expr = StripCasts(expr);
        return expr is ConstExpr constant && constant.Value == 0;
    }

    private static bool IsStepFromPhi(Expr expr, int phiId)
    {
        expr = StripCasts(expr);
        if (expr is not BinaryExpr binary)
            return false;

        if (binary.Op == BinaryOp.Add)
        {
            return
                (HasNode(binary.Left, phiId) && IsOne(binary.Right)) ||
                (HasNode(binary.Right, phiId) && IsOne(binary.Left));
        }

        if (binary.Op == BinaryOp.Subtract)
            return HasNode(binary.Left, phiId) && IsOne(binary.Right);

        return false;
    }

    private static bool IsOne(Expr expr)
    {
        expr = StripCasts(expr);
        return expr is ConstExpr constant && constant.Value == 1;
    }

    private static Expr StripCasts(Expr expr)
    {
        while (true)
        {
            if (expr is SliceExpr extract && extract.Shift == 0)
            {
                expr = extract.Value;
                continue;
            }

            if (expr is UnaryExpr unary &&
                (unary.Op == UnaryOp.ZeroExtend ||
                 unary.Op == UnaryOp.SignExtend))
            {
                expr = unary.Value;
                continue;
            }

            return expr;
        }
    }

    private static Expr SwapCore(
        Expr expr,
        ExprMaker maker,
        IReadOnlyDictionary<int, Expr> swaps,
        Dictionary<int, Expr> memo,
        HashSet<int> seen,
        ref int budget)
    {
        if (swaps.TryGetValue(expr.Id, out var direct))
            return maker.Cast(direct, expr.Bits);

        if (memo.TryGetValue(expr.Id, out var cached))
            return cached;

        if (--budget < 0)
        {
            throw new PlanException(
                "The native expr slice exceeded the rewrite safety budget. " +
                "This usually means a loop/event phi was not reduced to the serialized load site.");
        }

        if (!seen.Add(expr.Id))
            return expr;

        Expr result;
        try
        {
            switch (expr)
            {
                case ConstExpr:
                case SlotExpr:
                case BadExpr:
                case LoadExpr:
                case CallExpr:
                    result = expr;
                    break;

                case PhiExpr phi:
                    {

                        memo[phi.Id] = phi;

                        if (phi.Choices.Count > 16_384)
                        {
                            throw new PlanException(
                                $"Phi {phi.Name} has {phi.Choices.Count} inputs; " +
                                "refusing to expand an unbounded control-flow merge.");
                        }

                        var items = new List<Expr>();
                        foreach (Expr choice in phi.Choices)
                        {
                            if (choice.Id == phi.Id)
                                continue;

                            Expr item = SwapCore(
                                choice,
                                maker,
                                swaps,
                                memo,
                                seen,
                                ref budget);

                            if (HasNode(item, phi.Id))
                                continue;

                            if (items.All(x => x.Id != item.Id))
                                items.Add(item);
                        }

                        if (items.Count == 1)
                        {
                            result = maker.Cast(items[0], phi.Bits);
                            break;
                        }

                        var resolved = items
                            .Where(x => !IsBad(x))
                            .GroupBy(x => x.Id)
                            .Select(x => x.First())
                            .ToList();

                        result = resolved.Count == 1
                            ? maker.Cast(resolved[0], phi.Bits)
                            : expr;
                        break;
                    }

                case UnaryExpr unary:
                    {
                        Expr newValue = SwapCore(
                            unary.Value,
                            maker,
                            swaps,
                            memo,
                            seen,
                            ref budget);

                        if (unary.Op == UnaryOp.ZeroExtend ||
                            unary.Op == UnaryOp.SignExtend)
                        {
                            result = maker.Cast(
                                newValue,
                                unary.Bits,
                                signExtend:
                                    unary.Op == UnaryOp.SignExtend);
                        }
                        else
                        {
                            result = maker.Unary(
                                unary.Op,
                                newValue,
                                unary.Bits);
                        }
                        break;
                    }

                case BinaryExpr binary:
                    result = maker.Binary(
                        binary.Op,
                        SwapCore(
                            binary.Left,
                            maker,
                            swaps,
                            memo,
                            seen,
                            ref budget),
                        SwapCore(
                            binary.Right,
                            maker,
                            swaps,
                            memo,
                            seen,
                            ref budget),
                        binary.Bits);
                    break;

                case SliceExpr extract:
                    result = maker.Extract(
                        SwapCore(
                            extract.Value,
                            maker,
                            swaps,
                            memo,
                            seen,
                            ref budget),
                        extract.Shift,
                        extract.Bits);
                    break;

                case MergeExpr insert:
                    result = maker.Insert(
                        SwapCore(
                            insert.Original,
                            maker,
                            swaps,
                            memo,
                            seen,
                            ref budget),
                        SwapCore(
                            insert.Inserted,
                            maker,
                            swaps,
                            memo,
                            seen,
                            ref budget),
                        insert.Shift,
                        insert.PutBits,
                        insert.Bits);
                    break;

                default:
                    throw new InvalidOperationException(
                        "Bad expr node.");
            }
        }
        finally
        {
            seen.Remove(expr.Id);
        }

        memo[expr.Id] = result;
        return result;
    }

    private static void Visit(
        Expr expr,
        HashSet<int> visited,
        Action<Expr> visitor,
        bool walkLoadAddr)
    {
        if (!visited.Add(expr.Id))
            return;

        visitor(expr);

        switch (expr)
        {
            case LoadExpr load when walkLoadAddr:
                Visit(load.Addr, visited, visitor, true);
                break;

            case UnaryExpr unary:
                Visit(unary.Value, visited, visitor, walkLoadAddr);
                break;

            case BinaryExpr binary:
                Visit(binary.Left, visited, visitor, walkLoadAddr);
                Visit(binary.Right, visited, visitor, walkLoadAddr);
                break;

            case SliceExpr extract:
                Visit(extract.Value, visited, visitor, walkLoadAddr);
                break;

            case MergeExpr insert:
                Visit(insert.Original, visited, visitor, walkLoadAddr);
                Visit(insert.Inserted, visited, visitor, walkLoadAddr);
                break;

            case PhiExpr phi:
                foreach (Expr choice in phi.Choices)
                    Visit(choice, visited, visitor, walkLoadAddr);
                break;
        }
    }
}
