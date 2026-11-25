using System.Runtime.CompilerServices;
using Jint.Native.Function;
using Jint.Runtime;
using Jint.Runtime.CallStack;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter.Expressions;

namespace Jint;

public partial class Engine
{
    // Kernel calls whose call stack frame and execution context are pushed only once something reads either stack:
    // each call's environment and the handler of its call, and how many of the innermost are not pushed yet.
    private FunctionEnvironment?[] _frameEnvironments = new FunctionEnvironment?[16];
    private JintExpression?[] _frameSites = new JintExpression?[16];
    private int _frameCount;
    internal int _framesNotPushed;

    // Whether a kernel call may push one frame in place of its call stack frame and execution context.
    internal readonly bool _pushesCallFrames;

    /// <summary>The call stack, with the frames of kernel calls pushed.</summary>
    internal JintCallStack CallStack
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_framesNotPushed != 0)
            {
                PushCallFrames();
            }

            return _callStack;
        }
    }

    /// <summary>How many frames the call stack has, counting those of kernel calls not pushed yet.</summary>
    internal int CallDepth => _callStack.Count + _framesNotPushed;

    /// <summary>The execution context stack, with the frames of kernel calls pushed.</summary>
    private ExecutionContextStack ExecutionContexts
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_framesNotPushed != 0)
            {
                PushCallFrames();
            }

            return _executionContexts;
        }
    }

    /// <summary><see cref="ExecutionContexts"/>, or null before the engine has one.</summary>
    private ExecutionContextStack? ExecutionContextsIfAny => _executionContexts is null ? null : ExecutionContexts;

    /// <summary>Whether a kernel call can push one frame now: no profiler records calls.</summary>
    internal bool CanPushCallFrame
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _pushesCallFrames && _callStack._profiler is null;
    }

    /// <summary>Pushes the frame of a kernel call of the function of <paramref name="environment"/> made at <paramref name="site"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PushCallFrame(FunctionEnvironment environment, JintExpression site)
    {
        var count = _frameCount;
        var environments = _frameEnvironments;
        if ((uint) count >= (uint) environments.Length)
        {
            GrowCallFrames();
            environments = _frameEnvironments;
        }

        environments[count] = environment;
        _frameSites[count] = site;
        _frameCount = count + 1;
        _framesNotPushed++;
    }

    /// <summary>Pops the innermost kernel call's frame, and its call stack frame and execution context where they were pushed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PopCallFrame()
    {
        var count = _frameCount - 1;
        _frameCount = count;
        _frameEnvironments[count] = null;
        _frameSites[count] = null;
        if (_framesNotPushed > 0)
        {
            _framesNotPushed--;
            return;
        }

        PopPushedCallFrame();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PopPushedCallFrame()
    {
        LeaveExecutionContext();
        _callStack.TryPopAndDiscard();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowCallFrames()
    {
        System.Array.Resize(ref _frameEnvironments, _frameEnvironments.Length * 2);
        System.Array.Resize(ref _frameSites, _frameSites.Length * 2);
    }

    /// <summary>Pushes the call stack frame and execution context of every kernel call that has not pushed them, outermost first.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PushCallFrames()
    {
        var count = _frameCount;
        var first = count - _framesNotPushed;
        _framesNotPushed = 0;
        for (var i = first; i < count; i++)
        {
            var environment = _frameEnvironments[i]!;
            var function = (ScriptFunction) environment._functionObject;
            _callStack.Push(function, _frameSites[i], in _executionContexts.Peek());
            var origin = function._functionOrigin;
            EnterFunctionExecutionContext(origin.ScriptOrModule, environment, function._privateEnvironment, origin.Realm, function, function._strict);
        }
    }
}
