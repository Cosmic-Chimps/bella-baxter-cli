namespace BellaCli.Services;

/// <summary>Why a secret name is reserved: the part of a child process it would take control of.</summary>
public enum ReservedEnvironmentNameClass
{
    /// <summary>The dynamic loader (<c>LD_*</c>, <c>DYLD_*</c>, glibc's module path).</summary>
    DynamicLoader,

    /// <summary>A shell's startup files and exported functions.</summary>
    ShellStartup,

    /// <summary>An interpreter's or runtime's options, module search path or injected profiler.</summary>
    InterpreterOptions,

    /// <summary>Where the system looks for executables.</summary>
    ExecutableSearchPath,
}

/// <summary>
/// Issue #1049 — the CLI's copy of the reserved environment-variable names: what <c>bella run</c> withholds from the
/// child it starts unless the operator passes <c>--allow-reserved-env</c>.
/// </summary>
/// <remarks>
/// <para>The API refuses these names on every write (<c>BellaBaxter.Features.Secrets.ReservedEnvironmentNames</c>);
/// this copy exists for secrets that were stored before that rule, and because the CLI builds in the public
/// <c>bella-baxter-cli</c> repository, where the API does not exist. <c>ReservedEnvironmentNamesAgreeTests</c> holds
/// the two tables equal in the monorepo; change one and that test fails until the other agrees.</para>
/// </remarks>
public static class ReservedEnvironmentNames
{
    /// <summary>Exact names, compared case-insensitively.</summary>
    public static readonly IReadOnlyDictionary<string, ReservedEnvironmentNameClass> Exact =
        new Dictionary<string, ReservedEnvironmentNameClass>(StringComparer.OrdinalIgnoreCase)
        {
            ["GCONV_PATH"] = ReservedEnvironmentNameClass.DynamicLoader,

            ["BASH_ENV"] = ReservedEnvironmentNameClass.ShellStartup,
            ["ZDOTDIR"] = ReservedEnvironmentNameClass.ShellStartup,
            ["PROMPT_COMMAND"] = ReservedEnvironmentNameClass.ShellStartup,
            ["SHELLOPTS"] = ReservedEnvironmentNameClass.ShellStartup,
            ["BASHOPTS"] = ReservedEnvironmentNameClass.ShellStartup,

            ["NODE_OPTIONS"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["NODE_PATH"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["PYTHONSTARTUP"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["PYTHONPATH"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["PYTHONHOME"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["PERL5OPT"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["PERL5LIB"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["PERLLIB"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["PERL5DB"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["RUBYOPT"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["RUBYLIB"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["JAVA_TOOL_OPTIONS"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["_JAVA_OPTIONS"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["JDK_JAVA_OPTIONS"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["CLASSPATH"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["DOTNET_STARTUP_HOOKS"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["DOTNET_ADDITIONAL_DEPS"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["CORECLR_ENABLE_PROFILING"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["COR_ENABLE_PROFILING"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["PHPRC"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["PHP_INI_SCAN_DIR"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["LUA_INIT"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["LUA_PATH"] = ReservedEnvironmentNameClass.InterpreterOptions,
            ["LUA_CPATH"] = ReservedEnvironmentNameClass.InterpreterOptions,

            ["PATH"] = ReservedEnvironmentNameClass.ExecutableSearchPath,
            ["PATHEXT"] = ReservedEnvironmentNameClass.ExecutableSearchPath,
        };

    /// <summary>Name prefixes, compared case-insensitively. Each family is wholly code-loading.</summary>
    public static readonly IReadOnlyList<(string Prefix, ReservedEnvironmentNameClass Class)> Prefixes =
    [
        ("LD_", ReservedEnvironmentNameClass.DynamicLoader),
        ("DYLD_", ReservedEnvironmentNameClass.DynamicLoader),
        ("BASH_FUNC_", ReservedEnvironmentNameClass.ShellStartup),
        ("CORECLR_PROFILER", ReservedEnvironmentNameClass.InterpreterOptions),
        ("COR_PROFILER", ReservedEnvironmentNameClass.InterpreterOptions),
    ];

    /// <summary>The class a name belongs to, or <c>null</c> for an ordinary name (including a blank one).</summary>
    public static ReservedEnvironmentNameClass? Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        if (Exact.TryGetValue(trimmed, out var exact)) return exact;
        foreach (var (prefix, @class) in Prefixes)
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return @class;
        return null;
    }

    /// <summary>What a name of this class would control in a process that receives it.</summary>
    public static string Describe(ReservedEnvironmentNameClass @class) => @class switch
    {
        ReservedEnvironmentNameClass.DynamicLoader => "configures the dynamic loader",
        ReservedEnvironmentNameClass.ShellStartup => "runs code when a shell starts",
        ReservedEnvironmentNameClass.InterpreterOptions => "sets an interpreter's or runtime's options or module search path",
        ReservedEnvironmentNameClass.ExecutableSearchPath => "decides where executables are found",
        _ => "controls how a process loads code",
    };
}
