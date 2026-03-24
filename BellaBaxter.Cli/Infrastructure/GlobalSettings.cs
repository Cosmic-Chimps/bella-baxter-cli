namespace BellaCli.Infrastructure;

public enum OutputMode
{
    Human,
    Json
}

public class GlobalSettings
{
    public OutputMode OutputMode { get; set; } = OutputMode.Human;

    public bool IsJsonMode => OutputMode == OutputMode.Json;
    public bool IsHumanMode => OutputMode == OutputMode.Human;
}
