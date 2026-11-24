/**
 * Reserves `window.ModelBrowser.DownloadHistory` for planned, not yet implemented features: a download history,
 * a warning before downloading a model the user already has, and "Installed" / "Update Available" card badges.
 * Open questions: where to store it (localStorage, user generic data, or a Models folder scan), how to match one
 * model across providers, and how to check for updates without flooding provider APIs.
 */
(function () {
    'use strict';

    window.ModelBrowser = window.ModelBrowser || {};
    window.ModelBrowser.DownloadHistory = {};
})();
