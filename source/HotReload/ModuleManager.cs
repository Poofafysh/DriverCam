using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HotReload
{
    /// <summary>
    /// Loads, reloads and unloads the hot module DLLs and drives their Update/OnGUI. Everything here runs on the Unity main
    /// thread except the FileSystemWatcher callbacks, which only record "this path changed" under a lock.
    /// No exception from a module (or from loading one) is allowed to escape into Unity.
    /// </summary>
    internal static class ModuleManager
    {
        private sealed class Slot
        {
            public IHotModule Module;
            public string Id;
            public HotContext Ctx;     // set when Load is about to be called; Unload is only called if this is set
            public bool Loaded;        // Load returned normally
            public bool Suspended;     // too many errors in a row; waits for the next reload
            public int UpdateErrors, GuiErrors;
        }

        private sealed class LoadedFile
        {
            public string Path;
            public string Name;                 // file name without .dll
            public byte[] Bytes;                // to skip reloads when the file didn't really change
            public Assembly Assembly;
            public HotLoadContext Context;      // null when loaded non-collectible (Individual)
            public readonly List<Slot> Slots = new();
        }

        /// <summary>One collectible load context per module version. Shared assemblies (game, BepInEx, Harmony, HotReload)
        /// resolve to the copies that are already loaded, so IHotModule is the same type on both sides.</summary>
        private sealed class HotLoadContext : AssemblyLoadContext
        {
            public HotLoadContext(string name) : base(name, isCollectible: true) { }
            protected override Assembly Load(AssemblyName name) => FindShared(name);
        }

        private enum Result { Ok, Failed, BadImage, RetryIndividual }

        private static readonly Dictionary<string, LoadedFile> Files = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<Assembly> IndividualHotAssemblies = new();            // non-collectible hot loads (kept forever anyway)
        private static readonly HashSet<string> IndividualOnly = new(StringComparer.OrdinalIgnoreCase); // runtime refused collectible for these
        private static readonly Dictionary<string, ManualLogSource> LogSources = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> Pending = new(StringComparer.OrdinalIgnoreCase);   // path -> last change (UTC), lock(Pending)
        private static readonly Dictionary<string, int> Retries = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Blocked = new(StringComparer.Ordinal);   // ids whose old Harmony patches couldn't be removed
        private const int MaxRetries = 25;
        private const int MaxErrorsInARow = 10;

        private static FileSystemWatcher _watcher;
        private static volatile bool _rescanAll;
        private static bool _started, _disabled;
        private static string _hotDir;
        private static int _generation, _contextCounter;

        private static string _toast;
        private static Color _toastColor;
        private static float _toastUntil, _toastStarted;
        private static bool _toastOk;

        private static ManualLogSource Log => Plugin.Log;

        internal static string HotDir
        {
            get
            {
                string f = Plugin.HotFolder != null ? Plugin.HotFolder.Value : null;
                if (string.IsNullOrWhiteSpace(f)) f = "hot";
                return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(f) ? f : System.IO.Path.Combine(Paths.BepInExRootPath, f));
            }
        }

        // ------------------------------------------------------------------ Unity callbacks

        internal static void Update()
        {
            if (!_started)
            {
                // First frame: the chainloader has finished, so "is this GUID already a normal plugin?" can be answered.
                _started = true;
                try { Start(); }
                catch (Exception e) { _disabled = true; Log.LogError($"HotReload could not start, hot modules disabled: {e}"); }
            }
            if (_disabled) return;

            try
            {
                CheckHotkey();
                ProcessPending();
            }
            catch (Exception e) { Log.LogError($"HotReload: {e}"); }

            foreach (var f in Files.Values)
            {
                foreach (var s in f.Slots)
                {
                    if (!s.Loaded || s.Suspended) continue;
                    try { s.Module.Update(); s.UpdateErrors = 0; }
                    catch (Exception e) { Fault(f, s, "Update", e, ref s.UpdateErrors); }
                }
            }
        }

        internal static void OnGUI()
        {
            if (_disabled) return;
            foreach (var f in Files.Values)
            {
                foreach (var s in f.Slots)
                {
                    if (!s.Loaded || s.Suspended) continue;
                    try { s.Module.OnGUI(); s.GuiErrors = 0; }
                    catch (Exception e) { Fault(f, s, "OnGUI", e, ref s.GuiErrors); }
                }
            }
            try { DrawToast(); }
            catch (Exception e) { _toast = null; Log.LogError($"HotReload toast: {e}"); }
        }

        private static void Fault(LoadedFile f, Slot s, string where, Exception e, ref int errorsInARow)
        {
            errorsInARow++;
            if (errorsInARow <= 3) Log.LogError($"{f.Name}.dll: {s.Id}.{where} threw: {e}");
            if (errorsInARow >= MaxErrorsInARow)
            {
                s.Suspended = true;
                Log.LogError($"{f.Name}.dll: {s.Id} suspended after {errorsInARow} errors in a row in {where}. Fix it and rebuild, or press {Plugin.ReloadKey.Value} to reload.");
                Toast($"{f.Name}: suspended after repeated errors in {where} (see LogOutput.log)", false);
            }
        }

        // ------------------------------------------------------------------ startup, watching, hotkey

        private static void Start()
        {
            _hotDir = HotDir;
            string plugins = WithSlash(System.IO.Path.GetFullPath(Paths.PluginPath));
            if (WithSlash(_hotDir).StartsWith(plugins, StringComparison.OrdinalIgnoreCase))
            {
                _disabled = true;
                Log.LogError($"HotFolder '{_hotDir}' is inside BepInEx/plugins, where BepInEx loads (and locks) every DLL at startup. " +
                             "Use a folder outside it (default: BepInEx/hot). Hot modules disabled.");
                return;
            }
            Directory.CreateDirectory(_hotDir);
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            if (Plugin.WatchFolder.Value) StartWatcher();

            var dlls = DllsInFolder();
            Log.LogInfo($"Watching {_hotDir} ({(Plugin.WatchFolder.Value ? "auto reload on change" : "auto reload off")}, " +
                        $"{Plugin.ReloadKey.Value} = reload all, LoadMode={Plugin.LoadMode.Value}). {dlls.Count} module DLL(s) found.");
            foreach (var path in dlls) ProcessFile(path, false);
        }

        private static void StartWatcher()
        {
            try
            {
                _watcher = new FileSystemWatcher(_hotDir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    InternalBufferSize = 64 * 1024,
                };
                _watcher.Changed += OnFsEvent;
                _watcher.Created += OnFsEvent;
                _watcher.Deleted += OnFsEvent;
                _watcher.Renamed += OnFsRenamed;
                _watcher.Error += OnFsError;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception e)
            {
                _watcher = null;
                Log.LogWarning($"Could not watch {_hotDir} ({e.Message}). Press {Plugin.ReloadKey.Value} to reload instead.");
            }
        }

        // FileSystemWatcher threads: record the change only. No Unity or module calls here.
        private static void OnFsEvent(object sender, FileSystemEventArgs e) => MarkChanged(e.FullPath);
        private static void OnFsRenamed(object sender, RenamedEventArgs e) { MarkChanged(e.OldFullPath); MarkChanged(e.FullPath); }
        private static void OnFsError(object sender, ErrorEventArgs e) => _rescanAll = true;

        private static void MarkChanged(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string ext = System.IO.Path.GetExtension(path);
            if (ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase)) path = System.IO.Path.ChangeExtension(path, ".dll");
            else if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) return;
            lock (Pending) Pending[path] = DateTime.UtcNow;
        }

        private static void CheckHotkey()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (!Enum.TryParse(Plugin.ReloadKey.Value, true, out Key key) || key == Key.None) key = Key.F11;
            var control = kb[key];
            if (control == null || !control.wasPressedThisFrame) return;

            lock (Pending) Pending.Clear();
            var dlls = DllsInFolder();
            Log.LogInfo($"{Plugin.ReloadKey.Value}: reloading {dlls.Count} hot module DLL(s) from {_hotDir}");
            foreach (var f in Files.Values.ToList())
                if (!dlls.Contains(f.Path, StringComparer.OrdinalIgnoreCase)) UnloadFile(f, "DLL no longer in the hot folder");
            foreach (var path in dlls) ProcessFile(path, true);
            if (dlls.Count == 0) Toast($"HotReload: no module DLLs in {_hotDir}", false);
        }

        private static void ProcessPending()
        {
            if (_rescanAll)
            {
                _rescanAll = false;
                Log.LogWarning("Folder watcher overflowed; rescanning the hot folder.");
                var now = DateTime.UtcNow;
                lock (Pending)
                {
                    foreach (var p in DllsInFolder()) Pending[p] = now;
                    foreach (var f in Files.Values) Pending[f.Path] = now;
                }
            }

            List<string> due = null;
            lock (Pending)
            {
                if (Pending.Count == 0) return;
                var now = DateTime.UtcNow;
                int debounce = Math.Max(50, Plugin.DebounceMs.Value);
                foreach (var kv in Pending)
                    if ((now - kv.Value).TotalMilliseconds >= debounce) (due ??= new List<string>()).Add(kv.Key);
                if (due == null) return;
                foreach (var p in due) Pending.Remove(p);
            }
            foreach (var path in due) ProcessFile(path, false);
        }

        private static void Retry(string path, string why)
        {
            Retries.TryGetValue(path, out int n);
            if (++n > MaxRetries)
            {
                Retries.Remove(path);
                Log.LogWarning($"Gave up on {System.IO.Path.GetFileName(path)}: {why}. Rebuild it or press {Plugin.ReloadKey.Value}.");
                Toast($"HotReload: could not read {System.IO.Path.GetFileName(path)} ({why})", false);
                return;
            }
            Retries[path] = n;
            lock (Pending) Pending[path] = DateTime.UtcNow;   // try again after the debounce
        }

        // ------------------------------------------------------------------ loading

        private static void ProcessFile(string path, bool force)
        {
            string fileName = System.IO.Path.GetFileName(path);
            if (IsIgnored(path)) return;

            if (!File.Exists(path))
            {
                Retries.Remove(path);
                if (Files.TryGetValue(path, out var gone))
                {
                    UnloadFile(gone, "DLL deleted");
                    Toast($"{gone.Name} unloaded (DLL removed)", true);
                }
                return;
            }

            byte[] bytes;
            try { bytes = ReadShared(path); }
            catch (IOException) { Retry(path, "the file is still being written"); return; }
            catch (UnauthorizedAccessException) { Retry(path, "access denied"); return; }
            if (bytes.Length < 512 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') { Retry(path, "not a complete DLL yet"); return; }

            if (!force && Files.TryGetValue(path, out var current) && current.Bytes.AsSpan().SequenceEqual(bytes))
            {
                Retries.Remove(path);
                return;   // same build written again
            }

            byte[] pdb = ReadPdb(path);
            bool collectible = string.Equals(Plugin.LoadMode.Value, "Collectible", StringComparison.OrdinalIgnoreCase) && !IndividualOnly.Contains(path);
            Result r;
            try { r = LoadVersion(path, bytes, pdb, collectible); }
            catch (Exception e) { Log.LogError($"{fileName}: unexpected error while loading: {e}"); r = Result.Failed; }

            if (r == Result.RetryIndividual)
            {
                IndividualOnly.Add(path);
                Log.LogWarning($"{fileName}: the runtime refused a collectible load; loading it non-collectible from now on (old versions stay in memory).");
                try { r = LoadVersion(path, bytes, pdb, false); }
                catch (Exception e) { Log.LogError($"{fileName}: unexpected error while loading: {e}"); r = Result.Failed; }
            }

            if (r == Result.BadImage) { Retry(path, "not a valid .NET assembly (yet)"); return; }
            Retries.Remove(path);
        }

        private static Result LoadVersion(string path, byte[] bytes, byte[] pdb, bool collectible)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            Files.TryGetValue(path, out var old);
            var sw = Stopwatch.StartNew();

            // 1. load the new assembly next to the old one (old keeps running if this fails)
            Assembly asm;
            HotLoadContext alc = null;
            try
            {
                if (collectible)
                {
                    alc = new HotLoadContext($"HotReload:{name}#{++_contextCounter}");
                    using var ms = new MemoryStream(bytes);
                    using var ps = pdb != null ? new MemoryStream(pdb) : null;
                    asm = alc.LoadFromStream(ms, ps);
                }
                else
                {
                    asm = pdb != null ? Assembly.Load(bytes, pdb) : Assembly.Load(bytes);
                    lock (IndividualHotAssemblies) IndividualHotAssemblies.Add(asm);
                }
            }
            catch (BadImageFormatException)
            {
                TryUnload(alc);
                return Result.BadImage;
            }
            catch (Exception e)
            {
                TryUnload(alc);
                if (collectible && IsCollectibleProblem(e)) return Result.RetryIndividual;
                LoadFailed(name, old, "could not be loaded", e);
                return Result.Failed;
            }

            // 2. find and create its IHotModule classes
            var slots = new List<Slot>();
            string problem;
            try { problem = CreateModules(asm, name, slots); }
            catch (Exception e) { problem = e.ToString(); }
            if (slots.Count == 0)
            {
                TryUnload(alc);
                if (collectible && problem != null && problem.IndexOf("collectible", StringComparison.OrdinalIgnoreCase) >= 0) return Result.RetryIndividual;
                LoadFailed(name, old, problem ?? "has no public class implementing HotReload.IHotModule with a parameterless constructor", null);
                return Result.Failed;
            }

            // 3. refuse duplicates: a normal plugin with the same GUID, another hot DLL with the same id
            foreach (var s in slots)
            {
                string why = null;
                if (Blocked.Contains(s.Id))
                    why = $"an earlier build of '{s.Id}' left Harmony patches that couldn't be removed. Restart the game to load it again.";
                else if (ChainloaderHas(s.Id, out string where))
                    why = $"'{s.Id}' is already loaded by BepInEx as a normal plugin ({where}). Both would run at once, so the hot module is NOT loaded. " +
                          "Remove that DLL from BepInEx/plugins (the normal build) and restart the game once to use hot mode.";
                else if (slots.Count(o => o.Id == s.Id) > 1)
                    why = $"two classes use the id '{s.Id}'.";
                else
                {
                    var other = Files.Values.FirstOrDefault(f => f != old && f.Slots.Any(o => o.Id == s.Id));
                    if (other != null) why = $"'{s.Id}' is already loaded from {other.Name}.dll.";
                }
                if (why == null) continue;
                TryUnload(alc);
                LoadFailed(name, old, why, null);
                return Result.Failed;
            }

            // 4. unload the old version, 5. load the new one (unless unloading it left patches behind)
            if (old != null) UnloadFile(old, "replaced by a new build");
            var blocked = slots.FirstOrDefault(s => Blocked.Contains(s.Id));
            if (blocked != null)
            {
                TryUnload(alc);
                LoadFailed(name, null, $"the previous build of '{blocked.Id}' left Harmony patches that couldn't be removed. Restart the game to load it again.", null);
                return Result.Failed;
            }

            var file = new LoadedFile { Path = path, Name = name, Bytes = bytes, Assembly = asm, Context = alc };
            Files[path] = file;
            var errors = new List<string>();
            foreach (var s in slots)
            {
                file.Slots.Add(s);
                try
                {
                    s.Ctx = MakeContext(s.Id, name, path);
                    s.Module.Load(s.Ctx);
                    s.Loaded = true;
                }
                catch (Exception e)
                {
                    UnloadSlot(name, s);   // Load may have changed things before it threw
                    if (collectible && IsCollectibleProblem(e))
                    {
                        UnloadFile(file, "collectible load refused");
                        return Result.RetryIndividual;
                    }
                    Log.LogError($"{name}.dll: {s.Id}.Load threw: {e}");
                    errors.Add(s.Id);
                }
            }

            string built = SafeLastWrite(path);
            string ids = string.Join(", ", slots.Select(s => s.Id + (s.Loaded ? "" : " (FAILED)")));
            string gens = string.Join(",", slots.Where(s => s.Ctx != null).Select(s => "#" + s.Ctx.Generation));
            string line = $"{(old != null ? "Reloaded" : "Loaded")} {name}.dll [{ids}] {gens}, built {built}, {(collectible ? "collectible" : "non-collectible")}, {sw.ElapsedMilliseconds} ms";
            if (errors.Count == 0)
            {
                Log.LogInfo(line);
                Toast($"{name} {(old != null ? "reloaded" : "loaded")} {gens} (built {built})", true);
                return Result.Ok;
            }
            Log.LogError(line);
            Toast($"{name}: Load failed for {string.Join(", ", errors)} (see LogOutput.log)", false);
            return Result.Failed;
        }

        private static string CreateModules(Assembly asm, string name, List<Slot> slots)
        {
            Type[] types;
            string problem = null;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
                var first = e.LoaderExceptions.FirstOrDefault(x => x != null);
                problem = $"some types could not be loaded: {first?.Message}";
                foreach (var le in e.LoaderExceptions.Where(x => x != null).Take(5)) Log.LogWarning($"{name}.dll: {le.Message}");
            }

            var contract = typeof(IHotModule);
            foreach (var t in types)
            {
                if (!t.IsClass || t.IsAbstract || t.IsGenericTypeDefinition) continue;
                if (!contract.IsAssignableFrom(t))
                {
                    if (t.GetInterfaces().Any(i => i.FullName == contract.FullName))
                        problem = $"{t.FullName} implements an IHotModule from a different HotReload.dll. Reference HotReload with Private=false " +
                                  "and don't copy HotReload.dll into the hot folder.";
                    continue;
                }
                if (t.GetConstructor(Type.EmptyTypes) == null) { problem = $"{t.FullName} needs a public parameterless constructor."; continue; }

                IHotModule m;
                string id;
                try
                {
                    m = (IHotModule)Activator.CreateInstance(t);
                    id = m.Id;
                }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                    problem = $"{t.FullName} could not be created: {inner}";
                    continue;
                }
                if (string.IsNullOrWhiteSpace(id)) { problem = $"{t.FullName}.Id is empty."; continue; }
                slots.Add(new Slot { Module = m, Id = id.Trim() });
            }
            return problem;
        }

        private static HotContext MakeContext(string id, string name, string path)
        {
            int gen = ++_generation;
            if (!LogSources.TryGetValue(name, out var log))
                LogSources[name] = log = BepInEx.Logging.Logger.CreateLogSource(name);
            return new HotContext
            {
                Id = id,
                Log = log,
                Config = new ConfigFile(System.IO.Path.Combine(Paths.ConfigPath, id + ".cfg"), true),
                HarmonyId = $"{id}.hot{gen}",
                Generation = gen,
                ModulePath = path,
                HotDir = _hotDir,
                ConfigDir = Paths.ConfigPath,
                PluginDir = Paths.PluginPath,
            };
        }

        private static void LoadFailed(string name, LoadedFile old, string why, Exception e)
        {
            string keep = old != null ? $" The previous {name} build keeps running." : "";
            Log.LogError($"{name}.dll: {why}{(e != null ? " " + e : "")}{keep}");
            Toast($"{name}: not loaded - {Shorten(why)}{(old != null ? " (old build kept)" : "")}", false);
        }

        // ------------------------------------------------------------------ unloading

        private static void UnloadFile(LoadedFile file, string reason)
        {
            for (int i = file.Slots.Count - 1; i >= 0; i--) UnloadSlot(file.Name, file.Slots[i]);
            file.Slots.Clear();
            if (Files.TryGetValue(file.Path, out var cur) && cur == file) Files.Remove(file.Path);
            TryUnload(file.Context);
            file.Context = null;
            file.Assembly = null;
            Log.LogInfo($"Unloaded {file.Name}.dll ({reason}).");
        }

        private static void UnloadSlot(string name, Slot s)
        {
            if (s.Ctx == null) return;   // Load was never called
            try { s.Module.Unload(); }
            catch (Exception e) { Log.LogError($"{name}.dll: {s.Id}.Unload threw; some of its game changes may stay until the game restarts: {e}"); }

            // Safety net for a module that forgot to unpatch, then verify: if any patch of this load survived, the old
            // prefixes would keep running next to the new build's, so new builds of this id are blocked until a restart.
            string hid = s.Ctx.HarmonyId;
            try
            {
                new Harmony(hid).UnpatchSelf();
                var left = Harmony.GetAllPatchedMethods()
                    .Where(m => { var info = Harmony.GetPatchInfo(m); return info != null && info.Owners.Contains(hid); })
                    .Select(m => $"{m.DeclaringType?.Name}.{m.Name}").ToList();
                if (left.Count > 0) BlockId(name, s.Id, $"Harmony patches of {hid} are still active after unpatching: {string.Join(", ", left)}");
            }
            catch (Exception e) { BlockId(name, s.Id, $"unpatching Harmony id {hid} failed: {e}"); }
            s.Loaded = false;
            s.Ctx = null;
        }

        private static void BlockId(string name, string id, string why)
        {
            Blocked.Add(id);
            Log.LogError($"{name}.dll: {why}. New builds of '{id}' are blocked until the game restarts, so old and new code can't run at once.");
            Toast($"{name}: old Harmony patches could not be removed - restart the game (see LogOutput.log)", false);
        }

        private static void TryUnload(HotLoadContext alc)
        {
            if (alc == null) return;
            try { alc.Unload(); }
            catch (Exception e) { Log.LogWarning($"Unloading {alc.Name} failed (it stays in memory): {e.Message}"); }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>An assembly that is already loaded and is not a hot module (game interop, BepInEx, Harmony, HotReload).</summary>
        private static Assembly FindShared(AssemblyName name)
        {
            if (name == null || string.IsNullOrEmpty(name.Name)) return null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic || IsHotAssembly(a)) continue;
                if (string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase)) return a;
            }
            return null;
        }

        private static bool IsHotAssembly(Assembly a)
        {
            if (AssemblyLoadContext.GetLoadContext(a) is HotLoadContext) return true;
            lock (IndividualHotAssemblies) return IndividualHotAssemblies.Contains(a);
        }

        /// <summary>Only answers for hot modules loaded non-collectible, so other plugins' resolution is untouched.</summary>
        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs e)
        {
            try
            {
                if (e.RequestingAssembly == null || !IsHotAssembly(e.RequestingAssembly)) return null;
                return FindShared(new AssemblyName(e.Name));
            }
            catch { return null; }
        }

        private static bool ChainloaderHas(string id, out string where)
        {
            where = null;
            try
            {
                var plugins = IL2CPPChainloader.Instance?.Plugins;
                if (plugins == null || !plugins.TryGetValue(id, out var info)) return false;
                where = string.IsNullOrEmpty(info.Location) ? id : info.Location;
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning($"Could not check the loaded plugins for '{id}': {e.Message}");
                return false;
            }
        }

        private static bool IsCollectibleProblem(Exception e)
        {
            for (var x = e; x != null; x = x.InnerException)
            {
                if (x is NotSupportedException || x.Message.IndexOf("collectible", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (x is ReflectionTypeLoadException rtle && rtle.LoaderExceptions.Any(l => l != null && IsCollectibleProblem(l))) return true;
            }
            return false;
        }

        private static List<string> DllsInFolder()
        {
            try
            {
                return Directory.GetFiles(_hotDir, "*.dll", SearchOption.TopDirectoryOnly)
                    .Where(p => System.IO.Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase) && !IsIgnored(p))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception e)
            {
                Log.LogWarning($"Could not list {_hotDir}: {e.Message}");
                return new List<string>();
            }
        }

        private static bool _warnedHostCopy;
        private static bool IsIgnored(string path)
        {
            if (!string.Equals(System.IO.Path.GetFileName(path), "HotReload.dll", StringComparison.OrdinalIgnoreCase)) return false;
            if (!_warnedHostCopy) { _warnedHostCopy = true; Log.LogWarning($"Ignoring {path}: the host belongs in BepInEx/plugins, not in the hot folder."); }
            return true;
        }

        /// <summary>Reads the whole file without locking it for others' reads. Throws IOException while a writer still has it open.</summary>
        private static byte[] ReadShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var buf = new byte[fs.Length];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) throw new IOException("file shrank while reading");
                read += n;
            }
            return buf;
        }

        /// <summary>The matching .pdb (for line numbers in stack traces), if it was written together with the DLL.</summary>
        private static byte[] ReadPdb(string dllPath)
        {
            try
            {
                string pdb = System.IO.Path.ChangeExtension(dllPath, ".pdb");
                if (!File.Exists(pdb)) return null;
                if (Math.Abs((File.GetLastWriteTimeUtc(pdb) - File.GetLastWriteTimeUtc(dllPath)).TotalSeconds) > 120) return null;
                return ReadShared(pdb);
            }
            catch { return null; }
        }

        private static string SafeLastWrite(string path)
        {
            try { return File.GetLastWriteTime(path).ToString("HH:mm:ss"); }
            catch { return "?"; }
        }

        private static string WithSlash(string p) => p.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;

        private static string Shorten(string s) => s.Length <= 110 ? s : s.Substring(0, 107) + "...";

        // ------------------------------------------------------------------ on-screen message

        private static void Toast(string text, bool ok)
        {
            float now = Time.unscaledTime;
            // several modules reloaded in the same moment: stack the lines
            if (_toast != null && now - _toastStarted < 0.5f && now < _toastUntil) { _toast += "\n" + text; _toastOk &= ok; }
            else { _toast = text; _toastStarted = now; _toastOk = ok; }
            _toastColor = _toastOk ? new Color(0.45f, 1f, 0.5f) : new Color(1f, 0.45f, 0.4f);   // any error line turns it red
            _toastUntil = now + (ok ? 3f : 6f);
        }

        private static void DrawToast()
        {
            if (_toast == null || !Plugin.ShowToast.Value) return;
            if (Time.unscaledTime > _toastUntil) { _toast = null; return; }

            float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            int lines = 1;
            foreach (char c in _toast) if (c == '\n') lines++;
            float w = 620 * s, lineH = 22 * s, pad = 8 * s;
            // top-centre, below TrafficDensity's toast (y = 70) and clear of DriverCam (left) and CurbFeel (top-right)
            var box = new Rect((Screen.width - w) * 0.5f, 130 * s, w, lines * lineH + 2 * pad);

            var prevColor = GUI.color;
            int prevFont = GUI.skin.label.fontSize;
            GUI.color = Color.white;
            GUI.Box(box, "");
            GUI.skin.label.fontSize = Mathf.RoundToInt(15 * s);
            GUI.color = _toastColor;
            GUI.Label(new Rect(box.x + pad, box.y + pad, box.width - 2 * pad, box.height - 2 * pad), _toast);
            GUI.skin.label.fontSize = prevFont;
            GUI.color = prevColor;
        }
    }
}
