namespace TextmapParser;

public sealed class ByteCursor
{
	public ByteCursor(byte[] data, int pos = 0, bool checkBounds = false)
	{
		Data = data ?? throw new ArgumentNullException(nameof(data));
		Pos = pos;
		CheckBounds = checkBounds;
	}

	public byte[] Data { get; }
	public int Pos { get; set; }
	public bool CheckBounds { get; set; }

	public void Check(int count)
	{
		if (count < 0 || Pos < 0 || Pos > Data.Length - count)
			throw new EndOfStreamException();
	}
}
