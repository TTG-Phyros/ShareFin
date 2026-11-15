// Sharefin: adds "Créer un lien de partage" to the item context menu (right click, or the ⋮ button).
// Injected into the web client's index.html by the File Transformation plugin.
(function () {
    'use strict';
    var SHAREABLE = ['Movie', 'Series', 'Season', 'Episode'];
    var target = null;

    // Remember which item the next menu is about: a card or list row, or the details page.
    function remember(e) {
        if (!e.target.closest || e.target.closest('.actionSheet')) { return; }
        var row = e.target.closest('[data-id][data-type]');
        var page = /[?&]id=([^&]+)/.exec(location.hash);
        if (row) {
            target = { id: row.getAttribute('data-id'), type: row.getAttribute('data-type') };
        } else if (page && e.target.closest('.btnMoreCommands')) {
            target = { id: page[1], type: null };
        }
    }
    document.addEventListener('contextmenu', remember, true);
    document.addEventListener('click', remember, true);

    function decorate(sheet) {
        var t = target;
        target = null;
        // "Edit" and "refresh" only appear in an item menu, for a user allowed to manage it.
        var model = sheet.querySelector('.actionSheetMenuItem[data-id="edit"], .actionSheetMenuItem[data-id="refresh"]');
        if (!t || !model || !window.ApiClient) { return; }
        var type = t.type ? Promise.resolve(t.type)
            : ApiClient.getItem(ApiClient.getCurrentUserId(), t.id).then(function (i) { return i.Type; });
        type.then(function (kind) {
            if (SHAREABLE.indexOf(kind) < 0 || !sheet.isConnected) { return; }
            var btn = model.cloneNode(true);
            btn.setAttribute('data-id', 'sharefin');
            var icon = btn.querySelector('.material-icons');
            if (icon) { icon.className = 'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons share'; }
            btn.querySelector('.actionSheetItemText').textContent = 'Créer un lien de partage';
            btn.addEventListener('click', function () {
                // The sheet closes itself, and may step back in history: navigate once it is gone.
                var wait = setInterval(function () {
                    if (sheet.isConnected) { return; }
                    clearInterval(wait);
                    setTimeout(function () { Dashboard.navigate('configurationpage?name=Sharefin&share=' + encodeURIComponent(t.id)); }, 100);
                }, 50);
            });
            model.parentNode.insertBefore(btn, model);
        });
    }

    new MutationObserver(function (records) {
        records.forEach(function (r) {
            r.addedNodes.forEach(function (n) {
                var sheet = n.nodeType === 1 && (n.classList.contains('actionSheet') ? n : n.querySelector('.actionSheet'));
                if (sheet) { decorate(sheet); }
            });
        });
    }).observe(document.body, { childList: true });
})();
