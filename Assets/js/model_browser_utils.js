(function () {
    'use strict';

    window.ModelBrowser = window.ModelBrowser || {};
    window.ModelBrowser.Utils = window.ModelBrowser.Utils || {};
    window.ModelBrowser.Providers = window.ModelBrowser.Providers || {};
    window.ModelBrowser.Components = window.ModelBrowser.Components || {};

    /** Promise wrapper around SwarmUI's callback-based genericRequest. */
    window.ModelBrowser.Utils.genericRequestAsync = function genericRequestAsync(url, in_data) {
        return new Promise((resolve, reject) => {
            if (typeof genericRequest !== 'function') {
                reject('genericRequest is not available');
                return;
            }
            genericRequest(url, in_data, data => resolve(data), 0, e => reject(e));
        });
    };

    /** Appends the `#.gguf` hint core's DoModelDownloadWS reads to pick the save extension, when fileName is GGUF but url is not. */
    window.ModelBrowser.Utils.appendExtensionHint = function appendExtensionHint(url, fileName) {
        if (!url || !fileName) {
            return url;
        }
        if (`${fileName}`.toLowerCase().endsWith('.gguf') && !`${url}`.toLowerCase().endsWith('.gguf')) {
            return `${url}#.gguf`;
        }
        return url;
    };

    /** Load a URL into the manual downloader, trigger validation, and scroll to it. */
    window.ModelBrowser.Utils.loadUrlIntoManualDownloader = function loadUrlIntoManualDownloader(url) {
        try {
            if (!window.modelDownloader || !modelDownloader.url || typeof modelDownloader.urlInput !== 'function') {
                return false;
            }
            modelDownloader.url.value = `${url || ''}`;
            modelDownloader.urlInput();
            modelDownloader.url.focus();
            modelDownloader.url.select();
            modelDownloader.url.scrollIntoView({ behavior: 'smooth', block: 'start' });
            return true;
        }
        catch {
            return false;
        }
    };

    /** Returns the destination folder from whichever folder UI is active, or '' for the root. Core's `mdl.folders` select
     * always exists, so `mdl.folderBrowser` is the sign that folder_browser_injection.js's tree UI is active. */
    window.ModelBrowser.Utils.resolveSelectedFolder = function resolveSelectedFolder(mdl) {
        if (!mdl) {
            return '';
        }
        const raw = mdl.folderBrowser ? mdl.selectedFolder : (mdl.folders ? mdl.folders.value : '');
        return raw && raw !== '(None)' ? raw : '';
    };

    /** Set metadata info and optional preview image in the manual downloader panel. */
    window.ModelBrowser.Utils.setManualDownloaderInfo = function setManualDownloaderInfo(infoHtml, rawMetadata, imageDataUrl) {
        try {
            if (!window.modelDownloader) {
                return false;
            }
            const mz = modelDownloader.metadataZone;
            const imgSide = modelDownloader.imageSide;
            if (!mz || !imgSide) {
                return false;
            }
            mz.innerHTML = infoHtml || '';
            mz.dataset.raw = rawMetadata || '';
            if (imageDataUrl) {
                mz.dataset.image = imageDataUrl;
                imgSide.replaceChildren();
                const img = document.createElement('img');
                img.src = imageDataUrl;
                imgSide.appendChild(img);
            }
            else {
                delete mz.dataset.image;
                imgSide.replaceChildren();
            }
            return true;
        }
        catch {
            return false;
        }
    };
})();
