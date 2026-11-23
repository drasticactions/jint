using Jint.Native;

namespace Jint.Runtime;

/// <summary>
/// What a string compilation compiles, as the host sees it before it is parsed: the arguments of <c>eval</c> or of a
/// <c>Function</c> constructor, as strings and as the values the script passed.
/// </summary>
/// <remarks>
/// https://tc39.es/proposal-dynamic-code-brand-checks/#sec-hostensurecancompilestrings — the host can tell a value that
/// stands for code (a TrustedScript) from a string, and the source that will be parsed from the parts of it.
/// </remarks>
public sealed class StringCompilation
{
    internal StringCompilation(StringCompilationKind kind, string[] parameterStrings, string bodyString, string codeString, JsValue[] parameterArgs, JsValue bodyArg, bool direct)
    {
        Kind = kind;
        ParameterStrings = parameterStrings;
        BodyString = bodyString;
        CodeString = codeString;
        ParameterArgs = parameterArgs;
        BodyArg = bodyArg;
        Direct = direct;
    }

    /// <summary>Whether <c>eval</c> or a <c>Function</c> constructor compiles.</summary>
    public StringCompilationKind Kind { get; }

    /// <summary>The parameters of a dynamic function, as strings; empty for <c>eval</c>.</summary>
    public IReadOnlyList<string> ParameterStrings { get; }

    /// <summary>The body of a dynamic function, or the source of <c>eval</c>, as a string.</summary>
    public string BodyString { get; }

    /// <summary>The whole source that is parsed: for a dynamic function, its <c>function anonymous(</c> form.</summary>
    public string CodeString { get; }

    /// <summary>The parameter values that the script passed to a <c>Function</c> constructor; empty for <c>eval</c>.</summary>
    public IReadOnlyList<JsValue> ParameterArgs { get; }

    /// <summary>The body or source value that the script passed, before it became a string.</summary>
    public JsValue BodyArg { get; }

    /// <summary>Whether a call to <c>eval</c> is a direct eval.</summary>
    public bool Direct { get; }
}

/// <summary>The kinds of string compilation.</summary>
public enum StringCompilationKind
{
    /// <summary><c>eval</c>, direct or indirect.</summary>
    Eval,

    /// <summary>A <c>Function</c>, <c>AsyncFunction</c>, <c>GeneratorFunction</c> or <c>AsyncGeneratorFunction</c> constructor.</summary>
    Function,
}
