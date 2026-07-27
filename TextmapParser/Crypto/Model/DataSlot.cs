using System.Text;

namespace TextmapParser;

public enum DataSlot
{
    MainCountRaw,
    MainIndex,
    MainKeyRaw,
    TextSizeRaw,
    DataBlockRaw,
    CopyCountRaw,
    CopyIndex,
    CopyLeftRaw,
    CopyRightRaw,
}

public sealed class EvalBag
{
    private readonly ulong[] _data = new ulong[Enum.GetValues(typeof(DataSlot)).Length];

    public ulong this[DataSlot slot]
    {
        get => _data[(int)slot];
        set => _data[(int)slot] = value;
    }

    public void Clear()
    {
        Array.Clear(_data, 0, _data.Length);
    }
}

public sealed class PlanException : Exception
{
    public PlanException(string message) : base(message) { }
}

public sealed class DecodePlan
{
    internal DecodePlan(
        Expr mainCount,
        Expr mainKey,
        Expr textSize,
        Expr dataBlock,
        Expr? copyCount,
        Expr? copyToKey,
        Expr? copyFromKey,
        IReadOnlyList<string> notes)
    {
        MainCount = mainCount;
        MainKey = mainKey;
        TextSize = textSize;
        DataBlock = dataBlock;
        CopyCount = copyCount;
        CopyToKey = copyToKey;
        CopyFromKey = copyFromKey;
        Notes = notes;
    }

    public Expr MainCount { get; }
    public Expr MainKey { get; }
    public Expr TextSize { get; }
    public Expr DataBlock { get; }

    public Expr? CopyCount { get; }
    public Expr? CopyToKey { get; }
    public Expr? CopyFromKey { get; }

    public bool HasCopies =>
        CopyCount != null &&
        CopyToKey != null &&
        CopyFromKey != null;

    public IReadOnlyList<string> Notes { get; }
}

public abstract class Expr
{
    internal Expr(int id, int bits)
    {
        Id = id;
        Bits = bits;
    }

    internal int Id { get; }
    public int Bits { get; }

    public ulong Get(EvalBag bag)
    {
        if (bag == null)
            throw new ArgumentNullException(nameof(bag));

        return GetCore(bag) & BitOps.Mask(Bits);
    }

    internal abstract ulong GetCore(EvalBag bag);
    internal abstract void Write(StringBuilder text, HashSet<int> seen);

    public override string ToString() =>
        ExprText.Format(this);
}
