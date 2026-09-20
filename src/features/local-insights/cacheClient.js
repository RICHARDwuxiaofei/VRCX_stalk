import { dbVars } from '../../services/database';

export const currentCacheAccount = () => dbVars.userId;

export async function cacheRequest(action, options = {}) {
    const accountId = currentCacheAccount();
    if (!accountId) throw new Error('Please sign in before opening the analysis cache.');
    const bridge = globalThis.SQLite;
    if (!bridge || typeof bridge.InsightsRequest !== 'function') {
        throw new Error('The native v3 cache module is missing. Install the complete VRCX Insights v3 package, not only its HTML files.');
    }
    const json = await bridge.InsightsRequest(JSON.stringify({ ...options, action, accountId }));
    if (currentCacheAccount() !== accountId) throw new Error('The active account changed. Reload this page.');
    const result = JSON.parse(json);
    if (result.error) throw new Error(result.error);
    return result;
}

/** No automatic timer/polling. Called only after a user's Start/Resume/Update action. */
export async function runCacheAnalysis({ onProgress = () => {}, cancelled = () => false } = {}) {
    if (cancelled()) return null;
    let status = await cacheRequest('start');
    onProgress(status);
    while (status.phase === 'ingesting' || status.phase === 'deriving') {
        if (cancelled()) return status;
        status = await cacheRequest('step', { jobId: status.jobId });
        if (cancelled()) return status;
        onProgress(status);
        // Return control to the renderer between native transactions.
        await new Promise((resolve) => setTimeout(resolve, 20));
    }
    return status;
}

export function cacheRange(kind, start = '', end = '', now = Date.now()) {
    if (kind === 'permanent') return { since: 0, until: now };
    if (kind === 'custom') {
        const since = Date.parse(start);
        const until = end ? Date.parse(end) : now;
        if (!Number.isFinite(since) || !Number.isFinite(until) || since >= until) {
            throw new Error('Choose a valid start and end time.');
        }
        return { since, until };
    }
    const days = Number(kind);
    if (![1, 7, 30].includes(days)) throw new Error('Unsupported time range.');
    return { since: now - days * 86400000, until: now };
}

export function savedCacheGroups(account = currentCacheAccount()) {
    try {
        const value = JSON.parse(localStorage.getItem(`localInsights.groups.${account}`) || '[]');
        if (!Array.isArray(value)) return [];
        return value.filter((g) => g && typeof g.id === 'string' && typeof g.name === 'string' && Array.isArray(g.members))
            .map((g) => ({ id: g.id, name: g.name.slice(0, 64), members: [...new Set(g.members.filter((id) => typeof id === 'string'))].slice(0, 200) }));
    } catch { return []; }
}

export function saveCacheGroups(groups, account = currentCacheAccount()) {
    if (!account || account !== currentCacheAccount()) throw new Error('The active account changed. Reload this page.');
    localStorage.setItem(`localInsights.groups.${account}`, JSON.stringify(groups));
}
