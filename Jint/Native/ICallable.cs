namespace Jint.Native;

/// <summary>
/// An object with a [[Call]] internal method. Public so that a host object that is not a function can have one (HTML's
/// <c>document.all</c>); such an object also calls <see cref="Object.ObjectInstance.DeclareCallable"/> from its constructor.
/// </summary>
public interface ICallable
{
    JsValue Call(JsValue thisObject, params JsCallArguments arguments);
}
