namespace TextmapParser;

public interface IMapDecoder
{
    bool Read(ByteCursor input, IDictionary<uint, string> map, DecodePlan plan);
}
