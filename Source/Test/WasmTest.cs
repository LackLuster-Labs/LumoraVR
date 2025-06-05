using Wasmtime;

namespace LumoraVR.Source.Test;

public class WasmTest
{
    public static void Test()
    {
        using var engine = new Engine();

        using var module = Module.FromText(
            engine,
            "hello",
            "(module (func $hello (import \"\" \"hello\")) (func (export \"run\") (call $hello)))"
        );

        using var linker = new Linker(engine);
        using var store = new Store(engine);

        linker.Define(
            "",
            "hello",
            Function.FromCallback(store, () => System.Console.WriteLine("Hello from C#!"))
        );

        var instance = linker.Instantiate(store, module);
        var run = instance.GetAction("run")!;
        run();
    }
}
