using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NoesisMcp.Engine;

/// <summary>
/// Turns a JSON object into a real CLR object graph so Noesis bindings like {Binding Score}
/// or {Binding Items} resolve in previews. Noesis binds through CLR properties, so each JSON
/// object becomes an emitted class with one public property per key.
/// </summary>
public static class SampleData
{
    static readonly ModuleBuilder Module = AssemblyBuilder
        .DefineDynamicAssembly(new AssemblyName("NoesisMcp.SampleData"), AssemblyBuilderAccess.Run)
        .DefineDynamicModule("SampleData");
    static readonly ConcurrentDictionary<string, Type> Cache = new();
    static int _counter;

    public static object? Build(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => BuildObject(o),
        JsonArray a => new ObservableCollection<object?>(a.Select(Build)),
        JsonValue v => Scalar(v),
        _ => null,
    };

    static object? Scalar(JsonValue v)
    {
        var el = v.GetValue<JsonElement>();
        return el.ValueKind switch
        {
            JsonValueKind.String when el.GetString() == "$command" => new SampleCommand(),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when el.TryGetInt32(out var i) => i,
            JsonValueKind.Number => el.GetDouble(),
            _ => null,
        };
    }

    static object BuildObject(JsonObject o)
    {
        var values = o.Select(kv => (Name: Sanitize(kv.Key), Value: Build(kv.Value))).ToList();
        var shape = string.Join("|", values.Select(p => p.Name + ":" + (p.Value?.GetType().FullName ?? "object")));
        var type = Cache.GetOrAdd(shape, _ => Emit(values.Select(p => (p.Name, p.Value?.GetType() ?? typeof(object))).ToList()));
        var instance = Activator.CreateInstance(type)!;
        foreach (var (name, value) in values) type.GetProperty(name)!.SetValue(instance, value);
        return instance;
    }

    static Type Emit(List<(string Name, Type Type)> props)
    {
        var tb = Module.DefineType($"Sample{Interlocked.Increment(ref _counter)}", TypeAttributes.Public | TypeAttributes.Class);
        foreach (var (name, type) in props)
        {
            var field = tb.DefineField("_" + name, type, FieldAttributes.Private);
            var prop = tb.DefineProperty(name, PropertyAttributes.None, type, null);
            const MethodAttributes attrs = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;

            var get = tb.DefineMethod("get_" + name, attrs, type, Type.EmptyTypes);
            var il = get.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);

            var set = tb.DefineMethod("set_" + name, attrs, null, new[] { type });
            il = set.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);

            prop.SetGetMethod(get);
            prop.SetSetMethod(set);
        }
        return tb.CreateType();
    }

    /// <summary>What "$command" in sample_data becomes: a real ICommand that does nothing.</summary>
    public sealed class SampleCommand : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }

    static string Sanitize(string key)
    {
        var chars = key.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray();
        var s = new string(chars);
        if (s.Length == 0 || char.IsDigit(s[0])) s = "_" + s;
        return s;
    }
}
