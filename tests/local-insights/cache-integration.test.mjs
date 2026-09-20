import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const read = path => readFileSync(new URL('../../' + path, import.meta.url), 'utf8');

test('cache page uses the shared navigation registry and the bottom default entry', () => {
    const definitions = read('src/shared/constants/ui.js');
    const layout = read('src/components/nav-menu/navLayoutDefaults.js');
    assert.match(definitions, /key: 'local-insights'.*labelKey: 'nav_tooltip.local_insights'.*routeName: 'local-insights'/);
    assert.match(layout, /key: 'direct-access' \},\s*\{ type: 'item', key: 'local-insights' \}/);
    assert(!read('src/components/nav-menu/NavMenu.vue').includes('LocalInsightsNav'));
});

test('active route points to the opt-in cache UI, not the old all-records loader', () => {
    const router = read('src/plugins/router.js');
    assert(router.includes("../features/local-insights/CacheReview.vue"));
    assert(!router.includes("../features/local-insights/ActivityReview.vue"));
    const page = read('src/features/local-insights/CacheReview.vue');
    assert(!page.includes('loadLocalRecords('));
    assert(!page.includes('indexRecords('));
    assert(!page.includes('buildGroupReport('));
});

test('new menu entry has English and Chinese labels', () => {
    for (const language of ['en', 'zh-CN', 'zh-TW']) {
        const json = JSON.parse(read(`src/localization/${language}.json`));
        assert.equal(typeof json.nav_tooltip.local_insights, 'string');
    }
});

test('native bridge is compiled through the partial SQLite integration', () => {
    assert(read('Dotnet/SQLite.cs').includes('public partial class SQLite'));
    assert(read('Dotnet/Insights/SQLiteInsightsBridge.cs').includes('InsightsRequest'));
});
