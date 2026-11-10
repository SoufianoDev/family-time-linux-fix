using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Injects the PropFix companion calls into Assembly-CSharp.
// usage: patchtool <pristine Assembly-CSharp.dll> <PropFix.dll> <out.dll> [managed dir]
class P
{
    static void Main(string[] a)
    {
        string target = a[0], modDll = a[1], outp = a[2];
        var res = new DefaultAssemblyResolver();
        res.AddSearchDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(target)));
        if (a.Length > 3) res.AddSearchDirectory(a[3]);
        var asm = AssemblyDefinition.ReadAssembly(target, new ReaderParameters { AssemblyResolver = res });
        var mod = asm.MainModule;
        var modAsm = AssemblyDefinition.ReadAssembly(modDll, new ReaderParameters { AssemblyResolver = res });
        var modType = modAsm.MainModule.GetType("PropFix")
            ?? throw new Exception("PropFix type missing - is the companion a PropFix build?");

        MethodReference ModMethod(string name) => mod.ImportReference(modType.Methods.First(x => x.Name == name));

        // 1) capture the map generation seed
        var pmm = mod.GetType("GAT.ProceduralMapManager") ?? throw new Exception("ProceduralMapManager missing");
        Inject(pmm.Methods.First(x => x.Name == "GenerateTrees"), ModMethod("Seed"), OpCodes.Ldarg_1);

        var pcm = mod.GetType("GAT.PropChunkManager") ?? throw new Exception("PropChunkManager missing");

        // 2) rebuild the atlas per foliage layer + CPU type counts
        Inject(pcm.Methods.First(x => x.Name == "LoadProceduralMap"), ModMethod("Rebuild"), OpCodes.Ldarg_1);

        // 3) keep the CPU type counts uploaded
        Inject(pcm.Methods.First(x => x.Name == "LateUpdate"), ModMethod("Tick"), OpCodes.Ldarg_0);

        asm.Write(outp);
        Console.WriteLine("written -> " + outp);
    }

    static void Inject(MethodDefinition method, MethodReference call, OpCode arg)
    {
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        il.InsertBefore(first, il.Create(arg));
        il.InsertBefore(first, il.Create(OpCodes.Call, call));
    }
}
