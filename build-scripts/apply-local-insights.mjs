/** Explicit, idempotent integration into pinned upstream v2026.09.16.
 * All anchors are checked before any source file is written.
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
if (readFileSync(resolve(root, 'Version'), 'utf8').trim() !== '2026.09.16') throw new Error('Expected upstream v2026.09.16. Review the integration before changing baseline.');
const changes = new Map();
const sourceOf = (path) => changes.get(path) ?? readFileSync(resolve(root, path), 'utf8').replace(/\r\n/g, '\n');
function patch(path, before, after, count = 1) {
    const source = sourceOf(path);
    if (source.split(after).length - 1 === count && !source.split(after).join('').includes(before)) return;
    if (source.split(before).length - 1 !== count) throw new Error(`Unexpected upstream anchor: ${path}: ${before}`);
    changes.set(path, source.split(before).join(after));
}
function upgradeOptional(path, before, after = '') {
    const source = sourceOf(path);
    if (source.includes(before)) changes.set(path, source.split(before).join(after));
}
// Upgrade worktrees that already ran the v1/v2 script, without duplicating entries.
upgradeOptional('src/components/nav-menu/NavMenu.vue', '\n        <LocalInsightsNav />');
upgradeOptional('src/components/nav-menu/NavMenu.vue', "\n    import LocalInsightsNav from '../../features/local-insights/LocalInsightsNav.vue';");
upgradeOptional('src/plugins/router.js', "../features/local-insights/ActivityReview.vue", "../features/local-insights/CacheReview.vue");
patch('src/plugins/router.js', "            { path: 'feed', name: 'feed', component: Feed },", "            { path: 'feed', name: 'feed', component: Feed },\n            { path: 'local-insights', name: 'local-insights', component: () => import('../features/local-insights/CacheReview.vue'), meta: { navKey: 'local-insights' } },");
patch('src/shared/constants/ui.js', '    ...toolNavDefinitions', "    { key: 'local-insights', icon: 'ri-history-line', tooltip: 'nav_tooltip.local_insights', labelKey: 'nav_tooltip.local_insights', routeName: 'local-insights' },\n    ...toolNavDefinitions");
patch('src/components/nav-menu/navLayoutDefaults.js', "        { type: 'item', key: 'direct-access' }", "        { type: 'item', key: 'direct-access' },\n        { type: 'item', key: 'local-insights' }");
for (const [locale, label] of [['en', 'Shared-session review'], ['zh-CN', '共同游玩回顾'], ['zh-TW', '共同遊玩回顧']]) {
    patch(`src/localization/${locale}.json`, '"nav_tooltip": {', `"nav_tooltip": {\n        "local_insights": "${label}",`);
}
patch('Dotnet/SQLite.cs', '    public class SQLite', '    public partial class SQLite');
patch('Dotnet/Program.cs', '                    "VRCX");', '                    "VRCX-Insights");');
patch('Dotnet/Program.cs', 'Version = $"VRCX {versionFile}";', 'Version = $"VRCX Insights v3 ({versionFile})";');
patch('Dotnet/Program.cs', '            Update.Check();', '            // Insights preview uses manual, isolated releases only.', 2);
patch('Dotnet/SQLite.cs', '            m_Connection = new SQLiteConnection(', '            InsightsIdentity.ValidateConfigDirectory(Path.GetDirectoryName(Path.GetFullPath(dataSource)));\n\n            m_Connection = new SQLiteConnection(');
patch('Dotnet/Program.cs', '%AppData%\\\\VRCX', '%AppData%\\\\VRCX-Insights');
patch('Dotnet/StartupArgs.cs', 'Process.GetProcessesByName("VRCX")', 'Process.GetProcessesByName("VRCX-Insights")');
patch('Dotnet/StartupArgs.cs', '            LaunchArguments = ParseArgs(args);', '            LaunchArguments = ParseArgs(args);\n            InsightsIdentity.ValidateConfigDirectory(LaunchArguments.ConfigDirectory);');
patch('Dotnet/IPC/IPCServer.cs', 'return $"vrcx-ipc-{hash}";', 'return $"vrcx-insights-ipc-{hash}";');
patch('Dotnet/VRCX-Cef.csproj', '<AssemblyName>VRCX</AssemblyName>', '<AssemblyName>VRCX-Insights</AssemblyName>');
patch('Dotnet/VRCX-Cef.csproj', '<Product>VRCX</Product>', '<Product>VRCX Insights</Product>');
patch('Dotnet/VRCX-Cef.csproj', '<AssemblyTitle>VRCX</AssemblyTitle>', '<AssemblyTitle>VRCX Insights</AssemblyTitle>');
patch('Dotnet/VRCX-Cef.csproj', 'build\\Cef\\VRCX.exe', 'build\\Cef\\VRCX-Insights.exe');
patch('Dotnet/AppApi/Cef/AppApiCef.cs', '"VRCX.exe"', '"VRCX-Insights.exe"');
patch('Dotnet/AppApi/Cef/AppApiCef.cs', 'key.SetValue("VRCX",', 'key.SetValue("VRCX-Insights",');
patch('Dotnet/AppApi/Cef/AppApiCef.cs', 'key.DeleteValue("VRCX",', 'key.DeleteValue("VRCX-Insights",');
patch('Dotnet/OverlayWebSocket/OverlayServer.cs', '127.0.0.1:34582', '127.0.0.1:34583');
patch('Dotnet/Overlay/Cef/OverlayClient.cs', '127.0.0.1:34582', '127.0.0.1:34583');
patch('src/stores/vrcxUpdater.js', 'const noUpdater = ref(false);', 'const noUpdater = ref(true);');
patch('src/stores/vrcxUpdater.js', 'noUpdater.value = await window.electron.getNoUpdater();', 'noUpdater.value = true; // Isolated preview: do not install upstream updates.');
for (const signature of ['async function checkForVRCXUpdate() {', 'async function loadBranchVersions() {', 'async function downloadVRCXUpdate(downloadUrl, hashString, size, releaseName) {', 'function installVRCXUpdate() {']) {
    patch('src/stores/vrcxUpdater.js', signature + '\n', signature + '\n        if (noUpdater.value) return false; // Manual fork releases only.\n');
}
for (const [path, content] of changes) writeFileSync(resolve(root, path), content);
console.log(`Integrated ${changes.size} source files. Cache route uses native customizable navigation; source database is not migrated.`);
