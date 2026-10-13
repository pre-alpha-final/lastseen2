export function open(dialog, receiver, selectUrl) {
    const previous = document.activeElement;
    const cancel = event => { event.preventDefault(); receiver.invokeMethodAsync('CancelFromKeyboard'); };
    const trap = event => {
        if (event.key !== 'Tab') return;
        const items = [...dialog.querySelectorAll('input, select, button, [tabindex]')]
            .filter(element => !element.matches(':disabled') && element.tabIndex >= 0);
        if (!items.length) { event.preventDefault(); dialog.focus(); return; }
        const first = items[0], last = items[items.length - 1];
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    };
    dialog.addEventListener('cancel', cancel);
    dialog.addEventListener('keydown', trap);
    dialog.showModal();
    const url = selectUrl ? dialog.querySelector('#bookmark-url') : null;
    url?.focus();
    url?.select();
    return { dispose() {
        dialog.removeEventListener('cancel', cancel);
        dialog.removeEventListener('keydown', trap);
        dialog.close();
        if (previous?.isConnected) previous.focus();
        else document.querySelector('.header-actions button:not(:disabled)')?.focus();
    } };
}
