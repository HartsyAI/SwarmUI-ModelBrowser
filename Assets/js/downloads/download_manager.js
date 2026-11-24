/* Renders one card per download from the server-side state in WebAPI/Downloads/DownloadManager.cs and forwards
 * button clicks. The server is the only source of truth, so a page reload just redraws from its list. */
(function () {
    'use strict';

    window.ModelBrowser = window.ModelBrowser || {};

    const POLL_INTERVAL_MS = 2000;

    /** Last known state of each download, keyed by ID. */
    const records = new Map();
    /** DOM handles for each download's card, keyed by ID. */
    const cardEls = new Map();
    let pollTimer = null;
    let zone = null;

    function utils() {
        return window.ModelBrowser && window.ModelBrowser.Utils;
    }

    async function call(endpoint, payload) {
        const u = utils();
        if (!u || typeof u.genericRequestAsync !== 'function') {
            return null;
        }
        try {
            return await u.genericRequestAsync(endpoint, payload);
        }
        catch (e) {
            // genericRequestAsync rejects with the raw error string; repackage it as the `{error}` callers expect.
            console.warn(`[ModelBrowser] ${endpoint} failed:`, e);
            const message = typeof e === 'string' ? e : (e && e.message) || 'Request failed.';
            return { error: message };
        }
    }

    function formatBytes(n) {
        return typeof fileSizeStringify === 'function' ? fileSizeStringify(n) : `${Math.round(n)} B`;
    }

    /** Builds the status line for a card. Returned as HTML (not text) since the errored case needs a link. */
    function statusHtml(rec) {
        const known = rec.totalBytes > 0;
        // A note for this attempt only, eg that a refused resume restarted from zero or that setup failed after the save.
        const noteHtml = rec.note ? `<br>${escapeHtml(rec.note)}` : '';
        if (rec.state === 'downloading') {
            const speed = rec.perSecond > 0 ? ` - ${formatBytes(rec.perSecond)}/s` : '';
            if (!known) {
                return `${translate('Downloading...')} ${formatBytes(rec.downloadedBytes)}${speed}${noteHtml}`;
            }
            const pct = Math.floor((rec.downloadedBytes / rec.totalBytes) * 100);
            return `${translate('Downloading...')} ${formatBytes(rec.downloadedBytes)} / ${formatBytes(rec.totalBytes)} (${pct}%)${speed}${noteHtml}`;
        }
        if (rec.state === 'paused') {
            return `${translate('Paused at')} ${formatBytes(rec.downloadedBytes)}${known ? ` / ${formatBytes(rec.totalBytes)}` : ''}. ${translate('The partial file is kept - Resume to continue.')}`;
        }
        if (rec.state === 'errored') {
            const msg = rec.error || translate('Unknown error.');
            let html = `${translate('Error:')} ${escapeHtml(msg)}`;
            if (msg.includes('got response code 401') || msg.includes('got response code 403')) {
                html += `<br>${translate('Set or check your')} <a href="#" onclick="getRequiredElementById('usersettingstabbutton').click();getRequiredElementById('userinfotabbutton').click();">${translate('API Key')}</a> ${translate('in User Settings.')}`;
            }
            else if (rec.downloadedBytes > 0) {
                html += `<br>${formatBytes(rec.downloadedBytes)} ${translate('was already saved and will resume from there.')}`;
            }
            return html;
        }
        if (rec.state === 'completed') {
            return `${translate('Done!')} ${formatBytes(rec.totalBytes || rec.downloadedBytes)}${noteHtml}`;
        }
        return '';
    }

    function makeButton(label, hint, onClick) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'basic-button';
        if (hint) {
            btn.title = hint;
        }
        btn.textContent = label;
        btn.onclick = onClick;
        return btn;
    }

    function renderActions(container, rec) {
        container.replaceChildren();
        if (rec.state === 'downloading') {
            container.appendChild(makeButton(translate('Cancel'), translate('Pause this download - the partial file is kept so it can be resumed.'), () => cancelDownload(rec.id)));
        }
        else if (rec.state === 'paused' || rec.state === 'errored') {
            const label = rec.state === 'errored' ? translate('Retry') : translate('Resume');
            container.appendChild(makeButton(label, translate('Continue this download from where it left off.'), () => resumeDownload(rec.id)));
            container.appendChild(makeButton(translate('Clear'), translate('Delete the partial file and remove this download from the list.'), () => clearDownload(rec.id)));
        }
        else if (rec.state === 'completed') {
            container.appendChild(makeButton(translate('Dismiss'), translate('Remove this from the list. The downloaded model is not affected.'), () => clearDownload(rec.id)));
        }
    }

    function ensureZone() {
        if (zone) {
            return zone;
        }
        if (window.modelDownloader && modelDownloader.activeZone) {
            zone = modelDownloader.activeZone;
        }
        return zone;
    }

    function buildCard(rec) {
        const root = document.createElement('div');
        root.className = 'model-browser-download-card';
        root.innerHTML = `
            <div class="card">
                <div class="card-header"></div>
                <div class="model-browser-download-progress"><div class="model-browser-download-progress-bar"></div></div>
                <div class="model-browser-download-status"></div>
                <div class="model-browser-download-actions"></div>
            </div>
        `;
        const els = {
            root,
            card: root.querySelector('.card'),
            header: root.querySelector('.card-header'),
            img: null,
            progressWrap: root.querySelector('.model-browser-download-progress'),
            bar: root.querySelector('.model-browser-download-progress-bar'),
            status: root.querySelector('.model-browser-download-status'),
            actions: root.querySelector('.model-browser-download-actions')
        };
        cardEls.set(rec.id, els);
        zone.insertBefore(root, zone.firstChild);
        return els;
    }

    function updateCard(rec) {
        const els = cardEls.get(rec.id) || buildCard(rec);
        els.header.textContent = `[${rec.type}] ${rec.name}`;
        if (rec.image && !els.img) {
            els.img = document.createElement('img');
            els.img.className = 'card-img-top';
            els.img.alt = 'Model thumbnail';
            els.img.src = rec.image;
            els.header.insertAdjacentElement('afterend', els.img);
        }
        const known = rec.totalBytes > 0;
        const pct = rec.state === 'completed' ? 100 : (known ? Math.min(100, (rec.downloadedBytes / rec.totalBytes) * 100) : 0);
        els.bar.style.width = `${pct}%`;
        els.progressWrap.classList.toggle('model-browser-progress-indeterminate', rec.state === 'downloading' && !known);
        els.status.innerHTML = statusHtml(rec);
        // State classes go on the outer `.model-browser-download-card`, which the state colors and Show completed select on.
        els.root.classList.remove('model-browser-state-downloading', 'model-browser-state-paused', 'model-browser-state-errored', 'model-browser-state-completed');
        els.root.classList.add(`model-browser-state-${rec.state}`);
        // Keeps a finished card with a note on screen even while completed downloads are hidden.
        els.root.classList.toggle('model-browser-has-note', !!rec.note);
        renderActions(els.actions, rec);
    }

    function removeCard(id) {
        const els = cardEls.get(id);
        if (els) {
            els.root.remove();
            cardEls.delete(id);
        }
    }

    /** Polls only while something is downloading; the other states do not change on the server by themselves. */
    function reconcilePolling() {
        const anyActive = [...records.values()].some(r => r.state === 'downloading');
        if (anyActive && !pollTimer) {
            pollTimer = setInterval(pollOnce, POLL_INTERVAL_MS);
        }
        else if (!anyActive && pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    function setRecord(rec) {
        if (!rec || !ensureZone()) {
            return;
        }
        records.set(rec.id, rec);
        updateCard(rec);
        reconcilePolling();
    }

    /** Replaces the known set with the server's list, dropping anything it no longer has (eg cleared in another tab). */
    function applyList(list) {
        if (!ensureZone()) {
            return;
        }
        const seen = new Set();
        for (const rec of list) {
            seen.add(rec.id);
            records.set(rec.id, rec);
            updateCard(rec);
        }
        for (const id of [...records.keys()]) {
            if (!seen.has(id)) {
                records.delete(id);
                removeCard(id);
            }
        }
        reconcilePolling();
    }

    async function pollOnce() {
        const resp = await call('ModelBrowserListDownloads', {});
        if (resp && resp.success) {
            applyList(resp.downloads || []);
        }
    }

    async function cancelDownload(id) {
        const resp = await call('ModelBrowserCancelDownload', { id });
        if (resp && resp.download) {
            setRecord(resp.download);
        }
    }

    function showFailure(message) {
        if (typeof showError === 'function') {
            showError(message);
        }
    }

    /** Drops a download from the panel, eg once it is cleared, or when another tab already cleared it. */
    function forget(id) {
        records.delete(id);
        removeCard(id);
        reconcilePolling();
    }

    async function resumeDownload(id) {
        const resp = await call('ModelBrowserResumeDownload', { id });
        if (resp && resp.download) {
            setRecord(resp.download);
        }
        else if (resp && resp.error === 'Download not found.') {
            forget(id);
        }
        else if (resp && resp.error) {
            console.warn(`[ModelBrowser] resume failed: ${resp.error}`);
            showFailure(`${translate('Could not resume the download:')} ${resp.error}`);
        }
    }

    async function clearDownload(id) {
        const resp = await call('ModelBrowserClearDownload', { id });
        if (resp && (resp.success || resp.error === 'Download not found.')) {
            forget(id);
        }
        else if (resp && resp.error) {
            console.warn(`[ModelBrowser] clear failed: ${resp.error}`);
            showFailure(`${translate('Could not clear the download:')} ${resp.error}`);
        }
    }

    /** Replacement for modelDownloader.run(): reads the fields core's ActiveModelDownload would and starts a server-side download. */
    async function startFromManualDownloader(mdl) {
        const u = utils();
        if (!mdl || !u || !ensureZone()) {
            return;
        }
        const folder = u.resolveSelectedFolder(mdl);
        const name = folder ? `${folder}/${mdl.name.value}` : mdl.name.value;
        const payload = {
            url: mdl.url.value,
            type: mdl.type ? mdl.type.value : '',
            name,
            metadata: mdl.metadataZone ? (mdl.metadataZone.dataset.raw || '') : '',
            image: mdl.metadataZone ? (mdl.metadataZone.dataset.image || '') : ''
        };
        mdl.button.disabled = true;
        const resp = await call('ModelBrowserStartDownload', payload);
        if (resp && resp.success && resp.download) {
            setRecord(resp.download);
            return;
        }
        mdl.button.disabled = false;
        const errorMsg = (resp && resp.error) || translate('unknown error');
        if (mdl.urlStatusArea) {
            mdl.urlStatusArea.innerText = `${translate('Failed to start download:')} ${errorMsg}`;
        }
        if (typeof showError === 'function') {
            showError(`${translate('Failed to start download:')} ${errorMsg}`);
        }
    }

    let initialized = false;

    /** Renders the downloads the server already knows of. Returns false while modelDownloader is not in the DOM yet, so the caller retries. */
    async function init() {
        if (initialized) {
            return true;
        }
        if (!ensureZone()) {
            return false;
        }
        const resp = await call('ModelBrowserListDownloads', {});
        if (resp && resp.success) {
            applyList(resp.downloads || []);
        }
        initialized = true;
        return true;
    }

    window.ModelBrowser.Downloads = { init, startFromManualDownloader };
})();
