namespace Jint.Native;

/// <summary>
/// An object with a [[Call]] internal method. Public so that a host object that is not a function can have one (HTML's
/// <c>document.all</c>); such an object also calls <see cref="Object.ObjectInstance.DeclareCallable"/> from its constructor.
/// </summary>
public interface ICallable
{
    /// <summary>
    /// Runs the object's [[Call]] with <paramref name="thisObject"/> as the this value and <paramref name="arguments"/>
    /// as the arguments, and returns the call's result. A script error leaves as a <see cref="Runtime.JavaScriptException"/>.
    /// </summary>
    JsValue Call(JsValue thisObject, params JsCallArguments arguments);
}
