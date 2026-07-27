namespace TextmapParser;

public sealed record AppOptions(
    string ModulePath,
    uint DecodeMethodRva,
    string DataRoot,
    int FileLimit = 2000,
    string FileSuffix = ".MiHoYoBinData")
{
    public static AppOptions Default => new(

        // Best attempt at finding the module path
        new List<string> { "YuanShen.exe", "GenshinImpact.exe" }
            .FirstOrDefault(f => File.Exists(f)) 
        ?? throw new FileNotFoundException("No valid module found."),

		// OSRELWin6.7.0 -> public static bool NOAOAPAGOHL(LINKFPODCFE JAPDAMAEONL, ref IDictionary<uint, string> POCLLJNFIKD) { }
		// This is a default one for OSRELWin6.7.0, but you can override with arguments if needed
		0xC1832E0,

		// Data/_ExcelBinOutput/TextMap/EN/Hash/{x}.MiHoYoBinData
		// Data/_ExcelBinOutput/TextMap_Medium/EN/Hash/{x}.MiHoYoBinData
		Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_ExcelBinOutput"));
}
