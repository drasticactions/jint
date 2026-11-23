using System.Diagnostics;
using System.Runtime.CompilerServices;
using Jint.Collections;
using Jint.Native.AsyncGenerator;
using Jint.Native.Function;
using Jint.Native.Generator;
using Jint.Runtime.Environments;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Runtime;

internal sealed class ExecutionContextStack
{
    private readonly RefStack<ExecutionContext> _stack;

    public ExecutionContextStack(int capacity)
    {
        _stack = new RefStack<ExecutionContext>(capacity);
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _stack._size;
    }

    public void ReplaceTopLexicalEnvironment(Environment newEnv)
    {
        var array = _stack._array;
        var size = _stack._size;
        ref var executionContext = ref array[size - 1];
        // In-place single-field write instead of rebuilding the whole 10-field struct; the
        // instance lives in a mutable array slot, so casting away the field's readonly is safe.
        Unsafe.AsRef(in executionContext.LexicalEnvironment) = newEnv;
    }

    public void ReplaceTopVariableEnvironment(Environment newEnv)
    {
        var array = _stack._array;
        var size = _stack._size;
        ref var executionContext = ref array[size - 1];
        Unsafe.AsRef(in executionContext.VariableEnvironment) = newEnv;
    }

    public void ReplaceTopPrivateEnvironment(PrivateEnvironment? newEnv)
    {
        var array = _stack._array;
        var size = _stack._size;
        ref var executionContext = ref array[size - 1];
        executionContext = executionContext.UpdatePrivateEnvironment(newEnv);
    }

    public bool ReplaceTopStrict(bool strict)
    {
        var array = _stack._array;
        var size = _stack._size;
        ref var executionContext = ref array[size - 1];
        // In-place single-field write instead of rebuilding the whole struct; the instance lives
        // in a mutable array slot, so casting away the field's readonly is safe. Used for the one
        // case where strictness changes without pushing a new context (a class body evaluated in
        // the enclosing context); returns the previous value so the caller can restore it.
        var previous = executionContext.Strict;
        Unsafe.AsRef(in executionContext.Strict) = strict;
        return previous;
    }

    public ref readonly ExecutionContext ReplaceTopGenerator(GeneratorInstance newEnv)
    {
        var array = _stack._array;
        var size = _stack._size;
        ref var executionContext = ref array[size - 1];
        executionContext = executionContext.UpdateGenerator(newEnv);
        return ref executionContext;
    }

    public ref readonly ExecutionContext ReplaceTopAsyncGenerator(AsyncGeneratorInstance newEnv)
    {
        var array = _stack._array;
        var size = _stack._size;
        ref var executionContext = ref array[size - 1];
        executionContext = executionContext.UpdateAsyncGenerator(newEnv);
        return ref executionContext;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly ExecutionContext Peek() => ref _stack.Peek();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly ExecutionContext Peek(int fromTop) => ref _stack.Peek(fromTop);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(in ExecutionContext context) => _stack.Push(in context);

    /// <summary>
    /// Pushes the context of a call of <paramref name="function"/>, whose lexical and variable environments are both
    /// <paramref name="environment"/>, writing the fields straight into the stack's slot.
    /// </summary>
    /// <remarks>
    /// A popped slot keeps its realm and script (see <see cref="Pop"/>) and nothing else, and the next call at the
    /// same depth usually runs in the same ones, so those two are compared before they are written: a compare costs
    /// less than the write barrier a reference store into the array pays. The fields a function call leaves null are
    /// null in a popped slot already.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly ExecutionContext PushFunctionContext(
        IScriptOrModule? scriptOrModule,
        Environment environment,
        PrivateEnvironment? privateEnvironment,
        Realm realm,
        Function function,
        bool strict)
    {
        var stack = _stack;
        if (stack._size == stack._array.Length)
        {
            stack.EnsureCapacity(stack._size + 1);
        }

        ref var slot = ref stack._array[stack._size++];
        Debug.Assert(slot.LexicalEnvironment is null && slot.Function is null && slot.Generator is null && slot.ParserOptions is null && slot.AsyncFunction is null && slot.AsyncGenerator is null && slot.PrivateEnvironment is null);

        if (!ReferenceEquals(slot.ScriptOrModule, scriptOrModule))
        {
            Unsafe.AsRef(in slot.ScriptOrModule) = scriptOrModule;
        }

        Unsafe.AsRef(in slot.LexicalEnvironment) = environment;
        Unsafe.AsRef(in slot.VariableEnvironment) = environment;
        if (privateEnvironment is not null)
        {
            Unsafe.AsRef(in slot.PrivateEnvironment) = privateEnvironment;
        }

        if (!ReferenceEquals(slot.Realm, realm))
        {
            Unsafe.AsRef(in slot.Realm) = realm;
        }

        Unsafe.AsRef(in slot.Function) = function;
        Unsafe.AsRef(in slot.Strict) = strict;

        return ref slot;
    }

    /// <summary>
    /// Pops the running context. Its slot is cleared except for the realm and the script, which live as long as the
    /// functions that run in them and which <see cref="PushFunctionContext"/> then often need not write again.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Pop()
    {
        var stack = _stack;
        if (stack._size == 0)
        {
            Throw.InvalidOperationException("stack is empty");
        }

        ref var slot = ref stack._array[--stack._size];

        Unsafe.AsRef(in slot.LexicalEnvironment) = null!;
        Unsafe.AsRef(in slot.VariableEnvironment) = null!;
        Unsafe.AsRef(in slot.PrivateEnvironment) = null;
        Unsafe.AsRef(in slot.Function) = null;
        Unsafe.AsRef(in slot.Generator) = null;
        Unsafe.AsRef(in slot.ParserOptions) = null;
        Unsafe.AsRef(in slot.AsyncFunction) = null;
        Unsafe.AsRef(in slot.AsyncGenerator) = null;
        Unsafe.AsRef(in slot.Strict) = false;
    }

    public IScriptOrModule? GetActiveScriptOrModule()
    {
        var array = _stack._array;
        var size = _stack._size;
        for (var i = size - 1; i > -1; --i)
        {
            ref readonly var context = ref array[i];
            if (context.ScriptOrModule is not null)
            {
                return context.ScriptOrModule;
            }
        }

        return null;
    }

    public ParserOptions? GetActiveParserOptions()
    {
        var array = _stack._array;
        var size = _stack._size;
        for (var i = size - 1; i > -1; --i)
        {
            ref readonly var context = ref array[i];
            if (context.ParserOptions is not null)
            {
                return context.ParserOptions;
            }
        }

        return null;
    }

}
