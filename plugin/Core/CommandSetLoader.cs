using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Live-reload capable command-assembly loader. Loads command set DLLs
    /// from BYTES (not from the file path) so the file on disk is never
    /// locked by Revit: the copy step of a new build can overwrite the DLL
    /// while Revit runs. The cache keys each assembly path by SHA-256 of its
    /// contents — Resolve returns the cached Assembly when the file hash is
    /// unchanged, and loads the new bytes into a fresh assembly object when
    /// the file changed. The stale loaded assembly stays resident until GC
    /// (acceptable for a dev workflow); the registry keeps using the newest
    /// instances because RegisterCommand overwrites by CommandName.
    /// </summary>
    public static class CommandSetLoader
    {
        private static readonly Dictionary<string, Tuple<Assembly, string>> Cache =
            new Dictionary<string, Tuple<Assembly, string>>(StringComparer.OrdinalIgnoreCase);

        private static bool _resolveHookInstalled;

        /// <summary>
        /// Installs an AppDomain.AssemblyResolve hook (once) that probes all
        /// directories containing known command assemblies + their sibling folders.
        /// Required because byte-loaded assemblies have no CodeBase for the
        /// default loader to probe dependencies (e.g. Microsoft.CodeAnalysis).
        /// </summary>
        public static void EnsureResolveHook(params string[] probeDirectories)
        {
            if (_resolveHookInstalled) return;
            _resolveHookInstalled = true;
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var name = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(name)) return null;

                foreach (var dir in unionOfProbeDirs(probeDirectories))
                {
                    string candidate = Path.Combine(dir, name + ".dll");
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            return Assembly.Load(File.ReadAllBytes(candidate));
                        }
                        catch { }
                    }
                }
                return null;
            };
        }

        private static IEnumerable<string> unionOfProbeDirs(string[] configured)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in configured)
            {
                if (d != null && seen.Add(d)) yield return d;
            }
            foreach (var key in Cache.Keys)
            {
                string dir = Path.GetDirectoryName(key);
                if (dir != null && seen.Add(dir)) yield return dir;
            }
        }

        public static Assembly Resolve(string assemblyPath, ILogger logger)
        {
            string hash = ComputeHash(assemblyPath);
            Tuple<Assembly, string> cached;
            if (Cache.TryGetValue(assemblyPath, out cached) && cached.Item2 == hash)
            {
                return cached.Item1;
            }

            var bytes = File.ReadAllBytes(assemblyPath);
            var assembly = Assembly.Load(bytes);
            Cache[assemblyPath] = Tuple.Create(assembly, hash);
            logger?.Info("程序集字节加载 (热重载): {0} [{1}]\nAssembly byte-loaded (hot reload): {0} [{1}]",
                Path.GetFileName(assemblyPath), hash.Substring(0, 8));
            return assembly;
        }

        /// <summary>Expels the cached assembly for this path (forces reload next time even if bytes unchanged).</summary>
        public static void Invalidate(string assemblyPath)
        {
            if (assemblyPath != null) Cache.Remove(assemblyPath);
        }

        public static string ComputeHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb2 = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb2.Append(b.ToString("x2"));
                return sb2.ToString();
            }
        }
    }
}
