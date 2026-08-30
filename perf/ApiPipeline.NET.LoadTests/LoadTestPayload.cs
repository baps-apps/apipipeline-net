using System.Text;

namespace ApiPipeline.NET.LoadTests;

internal static class LoadTestPayload
{
    public static readonly string Json = "{\"payload\":\"" + new string('x', 1010) + "\"}";

    static LoadTestPayload()
    {
        if (Encoding.UTF8.GetByteCount(Json) != 1024)
        {
            throw new InvalidOperationException("The load-test payload must be exactly 1 KB.");
        }
    }
}
