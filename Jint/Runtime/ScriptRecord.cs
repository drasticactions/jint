using Jint.Native.Function;

namespace Jint.Runtime;

internal sealed class ScriptRecord : IScriptOrModule
{
    private FunctionOrigin? _functionOrigin;

    public ScriptRecord(
        Realm realm,
        Script ecmaScriptCode,
        string? location,
        ParsingConstraints parsingConstraints,
        ParserOptions? parserOptions)
    {
        Realm = realm;
        EcmaScriptCode = ecmaScriptCode;
        Location = location;
        ParsingConstraints = parsingConstraints;
        ParserOptions = parserOptions;
    }

    public Realm Realm { get; }
    public Script EcmaScriptCode { get; }
    public string? Location { get; }
    public ParsingConstraints ParsingConstraints { get; }
    public ParserOptions? ParserOptions { get; }

    Program IScriptOrModule.Program => EcmaScriptCode;

    FunctionOrigin IScriptOrModule.GetFunctionOrigin(Realm realm)
    {
        if (!ReferenceEquals(realm, Realm))
        {
            return new FunctionOrigin(realm, this);
        }

        return _functionOrigin ??= new FunctionOrigin(realm, this);
    }
}
