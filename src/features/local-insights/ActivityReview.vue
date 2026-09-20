<template>
    <section class="li-page" aria-labelledby="li-title">
        <header class="li-header">
            <div>
                <div class="li-eyebrow">VRCX INSIGHTS <span>{{ demoMode ? c.demo : c.local }}</span></div>
                <h1 id="li-title">{{ c.title }}</h1>
                <p>{{ c.subtitle }}</p>
            </div>
            <div class="li-actions">
                <button type="button" @click="loadDemo">{{ c.loadDemo }}</button>
                <button type="button" class="li-primary" :disabled="loading" @click="refresh">{{ demoMode ? c.live : c.refresh }}</button>
            </div>
        </header>

        <p v-if="demoMode" class="li-notice" role="status">{{ c.demoNotice }}</p>

        <div class="li-controls">
            <label>{{ t.range }}
                <select v-model="rangeKind" :disabled="loading" @change="onRangeChanged">
                    <option value="1">{{ t.day1 }}</option>
                    <option value="7">{{ t.day7 }}</option>
                    <option value="30">{{ t.day30 }}</option>
                    <option value="custom">{{ t.custom }}</option>
                    <option value="permanent">{{ t.permanent }}</option>
                </select>
            </label>
            <template v-if="rangeKind === 'custom'">
                <label>{{ t.start }}<input v-model="customSince" type="datetime-local" /></label>
                <label>{{ t.end }}<input v-model="customUntil" type="datetime-local" /></label>
                <button type="button" @click="refresh">{{ t.apply }}</button>
            </template>

            <label>{{ t.mode }}
                <select v-model="viewMode" @change="resetSelection">
                    <option value="person">{{ t.person }}</option>
                    <option value="group">{{ t.group }}</option>
                </select>
            </label>

            <div v-if="viewMode === 'person'" class="li-autocomplete">
                <label>{{ t.search }}
                    <input v-model="search" type="search" :placeholder="t.searchHint" @focus="showSuggestions = true" @input="showSuggestions = true" />
                </label>
                <div v-if="showSuggestions && search && suggestions.length" class="li-suggestions">
                    <button v-for="person in suggestions.slice(0, 12)" :key="person.id" type="button" @click="selectPerson(person)">
                        <strong>{{ person.name }}</strong><small>{{ person.id }}</small>
                    </button>
                </div>
                <div v-if="selectedId" class="li-selected">
                    <span>{{ selectedPersonName }}</span><button type="button" @click="selectedId = ''; search = ''">×</button>
                </div>
            </div>

            <template v-else>
                <label class="li-person">{{ t.group }}
                    <select v-model="selectedGroupId" :disabled="!groups.length" @change="selectedGroupMember = ''">
                        <option value="">{{ t.chooseGroup }}</option>
                        <option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }} ({{ group.members.length }})</option>
                    </select>
                </label>
                <button type="button" @click="showGroupEditor = !showGroupEditor">{{ t.manageGroups }}</button>
            </template>

            <label class="li-checkbox"><input v-model="friendsOnly" type="checkbox" />{{ c.friendsOnly }}</label>
        </div>

        <div v-if="showGroupEditor" class="li-card li-group-editor">
            <h2>{{ t.manageGroups }}</h2>
            <div class="li-group-form">
                <input v-model="newGroupName" :placeholder="t.groupName" />
                <button type="button" :disabled="!newGroupName.trim() || !draftGroupMembers.length" @click="saveGroup">{{ t.addGroup }}</button>
            </div>
            <div class="li-member-picker">
                <label v-for="person in eligiblePeople" :key="person.id">
                    <input v-model="draftGroupMembers" type="checkbox" :value="person.id" /> {{ person.name }}
                </label>
            </div>
            <div v-if="groups.length" class="li-groups-list">
                <div v-for="group in groups" :key="group.id">
                    <strong>{{ group.name }}</strong><span>{{ group.members.length }} {{ t.members }}</span>
                    <button type="button" @click="deleteGroup(group.id)">{{ t.delete }}</button>
                </div>
            </div>
        </div>

        <div v-if="loading" class="li-empty" role="status">{{ c.loading }}</div>
        <div v-else-if="error" class="li-empty li-error" role="alert"><h2>{{ c.error }}</h2><p>{{ error }}</p></div>
        <div v-else-if="viewMode === 'person' && !report" class="li-empty"><h2>{{ eligiblePeople.length ? t.choosePerson : c.empty }}</h2><p>{{ c.chooseHint }}</p></div>
        <div v-else-if="viewMode === 'group' && !selectedGroup" class="li-empty"><h2>{{ t.chooseGroup }}</h2><p>{{ t.groupHint }}</p></div>

        <template v-else-if="viewMode === 'group' && selectedGroup">
            <div class="li-profile">
                <span class="li-avatar">G</span>
                <div><h2>{{ selectedGroup.name }}</h2><p>{{ selectedGroup.members.length }} {{ t.members }}</p></div>
                <button v-if="selectedGroupMember" type="button" @click="selectedGroupMember = ''">{{ t.backToGroup }}</button>
            </div>

            <div v-if="!selectedGroupMember" class="li-grid">
                <article class="li-card">
                    <div class="li-card-heading"><h2>{{ t.groupMembers }}</h2><span>{{ groupReport?.members.length || 0 }}</span></div>
                    <div class="li-groups-list">
                        <button v-for="member in groupReport?.members || []" :key="member.id" type="button" class="li-member-row" @click="selectedGroupMember = member.id">
                            <span><strong>{{ member.name }}</strong><small>{{ duration(member.observedMs) }} · {{ member.completeSessions }} {{ c.sessions }}</small></span>
                            <span>›</span>
                        </button>
                    </div>
                </article>
                <article class="li-card">
                    <div class="li-card-heading"><h2>{{ t.groupPairs }}</h2><span>{{ groupReport?.pairings.length || 0 }}</span></div>
                    <div v-if="!groupReport?.pairings.length" class="li-small">{{ c.noPeers }}</div>
                    <div v-for="pair in groupReport?.pairings || []" :key="`${pair.leftId}|${pair.rightId}`" class="li-pair">
                        <strong>{{ pair.leftName }} ↔ {{ pair.rightName }}</strong>
                        <span>{{ duration(pair.observedMs) }} · {{ pair.segments }} {{ t.segments }}</span>
                    </div>
                </article>
            </div>
        </template>

        <template v-if="activeReport">
            <div class="li-profile">
                <span class="li-avatar">{{ activeReport.target.name.slice(0, 1) }}</span>
                <div><h2>{{ activeReport.target.name }}</h2><p>{{ formatDate(activeReport.since) }} - {{ formatDate(activeReport.until) }}</p></div>
                <button type="button" class="li-copy" @click="copySummary">{{ c.copy }}</button>
            </div>

            <div class="li-grid">
                <article class="li-card li-timeline">
                    <div class="li-card-heading"><h2>{{ c.timeline }}</h2><span>{{ activeReport.timeline.length }} {{ c.events }}</span></div>
                    <ol>
                        <li v-for="event in pagedTimeline" :key="`${event.type}-${event.rowId}-${event.at}`">
                            <span class="li-event-dot" :class="{ 'li-left': event.type === 'OnPlayerLeft' }"></span>
                            <div class="li-event-body">
                                <div class="li-event-top"><strong>{{ event.type === 'OnPlayerJoined' ? c.joined : c.left }}</strong><time>{{ formatDate(event.at) }}</time></div>
                                <div class="li-world">{{ event.worldName }}</div>
                            </div>
                        </li>
                    </ol>
                    <div class="li-pager">
                        <button v-if="timelineStage === 10 && activeReport.timeline.length > 10" type="button" @click="timelineStage = 30">{{ t.expand20 }}</button>
                        <template v-else-if="timelineStage === 30 && activeReport.timeline.length > 30">
                            <label>{{ t.perPage }}<select v-model.number="timelinePageSize"><option :value="10">10</option><option :value="20">20</option><option :value="30">30</option><option :value="50">50</option></select></label>
                            <button v-for="page in timelinePages" :key="page" type="button" :class="{ 'li-primary': timelinePage === page }" @click="timelinePage = page">{{ page }}</button>
                        </template>
                    </div>
                </article>

                <aside class="li-summary">
                    <div class="li-metrics">
                        <div class="li-card"><span>{{ c.duration }}</span><strong>{{ duration(activeReport.observedMs) }}</strong></div>
                        <div class="li-card"><span>{{ c.sessions }}</span><strong>{{ activeReport.completeSessions }}</strong></div>
                    </div>
                    <article class="li-card"><h2>{{ c.ranking }}</h2>
                        <div v-for="person in activeReport.companions.slice(0, 30)" :key="person.id" class="li-bar-row">
                            <div><span>{{ person.name }}</span><strong>{{ duration(person.observedMs) }}</strong></div>
                        </div>
                    </article>
                </aside>
            </div>

            <article class="li-card li-logs">
                <div class="li-card-heading"><h2>{{ t.logs }}</h2><span>{{ filteredLogs.length }}</span></div>
                <div class="li-log-controls">
                    <label>{{ t.logType }}
                        <select v-model="logFilter">
                            <option value="all">{{ t.all }}</option>
                            <option value="join">{{ t.joinRoom }}</option>
                            <option value="left">{{ t.leaveRoom }}</option>
                            <option value="status">{{ t.statusChange }}</option>
                            <option value="bio">{{ t.bioChange }}</option>
                        </select>
                    </label>
                    <label>{{ t.perPage }}<select v-model.number="logPageSize"><option :value="10">10</option><option :value="20">20</option><option :value="50">50</option><option :value="100">100</option></select></label>
                </div>
                <ul class="li-log-list">
                    <li v-for="row in pagedLogs" :key="`${row.type}-${row.rowId}-${row.created_at}`">
                        <time>{{ formatDate(row.created_at) }}</time>
                        <strong>{{ logTitle(row) }}</strong>
                        <span>{{ logDetail(row) }}</span>
                    </li>
                </ul>
                <div class="li-pager">
                    <button v-if="logStage === 10 && filteredLogs.length > 10" type="button" @click="logStage = 30">{{ t.expand20 }}</button>
                    <template v-else-if="logStage === 30 && filteredLogs.length > 30">
                        <button v-for="page in logPages" :key="page" type="button" :class="{ 'li-primary': logPage === page }" @click="logPage = page">{{ page }}</button>
                    </template>
                </div>
            </article>
        </template>

        <footer class="li-policy"><details><summary>{{ c.policy }}</summary><p>{{ c.evidence }}</p><p>{{ c.privacy }}</p></details></footer>
    </section>
</template>

<script setup>
import { computed, onMounted, ref, shallowRef, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { indexRecords, listPeople, buildReport, buildGroupReport } from './analytics.mjs';
import { loadLocalRecords, currentAccountId } from './reader.js';
import { en, zh } from './copy.mjs';
import fixture from '../../../tests/local-insights/fixtures.json';

defineOptions({ name: 'LocalInsights' });
const { locale } = useI18n();
const c = computed(() => String(locale.value).startsWith('zh') ? zh : en);
const t = computed(() => String(locale.value).startsWith('zh') ? {
    range:'时间范围', day1:'24 小时', day7:'7 天', day30:'30 天', custom:'自定义', permanent:'永久', start:'开始', end:'结束', apply:'应用',
    mode:'查看类型', person:'看人', group:'看分组', search:'筛选名称', searchHint:'输入名字，下面直接选人', choosePerson:'输入名字并从联想中选择玩家',
    chooseGroup:'选择分组', manageGroups:'管理分组', groupName:'分组名称', addGroup:'添加分组', members:'人', delete:'删除', groupHint:'选择一个分组查看组内共同游玩记录。',
    groupMembers:'组内成员', groupPairs:'组内相互共同游玩回顾', segments:'段', backToGroup:'返回分组概览',
    expand20:'再展开 20 条', perPage:'每页', logs:'日志', logType:'日志类型', all:'全部', joinRoom:'进入房间', leaveRoom:'退出房间', statusChange:'切换状态', bioChange:'更改 Bio'
} : {
    range:'Range', day1:'24 hours', day7:'7 days', day30:'30 days', custom:'Custom', permanent:'Permanent', start:'Start', end:'End', apply:'Apply',
    mode:'View', person:'Person', group:'Group', search:'Filter names', searchHint:'Type a name and select a suggestion', choosePerson:'Type a name and select a player',
    chooseGroup:'Choose a group', manageGroups:'Manage groups', groupName:'Group name', addGroup:'Add group', members:'members', delete:'Delete', groupHint:'Choose a group to review internal shared sessions.',
    groupMembers:'Group members', groupPairs:'Shared sessions inside group', segments:'segments', backToGroup:'Back to group overview',
    expand20:'Expand 20 more', perPage:'Per page', logs:'Logs', logType:'Log type', all:'All', joinRoom:'Joined room', leaveRoom:'Left room', statusChange:'Status changed', bioChange:'Bio changed'
});

const index = shallowRef(null);
const friendIds = shallowRef(new Set());
const activityLogs = shallowRef([]);
const selectedId = ref('');
const search = ref('');
const friendsOnly = ref(false);
const loading = ref(false);
const error = ref('');
const demoMode = ref(false);
const showSuggestions = ref(false);
const viewMode = ref('person');
const rangeKind = ref('7');
const customSince = ref('');
const customUntil = ref('');
const groups = ref(loadGroups());
const selectedGroupId = ref('');
const selectedGroupMember = ref('');
const showGroupEditor = ref(false);
const newGroupName = ref('');
const draftGroupMembers = ref([]);
const timelineStage = ref(10);
const timelinePage = ref(1);
const timelinePageSize = ref(30);
const logStage = ref(10);
const logPage = ref(1);
const logPageSize = ref(30);
const logFilter = ref('all');

const eligiblePeople = computed(() => index.value ? listPeople(index.value, friendsOnly.value ? friendIds.value : null) : []);
const suggestions = computed(() => {
    const q = search.value.trim().toLocaleLowerCase();
    if (!q) return [];
    return eligiblePeople.value.filter((p) => p.name.toLocaleLowerCase().includes(q) || p.id.toLocaleLowerCase().includes(q));
});
const selectedPersonName = computed(() => eligiblePeople.value.find((p) => p.id === selectedId.value)?.name || selectedId.value);
const selectedGroup = computed(() => groups.value.find((group) => group.id === selectedGroupId.value) || null);
const report = computed(() => {
    if (!index.value || !selectedId.value) return null;
    try { return buildReport(index.value, selectedId.value, { friendIds: friendIds.value }); } catch { return null; }
});
const groupReport = computed(() => {
    if (!index.value || !selectedGroup.value) return null;
    try { return buildGroupReport(index.value, selectedGroup.value.members, { friendIds: friendIds.value }); } catch { return null; }
});
const groupMemberReport = computed(() => {
    if (!index.value || !selectedGroupMember.value) return null;
    try { return buildReport(index.value, selectedGroupMember.value, { friendIds: friendIds.value }); } catch { return null; }
});
const activeReport = computed(() => viewMode.value === 'person' ? report.value : groupMemberReport.value);

const pagedTimeline = computed(() => {
    const rows = activeReport.value?.timeline || [];
    if (timelineStage.value === 10) return rows.slice(0, 10);
    if (timelineStage.value === 30 && rows.length <= 30) return rows.slice(0, 30);
    const start = (timelinePage.value - 1) * timelinePageSize.value;
    return rows.slice(start, start + timelinePageSize.value);
});
const timelinePages = computed(() => Math.min(5, Math.max(1, Math.ceil((activeReport.value?.timeline.length || 0) / timelinePageSize.value))));

const targetIds = computed(() => {
    if (viewMode.value === 'person') return selectedId.value ? new Set([selectedId.value]) : new Set();
    if (selectedGroupMember.value) return new Set([selectedGroupMember.value]);
    return new Set(selectedGroup.value?.members || []);
});
const filteredLogs = computed(() => {
    let rows = activityLogs.value.filter((row) => !targetIds.value.size || targetIds.value.has(row.userId));
    if (logFilter.value === 'join') rows = rows.filter((row) => row.type === 'OnPlayerJoined');
    else if (logFilter.value === 'left') rows = rows.filter((row) => row.type === 'OnPlayerLeft');
    else if (logFilter.value === 'status') rows = rows.filter((row) => row.type === 'Status');
    else if (logFilter.value === 'bio') rows = rows.filter((row) => row.type === 'Bio');
    return rows;
});
const pagedLogs = computed(() => {
    const rows = filteredLogs.value;
    if (logStage.value === 10) return rows.slice(0, 10);
    if (logStage.value === 30 && rows.length <= 30) return rows.slice(0, 30);
    const start = (logPage.value - 1) * logPageSize.value;
    return rows.slice(start, start + logPageSize.value);
});
const logPages = computed(() => Math.min(5, Math.max(1, Math.ceil(filteredLogs.value.length / logPageSize.value))));

function duration(ms) { return `${(Math.round(ms / 6000) / 10).toLocaleString()} ${c.value.minutes}`; }
function formatDate(value) { try { return new Date(value).toLocaleString(); } catch { return String(value); } }
function rangeRequest() {
    if (rangeKind.value === 'permanent') return { kind: 'permanent' };
    if (rangeKind.value === 'custom') return { kind: 'custom', since: customSince.value, until: customUntil.value || new Date().toISOString() };
    return { kind: 'days', days: Number(rangeKind.value) };
}
function onRangeChanged() { if (rangeKind.value !== 'custom') refresh(); }
function selectPerson(person) { selectedId.value = person.id; search.value = person.name; showSuggestions.value = false; resetPaging(); }
function resetSelection() { selectedId.value = ''; search.value = ''; selectedGroupMember.value = ''; resetPaging(); }
function resetPaging() { timelineStage.value = 10; timelinePage.value = 1; logStage.value = 10; logPage.value = 1; }
function loadGroups() { try { return JSON.parse(localStorage.getItem(`localInsights.groups.${currentAccountId()}`) || '[]'); } catch { return []; } }
function persistGroups() { try { localStorage.setItem(`localInsights.groups.${currentAccountId()}`, JSON.stringify(groups.value)); } catch { /* optional */ } }
function saveGroup() {
    const name = newGroupName.value.trim();
    if (!name || !draftGroupMembers.value.length) return;
    groups.value.push({ id: `grp-${Date.now()}`, name, members: [...new Set(draftGroupMembers.value)] });
    newGroupName.value = ''; draftGroupMembers.value = []; persistGroups();
}
function deleteGroup(id) {
    groups.value = groups.value.filter((group) => group.id !== id);
    if (selectedGroupId.value === id) selectedGroupId.value = '';
    persistGroups();
}
async function refresh() {
    loading.value = true; error.value = ''; demoMode.value = false; resetPaging();
    try {
        const data = await loadLocalRecords(rangeRequest());
        index.value = indexRecords(data.records, data);
        friendIds.value = data.friendIds;
        activityLogs.value = data.activityLogs || [];
        groups.value = loadGroups();
    } catch (e) {
        index.value = null; activityLogs.value = []; error.value = e instanceof Error ? e.message : String(e);
    } finally { loading.value = false; }
}
function loadDemo() {
    demoMode.value = true; error.value = ''; search.value = ''; selectedId.value = fixture.targetId;
    friendIds.value = new Set(fixture.friendIds); index.value = indexRecords(fixture.records, fixture); activityLogs.value = fixture.records;
    resetPaging();
}
async function copySummary() {
    if (!activeReport.value) return;
    try { await navigator.clipboard.writeText(JSON.stringify(activeReport.value, null, 2)); } catch { /* ignored */ }
}
function logTitle(row) {
    if (row.type === 'OnPlayerJoined') return t.value.joinRoom;
    if (row.type === 'OnPlayerLeft') return t.value.leaveRoom;
    if (row.type === 'Status') return t.value.statusChange;
    if (row.type === 'Bio') return t.value.bioChange;
    return row.type;
}
function logDetail(row) {
    if (row.type === 'Status') return `${row.previousStatus || '—'} → ${row.status || '—'} ${row.statusDescription || ''}`;
    if (row.type === 'Bio') return `${row.previousBio || '—'} → ${row.bio || '—'}`;
    return row.location || '';
}

watch([selectedId, selectedGroupId, selectedGroupMember, logFilter, timelinePageSize, logPageSize], resetPaging);
onMounted(refresh);
</script>

<style scoped>
.li-page{height:100%;overflow:auto;padding:clamp(16px,2.3vw,32px);color:var(--foreground);background:var(--background);font-size:14px;line-height:1.55}.li-header{display:flex;justify-content:space-between;gap:20px;align-items:center;margin-bottom:24px}.li-eyebrow{font-size:11px;font-weight:750;letter-spacing:.16em;color:var(--muted-foreground)}.li-eyebrow span{margin-left:12px;letter-spacing:0;padding:2px 9px;border:1px solid var(--border);border-radius:20px}.li-page h1{font-size:28px;margin:8px 0}.li-page h2{font-size:15px;margin:0 0 12px}.li-actions{display:flex;gap:8px}.li-page button,.li-page input,.li-page select{font:inherit;border:1px solid var(--border);border-radius:9px;background:var(--background);color:var(--foreground);padding:8px 12px;min-height:38px}.li-page button{cursor:pointer}.li-primary{background:var(--primary)!important;color:var(--primary-foreground)!important}.li-controls{display:flex;flex-wrap:wrap;gap:14px;align-items:end;padding:18px;background:var(--card);border:1px solid var(--border);border-radius:14px;margin-bottom:20px}.li-controls label{display:flex;flex-direction:column;gap:6px;font-size:12px;color:var(--muted-foreground)}.li-person{min-width:220px}.li-checkbox{flex-direction:row!important;align-items:center}.li-checkbox input{width:16px;height:16px;min-height:auto}.li-autocomplete{position:relative;min-width:280px}.li-suggestions{position:absolute;z-index:20;top:64px;left:0;right:0;background:var(--popover);border:1px solid var(--border);border-radius:10px;max-height:320px;overflow:auto;padding:6px}.li-suggestions button{display:flex;width:100%;justify-content:space-between;text-align:left;border:0}.li-suggestions small{color:var(--muted-foreground)}.li-selected{display:flex;align-items:center;gap:8px;margin-top:6px}.li-selected button{min-height:auto;padding:2px 8px}.li-card{background:var(--card);border:1px solid var(--border);border-radius:14px;padding:20px}.li-group-editor{margin-bottom:20px}.li-group-form{display:flex;gap:8px}.li-member-picker{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:8px;max-height:260px;overflow:auto;margin:14px 0}.li-member-picker label{display:flex;gap:8px;align-items:center}.li-groups-list{display:flex;flex-direction:column;gap:8px}.li-groups-list>div,.li-member-row{display:flex;justify-content:space-between;align-items:center;gap:12px}.li-member-row{width:100%;text-align:left}.li-member-row span:first-child{display:flex;flex-direction:column}.li-grid{display:grid;grid-template-columns:minmax(0,1.3fr) minmax(280px,1fr);gap:20px;align-items:start}.li-profile{display:flex;align-items:center;gap:14px;margin:20px 0}.li-avatar{width:48px;height:48px;display:grid;place-items:center;border-radius:14px;background:var(--accent);font-size:22px;font-weight:750}.li-copy{margin-left:auto}.li-card-heading{display:flex;justify-content:space-between;gap:12px}.li-timeline ol{list-style:none;padding:0}.li-timeline li{display:flex;gap:14px;padding:0 0 20px}.li-event-dot{width:11px;height:11px;margin-top:6px;border-radius:50%;background:var(--primary)}.li-event-dot.li-left{background:var(--card);border:2px solid var(--muted-foreground)}.li-event-body{flex:1}.li-event-top{display:flex;justify-content:space-between;gap:12px}.li-event-top time,.li-small{color:var(--muted-foreground);font-size:11px}.li-summary{display:flex;flex-direction:column;gap:16px}.li-metrics{display:grid;grid-template-columns:1fr 1fr;gap:12px}.li-metrics span{display:block;color:var(--muted-foreground);font-size:11px}.li-metrics strong{font-size:24px}.li-bar-row,.li-pair{padding:10px 0;border-top:1px solid var(--border);display:flex;justify-content:space-between;gap:12px}.li-pager{display:flex;gap:6px;flex-wrap:wrap;align-items:center;margin-top:12px}.li-pager label{display:flex;align-items:center;gap:6px}.li-logs{margin-top:20px}.li-log-controls{display:flex;gap:12px;flex-wrap:wrap;margin-bottom:12px}.li-log-controls label{display:flex;align-items:center;gap:6px}.li-log-list{list-style:none;padding:0;margin:0}.li-log-list li{display:grid;grid-template-columns:180px 140px 1fr;gap:12px;padding:10px 0;border-top:1px solid var(--border)}.li-log-list time{font-size:11px;color:var(--muted-foreground)}.li-empty{text-align:center;padding:64px 24px;border:1px dashed var(--border);border-radius:14px}.li-error{border-color:var(--destructive)}.li-notice{padding:12px 16px;background:var(--accent);border-radius:9px}.li-policy{border-top:1px solid var(--border);margin-top:24px;padding-top:16px;font-size:12px;color:var(--muted-foreground)}@media(max-width:950px){.li-grid{grid-template-columns:1fr}}@media(max-width:650px){.li-header{align-items:flex-start;flex-direction:column}.li-controls{flex-direction:column;align-items:stretch}.li-log-list li{grid-template-columns:1fr}.li-autocomplete{min-width:0}}
</style>
