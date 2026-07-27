using System.Text;

namespace TextmapParser;

internal static class ExprText
{
	private const int NodeLimit = 512;
	private const int CharLimit = 16_384;
	private const int DepthLimit = 96;
	private const int ChoiceLimit = 8;

	public static string Format(
		Expr expr,
		int nodeLimit = NodeLimit,
		int charLimit = CharLimit)
	{
		if (expr == null)
			return "<null>";

		var writer = new TextWriter(nodeLimit, charLimit);
		writer.Append(expr, 0);
		return writer.Done();
	}

	private sealed class TextWriter
	{
		private readonly int _nodeLimit;
		private readonly int _charLimit;
		private readonly StringBuilder _text = new StringBuilder();
		private readonly HashSet<int> _seen = new HashSet<int>();
		private int _nodeCount;
		private bool _cut;

		public TextWriter(int nodeLimit, int charLimit)
		{
			_nodeLimit = Math.Max(16, nodeLimit);
			_charLimit = Math.Max(256, charLimit);
		}

		public string Done()
		{
			if (_cut && _text.Length < _charLimit)
				_text.Append("…<truncated>");
			return _text.ToString();
		}

		public void Append(Expr expr, int level)
		{
			if (_cut)
				return;

			if (level > DepthLimit || ++_nodeCount > _nodeLimit)
			{
				Truncate();
				return;
			}

			if (!_seen.Add(expr.Id))
			{
				AddText("<cycle#");
				AddText(expr.Id.ToString());
				AddText(">");
				return;
			}

			switch (expr)
			{
				case ConstExpr constant:
					AddText("0x");
					AddText(constant.Value.ToString("X"));
					AddText(":");
					AddText(constant.Bits.ToString());
					break;

				case SlotExpr slot:
					AddText(slot.Slot.ToString());
					if (slot.Bits != 64)
					{
						AddText(":");
						AddText(slot.Bits.ToString());
					}
					break;

				case BadExpr unknown:
					AddText("unknown(");
					AddText(unknown.Why.Length <= 160
						? unknown.Why
						: unknown.Why.Substring(0, 160) + "…");
					AddText(")");
					break;

				case LoadExpr load:
					AddText("load");
					AddText(load.Bits.ToString());
					AddText("@0x");
					AddText(load.IP.ToString("X"));
					AddText("#");
					AddText(load.Order.ToString());
					break;

				case CallExpr call:
					AddText("call_result@0x");
					AddText(call.IP.ToString("X"));
					break;

				case UnaryExpr unary:
					AddText(unary.Op.ToString());
					AddText("(");
					Append(unary.Value, level + 1);
					AddText(")");
					break;

				case BinaryExpr binary:
					AddText("(");
					Append(binary.Left, level + 1);
					AddText(" ");
					AddText(binary.Op.ToString());
					AddText(" ");
					Append(binary.Right, level + 1);
					AddText(")");
					break;

				case SliceExpr extract:
					AddText("extract(");
					Append(extract.Value, level + 1);
					AddText(",");
					AddText(extract.Shift.ToString());
					AddText(",");
					AddText(extract.Bits.ToString());
					AddText(")");
					break;

				case MergeExpr insert:
					AddText("insert(");
					Append(insert.Original, level + 1);
					AddText(",");
					Append(insert.Inserted, level + 1);
					AddText(",");
					AddText(insert.Shift.ToString());
					AddText(",");
					AddText(insert.PutBits.ToString());
					AddText(")");
					break;

				case PhiExpr phi:
					AddText(phi.Name);
					AddText("{");
					int count = Math.Min(phi.Choices.Count, ChoiceLimit);
					for (int i = 0; i < count; i++)
					{
						if (i != 0)
							AddText(", ");
						Append(phi.Choices[i], level + 1);
					}
					if (phi.Choices.Count > count)
					{
						AddText(", … +");
						AddText((phi.Choices.Count - count).ToString());
						AddText(" inputs");
					}
					AddText("}");
					break;

				default:
					AddText("<expr#");
					AddText(expr.Id.ToString());
					AddText(">");
					break;
			}

			_seen.Remove(expr.Id);
		}

		private void AddText(string value)
		{
			if (_cut)
				return;

			int remaining = _charLimit - _text.Length;
			if (remaining <= 0)
			{
				Truncate();
				return;
			}

			if (value.Length <= remaining)
			{
				_text.Append(value);
				return;
			}

			_text.Append(value, 0, remaining);
			Truncate();
		}

		private void Truncate()
		{
			_cut = true;
		}
	}
}
