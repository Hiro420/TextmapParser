using System.Text.Encodings.Web;
using System.Text.Json;

namespace TextmapParser;

public sealed class TextMapBatch
{
	private readonly AppOptions _options;
	private readonly CodeReader _codeReader;
	private readonly IPlanReader _planReader;
	private readonly IMapDecoder _mapReader;

	public TextMapBatch(
		AppOptions options,
		CodeReader codeReader,
		IPlanReader planReader,
		IMapDecoder mapReader)
	{
		_options = options;
		_codeReader = codeReader;
		_planReader = planReader;
		_mapReader = mapReader;
	}

	public int Run()
	{
		NativeModule module = NativeModule.Open(_options.ModulePath);
		IReadOnlyList<Iced.Intel.Instruction> code =
			_codeReader.Read(module, _options.DecodeMethodRva);
		DecodePlan plan = _planReader.Read(code);

		var medium = new Dictionary<uint, string>();
		var normal = new Dictionary<uint, string>();

		int next = ReadFolder("Medium", GetFolder("TextMap_Medium"), 0, medium, plan);
		if (next < 0)
			return 1;

		if (ReadFolder("Normal TextMap", GetFolder("TextMap"), next, normal, plan) < 0)
			return 1;

		Console.WriteLine($"Medium entries: {medium.Count}");
		Console.WriteLine($"Normal entries: {normal.Count}");
		Save("TextMap_Medium.json", medium);
		Save("TextMap.json", normal);
		return 0;
	}

	private string GetFolder(string name) =>
		Path.Combine(_options.DataRoot, name, "EN", "Hash");

	private int ReadFolder(
		string label,
		string folder,
		int start,
		IDictionary<uint, string> map,
		DecodePlan plan)
	{
		for (int i = start; i < _options.FileLimit; i++)
		{
			string file = Path.Combine(folder, $"{i}{_options.FileSuffix}");
			if (!File.Exists(file))
			{
				Console.WriteLine($"{label} ended at index {i - 1}.");
				return i;
			}

			Console.WriteLine($"Parsing {label} file: {i}{_options.FileSuffix}");
			if (!ReadFile(file, map, plan))
			{
				Console.WriteLine($"Error parsing {label} at index {i}. Stopping.");
				return -1;
			}
		}

		return _options.FileLimit;
	}

	private bool ReadFile(string file, IDictionary<uint, string> map, DecodePlan plan)
	{
		try
		{
			var input = new ByteCursor(File.ReadAllBytes(file));
			return _mapReader.Read(input, map, plan);
		}
		catch (Exception error)
		{
			Console.WriteLine($"Error parsing file: {file}");
			Console.WriteLine(error);
			return false;
		}
	}

	private static void Save(string path, IDictionary<uint, string> map)
	{
		var json = new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};
		File.WriteAllText(path, JsonSerializer.Serialize(map, json));
	}
}
