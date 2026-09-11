using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.UI;
using revit_mcp_plugin.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace revit_mcp_plugin.Configuration
{
    public class ConfigurationManager
    {
        private readonly ILogger _logger;
        private readonly string _configPath;

        public FrameworkConfig Config { get; private set; }

        public ConfigurationManager(ILogger logger)
        {
            _logger = logger;

            // 配置文件路径
            // Configuration file path.
            _configPath = PathManager.GetCommandRegistryFilePath();
        }

        /// <summary>
        /// <para>加载配置</para>
        /// <para>Load configuration from a JSON file.</para>
        /// </summary>
        public void LoadConfiguration()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    Config = JsonConvert.DeserializeObject<FrameworkConfig>(json);
                    _logger.Info("已加载配置文件: {0}\nConfiguration file loaded: {0}", _configPath);
                }
                else
                {
                    _logger.Error("未找到配置文件\nNo configuration file found.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("加载配置文件失败: {0}\nFailed to load configuration file: {0}", ex.Message);
            }

            if (Config == null)
            {
                Config = new FrameworkConfig();
            }
            if (Config.Commands == null)
            {
                Config.Commands = new List<CommandConfig>();
            }

            // 自愈：将注册表与磁盘上的命令清单对齐
            // Self-heal: align the registry with the command manifests on disk.
            ReconcileWithManifests();

            // 记录加载时间
            // Register load time.
            _lastConfigLoadTime = DateTime.Now;
        }

        /// <summary>
        /// 将配置写回 commandRegistry.json。
        /// Writes the configuration back to commandRegistry.json.
        /// </summary>
        public void SaveConfiguration()
        {
            try
            {
                string json = JsonConvert.SerializeObject(Config, Formatting.Indented);
                File.WriteAllText(_configPath, json);
                _logger.Info("已保存配置文件: {0}\nConfiguration file saved: {0}", _configPath);
            }
            catch (Exception ex)
            {
                _logger.Error("保存配置文件失败: {0}\nFailed to save configuration file: {0}", ex.Message);
            }
        }

        /// <summary>
        /// <para>
        /// 将内存中的命令注册表与各命令集的 command.json 清单对齐：清单中出现但注册表
        /// 缺少的命令以 enabled=false 添加（保守；不影响现有启用状态），注册表中已无对应
        /// 清单的条目被移除。有变化时写回磁盘。
        /// </para>
        /// <para>Align the in-memory command registry with the per-set command.json
        /// manifests: commands present in a manifest but missing from the registry are
        /// added as enabled:false (existing enabled state untouched), and registry
        /// entries with no corresponding manifest are removed. Writes back only when
        /// something changed.</para>
        /// </summary>
        private void ReconcileWithManifests()
        {
            try
            {
                string commandsDir = PathManager.GetCommandsDirectoryPath();
                if (!Directory.Exists(commandsDir))
                {
                    _logger.Warning("命令目录不存在，跳过注册表自愈: {0}\nCommands directory missing; registry reconcile skipped: {0}", commandsDir);
                    return;
                }

                // 1. 收集清单命令: commandName -> 具体化的注册表条目（含 DLL 探测出的版本列表）
                var manifestCommands = new Dictionary<string, CommandConfig>(StringComparer.OrdinalIgnoreCase);
                foreach (var setDir in Directory.GetDirectories(commandsDir))
                {
                    if (Path.GetFileName(setDir).StartsWith(".")) continue;
                    string commandJsonPath = Path.Combine(setDir, "command.json");
                    if (!File.Exists(commandJsonPath)) continue;

                    CommandJson manifest;
                    try
                    {
                        manifest = JsonConvert.DeserializeObject<CommandJson>(File.ReadAllText(commandJsonPath));
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning("解析 command.json 失败 ({0}): {1}\nFailed to parse command.json ({0}): {1}", commandJsonPath, ex.Message);
                        continue;
                    }
                    if (manifest?.Commands == null) continue;

                    // 年份子目录探测各命令 DLL 是否真实存在（{VERSION} 需要具体年份）
                    var yearDirs = Directory.GetDirectories(setDir)
                        .Select(Path.GetFileName)
                        .Where(name => int.TryParse(name, out _))
                        .ToList();

                    foreach (var item in manifest.Commands)
                    {
                        if (string.IsNullOrWhiteSpace(item?.CommandName)) continue;

                        var supported = new List<string>();
                        string dllTemplate = null;
                        foreach (var year in yearDirs)
                        {
                            string dllPath = string.IsNullOrEmpty(item.AssemblyPath)
                                ? null
                                : Path.Combine(setDir, year, item.AssemblyPath);
                            if (dllPath != null && File.Exists(dllPath))
                            {
                                supported.Add(year);
                                if (dllTemplate == null)
                                    dllTemplate = Path.Combine(manifest.Name, "{VERSION}", item.AssemblyPath);
                            }
                        }

                        if (supported.Count == 0)
                        {
                            // DLL 不在任何年份目录中 - 无法构造可解析的条目，跳过（不写入注册表）
                            _logger.Warning("清单命令缺少 DLL，跳过自愈条目 {0} ({1})\nManifest command has no DLL found; self-heal skipped for {0} ({1})",
                                item.CommandName, manifest.Name);
                            continue;
                        }

                        manifestCommands[item.CommandName] = new CommandConfig
                        {
                            CommandName = item.CommandName,
                            Description = item.Description ?? "",
                            AssemblyPath = dllTemplate,
                            SupportedRevitVersions = supported.ToArray(),
                            Developer = manifest.Developer ?? new DeveloperInfo { Name = "Unspecified" },
                            // 默认禁用 - 启用状态永远以用户设置为准
                            Enabled = false
                        };
                    }
                }

                // 2. 保留仍与清单对应的现有条目（保留启用状态与用户数据），移除其余
                var beforeCount = Config.Commands.Count;
                var byNameMap = new Dictionary<string, CommandConfig>(StringComparer.OrdinalIgnoreCase);
                foreach (var cmd in Config.Commands)
                {
                    if (!string.IsNullOrWhiteSpace(cmd?.CommandName) && manifestCommands.ContainsKey(cmd.CommandName))
                        byNameMap[cmd.CommandName] = cmd;
                }
                int removedCount = beforeCount - byNameMap.Count;
                if (removedCount > 0)
                {
                    _logger.Info("自愈: 移除 {0} 个已无清单对应的注册表条目\nSelf-heal: removed {0} registry entries with no corresponding manifest", removedCount);
                    Config.Commands = byNameMap.Values.ToList();
                }

                // 3. 补入清单中存在但注册表缺少的命令（enabled=false）
                var existingNames = new HashSet<string>(byNameMap.Keys, StringComparer.OrdinalIgnoreCase);
                int addedCount = 0;
                foreach (var mc in manifestCommands.Values)
                {
                    if (!existingNames.Contains(mc.CommandName))
                    {
                        Config.Commands.Add(mc);
                        _logger.Info("自愈: 注册表缺失命令 {0}，已添加（默认禁用）\nSelf-heal: manifest command {0} missing from registry; added (disabled by default)", mc.CommandName);
                        addedCount++;
                    }
                }

                // 4. 有变化才写回
                if (removedCount > 0 || addedCount > 0)
                {
                    SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                _logger.Error("注册表自愈失败: {0}\nRegistry self-heal failed: {0}", ex.Message);
            }
        }

        private DateTime _lastConfigLoadTime;
    }
}
