#!/usr/bin/env python3
import os
import sys
import shutil
import subprocess
import tempfile
import platform
import argparse

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LIB_DIR = os.path.join(ROOT, "lib")
SRC_DIR = os.path.join(ROOT, "src")

def _find_dotnet():
    """Find .NET SDK binary."""
    candidates = [
        os.path.expanduser("~/.dotnet-arm64/dotnet"),
        os.path.expanduser("~/.dotnet/dotnet"),
        "dotnet",
    ]
    if os.name == 'nt':
        candidates.append("dotnet.exe")
        
    for p in candidates:
        try:
            # On Windows, 'where' can help find the full path if only 'dotnet' is known
            if p == "dotnet" and os.name == 'nt':
                w = subprocess.run(["where", "dotnet"], capture_output=True, text=True, shell=True)
                if w.returncode == 0:
                    p = w.stdout.splitlines()[0].strip()

            r = subprocess.run([p, "--version"], capture_output=True, text=True, timeout=5, shell=(os.name == 'nt' and not os.path.isabs(p)))
            if r.returncode == 0:
                return os.path.abspath(p)
        except (FileNotFoundError, subprocess.TimeoutExpired, PermissionError):
            continue
    return None

DOTNET = _find_dotnet()

def _find_game_dir():
    """Auto-detect STS2 Steam install directory."""
    system = platform.system()
    candidates = []
    if system == "Darwin":
        base = os.path.expanduser("~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources")
        candidates = [
            os.path.join(base, "data_sts2_macos_arm64"),
            os.path.join(base, "data_sts2_macos_x86_64"),
        ]
    elif system == "Linux":
        for steam in ["~/.steam/steam", "~/.local/share/Steam"]:
            candidates.append(os.path.expanduser(f"{steam}/steamapps/common/Slay the Spire 2"))
        candidates.append("/media/plp/Games/SteamLibrary/steamapps/common/Slay the Spire 2")
    elif system == "Windows":
        candidates = [
            r"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
            r"C:\Program Files\Steam\steamapps\common\Slay the Spire 2",
        ]

    for d in candidates:
        if os.path.isdir(d):
            return d
    return None

def copy_dlls(game_dir):
    """Copy required DLLs from game directory to lib/."""
    os.makedirs(LIB_DIR, exist_ok=True)
    dlls = [
        "sts2.dll", "SmartFormat.dll", "SmartFormat.ZString.dll",
        "Sentry.dll", "MonoMod.Backports.dll",
        "MonoMod.ILHelpers.dll", "0Harmony.dll", "System.IO.Hashing.dll",
    ]
    
    print(f"\n📦 Copying DLLs to lib/...")
    for dll in dlls:
        src = os.path.join(game_dir, dll)
        dst = os.path.join(LIB_DIR, dll)
        
        if os.path.isfile(src):
            shutil.copy2(src, dst)
            print(f"  ✓ {dll}")
        else:
            # Search subdirectories (like SteamLibrary/steamapps/common/Slay the Spire 2/...)
            found = False
            for root_d, _, files in os.walk(game_dir):
                if dll in files:
                    shutil.copy2(os.path.join(root_d, dll), dst)
                    print(f"  ✓ {dll} (found in {os.path.relpath(root_d, game_dir)})")
                    found = True
                    break
            if not found:
                print(f"  ✗ {dll} not found")

    # Backup original sts2.dll
    sts2 = os.path.join(LIB_DIR, "sts2.dll")
    backup = os.path.join(LIB_DIR, "sts2.dll.original")
    if os.path.isfile(sts2) and not os.path.isfile(backup):
        shutil.copy2(sts2, backup)
        print("  ✓ Backed up sts2.dll.original")

def build_stubs():
    """Build the stub projects and copy DLLs."""
    if not DOTNET:
        print("❌ .NET SDK not found. Cannot build stubs.")
        return False

    print("\n🏗️ Building stubs...")
    projects = [
        os.path.join(SRC_DIR, "GodotStubs", "GodotStubs.csproj"),
        os.path.join(SRC_DIR, "SteamworksStubs", "SteamworksStubs.csproj"),
    ]
    
    for proj in projects:
        proj = os.path.abspath(proj)
        print(f"  Building {os.path.basename(proj)}...")
        if not os.path.exists(proj):
            print(f"  ❌ File not found: {proj}")
            return False
            
        r = subprocess.run([DOTNET, "build", proj, "-c", "Release"], 
                           capture_output=True, text=True)
        if r.returncode != 0:
            print(f"  ❌ Build failed for {proj}")
            if r.stdout:
                print("--- STDOUT ---")
                print(r.stdout)
            if r.stderr:
                print("--- STDERR ---")
                print(r.stderr)
            return False

    # Copy output DLLs to lib/
    shutil.copy2(os.path.join(SRC_DIR, "GodotStubs", "bin", "Release", "net9.0", "GodotSharp.dll"), 
                 os.path.join(LIB_DIR, "GodotSharp.dll"))
    shutil.copy2(os.path.join(SRC_DIR, "SteamworksStubs", "bin", "Release", "net9.0", "Steamworks.NET.dll"), 
                 os.path.join(LIB_DIR, "Steamworks.NET.dll"))
    print("  ✓ Stubs ready")
    return True

PATCH_PROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Mono.Cecil" Version="0.11.6" />
  </ItemGroup>
</Project>
"""

PATCH_CS = r"""using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

var dllPath = args[0];
Console.WriteLine($"Patching {dllPath}...");

var resolver = new DefaultAssemblyResolver();
var libDir = Path.GetDirectoryName(dllPath)!;
resolver.AddSearchDirectory(libDir);
// Also search for GodotSharp.dll in the GodotStubs output (fallback)
var stubsDir = Path.Combine(Path.GetDirectoryName(libDir)!, "src", "GodotStubs", "bin", "Debug", "net9.0");
if (Directory.Exists(stubsDir)) resolver.AddSearchDirectory(stubsDir);
// Add .NET runtime directory for system assemblies
var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
if (runtimeDir != null) resolver.AddSearchDirectory(runtimeDir);
var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters {
    AssemblyResolver = resolver,
    ReadingMode = ReadingMode.Deferred
});

// Helper to safely import methods from other assemblies
MethodReference ImportMethod(Type type, string methodName, params Type[] paramTypes) {
    var method = type.GetMethod(methodName, paramTypes);
    return module.ImportReference(method);
}

int patches = 0;

// Patch 1: Task.Yield() — make YieldAwaitable.YieldAwaiter.IsCompleted return true
// This prevents async deadlocks in headless mode
foreach (var type in module.Types)
{
    foreach (var nested in type.NestedTypes)
    {
        foreach (var nested2 in nested.NestedTypes)
        {
            if (nested2.Name.Contains("YieldAwaiter") || nested2.Name == "<>c")
            {
                foreach (var method in nested2.Methods)
                {
                    if (method.Name == "get_IsCompleted" && method.Body != null)
                    {
                        var il = method.Body.GetILProcessor();
                        il.Body.Instructions.Clear();
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Ret);
                        patches++;
                        Console.WriteLine($"  Patched {type.Name}.{nested.Name}.{nested2.Name}.IsCompleted");
                    }
                }
            }
        }
    }
}

// Patch 2: WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction → return Task.CompletedTask
foreach (var type in module.Types)
{
    foreach (var method in type.Methods)
    {
        if (method.Name == "WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction" && method.Body != null)
        {
            var il = method.Body.GetILProcessor();
            il.Body.Instructions.Clear();
            // return Task.CompletedTask
            var taskType = module.ImportReference(typeof(System.Threading.Tasks.Task));
            var completedProp = module.ImportReference(
                typeof(System.Threading.Tasks.Task).GetProperty("CompletedTask")!.GetGetMethod()!);
            il.Emit(OpCodes.Call, completedProp);
            il.Emit(OpCodes.Ret);
            patches++;
            Console.WriteLine($"  Patched {type.Name}.{method.Name} → Task.CompletedTask");
        }
}
}

// Patch 3: Fix Steam API TypeLoadException
var steamInit = module.GetType("MegaCrit.Sts2.Core.Platform.Steam.SteamInitializer");
if (steamInit != null) {
    var prop = steamInit.Properties.FirstOrDefault(p => p.Name == "InitResult");
    if (prop != null) steamInit.Properties.Remove(prop);
    var field = steamInit.Fields.FirstOrDefault(f => f.Name.Contains("InitResult"));
    if (field != null) steamInit.Fields.Remove(field);
    var getter = steamInit.Methods.FirstOrDefault(m => m.Name == "get_InitResult");
    if (getter != null) steamInit.Methods.Remove(getter);
    var setter = steamInit.Methods.FirstOrDefault(m => m.Name == "set_InitResult");
    if (setter != null) steamInit.Methods.Remove(setter);
    var initInt = steamInit.Methods.FirstOrDefault(m => m.Name == "InitializeInternal");
    if (initInt != null && initInt.Body != null) {
        initInt.Body.Instructions.Clear();
        initInt.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        initInt.Body.ExceptionHandlers.Clear();
        initInt.Body.Variables.Clear();
    }
    patches++;
    Console.WriteLine("  Patched SteamInitializer");
}
var nGame = module.GetType("MegaCrit.Sts2.Core.Nodes.NGame");
if (nGame != null) {
    var onSteam = nGame.Methods.FirstOrDefault(m => m.Name == "OnSteamNoLongerRunning");
    if (onSteam != null && onSteam.Body != null) {
        onSteam.Body.Instructions.Clear();
        onSteam.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        onSteam.Body.ExceptionHandlers.Clear();
        onSteam.Body.Variables.Clear();
    }
}

// Patch 4: Fix LocTable.GetRawText to handle missing keys without throwing
var locTable = module.GetType("MegaCrit.Sts2.Core.Localization.LocTable");
if (locTable != null) {
    var getRawText = locTable.Methods.FirstOrDefault(m => m.Name == "GetRawText" && m.Parameters.Count == 1);
    if (getRawText != null && getRawText.Body != null) {
        var il = getRawText.Body.GetILProcessor();
        il.Body.Instructions.Clear();
        il.Body.ExceptionHandlers.Clear();
        il.Body.Variables.Clear();

        var resultVar = new VariableDefinition(module.TypeSystem.String);
        il.Body.Variables.Add(resultVar);

        var fieldDict = locTable.Fields.FirstOrDefault(f => f.Name == "_translations");
        // Import TryGetValue directly from the BCL to avoid scope issues
        var tryGetValue = ImportMethod(typeof(Dictionary<string, string>), "TryGetValue", typeof(string), typeof(string).MakeByRefType());

        var insRet = Instruction.Create(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fieldDict);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, resultVar);
        il.Emit(OpCodes.Callvirt, tryGetValue);
        il.Emit(OpCodes.Brfalse, insRet);
        il.Emit(OpCodes.Ldloc, resultVar);
        il.Emit(OpCodes.Ret);
        il.Append(insRet);
        il.Append(Instruction.Create(OpCodes.Ret));
        patches++;
        Console.WriteLine("  Patched LocTable.GetRawText");
    }
}

// Patch 5: Cmd.Wait → Task.CompletedTask
var cmdType = module.GetType("MegaCrit.Sts2.Core.Commands.Cmd");
if (cmdType != null) {
    var taskType = module.ImportReference(typeof(System.Threading.Tasks.Task));
    var completedProp = module.ImportReference(
        typeof(System.Threading.Tasks.Task).GetProperty("CompletedTask")!.GetGetMethod()!);

    foreach (var method in cmdType.Methods) {
        if (method.Name == "Wait" && method.Body != null) {
            var il = method.Body.GetILProcessor();
            il.Body.Instructions.Clear();
            il.Emit(OpCodes.Call, completedProp);
            il.Emit(OpCodes.Ret);
            patches++;
            Console.WriteLine($"  Patched Cmd.{method.Name}");
        }
    }
}

Console.WriteLine($"Applied {patches} patches");
var outPath = dllPath + ".patched";
module.Write(outPath);
module.Dispose();
File.Delete(dllPath);
File.Move(outPath, dllPath);
Console.WriteLine("Done!");
"""

def apply_patches():
    """Apply IL patches to sts2.dll."""
    if not DOTNET:
        return False
    
    sts2_dll = os.path.join(LIB_DIR, "sts2.dll")
    if not os.path.isfile(sts2_dll):
        print("❌ sts2.dll not found in lib/")
        return False

    print("\n🔨 Applying IL patches to sts2.dll...")
    
    with tempfile.TemporaryDirectory() as tmpdir:
        with open(os.path.join(tmpdir, "Patcher.csproj"), "w", encoding='utf-8') as f:
            f.write(PATCH_PROJ)
        with open(os.path.join(tmpdir, "Program.cs"), "w", encoding='utf-8') as f:
            f.write(PATCH_CS)
        
        # Run patcher
        r = subprocess.run([DOTNET, "run", "--", sts2_dll], 
                           cwd=tmpdir, capture_output=True, text=True)
        if r.returncode != 0:
            print("  ❌ Patching failed")
            print(r.stdout)
            print(r.stderr)
            return False
        print("  ✓ Patching complete")
    return True

def build_headless():
    """Build the final headless project."""
    if not DOTNET:
        return False
    
    print("\n🏗️ Building Sts2Headless...")
    proj = os.path.join(SRC_DIR, "Sts2Headless", "Sts2Headless.csproj")
    r = subprocess.run([DOTNET, "build", proj], 
                       capture_output=True, text=True)
    if r.returncode != 0:
        print("  ❌ Build failed")
        if r.stdout:
            print("--- STDOUT ---")
            print(r.stdout)
        if r.stderr:
            print("--- STDERR ---")
            print(r.stderr)
        return False
    print("  ✓ Build succeeded")
    return True

def main():
    parser = argparse.ArgumentParser(description="Setup sts2-cli environment")
    parser.add_argument("game_dir", nargs="?", help="Path to Slay the Spire 2 installation")
    args = parser.parse_args()

    print("🚀 Starting sts2-cli setup...")

    if not DOTNET:
        print("❌ .NET SDK not found. Please install .NET 9+ SDK.")
        sys.exit(1)

    game_dir = args.game_dir or _find_game_dir()
    if not game_dir:
        print("❌ Could not find Slay the Spire 2 installation.")
        print("   Please provide the path: python python/setup.py C:\\Path\\To\\Game")
        sys.exit(1)

    print(f"📁 Game directory: {game_dir}")

    copy_dlls(game_dir)
    if not build_stubs():
        sys.exit(1)
    if not apply_patches():
        sys.exit(1)
    if not build_headless():
        sys.exit(1)

    print("\n✅ Setup complete! You can now run: python python/play.py")

if __name__ == "__main__":
    main()
