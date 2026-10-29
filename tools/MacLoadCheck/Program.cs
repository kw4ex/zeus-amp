// SPDX-License-Identifier: GPL-2.0-or-later
//
// CI check for the macOS package: load the unzipped feature into its own
// AssemblyLoadContext (the way a plugin host does, with no special native
// library handling), then prove the bundled Mac serial implementation and its
// native helper load. Exit 0 = good. Never opens a real device.

using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.WriteLine("usage: MacLoadCheck <unzipped package folder>");
    return 2;
}
var dir = Path.GetFullPath(args[0]);
var failures = 0;
void Report(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
    if (!ok) failures++;
}

var alc = new PluginLoadContext(dir);
var asm = alc.LoadFromAssemblyPath(Path.Combine(dir, "Zeus.Community.Kpa1500.dll"));
Report("feature assembly loads", asm is not null);

var pluginType = asm!.GetType("Zeus.Community.Kpa1500.Kpa1500Plugin");
Report("plugin entry type found", pluginType is not null);
var instance = pluginType is null ? null : Activator.CreateInstance(pluginType);
Report("plugin implements IZeusPlugin", instance is Zeus.Plugins.Contracts.IZeusPlugin);

var transports = asm.GetType("Zeus.Community.Kpa1500.Transports", throwOnError: true)!;
var names = (string[])transports.GetMethod("SerialPortNames")!.Invoke(null, null)!;
Report("serial port list", true, names.Length == 0 ? "(no ports on this machine)" : string.Join(", ", names));

// Opening a port that does not exist must fail with an ordinary I/O error.
// A DllNotFoundException / PlatformNotSupportedException would mean the Mac
// serial implementation or its native library did not load.
const string missing = "/dev/cu.kpa1500-loadcheck-missing";
try
{
    transports.GetMethod("OpenSerial")!.Invoke(null, new object[] { missing, 38400 });
    Report("open missing port fails cleanly", false, "unexpectedly opened");
}
catch (TargetInvocationException tie)
{
    var inner = tie.InnerException!;
    var chain = inner.GetType().Name + (inner.InnerException is { } ii ? " <- " + ii.GetType().Name : "");
    var bad = Contains<DllNotFoundException>(inner) || Contains<EntryPointNotFoundException>(inner) ||
              Contains<TypeInitializationException>(inner) || Contains<BadImageFormatException>(inner) ||
              inner.Message.Contains("not available in this build", StringComparison.Ordinal);
    Report("Mac serial library and native helper load", !bad, chain + ": " + inner.Message);
}

var ports = alc.Assemblies.FirstOrDefault(a => a.GetName().Name == "System.IO.Ports");
Report("System.IO.Ports came from the package", ports is not null &&
    Path.GetDirectoryName(ports.Location) == dir, ports?.Location ?? "not loaded");

Console.WriteLine(failures == 0 ? "MAC LOAD CHECK PASSED" : $"MAC LOAD CHECK FAILED ({failures})");
return failures == 0 ? 0 : 1;

static bool Contains<T>(Exception? e) where T : Exception
{
    for (; e is not null; e = e.InnerException) if (e is T) return true;
    return false;
}

sealed class PluginLoadContext(string dir) : AssemblyLoadContext("kpa1500-check", isCollectible: false)
{
    protected override Assembly? Load(AssemblyName name)
    {
        var path = Path.Combine(dir, name.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null; // else: the shared/host copy
    }
}
