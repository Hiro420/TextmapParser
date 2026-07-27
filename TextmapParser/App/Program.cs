namespace TextmapParser;

public static class Program
{
    public static int Main(string[] args)
    {
        AppOptions options = AppOptions.Default;
        var app = new TextMapBatch(
            options,
            new CodeReader(),
            new PlanReader(),
            new MapDecoder());
        return app.Run();
    }
}
