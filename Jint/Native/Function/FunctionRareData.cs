using Jint.Native.Object;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Environments;

namespace Jint.Native.Function;

/// <summary>
/// What only some functions carry: a method's home object, a class element's private environment, a class's
/// elements, a materialized restricted property and a constructor's shape feedback. Held apart from the function
/// object so that the closures a script makes by the thousand (a method a constructor defines for every object)
/// are not as large as a class constructor; a function gets one the first time it stores any of it.
/// </summary>
internal sealed class FunctionRareData
{
    internal JsValue HomeObject = JsValue.Undefined;
    internal PrivateEnvironment? PrivateEnvironment;

    internal JsValue? ClassFieldInitializerName;
    internal List<PrivateElement>? PrivateMethods;
    internal List<ClassFieldDefinition>? Fields;

    internal PropertyDescriptor? ArgumentsDescriptor;
    internal PropertyDescriptor? CallerDescriptor;

    internal byte CtorStaticEligibility;
    internal int CtorSampleCount;
    internal Shape? CtorEmptyShape;
    internal ObjectInstance? CtorEmptyShapeProto;
}
