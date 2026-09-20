<template>
    <section class="ic-page" aria-labelledby="insights-title">
        <header class="ic-header">
            <div><small>VRCX INSIGHTS · LOCAL CACHE v4</small><h1 id="insights-title">{{ s('共同游玩回顾', 'Shared-session review') }}</h1>
                <p>{{ s('先创建本地分析文件，再由你决定何时开始分析。', 'Create a local analysis file, then choose when to analyze it.') }}</p></div>
            <button type="button" :disabled="busy || inspecting" @click="inspect">{{ s('刷新文件状态', 'Refresh file status') }}</button>
        </header>
        <div v-if="error" class="ic-error" role="alert"><strong>{{ s('操作未完成', 'Operation did not complete') }}</strong><p>{{ error }}</p></div>
        <p v-if="notice" class="ic-notice" role="status">{{ notice }}</p>
        <p v-if="inspecting" role="status">{{ s('正在读取分析文件状态，不会开始扫描历史记录。', 'Reading file status only; historical analysis has not started.') }}</p>
        <article v-if="status" class="ic-card ic-file">
            <div class="ic-card-heading"><h2>{{ s('本地分析文件', 'Local analysis file') }}</h2><span>{{ phaseLabel }}</span></div>
            <code class="ic-path">{{ status.path }}</code>
            <p>{{ s('首次分析可能耗费较长时间，并额外占用磁盘空间。原始 VRCX 数据只读；分析缓存单独保存。进入此页面不会自动分析。', 'Initial analysis may take a long time and use additional disk space. Original VRCX data is read-only; the cache is separate. Opening this page never starts analysis automatically.') }}</p>
            <p v-if="status.sourceProfile === 'legacy-adapted'" class="ic-notice" data-test="legacy-source">{{ s('检测到较老的 VRCX 数据库结构：程序正在通过兼容层把旧字段转换成当前分析格式。不会修改、升级或覆盖你的原始数据库。', 'An older VRCX database layout was detected. A compatibility adapter normalizes legacy fields into the current analysis format without modifying, upgrading, or overwriting the source database.') }}</p>
            <label v-if="!status.exists || status.phase === 'created'" class="ic-consent"><input v-model="consent" data-test="consent" type="checkbox" />{{ s('我已了解耗时和本地缓存占用。', 'I understand the time and local disk-space requirements.') }}</label>
            <div class="ic-actions">
                <button v-if="!status.exists" data-test="create" type="button" class="ic-primary" :disabled="!consent || busy" @click="createFile(false)">{{ s('创建分析文件（不开始扫描）', 'Create analysis file (no scan)') }}</button>
                <button v-else-if="status.phase === 'created'" data-test="start" type="button" class="ic-primary" :disabled="!consent || busy" @click="analyze">{{ s('开始首次分析', 'Start initial analysis') }}</button>
                <template v-else-if="status.phase === 'ready'">
                    <button data-test="open" type="button" class="ic-primary" :disabled="busy" @click="openCache">{{ s('打开已生成的回顾', 'Open cached review') }}</button>
                    <button data-test="update" type="button" :disabled="busy" @click="analyze">{{ s('更新缓存（增量）', 'Update cache (incremental)') }}</button>
                </template>
                <button v-else-if="!busy && ['ingesting', 'deriving'].includes(status.phase)" data-test="resume" type="button" class="ic-primary" @click="analyze">{{ s('继续未完成的分析', 'Resume unfinished analysis') }}</button>
                <button v-if="busy" data-test="pause" type="button" :disabled="stopRequested" @click="stopRequested = true">{{ stopRequested ? s('正在保存当前批次…', 'Saving current batch…') : s('暂停分析', 'Pause analysis') }}</button>
                <button v-if="status.exists" data-test="rebuild" type="button" :disabled="busy" @click="confirmRebuild = true">{{ s('重建缓存', 'Rebuild cache') }}</button>
                <button v-if="status.exists" type="button" @click="copyPath">{{ s('复制文件路径', 'Copy file path') }}</button>
            </div>
            <div v-if="confirmRebuild" class="ic-confirm" role="alert">
                <p>{{ s('只重建分析缓存，原始数据库不会修改。旧缓存会保留为 .previous 文件；重建后仍须手动开始分析。', 'Only the analysis cache will be rebuilt. The source is unchanged; the previous cache is retained as a .previous file. Analysis still requires a separate Start action.') }}</p>
                <button data-test="confirm-rebuild" type="button" :disabled="busy" @click="createFile(true)">{{ s('确认重建', 'Confirm rebuild') }}</button>
                <button type="button" @click="confirmRebuild = false">{{ s('取消', 'Cancel') }}</button>
            </div>
            <div v-if="['ingesting', 'deriving'].includes(status.phase)" class="ic-progress" role="status">
                <strong>{{ status.phase === 'ingesting' ? s('阶段 1/2：分批整理源记录', 'Stage 1/2: importing source batches') : s('阶段 2/2：建立在场片段索引', 'Stage 2/2: materializing presence sessions') }}</strong>
                <progress :value="progressDone" :max="Math.max(1, progressTotal)"></progress>
                <span>{{ number(progressDone) }} / {{ number(progressTotal) }} · {{ busy ? s('处理中', 'Running') : s('进度已保存，等待继续', 'Checkpoint saved; waiting to resume') }}</span>
            </div>
            <p v-if="status.phase === 'ready'" class="ic-meta">{{ s('缓存更新于', 'Cache updated') }} {{ date(status.updatedAt) }} · {{ number(status.eventCount) }} {{ s('条记录', 'events') }} · {{ number(status.sessionCount) }} {{ s('个完整在场片段', 'complete sessions') }}</p>
            <details v-if="status.exists" class="ic-meta"><summary>{{ s('数据覆盖与诊断', 'Coverage and diagnostics') }}</summary>
                <p>{{ s('源数据库格式', 'Source database format') }}: {{ sourceProfileLabel }}<template v-if="status.legacyAdapters"> · {{ number(status.legacyAdapters) }} {{ s('个兼容适配', 'legacy adapters') }}</template></p>
                <p>{{ s('无有效时间戳的记录', 'Records with invalid timestamps') }}: {{ number(status.rejected) }}</p>
                <p>{{ s('仅分析当前程序已有的日志；其他账号和其他软件的记录不会自动合并。批量导入、替换数据库或修改旧记录后，建议重建缓存。', 'Only logs already stored by this application are analyzed. Other accounts or applications are not merged automatically. Rebuild after bulk imports, database replacement, or old-row edits.') }}</p>
                <p v-for="warning in warnings" :key="warning">{{ warning }}</p>
            </details>
        </article>
        <template v-if="opened && status?.phase === 'ready' && !busy">
            <div class="ic-controls ic-card">
                <label>{{ s('时间范围', 'Range') }}<select v-model="rangeKind" data-test="range" @change="rangeChanged">
                    <option value="1">{{ s('24 小时', '24 hours') }}</option><option value="7">{{ s('7 天', '7 days') }}</option><option value="30">{{ s('30 天', '30 days') }}</option>
                    <option value="custom">{{ s('自定义', 'Custom') }}</option><option value="permanent">{{ s('永久（全部历史）', 'All time') }}</option>
                </select></label>
                <template v-if="rangeKind === 'custom'"><label>{{ s('开始', 'Start') }}<input v-model="customStart" type="datetime-local" /></label><label>{{ s('结束（可留空）', 'End (optional)') }}<input v-model="customEnd" type="datetime-local" /></label><button type="button" @click="applyRange">{{ s('应用', 'Apply') }}</button></template>
                <label>{{ s('查看类型', 'View') }}<select v-model="viewMode" data-test="mode" @change="changeMode"><option value="person">{{ s('看人', 'Person') }}</option><option value="group">{{ s('看分组', 'Group') }}</option></select></label>
                <div v-if="viewMode === 'person'" class="ic-search">
                    <label>{{ s('筛选名称', 'Find a person') }}<input v-model="search" data-test="person-search" role="combobox" aria-controls="ic-suggestions" :aria-expanded="suggestions.length > 0" :aria-activedescendant="suggestions.length ? `ic-option-${highlight}` : undefined" autocomplete="off" @input="findPeople(false)" @keydown.down.prevent="move(1)" @keydown.up.prevent="move(-1)" @keydown.enter.prevent="pickHighlighted" @keydown.esc="suggestions = []" /></label>
                    <div v-if="suggestions.length" id="ic-suggestions" role="listbox" class="ic-suggestions"><button v-for="(person, i) in suggestions" :id="`ic-option-${i}`" :key="person.id" type="button" role="option" :aria-selected="i === highlight" @click="pickPerson(person)"><strong>{{ person.name }}</strong><small>{{ person.id }}</small></button></div>
                </div>
                <template v-else><label>{{ s('分组', 'Group') }}<select v-model="selectedGroupId" data-test="group" @change="groupMember = ''"><option value="">{{ s('选择分组', 'Choose a group') }}</option><option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }} ({{ group.members.length }})</option></select></label><button type="button" data-test="manage-groups" @click="showGroups = !showGroups">{{ s('管理分组', 'Manage groups') }}</button></template>
                <label class="ic-checkbox"><input v-model="friendsOnly" type="checkbox" @change="findPeople(false)" />{{ s('联想只显示好友', 'Suggest friends only') }}</label>
            </div>
            <article v-if="showGroups" class="ic-card ic-group-editor">
                <h2>{{ s('本地自定义分组（每组最多 200 人）', 'Local groups (up to 200 people per group)') }}</h2>
                <div class="ic-actions"><input v-model="groupName" data-test="group-name" maxlength="64" :placeholder="s('分组名称', 'Group name')" /><input v-model="draftSearch" data-test="member-search" :placeholder="s('输入名字添加成员', 'Search members')" @input="findPeople(true)" /></div>
                <div class="ic-draft-suggestions"><button v-for="person in draftSuggestions" :key="person.id" type="button" @click="addMember(person)">{{ person.name }} +</button></div>
                <div class="ic-chips"><button v-for="id in draftMembers" :key="id" type="button" @click="draftMembers = draftMembers.filter(x => x !== id)">{{ knownNames[id] || id }} ×</button></div>
                <div class="ic-actions"><button data-test="save-group" type="button" :disabled="!groupName.trim() || !draftMembers.length || draftMembers.length > 200" @click="storeGroup">{{ editingGroup ? s('保存修改', 'Save changes') : s('创建分组', 'Create group') }}</button><button v-if="editingGroup" type="button" @click="clearDraft">{{ s('取消编辑', 'Cancel edit') }}</button></div>
                <div v-for="group in groups" :key="group.id" class="ic-group-row"><strong>{{ group.name }}</strong><span>{{ group.members.length }}</span><button type="button" @click="editGroup(group)">{{ s('编辑', 'Edit') }}</button><button type="button" @click="removeGroup(group.id)">{{ s('删除', 'Delete') }}</button></div>
            </article>
            <div v-if="viewMode === 'group' && groupMember" class="ic-actions"><button type="button" data-test="group-back" @click="groupMember = ''">← {{ s('返回组内共同游玩概览', 'Back to group overview') }}</button></div>
            <p v-if="!reviewIds.length" class="ic-empty">{{ s('输入名字直接选人，或选择一个本地分组。无需重新分析整个数据库。', 'Select a name suggestion or a local group. The source database is not rescanned.') }}</p>
            <p v-if="loading.pairs && !report" role="status">{{ s('正在查询缓存…', 'Querying the cache…') }}</p>
            <div v-if="report && !error" class="ic-grid">
                <div class="ic-left">
                    <article class="ic-card ic-timeline"><div class="ic-card-heading"><h2>{{ s('相遇记录', 'Recorded encounters') }}</h2><span>{{ number(timeline?.total) }}</span></div>
                        <ol><li v-for="row in timeline?.rows || []" :key="row.id"><div><strong>{{ title(row) }}</strong><time>{{ date(row.at) }}</time></div><p><b v-if="reviewIds.length > 1">{{ row.displayName }} · </b>{{ row.worldName || row.location }}</p><details><summary>{{ s('来源', 'Source') }}: {{ row.source }} #{{ row.sourceId }}</summary><code>{{ row.location }}</code></details></li></ol>
                        <p v-if="!timeline?.rows.length">{{ s('这个范围没有已记录的相遇事件。', 'No recorded encounters in this range.') }}</p>
                        <CachePager v-bind="pagerProps('timeline', timeline)" :zh="zh" :label="s('相遇记录分页', 'Encounter pages')" @page="changePage('timeline', $event)" @size="changeSize('timeline', $event)" @expand="expand('timeline')" />
                    </article>
                    <article class="ic-card ic-logs"><div class="ic-card-heading"><h2>{{ s('日志', 'Logs') }}</h2><label>{{ s('类型', 'Type') }} <select v-model="logFilter" data-test="log-filter" @change="changeLogFilter"><option value="all">{{ s('全部已缓存类型', 'All cached types') }}</option><option value="join">{{ s('进入房间', 'Joined room') }}</option><option value="left">{{ s('退出房间', 'Left room') }}</option><option value="status">{{ s('切换状态', 'Status change') }}</option><option value="bio">{{ s('更改 Bio', 'Bio change') }}</option></select></label></div>
                        <ol><li v-for="row in logs?.rows || []" :key="row.id"><div><strong>{{ title(row) }}</strong><time>{{ date(row.at) }}</time></div><p v-if="reviewIds.length > 1">{{ row.displayName }}</p><pre>{{ detail(row) }}</pre><small>{{ row.source }} #{{ row.sourceId }}</small></li></ol>
                        <p v-if="!logs?.rows.length">{{ s('没有符合筛选条件的日志。', 'No matching logs.') }}</p>
                        <CachePager v-bind="pagerProps('logs', logs)" :zh="zh" :label="s('日志分页', 'Log pages')" @page="changePage('logs', $event)" @size="changeSize('logs', $event)" @expand="expand('logs')" />
                    </article>
                </div>
                <aside class="ic-right">
                    <article class="ic-card"><h2>{{ groupOnly ? selectedGroup?.name : report.members[0]?.name }}</h2>
                        <div v-for="member in report.members" :key="member.id" class="ic-member"><button v-if="groupOnly" type="button" @click="groupMember = member.id">{{ member.name }} →</button><strong v-else>{{ member.name }}</strong><p>{{ duration(member.observedMs) }} · {{ member.completeSessions }} {{ s('个完整片段', 'complete sessions') }}</p><small>{{ member.incompleteSessions }} {{ s('个未闭合片段（不补算时长）', 'unclosed records (not extrapolated)') }}</small></div>
                    </article>
                    <article class="ic-card ic-pairs"><h2>{{ groupOnly ? s('组内两两共同游玩', 'Pairwise co-presence within group') : s('共同在场的人', 'Observed companions') }}</h2><p>{{ s('只算完整记录在同一观察场次、同一实例中的时间交集；不代表关系或实际互动。', 'Only complete, same-visit, same-instance overlaps count; this is not proof of interaction or a relationship.') }}</p>
                        <div v-for="pair in report.pairs.rows" :key="`${pair.leftId}|${pair.rightId}`" class="ic-pair"><div><button v-if="groupOnly" type="button" @click="choose(pair.leftId, pair.leftName)">{{ pair.leftName }}</button><span v-if="groupOnly"> ↔ </span><button type="button" @click="choose(pair.rightId, pair.rightName)">{{ pair.rightName }}</button></div><strong>{{ duration(pair.observedMs) }}</strong><small>{{ pair.segments }} {{ s('段', 'segments') }}</small></div>
                        <p v-if="!report.pairs.rows.length">{{ s('没有完整的共同在场片段。', 'No complete overlapping sessions.') }}</p>
                        <CachePager v-bind="pagerProps('pairs', report.pairs)" :zh="zh" :label="s('共同在场分页', 'Companion pages')" @page="changePage('pairs', $event)" @size="changeSize('pairs', $event)" />
                    </article>
                </aside>
            </div>
        </template>
        <footer class="ic-policy">{{ s('仅使用本机保存的记录；不推测隐藏位置，不把房主默认当作在场者，不自动加入群组。缓存中的全局游戏日志无法追溯每条记录当时属于哪个登录账号。', 'Local recorded evidence only: no hidden-location inference, presumed creator presence, or automatic group joining. Historical global GameLog rows do not identify the account that originally recorded each row.') }}</footer>
    </section>
</template>
<script setup>
import { computed, onActivated, onBeforeUnmount, onDeactivated, onMounted, reactive, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import CachePager from './CachePager.vue';
import { cacheRequest, runCacheAnalysis, currentCacheAccount, cacheRange, savedCacheGroups, saveCacheGroups } from './cacheClient.js';
defineOptions({ name: 'LocalInsights' });
const { locale } = useI18n();
const zh = computed(() => String(locale.value).startsWith('zh'));
const s = (chinese, english) => zh.value ? chinese : english;
const status = ref(null), consent = ref(false), busy = ref(false), inspecting = ref(false), opened = ref(false);
const error = ref(''), notice = ref(''), stopRequested = ref(false), confirmRebuild = ref(false);
const account = ref(''), viewMode = ref('person'), selectedId = ref(''), search = ref(''), friendsOnly = ref(false);
const rangeKind = ref('7'), customStart = ref(''), customEnd = ref(''), appliedRange = ref(cacheRange('7'));
const suggestions = ref([]), draftSuggestions = ref([]), highlight = ref(0), knownNames = reactive({});
const groups = ref([]), selectedGroupId = ref(''), groupMember = ref(''), showGroups = ref(false);
const groupName = ref(''), draftSearch = ref(''), draftMembers = ref([]), editingGroup = ref('');
const report = ref(null), timeline = ref(null), logs = ref(null), logFilter = ref('all');
const configs = reactive({ timeline: { page: 1, size: 10, preview: true }, logs: { page: 1, size: 10, preview: true }, pairs: { page: 1, size: 30, preview: false } });
const loading = reactive({ timeline: false, logs: false, pairs: false });
const tokens = { timeline: 0, logs: 0, pairs: 0, search: 0, draft: 0 };
let lifecycle = 0, reviewJob = 0, mounted = false, active = true, accountTimer;
const searchTimers = {};
const selectedGroup = computed(() => groups.value.find(g => g.id === selectedGroupId.value));
const groupOnly = computed(() => viewMode.value === 'group' && !groupMember.value);
const reviewIds = computed(() => viewMode.value === 'person' ? (selectedId.value ? [selectedId.value] : []) : groupMember.value ? [groupMember.value] : (selectedGroup.value?.members || []));
const viewKey = computed(() => JSON.stringify([account.value, reviewIds.value, appliedRange.value, groupOnly.value]));
const progressDone = computed(() => Number(status.value?.phase === 'deriving' ? status.value?.derived : status.value?.imported) || 0);
const progressTotal = computed(() => Number(status.value?.phase === 'deriving' ? status.value?.deriveTotal : status.value?.total) || 0);
const phaseLabel = computed(() => ({ missing: s('尚未创建', 'Not created'), created: s('已创建 · 尚未分析', 'Created · not analyzed'), ingesting: s('整理记录中', 'Importing'), deriving: s('建立索引中', 'Indexing'), ready: s('可打开', 'Ready to open'), incompatible: s('需要重建', 'Rebuild required') }[status.value?.phase] || status.value?.phase));
const sourceProfileLabel = computed(() => ({ uninspected: s('尚未检查（开始分析时检测）', 'Not inspected yet (checked when analysis starts)'), current: s('当前格式', 'Current format'), 'legacy-adapted': s('旧版格式 · 已兼容转换', 'Legacy format · compatibility-normalized') }[status.value?.sourceProfile] || status.value?.sourceProfile || '—'));
const warnings = computed(() => { try { return JSON.parse(status.value?.warnings || '[]'); } catch { return []; } });
const number = (n) => Number(n || 0).toLocaleString();
const date = (value) => value ? new Date(value).toLocaleString() : '—';
const duration = (ms) => `${(Math.round(Number(ms || 0) / 6000) / 10).toLocaleString()} ${s('分钟', 'min')}`;
const message = (e) => e instanceof Error ? e.message : String(e);
const valid = (generation, key = account.value) => active && generation === lifecycle && key === currentCacheAccount();
function clearResults() {
    reviewJob += 1;
    for (const kind of ['timeline', 'logs', 'pairs']) { tokens[kind] += 1; loading[kind] = false; }
    report.value = null; timeline.value = null; logs.value = null;
}
function clearPersonalView() {
    opened.value = false; selectedId.value = ''; search.value = ''; suggestions.value = []; draftSuggestions.value = [];
    selectedGroupId.value = ''; groupMember.value = ''; groups.value = []; showGroups.value = false; clearDraft();
    for (const key of Object.keys(knownNames)) delete knownNames[key];
    clearResults();
}
async function inspect() {
    if (busy.value) return;
    const generation = ++lifecycle;
    account.value = currentCacheAccount();
    const key = account.value;
    clearPersonalView(); status.value = null; error.value = ''; notice.value = ''; consent.value = false; inspecting.value = true;
    try {
        const result = await cacheRequest('status');
        if (!valid(generation, key)) return;
        status.value = result; groups.value = savedCacheGroups(key);
    } catch (e) { if (valid(generation, key)) error.value = message(e); }
    finally { if (generation === lifecycle) inspecting.value = false; }
}
async function createFile(rebuild) {
    if (busy.value || (!rebuild && !consent.value)) return;
    const generation = ++lifecycle, key = account.value;
    clearResults(); opened.value = false; busy.value = true; error.value = ''; confirmRebuild.value = false;
    try {
        const result = await cacheRequest(rebuild ? 'rebuild' : 'create');
        if (valid(generation, key)) { status.value = result; notice.value = s('分析文件已生成；尚未扫描任何历史记录。请手动开始首次分析。', 'Analysis file created; no history has been scanned. Start initial analysis explicitly.'); }
    } catch (e) { if (valid(generation, key)) error.value = message(e); }
    finally { if (generation === lifecycle) busy.value = false; }
}
async function analyze() {
    if (busy.value || (status.value?.phase === 'created' && !consent.value)) return;
    const generation = ++lifecycle, key = account.value;
    clearResults(); opened.value = false; busy.value = true; stopRequested.value = false; error.value = ''; notice.value = '';
    try {
        const result = await runCacheAnalysis({
            cancelled: () => stopRequested.value || !valid(generation, key),
            onProgress: value => { if (valid(generation, key)) status.value = value; }
        });
        if (valid(generation, key) && result) {
            status.value = result;
            notice.value = result.phase === 'ready'
                ? (result.sourceProfile === 'legacy-adapted'
                    ? s('旧版 VRCX 数据已通过兼容层整理到独立分析文件；原始数据库未修改。现在可以打开回顾。', 'Legacy VRCX data was normalized into the separate analysis file; the source database was not modified. The review is ready.')
                    : s('分析文件已就绪，点击“打开已生成的回顾”查看。', 'Analysis file is ready. Open the cached review to inspect it.'))
                : s('已暂停，批次进度已保存。下次手动继续即可。', 'Paused; the batch checkpoint is saved. Resume manually later.');
        }
    } catch (e) { if (valid(generation, key)) error.value = message(e); }
    finally { if (generation === lifecycle) busy.value = false; }
}
function openCache() {
    try { appliedRange.value = cacheRange(rangeKind.value, customStart.value, customEnd.value); opened.value = true; error.value = ''; notice.value = ''; }
    catch (e) { opened.value = true; error.value = message(e); }
}
function changeMode() { selectedId.value = ''; search.value = ''; groupMember.value = ''; suggestions.value = []; clearResults(); }
function rangeChanged() { if (rangeKind.value !== 'custom') applyRange(); }
function applyRange() { try { appliedRange.value = cacheRange(rangeKind.value, customStart.value, customEnd.value); error.value = ''; } catch (e) { error.value = message(e); } }
function pickPerson(person) { knownNames[person.id] = person.name; selectedId.value = person.id; search.value = person.name; suggestions.value = []; }
function choose(id, name) { knownNames[id] = name; if (viewMode.value === 'group' && selectedGroup.value?.members.includes(id)) groupMember.value = id; else { viewMode.value = 'person'; pickPerson({ id, name }); } }
function move(delta) { if (suggestions.value.length) highlight.value = (highlight.value + delta + suggestions.value.length) % suggestions.value.length; }
function pickHighlighted() { if (suggestions.value[highlight.value]) pickPerson(suggestions.value[highlight.value]); }
function findPeople(draft) {
    const kind = draft ? 'draft' : 'search', token = ++tokens[kind], generation = lifecycle, key = account.value;
    clearTimeout(searchTimers[kind]);
    const query = (draft ? draftSearch.value : search.value).trim();
    if (!query || !opened.value) { if (draft) draftSuggestions.value = []; else suggestions.value = []; return; }
    searchTimers[kind] = setTimeout(async () => {
        try {
            const result = await cacheRequest('people', { search: query, friendsOnly: friendsOnly.value });
            if (!valid(generation, key) || token !== tokens[kind]) return;
            for (const p of result.rows) knownNames[p.id] = p.name;
            if (draft) draftSuggestions.value = result.rows.filter(p => !draftMembers.value.includes(p.id));
            else { suggestions.value = result.rows; highlight.value = 0; }
        } catch (e) { if (valid(generation, key) && token === tokens[kind]) error.value = message(e); }
    }, 180);
}
function clearDraft() { editingGroup.value = ''; groupName.value = ''; draftSearch.value = ''; draftMembers.value = []; draftSuggestions.value = []; }
function addMember(person) { if (draftMembers.value.length < 200 && !draftMembers.value.includes(person.id)) draftMembers.value.push(person.id); knownNames[person.id] = person.name; draftSuggestions.value = []; draftSearch.value = ''; }
function editGroup(group) { editingGroup.value = group.id; groupName.value = group.name; draftMembers.value = [...group.members]; }
function storeGroup() {
    const name = groupName.value.trim();
    if (!name || !draftMembers.value.length || draftMembers.value.length > 200) return;
    const id = editingGroup.value || globalThis.crypto?.randomUUID?.() || `group-${Date.now()}`;
    const group = { id, name, members: [...new Set(draftMembers.value)] };
    const next = groups.value.filter(g => g.id !== id).concat(group);
    try { saveCacheGroups(next, account.value); groups.value = next; selectedGroupId.value = id; groupMember.value = ''; clearDraft(); }
    catch (e) { error.value = message(e); }
}
function removeGroup(id) { try { const next = groups.value.filter(g => g.id !== id); saveCacheGroups(next, account.value); groups.value = next; if (selectedGroupId.value === id) selectedGroupId.value = ''; } catch (e) { error.value = message(e); } }
const pagerProps = (kind, data) => ({ total: data?.total || 0, page: data?.page || configs[kind].page, size: configs[kind].size, preview: configs[kind].preview, busy: loading[kind] });
async function loadBlock(kind) {
    if (!opened.value || !reviewIds.value.length || status.value?.phase !== 'ready' || busy.value) return;
    const generation = lifecycle, key = account.value, context = viewKey.value, token = ++tokens[kind];
    const options = { ...appliedRange.value, userIds: [...reviewIds.value], groupOnly: groupOnly.value, page: configs[kind].page, size: configs[kind].size };
    if (kind !== 'pairs') options.filter = kind === 'timeline' ? 'encounters' : logFilter.value;
    loading[kind] = true;
    try {
        const result = await cacheRequest(kind === 'pairs' ? 'summary' : 'events', options);
        if (!valid(generation, key) || context !== viewKey.value || token !== tokens[kind]) return;
        error.value = '';
        if (kind === 'pairs') { report.value = result; for (const m of result.members) knownNames[m.id] = m.name; }
        else if (kind === 'timeline') timeline.value = result;
        else logs.value = result;
        configs[kind].page = kind === 'pairs' ? result.pairs.page : result.page;
    } catch (e) { if (valid(generation, key) && context === viewKey.value && token === tokens[kind]) { error.value = message(e); clearResults(); } }
    finally { if (token === tokens[kind]) loading[kind] = false; }
}
async function reloadReview() {
    clearResults();
    const job = reviewJob;
    for (const kind of ['timeline', 'logs']) Object.assign(configs[kind], { page: 1, size: 10, preview: true });
    configs.pairs.page = 1;
    if (!reviewIds.value.length || !opened.value) return;
    await loadBlock('pairs');
    if (job !== reviewJob) return;
    await loadBlock('timeline');
    if (job !== reviewJob) return;
    await loadBlock('logs');
}
function changePage(kind, page) { configs[kind].page = page; configs[kind].preview = false; loadBlock(kind); }
function changeSize(kind, size) { Object.assign(configs[kind], { page: 1, size, preview: false }); loadBlock(kind); }
function expand(kind) { changeSize(kind, 30); }
function changeLogFilter() { logs.value = null; Object.assign(configs.logs, { page: 1, size: 10, preview: true }); loadBlock('logs'); }
function title(row) { return ({ OnPlayerJoined: s('进入房间', 'Joined room'), OnPlayerLeft: s('退出房间', 'Left room'), Status: s('切换状态', 'Status changed'), Bio: s('更改 Bio', 'Bio changed'), GPS: s('位置变化', 'Location changed'), Online: s('上线', 'Online'), Offline: s('离线', 'Offline'), Avatar: s('头像变化', 'Avatar changed') })[row.type] || row.type; }
function detail(row) {
    const d = row.details || {};
    if (row.type === 'Bio') return `${d.previous_bio || '—'}\n↓\n${d.bio || '—'}`;
    if (row.type === 'Status') return `${d.previous_status || '—'} ${d.previous_status_description || ''}\n↓\n${d.status || '—'} ${d.status_description || ''}`;
    return row.location || Object.entries(d).map(([key, value]) => `${key}: ${value}`).join('\n');
}
async function copyPath() { try { await navigator.clipboard.writeText(status.value.path); notice.value = s('文件路径已复制。', 'File path copied.'); } catch { notice.value = s('无法使用剪贴板，请手动复制上方路径。', 'Clipboard unavailable. Copy the path shown above manually.'); } }
watch(viewKey, reloadReview);
function detach() { active = false; lifecycle += 1; stopRequested.value = true; busy.value = false; inspecting.value = false; clearPersonalView(); for (const timer of Object.values(searchTimers)) clearTimeout(timer); }
onMounted(() => { mounted = true; inspect(); accountTimer = setInterval(() => { if (active && account.value !== currentCacheAccount()) { lifecycle += 1; stopRequested.value = true; busy.value = false; inspect(); } }, 1000); });
onDeactivated(detach);
onActivated(() => { if (mounted && !active) { active = true; inspect(); } });
onBeforeUnmount(() => { detach(); clearInterval(accountTimer); });
</script>
<style scoped>
.ic-page{height:100%;overflow:auto;padding:clamp(16px,2.3vw,32px);background:var(--background);color:var(--foreground);font-size:14px;line-height:1.6}.ic-header{display:flex;align-items:center;justify-content:space-between;gap:20px;margin-bottom:22px}.ic-header small{font-size:11px;letter-spacing:.14em;color:var(--muted-foreground)}.ic-page h1{font-size:28px;font-weight:700;margin:8px 0}.ic-page h2{font-size:16px;font-weight:700;margin:0 0 12px}.ic-page p{margin:8px 0;color:var(--muted-foreground)}.ic-page button,.ic-page input,.ic-page select{font:inherit;border:1px solid var(--border);border-radius:8px;padding:8px 12px;background:var(--background);color:var(--foreground);min-height:36px}.ic-page button{cursor:pointer}.ic-page button:hover{background:var(--accent)}.ic-page button:disabled{opacity:.5;cursor:default}.ic-page :is(button,input,select,summary):focus-visible{outline:2px solid var(--ring);outline-offset:3px}.ic-page .ic-primary{background:var(--primary);color:var(--primary-foreground)}.ic-card{padding:20px;border:1px solid var(--border);border-radius:14px;background:var(--card);min-width:0;margin-bottom:18px}.ic-card-heading{display:flex;align-items:baseline;justify-content:space-between;gap:12px;flex-wrap:wrap}.ic-path{display:block;overflow-wrap:anywhere;user-select:text;font-size:12px;padding:10px;background:var(--muted);border-radius:8px}.ic-actions{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin:12px 0}.ic-consent,.ic-checkbox{display:flex;gap:10px;align-items:center;margin:14px 0}.ic-consent input,.ic-checkbox input{min-height:auto;width:16px;height:16px}.ic-confirm{padding:16px;background:var(--accent);border-radius:9px}.ic-confirm button{margin-right:10px}.ic-progress{display:flex;flex-direction:column;gap:8px;margin-top:18px}.ic-progress progress{width:100%;height:14px;accent-color:var(--primary)}.ic-meta{font-size:12px}.ic-error{padding:16px;border:1px solid var(--destructive);border-radius:10px;margin-bottom:18px;overflow-wrap:anywhere}.ic-notice{padding:12px;background:var(--accent);border-radius:8px}.ic-controls{display:flex;gap:14px;align-items:end;flex-wrap:wrap}.ic-controls label{display:flex;flex-direction:column;gap:6px;font-size:12px}.ic-controls .ic-checkbox{flex-direction:row;margin:0;min-height:36px}.ic-search{position:relative;flex:1;min-width:200px}.ic-suggestions{position:absolute;left:0;right:0;top:100%;z-index:10;max-height:300px;overflow:auto;border:1px solid var(--border);border-radius:8px;padding:6px;background:var(--popover);box-shadow:0 8px 22px #0003}.ic-suggestions button{display:flex;flex-direction:column;width:100%;text-align:left;border:0;overflow-wrap:anywhere}.ic-suggestions button[aria-selected=true]{background:var(--accent)}.ic-suggestions small{font-size:10px;color:var(--muted-foreground)}.ic-grid{display:grid;grid-template-columns:minmax(0,1.3fr) minmax(260px,1fr);gap:20px;align-items:start}.ic-left,.ic-right{min-width:0}.ic-timeline ol,.ic-logs ol{list-style:none;margin:0;padding:0}.ic-timeline li,.ic-logs li{padding:14px 0;border-bottom:1px solid var(--border);overflow-wrap:anywhere}.ic-timeline li>div,.ic-logs li>div{display:flex;gap:12px;align-items:baseline;justify-content:space-between;flex-wrap:wrap}.ic-page time,.ic-page small,.ic-timeline summary{font-size:11px;color:var(--muted-foreground)}.ic-logs pre{white-space:pre-wrap;overflow-wrap:anywhere;font:inherit;margin:8px 0}.ic-member,.ic-pair{border-top:1px solid var(--border);padding:14px 0}.ic-pair strong,.ic-pair small{display:block}.ic-pair button{border:0;padding:0;min-height:28px;text-align:left;color:var(--primary)}.ic-member button{width:100%;text-align:left}.ic-chips,.ic-draft-suggestions{display:flex;gap:6px;flex-wrap:wrap;margin-top:12px}.ic-chips button{font-size:12px;overflow-wrap:anywhere;max-width:100%}.ic-group-row{display:flex;align-items:center;gap:10px;border-top:1px solid var(--border);padding:10px 0}.ic-group-row strong{flex:1}.ic-empty{padding:30px;text-align:center}.ic-policy{border-top:1px solid var(--border);padding-top:18px;font-size:12px;color:var(--muted-foreground)}@media(max-width:1000px){.ic-grid{grid-template-columns:1fr}}@media(max-width:650px){.ic-header{align-items:flex-start;flex-direction:column}.ic-controls{flex-direction:column;align-items:stretch}.ic-search{min-width:0}.ic-group-row{flex-wrap:wrap}}
</style>
