<template>
    <nav class="cache-pager" :aria-label="label">
        <span>{{ zh ? '共' : 'Total' }} {{ total.toLocaleString() }} {{ zh ? '条' : 'records' }}</span>
        <label>{{ zh ? '每页' : 'Per page' }}
            <select :value="size" :disabled="busy" @change="$emit('size', Number($event.target.value))">
                <option v-for="n in [10, 20, 30, 50, 100]" :key="n" :value="n">{{ n }}</option>
            </select>
        </label>
        <button v-if="preview && total > 10" type="button" :disabled="busy" @click="$emit('expand')">{{ zh ? '再展开 20 条' : 'Show 20 more' }}</button>
        <template v-if="!preview && pages > 1">
            <button type="button" :disabled="busy || page <= 1" @click="$emit('page', 1)">{{ zh ? '首页' : 'First' }}</button>
            <button type="button" :disabled="busy || page <= 1" @click="$emit('page', page - 1)">{{ zh ? '上一页' : 'Previous' }}</button>
            <button v-for="n in visiblePages" :key="n" type="button" :disabled="busy" :aria-current="n === page ? 'page' : undefined" @click="$emit('page', n)">{{ n }}</button>
            <button type="button" :disabled="busy || page >= pages" @click="$emit('page', page + 1)">{{ zh ? '下一页' : 'Next' }}</button>
            <button type="button" :disabled="busy || page >= pages" @click="$emit('page', pages)">{{ zh ? '末页' : 'Last' }}</button>
            <span>{{ page }} / {{ pages }}</span>
        </template>
    </nav>
</template>
<script setup>
import { computed } from 'vue';
const props = defineProps({
    total: { type: Number, default: 0 }, page: { type: Number, default: 1 }, size: { type: Number, default: 10 },
    preview: Boolean, busy: Boolean, zh: Boolean, label: { type: String, default: 'Pagination' }
});
defineEmits(['page', 'size', 'expand']);
const pages = computed(() => Math.max(1, Math.ceil(props.total / props.size)));
const visiblePages = computed(() => {
    const start = Math.max(1, Math.min(props.page - 2, pages.value - 4));
    return Array.from({ length: Math.min(5, pages.value) }, (_, i) => start + i);
});
</script>
<style scoped>
.cache-pager{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin-top:16px;font-size:12px}.cache-pager label{display:flex;align-items:center;gap:6px}.cache-pager button,.cache-pager select{padding:6px 10px;border:1px solid var(--border);border-radius:7px;background:var(--background);color:var(--foreground)}.cache-pager button[aria-current=page]{background:var(--primary);color:var(--primary-foreground)}.cache-pager button:disabled{opacity:.5}.cache-pager :focus-visible{outline:2px solid var(--ring);outline-offset:2px}
</style>
