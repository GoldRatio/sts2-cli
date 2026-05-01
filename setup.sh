#!/bin/bash
# setup.sh — Copy game DLLs from Steam installation to lib/
#
# Prerequisites:
#   - Slay the Spire 2 installed via Steam
#   - .NET 9+ SDK (ARM64 for Apple Silicon, x64 for Intel/Linux)
#
# Usage:
#   ./setup.sh                    # Auto-detect Steam path
#   ./setup.sh /path/to/game      # Manual game directory

set -e

# ── Locate game directory ──

GAME_DIR="$1"

if [ -z "$GAME_DIR" ]; then
    # Auto-detect based on platform
    case "$(uname -s)" in
        Darwin)
            GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
            if [ ! -d "$GAME_DIR" ]; then
                # Try x86_64
                GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_x86_64"
            fi
            ;;
        Linux)
            GAME_DIR="$HOME/.steam/steam/steamapps/common/Slay the Spire 2"
            if [ ! -d "$GAME_DIR" ]; then
                GAME_DIR="$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2"
            fi
            ;;
        MINGW*|MSYS*|CYGWIN*)
            GAME_DIR="C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2"
            ;;
    esac
fi

if [ ! -d "$GAME_DIR" ]; then
    echo "❌ Game directory not found: $GAME_DIR"
    echo ""
    echo "Usage: ./setup.sh /path/to/game/data"
    echo ""
    echo "On macOS, this is usually:"
    echo "  ~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
    exit 1
fi

echo "📁 Game directory: $GAME_DIR"

# ── Copy DLLs ──

mkdir -p lib

DLLS=(
    "sts2.dll"
    "SmartFormat.dll"
    "SmartFormat.ZString.dll"
    "Sentry.dll"
    "MonoMod.Backports.dll"
    "MonoMod.ILHelpers.dll"
    "0Harmony.dll"
    "System.IO.Hashing.dll"
)

echo ""
echo "📦 Copying DLLs to lib/..."
for dll in "${DLLS[@]}"; do
    src="$GAME_DIR/$dll"
    if [ -f "$src" ]; then
        cp "$src" "lib/$dll"
        echo "  ✓ $dll"
    else
        echo "  ✗ $dll not found at $src"
        # Try searching subdirectories
        found=$(find "$GAME_DIR" -name "$dll" -print -quit 2>/dev/null)
        if [ -n "$found" ]; then
            cp "$found" "lib/$dll"
            echo "    → found at $found"
        else
            echo "    ⚠ Skipped (may cause build errors)"
        fi
    fi
done

# Back up original sts2.dll
if [ -f "lib/sts2.dll" ] && [ ! -f "lib/sts2.dll.original" ]; then
    cp "lib/sts2.dll" "lib/sts2.dll.original"
    echo "  ✓ Backed up sts2.dll.original"
fi

# ── Detect .NET SDK ──

DOTNET=""
if [ -x "$HOME/.dotnet-arm64/dotnet" ]; then
    DOTNET="$HOME/.dotnet-arm64/dotnet"
elif command -v dotnet &>/dev/null; then
    DOTNET="dotnet"
fi

if [ -z "$DOTNET" ]; then
    echo ""
    echo "❌ .NET SDK not found."
    echo "   Install .NET 9+ from https://dotnet.microsoft.com/download"
    echo "   Or set DOTNET env var to your dotnet binary path."
    exit 1
fi

echo ""
echo "🔧 .NET SDK: $DOTNET ($($DOTNET --version))"

# ── Build Stubs ──

echo ""
echo "🏗️ Building stubs..."
$DOTNET build src/GodotStubs/GodotStubs.csproj -c Release > /dev/null
$DOTNET build src/SteamworksStubs/SteamworksStubs.csproj -c Release > /dev/null

cp src/GodotStubs/bin/Release/net9.0/GodotSharp.dll lib/
cp src/SteamworksStubs/bin/Release/net9.0/Steamworks.NET.dll lib/
echo "  ✓ GodotSharp.dll (stub)"
echo "  ✓ Steamworks.NET.dll (stub)"

# ── IL Patch sts2.dll ──

echo ""
echo "🔨 Applying IL patches to sts2.dll..."

# Create a temporary patching project
PATCH_DIR=$(mktemp -d)
cat > "$PATCH_DIR/Patcher.csproj" << 'PROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Mono.Cecil" Version="0.11.6" />
  </ItemGroup>
</Project>
PROJ

cat > "$PATCH_DIR/Program.cs" << 'CSHARP'
using System;
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
CSHARP

REPO_DIR="$(pwd)"
cd "$PATCH_DIR"
$DOTNET run -- "$REPO_DIR/lib/sts2.dll" 2>&1
cd "$REPO_DIR"
rm -rf "$PATCH_DIR"

# ── Build ──

echo ""
echo "🏗️ Building..."
$DOTNET build src/Sts2Headless/Sts2Headless.csproj 2>&1 | tail -5

echo ""
echo "✅ Setup complete!"
echo ""
echo "To play:"
echo "  python3 python/play.py"
echo ""
echo "To run batch games:"
echo "  python3 python/play_full_run.py 10"
