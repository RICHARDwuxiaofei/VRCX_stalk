import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createI18n } from 'vue-i18n';
const state = vi.hoisted(() => ({ dbVars: { userId: 'usr_00000000-0000-0000-0000-000000000001' } }));
vi.mock('../../src/services/database', () => ({ dbVars: state.dbVars }));
import CacheReview from '../../src/features/local-insights/CacheReview.vue';
import CachePager from '../../src/features/local-insights/CachePager.vue';
import { cacheRange, savedCacheGroups } from '../../src/features/local-insights/cacheClient.js';

const account = state.dbVars.userId;
const ids = ['usr_10000000-0000-0000-0000-000000000000', 'usr_10000000-0000-0000-0000-000000000001'];
const people = [{ id: ids[0], name: 'Alex' }, { id: ids[1], name: 'Blair' }];
let wrapper, calls, status, bridge, failure, heldStep;
const now = Date.parse('2026-09-20T12:00:00Z');
const rows = Array.from({ length: 250 }, (_, i) => ({ id: i + 1, source: 'gamelog_join_leave', sourceId: i + 1, at: now - (i + 1) * 1000,
    type: i % 2 ? 'OnPlayerLeft' : 'OnPlayerJoined', userId: ids[0], displayName: 'Alex', location: 'wrld_demo:1', worldName: 'Demo room', details: {} }));
rows.push({ id: 251, source: 'feed_status', sourceId: 1, at: now - 100, type: 'Status', userId: ids[0], displayName: 'Alex', details: { previous_status: 'active', status: 'busy' } });
rows.push({ id: 252, source: 'feed_bio', sourceId: 1, at: now - 50, type: 'Bio', userId: ids[0], displayName: 'Alex', details: { previous_bio: 'old', bio: 'new' } });
function ready() { status = { ...status, exists: true, phase: 'ready', eventCount: 126023, sessionCount: 60010, updatedAt: new Date(now).toISOString() }; }
function start(locale = 'en') {
    wrapper = mount(CacheReview, { global: { plugins: [createI18n({ legacy: false, locale, messages: { en: {}, 'zh-CN': {} } })] } });
    return wrapper;
}
async function tick(ms = 200) { await vi.advanceTimersByTimeAsync(ms); await flushPromises(); }
async function open() { ready(); start(); await flushPromises(); await wrapper.find('[data-test=open]').trigger('click'); await flushPromises(); }
async function pickAlex() {
    await wrapper.find('[data-test=person-search]').setValue('Alex'); await tick();
    await wrapper.find('.ic-suggestions button').trigger('click'); await flushPromises();
}

beforeEach(() => {
    vi.useFakeTimers(); vi.setSystemTime(now); state.dbVars.userId = account; localStorage.clear(); calls = []; failure = ''; heldStep = null;
    status = { exists: false, phase: 'missing', path: 'C:\\Profile\\AnalyticsCache\\account\\analysis-v1.db', warnings: '[]', imported: 0, total: 2000, derived: 0, deriveTotal: 2000, eventCount: 0, rejected: 0 };
    bridge = vi.fn(async (json) => {
        const q = JSON.parse(json); calls.push(q);
        if (failure === q.action) return JSON.stringify({ error: 'synthetic database error' });
        let response;
        if (q.action === 'status') response = status;
        else if (q.action === 'create' || q.action === 'rebuild') { status = { ...status, exists: true, phase: 'created' }; response = status; }
        else if (q.action === 'start') { status = { ...status, phase: 'ingesting', jobId: 'test-job' }; response = status; }
        else if (q.action === 'step') {
            if (heldStep) return heldStep();
            ready(); response = status;
        }
        else if (q.action === 'people') response = { rows: people.filter(p => p.name.toLowerCase().includes(q.search.toLowerCase())) };
        else if (q.action === 'summary') response = {
            members: q.userIds.map(id => ({ id, name: people.find(p => p.id === id)?.name || id, observedMs: 600000, completeSessions: 2, incompleteSessions: 1 })),
            pairs: { rows: [{ leftId: ids[0], rightId: ids[1], leftName: 'Alex', rightName: 'Blair', observedMs: 300000, segments: 1 }], total: 1, page: 1, size: q.size }
        };
        else if (q.action === 'events') {
            let filtered = rows.filter(r => q.userIds.includes(r.userId) && r.at >= q.since && r.at < q.until);
            if (q.filter === 'encounters') filtered = filtered.filter(r => r.type.startsWith('OnPlayer'));
            if (q.filter === 'status') filtered = filtered.filter(r => r.type === 'Status');
            if (q.filter === 'bio') filtered = filtered.filter(r => r.type === 'Bio');
            if (q.filter === 'join') filtered = filtered.filter(r => r.type === 'OnPlayerJoined');
            if (q.filter === 'left') filtered = filtered.filter(r => r.type === 'OnPlayerLeft');
            response = { rows: filtered.slice((q.page - 1) * q.size, q.page * q.size), total: filtered.length, page: q.page, size: q.size };
        }
        else throw new Error('Unexpected action ' + q.action);
        return JSON.stringify(response);
    });
    vi.stubGlobal('SQLite', { InsightsRequest: bridge });
});
afterEach(() => { wrapper?.unmount(); wrapper = null; vi.unstubAllGlobals(); vi.useRealTimers(); });

describe('opt-in native cache page', () => {
    it('only reads metadata when opened and never starts automatic analysis', async () => {
        start(); await tick(5000);
        expect(calls.map(c => c.action)).toEqual(['status']);
        expect(wrapper.text()).toContain('Initial analysis may take a long time');
        expect(wrapper.find('[data-test=create]').attributes('disabled')).toBeDefined();
    });
    it('requires consent, creates a file, and waits for a second explicit start', async () => {
        start(); await flushPromises();
        await wrapper.find('[data-test=consent]').setValue(true);
        await wrapper.find('[data-test=create]').trigger('click'); await tick(1000);
        expect(calls.map(c => c.action)).toEqual(['status', 'create']);
        expect(wrapper.find('[data-test=start]').exists()).toBe(true);
        expect(wrapper.text()).toContain('no history has been scanned');
        await wrapper.find('[data-test=start]').trigger('click'); await tick(100);
        expect(calls.map(c => c.action)).toEqual(['status', 'create', 'start', 'step']);
        expect(wrapper.find('[data-test=open]').exists()).toBe(true);
    });
    it('reuses ready files without a source scan or implicit incremental update', async () => {
        await open(); await tick(1000);
        expect(calls.map(c => c.action)).toEqual(['status']);
        await pickAlex();
        expect(calls.some(c => ['start', 'step'].includes(c.action))).toBe(false);
        expect(wrapper.find('.ic-grid').exists()).toBe(true);
    });
    it('pauses between native batches and retains resumable progress', async () => {
        let resolveStep;
        heldStep = () => new Promise(resolve => { resolveStep = resolve; });
        await open();
        await wrapper.find('[data-test=update]').trigger('click'); await flushPromises();
        await wrapper.find('[data-test=pause]').trigger('click');
        resolveStep(JSON.stringify({ ...status, exists: true, phase: 'ingesting', jobId: 'test-job', imported: 2000 }));
        await tick(100);
        expect(calls.filter(c => c.action === 'step')).toHaveLength(1);
        expect(wrapper.find('[data-test=resume]').exists()).toBe(true);
        expect(wrapper.text()).toContain('checkpoint is saved');
    });
    it('requires confirmation before rebuilding and still does not start scanning', async () => {
        await open(); await wrapper.find('[data-test=rebuild]').trigger('click');
        expect(calls.some(c => c.action === 'rebuild')).toBe(false);
        await wrapper.find('[data-test=confirm-rebuild]').trigger('click'); await flushPromises();
        expect(calls.map(c => c.action)).toEqual(['status', 'rebuild']);
        expect(wrapper.find('[data-test=start]').exists()).toBe(true);
    });
    it('selects a person directly from autocomplete without a second selector', async () => {
        await open(); await pickAlex();
        expect(wrapper.find('.ic-right').text()).toContain('Alex');
        expect(wrapper.findAll('.ic-controls select')).toHaveLength(2);
        expect(calls.find(c => c.action === 'summary').userIds).toEqual([ids[0]]);
    });
    it('requests 10 rows, then 30, then real page 6', async () => {
        await open(); await pickAlex();
        expect(wrapper.findAll('.ic-timeline li')).toHaveLength(10);
        const expand = wrapper.find('.ic-timeline').findAll('button').find(b => b.text() === 'Show 20 more');
        await expand.trigger('click'); await flushPromises();
        expect(wrapper.findAll('.ic-timeline li')).toHaveLength(30);
        const next = () => wrapper.find('.ic-timeline').findAll('button').find(b => b.text() === 'Next');
        for (let i = 0; i < 5; i++) { await next().trigger('click'); await flushPromises(); }
        expect(calls.filter(c => c.action === 'events' && c.filter === 'encounters').at(-1)).toMatchObject({ page: 6, size: 30 });
        expect(wrapper.findAll('.ic-timeline li')).toHaveLength(30);
    });
    it('changing page size actually requests that count, rather than resetting to ten', async () => {
        await open(); await pickAlex();
        await wrapper.find('.ic-timeline .cache-pager select').setValue('20'); await flushPromises();
        expect(wrapper.findAll('.ic-timeline li')).toHaveLength(20);
        expect(calls.filter(c => c.action === 'events' && c.filter === 'encounters').at(-1).size).toBe(20);
    });
    it('filters Status and Bio using backend query parameters', async () => {
        await open(); await pickAlex();
        await wrapper.find('[data-test=log-filter]').setValue('status'); await flushPromises();
        expect(wrapper.findAll('.ic-logs li')).toHaveLength(1);
        expect(wrapper.find('.ic-logs').text()).toContain('Status changed');
        await wrapper.find('[data-test=log-filter]').setValue('bio'); await flushPromises();
        expect(wrapper.find('.ic-logs pre').text()).toContain('old');
        expect(wrapper.find('.ic-logs pre').text()).toContain('new');
    });
    it('permanent queries use the cache and epoch boundaries, not a 31-day loading limit', async () => {
        await open(); await pickAlex();
        await wrapper.find('[data-test=range]').setValue('permanent'); await flushPromises();
        expect(calls.filter(c => c.action === 'summary').at(-1).since).toBe(0);
        expect(calls.some(c => c.action === 'start')).toBe(false);
    });
    it('opens saved v2 groups, then a member, then returns to group overview', async () => {
        localStorage.setItem(`localInsights.groups.${account}`, JSON.stringify([{ id: 'g1', name: 'Friends', members: ids }]));
        await open(); await wrapper.find('[data-test=mode]').setValue('group');
        await wrapper.find('[data-test=group]').setValue('g1'); await flushPromises();
        expect(calls.filter(c => c.action === 'summary').at(-1).userIds).toEqual(ids);
        await wrapper.find('.ic-member button').trigger('click'); await flushPromises();
        expect(calls.filter(c => c.action === 'summary').at(-1).userIds).toEqual([ids[0]]);
        await wrapper.find('[data-test=group-back]').trigger('click'); await flushPromises();
        expect(calls.filter(c => c.action === 'summary').at(-1).userIds).toEqual(ids);
    });
    it('creates a local group from cached autocomplete results', async () => {
        await open(); await wrapper.find('[data-test=mode]').setValue('group');
        await wrapper.find('[data-test=manage-groups]').trigger('click');
        await wrapper.find('[data-test=group-name]').setValue('Raid team');
        await wrapper.find('[data-test=member-search]').setValue('Alex'); await tick();
        await wrapper.find('.ic-draft-suggestions button').trigger('click');
        await wrapper.find('[data-test=save-group]').trigger('click'); await flushPromises();
        expect(savedCacheGroups()[0]).toMatchObject({ name: 'Raid team', members: [ids[0]] });
    });
    it('never displays a prior account report after switching accounts', async () => {
        await open(); await pickAlex();
        state.dbVars.userId = 'usr_00000000-0000-0000-0000-000000000002';
        status = { ...status, exists: false, phase: 'missing' };
        await tick(1001);
        expect(wrapper.find('.ic-grid').exists()).toBe(false);
        expect(wrapper.find('[data-test=create]').exists()).toBe(true);
    });
    it('removes stale reports after a failed cache query', async () => {
        await open(); await pickAlex(); failure = 'summary';
        await wrapper.find('[data-test=range]').setValue('30'); await flushPromises();
        expect(wrapper.find('[role=alert]').text()).toContain('synthetic database error');
        expect(wrapper.find('.ic-grid').exists()).toBe(false);
    });
    it('renders the long-running analysis warning in Chinese', async () => {
        start('zh-CN'); await flushPromises();
        expect(wrapper.text()).toContain('首次分析可能耗费较长时间');
        expect(wrapper.text()).toContain('进入此页面不会自动分析');
    });
    it('ignores responses after unmounting the page', async () => {
        let release;
        bridge.mockImplementationOnce(() => new Promise(resolve => { release = resolve; }));
        start(); await flushPromises(); wrapper.unmount(); wrapper = null;
        release(JSON.stringify({ ...status, phase: 'ready', exists: true })); await flushPromises();
        expect(calls.some(c => c.action === 'start')).toBe(false);
    });
});

describe('pagination and range primitives', () => {
    it('shows five moving page buttons without limiting the dataset to five pages', async () => {
        wrapper = mount(CachePager, { props: { total: 3000, size: 30, page: 6 } });
        expect(wrapper.text()).toContain('6 / 100');
        const next = wrapper.findAll('button').find(b => b.text() === 'Next');
        await next.trigger('click'); expect(wrapper.emitted('page')[0]).toEqual([7]);
    });
    it('accepts multi-year custom ranges and validates reversed dates', () => {
        expect(cacheRange('custom', '2020-01-01T00:00:00Z', '2026-01-01T00:00:00Z').since).toBe(Date.parse('2020-01-01T00:00:00Z'));
        expect(() => cacheRange('custom', '2026-01-01', '2020-01-01')).toThrow();
        expect(cacheRange('1', '', '', now).until - cacheRange('1', '', '', now).since).toBe(86400000);
    });
});
