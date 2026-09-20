import { beforeEach, afterEach, describe, it, expect, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { createI18n } from 'vue-i18n';
const mock = vi.hoisted(() => ({ load: vi.fn(), account: 'demo:observer' }));
vi.mock('../../src/features/local-insights/reader.js', () => ({
    loadLocalRecords: mock.load,
    currentAccountId: () => mock.account
}));
import ActivityReview from '../../src/features/local-insights/ActivityReview.vue';
import fixture from './fixtures.json';

let wrapper;
function start(locale = 'en') {
    wrapper = mount(ActivityReview, {
        global: { plugins: [createI18n({ legacy: false, locale, messages: { en: {}, 'zh-CN': {} } })] }
    });
    return wrapper;
}
beforeEach(() => {
    mock.account = fixture.observerId;
    localStorage.clear();
    mock.load.mockReset();
    mock.load.mockResolvedValue({ ...fixture, records: [], activityLogs: [], friendIds: new Set() });
});
afterEach(() => wrapper?.unmount());

describe('local review UI v2', () => {
    it('offers 24h, 7d, 30d, custom and permanent ranges', async () => {
        start(); await flushPromises();
        const options = wrapper.find('.li-controls select').findAll('option').map((o) => o.text());
        expect(options).toEqual(expect.arrayContaining(['24 hours', '7 days', '30 days', 'Custom', 'Permanent']));
    });

    it('uses autocomplete selection instead of a second player selector', async () => {
        start(); await flushPromises();
        await wrapper.find('.li-actions button').trigger('click');
        await flushPromises();
        const search = wrapper.find('input[type=search]');
        await search.setValue('Alex');
        await flushPromises();
        expect(wrapper.find('.li-suggestions').text()).toContain('Alex');
        expect(wrapper.find('.li-suggestions button').exists()).toBe(true);
        await wrapper.find('.li-suggestions button').trigger('click');
        expect(wrapper.find('.li-profile h2').text()).toContain('Alex');
        expect(wrapper.findAll('.li-controls select').some((select) => select.classes().includes('li-person'))).toBe(false);
    });

    it('switches to group mode and can save a local group', async () => {
        start(); await flushPromises();
        await wrapper.find('.li-actions button').trigger('click'); await flushPromises();
        const selects = wrapper.findAll('.li-controls select');
        await selects[1].setValue('group');
        await flushPromises();
        const manage = wrapper.findAll('.li-controls button').find((button) => button.text() === 'Manage groups');
        await manage.trigger('click');
        await wrapper.find('.li-group-form input').setValue('Raid Team');
        const boxes = wrapper.findAll('.li-member-picker input[type=checkbox]');
        await boxes[0].setValue(true);
        await boxes[1].setValue(true);
        await wrapper.find('.li-group-form button').trigger('click');
        expect(wrapper.text()).toContain('Raid Team');
        expect(JSON.parse(localStorage.getItem('localInsights.groups.demo:observer'))[0].members.length).toBe(2);
    });

    it('shows 10 encounter rows first and expands by 20', async () => {
        const many = [];
        for (let i = 0; i < 35; i += 1) {
            many.push({
                rowId: String(100 + i),
                created_at: new Date(Date.parse('2026-09-19T18:05:00Z') + i * 60000).toISOString(),
                type: i % 2 ? 'OnPlayerLeft' : 'OnPlayerJoined',
                userId: fixture.targetId,
                displayName: 'Alex (demo)',
                location: fixture.records[0].location
            });
        }
        mock.load.mockResolvedValue({ ...fixture, records: [fixture.records[0], ...many], activityLogs: many, friendIds: new Set(fixture.friendIds) });
        start(); await flushPromises();
        const search = wrapper.find('input[type=search]');
        await search.setValue('Alex'); await flushPromises();
        await wrapper.find('.li-suggestions button').trigger('click'); await flushPromises();
        expect(wrapper.findAll('.li-timeline li')).toHaveLength(10);
        await wrapper.find('.li-pager button').trigger('click'); await flushPromises();
        expect(wrapper.findAll('.li-timeline li')).toHaveLength(30);
    });

    it('filters local activity logs by join/status/bio types', async () => {
        const logs = [
            { rowId:'l1', created_at:'2026-09-19T21:00:00Z', type:'OnPlayerJoined', userId:fixture.targetId, displayName:'Alex', location:'wrld_demo:1' },
            { rowId:'l2', created_at:'2026-09-19T21:01:00Z', type:'Status', userId:fixture.targetId, displayName:'Alex', previousStatus:'active', status:'busy' },
            { rowId:'l3', created_at:'2026-09-19T21:02:00Z', type:'Bio', userId:fixture.targetId, displayName:'Alex', previousBio:'old', bio:'new' }
        ];
        mock.load.mockResolvedValue({ ...fixture, records: fixture.records, activityLogs: logs, friendIds: new Set(fixture.friendIds) });
        start(); await flushPromises();
        await wrapper.find('input[type=search]').setValue('Alex'); await flushPromises();
        await wrapper.find('.li-suggestions button').trigger('click'); await flushPromises();
        const filter = wrapper.find('.li-log-controls select');
        await filter.setValue('status'); await flushPromises();
        expect(wrapper.findAll('.li-log-list li')).toHaveLength(1);
        expect(wrapper.find('.li-log-list').text()).toContain('Status changed');
    });

    it('keeps failed reads from displaying stale reports', async () => {
        start(); await flushPromises();
        await wrapper.find('.li-actions button').trigger('click'); await flushPromises();
        mock.load.mockRejectedValueOnce(new Error('database is locked'));
        await wrapper.find('.li-primary').trigger('click'); await flushPromises();
        expect(wrapper.find('[role=alert]').text()).toContain('database is locked');
        expect(wrapper.find('.li-grid').exists()).toBe(false);
    });
});
