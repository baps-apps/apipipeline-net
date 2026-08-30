namespace ApiPipeline.NET.LoadTests;

internal sealed record LoadTestProfile
{
    private LoadTestProfile(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public bool UsesPipeline => this != Baseline;

    public bool UsesRedis => this == Redis;

    public static LoadTestProfile Baseline { get; } = new("baseline");

    public static LoadTestProfile Pipeline { get; } = new("pipeline");

    public static LoadTestProfile Redis { get; } = new("redis");

    public static LoadTestProfile Parse(string value) => value switch
    {
        "baseline" => Baseline,
        "pipeline" => Pipeline,
        "redis" => Redis,
        _ => throw new ArgumentException(
            $"Unknown load-test profile '{value}'. Expected baseline, pipeline, or redis.",
            nameof(value)),
    };

    public override string ToString() => Name;
}
