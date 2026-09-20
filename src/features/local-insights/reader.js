import sqliteService from '../../services/sqlite.js';
import { dbVars } from '../../services/database';
import { readDatabase } from './readDatabase.mjs';

export function currentAccountId() {
    return dbVars.userId;
}

export function resolveRange(range = { kind: 'days', days: 7 }) {
    const until = range.until ? new Date(range.until) : new Date();
    if (!Number.isFinite(until.getTime())) throw new Error('Invalid end time.');
    let since;
    if (range.kind === 'permanent') {
        since = new Date('2000-01-01T00:00:00.000Z');
    } else if (range.kind === 'custom') {
        since = new Date(range.since);
        if (!Number.isFinite(since.getTime())) throw new Error('Invalid start time.');
    } else {
        const days = Number(range.days ?? 7);
        if (![1, 7, 30].includes(days)) throw new Error('Unsupported time range.');
        since = new Date(until.getTime() - days * 86400000);
    }
    if (since >= until) throw new Error('Start time must be before end time.');
    return { since: since.toISOString(), until: until.toISOString() };
}

export async function loadLocalRecords(range = { kind: 'days', days: 7 }) {
    const observerId = dbVars.userId;
    const { since, until } = resolveRange(range);
    return readDatabase(sqliteService.execute.bind(sqliteService), {
        since, until, observerId, userPrefix: dbVars.userPrefix,
        isCurrent: () => dbVars.userId === observerId
    });
}
