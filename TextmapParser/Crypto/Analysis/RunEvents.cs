using Iced.Intel;

namespace TextmapParser;

internal sealed class CallHit
{
    public CallHit(int codeIndex, ulong ip, bool indirect, CallExpr result)
    {
        Index = codeIndex;
        IP = ip;
        Indirect = indirect;
        Result = result;
    }

    public int Index { get; }
    public ulong IP { get; }
    public bool Indirect { get; }
    public CallExpr Result { get; }

    public Expr? Rcx { get; set; }
    public Expr? Rdx { get; set; }
    public Expr? R8 { get; set; }
    public Expr? R9 { get; set; }
}

internal sealed class CompareHit
{
    public CompareHit(int codeIndex, ulong ip)
    {
        Index = codeIndex;
        IP = ip;
    }

    public int Index { get; }
    public ulong IP { get; }
    public Expr? Left { get; set; }
    public Expr? Right { get; set; }
}

internal sealed class StoreHit
{
    public StoreHit(int codeIndex, ulong ip, int bits)
    {
        Index = codeIndex;
        IP = ip;
        Bits = bits;
    }

    public int Index { get; }
    public ulong IP { get; }
    public int Bits { get; }
    public Expr? Addr { get; set; }
    public Expr? Value { get; set; }
}

internal sealed class RunResult
{
    public RunResult(
        IReadOnlyList<Instruction> code,
        IReadOnlyList<CallHit> calls,
        IReadOnlyList<CompareHit> compares,
        IReadOnlyList<StoreHit> stores,
        ExprMaker maker)
    {
        Code = code;
        CallHits = calls;
        CompareHits = compares;
        StoreHits = stores;
        Maker = maker;
    }

    public IReadOnlyList<Instruction> Code { get; }
    public IReadOnlyList<CallHit> CallHits { get; }
    public IReadOnlyList<CompareHit> CompareHits { get; }
    public IReadOnlyList<StoreHit> StoreHits { get; }
    public ExprMaker Maker { get; }
}
