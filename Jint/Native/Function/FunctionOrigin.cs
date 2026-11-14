using Jint.Runtime;

namespace Jint.Native.Function;

/// <summary>
/// A function's <c>[[Realm]]</c> and <c>[[ScriptOrModule]]</c>, held together so that a function object carries one
/// reference for both. Every function a script or module creates in its own realm shares that script's one
/// instance; a function created where no script runs shares its realm's.
/// </summary>
internal sealed class FunctionOrigin
{
    internal readonly Realm Realm;
    internal readonly IScriptOrModule? ScriptOrModule;

    internal FunctionOrigin(Realm realm, IScriptOrModule? scriptOrModule)
    {
        Realm = realm;
        ScriptOrModule = scriptOrModule;
    }

    internal static FunctionOrigin Of(Realm realm, IScriptOrModule? scriptOrModule)
    {
        if (realm is null)
        {
            return new FunctionOrigin(realm!, scriptOrModule);
        }

        if (scriptOrModule is null)
        {
            return realm._scriptlessFunctionOrigin ??= new FunctionOrigin(realm, null);
        }

        return scriptOrModule.GetFunctionOrigin(realm);
    }
}
