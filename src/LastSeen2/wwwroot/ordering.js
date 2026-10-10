import Sortable from './vendor/sortablejs/sortable.complete.esm.js';
let instances = [], root, snapshot, pending = false;
export function dispose() {
    for (const instance of instances) instance.destroy();
    instances = []; root = null; snapshot = null; pending = false;
}
export function sync(element, receiver) {
    if (root !== element || instances.some(s => !s.el.isConnected)) {
        dispose(); root = element;
    }
    if (!root) return;
    const lists = [...root.querySelectorAll('[data-order-list]')];
    for (const instance of [...instances]) {
        if (!lists.includes(instance.el)) { instance.destroy(); instances.splice(instances.indexOf(instance), 1); }
    }
    for (const list of lists) {
        if (instances.some(s => s.el === list)) continue;
        instances.push(new Sortable(list, {
            group: list.dataset.orderList, draggable: '[data-id]', handle: '.drag-handle',
            animation: 150, ghostClass: 'order-ghost', chosenClass: 'order-chosen',
            forceFallback: true, fallbackOnBody: true, fallbackTolerance: 5,
            delay: 120, delayOnTouchOnly: true, touchStartThreshold: 4, emptyInsertThreshold: 24,
            onStart() { snapshot = [...root.querySelectorAll('[data-order-list]')].map(el => [el, [...el.childNodes]]); },
            onEnd(event) {
                const index = event.newDraggableIndex;
                const group = event.to.dataset.group || '';
                const kind = event.from.dataset.orderList;
                const id = event.item.dataset.id;
                const changed = event.from !== event.to || event.oldDraggableIndex !== index;
                // Restore all nodes, including Blazor's component boundary comments, before any .NET render.
                for (const [el, children] of snapshot || []) for (const child of children) el.appendChild(child);
                snapshot = null;
                if (!changed || pending || root?.dataset.orderDisabled === 'true') return;
                pending = true;
                for (const s of instances) s.option('disabled', true);
                receiver.invokeMethodAsync('DropAsync', kind, id, group, index)
                    .catch(() => { /* Disposal/navigation can end an in-flight callback. */ })
                    .finally(() => { pending = false; if (root) for (const s of instances) s.option('disabled', root.dataset.orderDisabled === 'true'); });
            }
        }));
    }
    for (const s of instances) s.option('disabled', pending || root.dataset.orderDisabled === 'true');
}
