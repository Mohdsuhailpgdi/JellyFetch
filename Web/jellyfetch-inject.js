/**
 * JellyFetch UI Injection Script — v1.0.2
 *
 * This file is embedded in Jellyfin.Plugin.JellyFetch.dll and automatically
 * written to /usr/share/jellyfin/web/ (or equivalent) when the plugin starts.
 * index.html is patched to load this script with a <script> tag.
 *
 * No webpack dependency. Pure DOM manipulation via hashchange + MutationObserver.
 * Globals used: window.ApiClient, window.Dashboard, window.Emby.Page
 */
(function () {
    'use strict';

    // ── Spin animation ──────────────────────────────────────────────────
    if (!document.getElementById('jf-styles')) {
        var st = document.createElement('style');
        st.id = 'jf-styles';
        st.textContent = [
            '@keyframes jf-spin{0%{transform:rotate(0deg)}100%{transform:rotate(360deg)}}',
            '.jf-spin{animation:jf-spin 1.2s linear infinite!important;display:inline-block!important;}'
        ].join('');
        document.head.appendChild(st);
    }

    // ── State ─────────────────────────────────────────────────────────
    var state = {
        itemId: null,
        isDownloadable: false,
        isDownloading: false,
        isPaused: false,
        status: '',
        progress: 0,
        options: [],
        description: '',
        language: 'English',
        pollTimer: null,
        navTimer: null
    };

    // ── Helpers ──────────────────────────────────────────────────────
    function getToken() {
        return (window.ApiClient && window.ApiClient.accessToken && window.ApiClient.accessToken()) || '';
    }

    function authHeaders() {
        return { 'Authorization': 'MediaBrowser Token="' + getToken() + '"' };
    }

    function jfFetch(url, opts) {
        return fetch(url, Object.assign({ headers: authHeaders() }, opts || {}));
    }

    function escHtml(s) {
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function getCurrentItemId() {
        var h = window.location.hash || '';
        var m = h.match(/[?&]id=([a-f0-9]+)/i);
        return m ? m[1] : null;
    }

    function isDetailsPage() {
        return /[/#]details\?/.test(window.location.hash || '');
    }

    function getApiClient() { return window.ApiClient; }

    function waitFor(predFn, timeoutMs) {
        return new Promise(function (resolve) {
            var deadline = Date.now() + (timeoutMs || 5000);
            (function check() {
                var el = predFn();
                if (el) { resolve(el); return; }
                if (Date.now() > deadline) { resolve(null); return; }
                setTimeout(check, 100);
            }());
        });
    }

    // ── DOM finders ──────────────────────────────────────────────────
    function getActivePage() {
        // Jellyfin renders the current page inside .mainAnimatedPage elements
        var pages = document.querySelectorAll('.mainAnimatedPage');
        return pages[pages.length - 1] || null;
    }

    function getButtonsArea(page) {
        if (!page) return null;
        return page.querySelector('.detailPageContent .itemDetailButtons')
            || page.querySelector('.itemDetailButtons')
            || page.querySelector('.detailButtons');
    }

    // ── Stop all background work ─────────────────────────────────────
    function stopAll() {
        if (state.pollTimer) { clearInterval(state.pollTimer); state.pollTimer = null; }
        if (state.navTimer)  { clearTimeout(state.navTimer);  state.navTimer  = null; }
    }

    function removeInjectedUI(page) {
        var p = page || getActivePage();
        if (p) {
            var btn = p.querySelector('#jf-dl-btn');
            if (btn) btn.remove();
            var modal = p.querySelector('#jf-dl-modal');
            if (modal) modal.remove();
        }
    }

    // ── Sync play/download buttons ───────────────────────────────────
    function syncButtons(page) {
        var btn = page && page.querySelector('#jf-dl-btn');
        if (!btn) return;

        if (state.isDownloading) {
            var sTxt = state.status || 'Downloading...';
            var pct  = state.progress || 0;
            var isErr = /fail|stop/i.test(sTxt);
            var isPaused = /pause/i.test(sTxt) || state.isPaused;
            var label = pct ? sTxt + ' (' + pct + '%)' : sTxt;
            var icon  = isErr ? 'error' : (isPaused ? 'pause' : 'sync');
            var spinCls = (!isErr && !isPaused) ? ' jf-spin' : '';
            var bg    = isErr ? '#f44336' : (isPaused ? '#ff9800' : '#2196f3');

            btn.style.cssText = 'background:' + bg + ';color:#fff;border:none;' +
                'padding:0 14px;border-radius:20px;display:inline-flex;align-items:center;' +
                'justify-content:center;min-width:160px;height:40px;cursor:pointer;' +
                'box-shadow:0 2px 10px rgba(33,150,243,0.4);font-weight:600;font-size:14px;';
            btn.innerHTML = '<span class="material-icons' + spinCls + '" style="margin-right:6px;font-size:18px;">' +
                icon + '</span>' + escHtml(label);
        } else {
            btn.style.cssText = 'background:#2196f3;color:#fff;border:none;' +
                'padding:0 14px;border-radius:20px;display:inline-flex;align-items:center;' +
                'height:40px;cursor:pointer;font-weight:600;font-size:14px;gap:6px;';
            btn.innerHTML = '<span class="material-icons" style="font-size:18px;">download</span>Download';
        }

        // Hide default play buttons when in download mode
        var shouldHidePlay = state.isDownloadable || state.isDownloading;
        page.querySelectorAll('.btnPlay,.btnResume,.btnShuffle,.btnInstantMix').forEach(function (b) {
            b.style.display = shouldHidePlay ? 'none' : '';
        });
    }

    // ── Inject the download button ────────────────────────────────────
    function injectButton(page, itemId) {
        if (page.querySelector('#jf-dl-btn')) return; // already injected
        var area = getButtonsArea(page);
        if (!area) return;

        var btn = document.createElement('button');
        btn.id = 'jf-dl-btn';
        btn.type = 'button';
        btn.className = 'detailButton';
        btn.addEventListener('click', function () {
            showModal(page, itemId);
        });
        area.prepend(btn);
        syncButtons(page);
    }

    // ── Download modal ────────────────────────────────────────────────
    function ensureModal(page) {
        var m = page.querySelector('#jf-dl-modal');
        if (!m) {
            m = document.createElement('div');
            m.id = 'jf-dl-modal';
            m.className = 'dialogContainer hide';
            m.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;z-index:99999;' +
                'background:rgba(0,0,0,0.8);display:flex;align-items:center;justify-content:center;' +
                'padding:20px;box-sizing:border-box;';
            page.appendChild(m);
        }
        return m;
    }

    function showModal(page, itemId) {
        var modal = ensureModal(page);
        renderModalContent(modal, page, itemId);
        modal.classList.remove('hide');
    }

    function renderModalContent(modal, page, itemId) {
        var inner = modal.querySelector('.jf-modal-inner') || document.createElement('div');
        inner.className = 'jf-modal-inner';
        inner.style.cssText = 'background:#222;padding:24px;border-radius:8px;width:90%;' +
            'max-width:600px;color:#fff;max-height:80vh;overflow-y:auto;box-shadow:0 4px 20px rgba(0,0,0,0.6);';

        if (state.isDownloading) {
            renderProgressView(inner, page, itemId);
        } else {
            renderOptionsView(inner, page, itemId);
        }

        if (!modal.contains(inner)) modal.appendChild(inner);
    }

    function renderProgressView(inner, page, itemId) {
        var sTxt = state.status || 'Downloading...';
        var pct  = state.progress || 0;
        var isErr = /fail|stop/i.test(sTxt);
        var isPaused = /pause/i.test(sTxt) || state.isPaused;

        var headerIcon = isErr ? 'error' : (isPaused ? 'pause' : 'sync');
        var headerSpin = (!isErr && !isPaused) ? ' jf-spin' : '';
        var headerClr  = isErr ? '#f44336' : (isPaused ? '#ff9800' : '#2196f3');
        var headerTxt  = isErr ? 'Download Failed' : (isPaused ? 'Download Paused' : 'Download in Progress');

        var pauseOrResumeBtn = isPaused
            ? '<button type="button" id="jf-btn-resume" style="background:#4caf50;color:#fff;padding:8px 18px;border-radius:4px;border:none;cursor:pointer;font-weight:bold;display:flex;align-items:center;gap:6px;">' +
              '<span class="material-icons" style="font-size:18px;">play_arrow</span>Resume</button>'
            : '<button type="button" id="jf-btn-pause" style="background:#ff9800;color:#fff;padding:8px 18px;border-radius:4px;border:none;cursor:pointer;font-weight:bold;display:flex;align-items:center;gap:6px;">' +
              '<span class="material-icons" style="font-size:18px;">pause</span>Pause</button>';

        inner.innerHTML =
            '<h2 id="jf-modal-header" style="margin-top:0;border-bottom:1px solid #333;padding-bottom:12px;display:flex;align-items:center;gap:10px;">' +
            '<span class="material-icons' + headerSpin + '" style="color:' + headerClr + ';">' + headerIcon + '</span>' + headerTxt + '</h2>' +
            '<div style="margin-bottom:16px;">' +
            '<div id="jf-modal-status" style="font-size:0.9em;color:#bbb;margin-bottom:12px;">' + escHtml(sTxt) + '</div>' +
            '<div style="background:#333;border-radius:8px;height:14px;overflow:hidden;margin-bottom:8px;">' +
            '<div id="jf-modal-bar" style="background:linear-gradient(90deg,#2196f3,#00bcd4);height:100%;width:' + pct + '%;transition:width 0.3s ease;"></div>' +
            '</div>' +
            '<div style="display:flex;justify-content:space-between;font-size:0.85em;color:#888;">' +
            '<span id="jf-modal-pct">' + pct + '% completed</span>' +
            '<span>Seedr / Torbox Cloud</span></div></div>' +
            '<div style="display:flex;gap:12px;margin-top:20px;padding-top:16px;border-top:1px solid #333;">' +
            pauseOrResumeBtn +
            '<button type="button" id="jf-btn-stop" style="background:#f44336;color:#fff;padding:8px 18px;border-radius:4px;border:none;cursor:pointer;font-weight:bold;display:flex;align-items:center;gap:6px;">' +
            '<span class="material-icons" style="font-size:18px;">stop</span>Stop</button></div>' +
            '<div style="margin-top:20px;text-align:right;">' +
            '<button type="button" id="jf-btn-close" style="background:#444;color:#fff;padding:8px 20px;border-radius:4px;border:none;cursor:pointer;font-weight:600;">Close</button></div>';

        // Wire buttons
        var closeBtn = inner.querySelector('#jf-btn-close');
        if (closeBtn) closeBtn.addEventListener('click', function () { inner.closest('#jf-dl-modal').classList.add('hide'); });

        var pauseBtn = inner.querySelector('#jf-btn-pause');
        if (pauseBtn) {
            pauseBtn.addEventListener('click', function () {
                pauseBtn.disabled = true;
                jfFetch('/System/Configuration/Downloaders/Pause/' + itemId, { method: 'POST' })
                    .then(function () { state.isPaused = true; renderModalContent(inner.closest('#jf-dl-modal'), page, itemId); })
                    .catch(function () { pauseBtn.disabled = false; });
            });
        }

        var resumeBtn = inner.querySelector('#jf-btn-resume');
        if (resumeBtn) {
            resumeBtn.addEventListener('click', function () {
                resumeBtn.disabled = true;
                jfFetch('/System/Configuration/Downloaders/Resume/' + itemId, { method: 'POST' })
                    .then(function () { state.isPaused = false; renderModalContent(inner.closest('#jf-dl-modal'), page, itemId); })
                    .catch(function () { resumeBtn.disabled = false; });
            });
        }

        var stopBtn = inner.querySelector('#jf-btn-stop');
        if (stopBtn) {
            stopBtn.addEventListener('click', function () {
                stopBtn.disabled = true;
                stopBtn.textContent = 'Stopping...';
                jfFetch('/System/Configuration/Downloaders/Stop/' + itemId, { method: 'POST' })
                    .then(function () {
                        stopAll();
                        state.isDownloading = false;
                        state.isPaused = false;
                        state.isDownloadable = true;
                        syncButtons(page);
                        renderModalContent(inner.closest('#jf-dl-modal'), page, itemId);
                    }).catch(function () {
                        stopAll();
                        state.isDownloading = false;
                        state.isDownloadable = true;
                        syncButtons(page);
                    });
            });
        }
    }

    function renderOptionsView(inner, page, itemId) {
        var opts = state.options || [];
        var langs = [];
        opts.forEach(function (m) {
            (m.languages || []).forEach(function (l) { if (langs.indexOf(l) < 0) langs.push(l); });
        });
        if (langs.length === 0) langs = ['English'];
        if (langs.indexOf(state.language) < 0) state.language = langs[0];

        var tabsHtml = langs.map(function (l) {
            var bg = l === state.language ? '#2196f3' : '#444';
            return '<button type="button" class="jf-lang-tab" data-lang="' + escHtml(l) + '" style="padding:6px 12px;border-radius:4px;border:none;cursor:pointer;background:' + bg + ';color:#fff;">' + escHtml(l) + '</button>';
        }).join('');

        var filtered = opts.filter(function (m) {
            return !m.languages || m.languages.length === 0 || m.languages.indexOf(state.language) >= 0;
        });

        var listHtml = filtered.length === 0
            ? '<div style="padding:10px;">No sizes available for ' + escHtml(state.language) + '.</div>'
            : filtered.map(function (m) {
                var size = (m.xl_gb > 0) ? m.xl_gb + ' GB' : 'Unknown Size';
                var badges = (m.languages || []).map(function (l) {
                    return '<span style="background:#333;padding:2px 6px;border-radius:4px;font-size:0.75em;margin-right:4px;">' + escHtml(l) + '</span>';
                }).join('');
                return '<div style="display:flex;justify-content:space-between;align-items:center;padding:12px;border:1px solid #444;border-radius:4px;" data-uri="' + escHtml(m.uri) + '" data-size="' + (m.xl_gb || 0) + '">' +
                    '<div><div style="font-size:1.1em;font-weight:bold;">' + escHtml(size) + '</div>' +
                    '<div style="font-size:0.8em;opacity:0.6;word-break:break-all;">' + escHtml(m.dn || '') + '</div>' +
                    '<div style="margin-top:4px;">' + badges + '</div></div>' +
                    '<button type="button" class="jf-dl-option-btn" style="background:#2196f3;color:#fff;border:none;padding:8px 16px;border-radius:4px;cursor:pointer;font-weight:bold;">Download</button>' +
                    '</div>';
            }).join('');

        inner.innerHTML =
            '<h2 style="margin-top:0;border-bottom:1px solid #333;padding-bottom:10px;">Available Downloads</h2>' +
            (state.description ? '<p style="margin:0 0 12px;font-size:0.9em;opacity:0.8;">' + escHtml(state.description) + '</p>' : '') +
            '<div id="jf-lang-tabs" style="display:flex;gap:10px;margin-bottom:15px;border-bottom:1px solid #333;padding-bottom:10px;overflow-x:auto;">' + tabsHtml + '</div>' +
            '<div id="jf-opt-list" style="display:flex;flex-direction:column;gap:12px;">' + listHtml + '</div>' +
            '<div style="margin-top:20px;text-align:right;">' +
            '<button type="button" id="jf-btn-close" style="background:#444;color:#fff;padding:8px 20px;border-radius:4px;border:none;cursor:pointer;font-weight:600;">Close</button></div>';

        var closeBtn = inner.querySelector('#jf-btn-close');
        if (closeBtn) closeBtn.addEventListener('click', function () { inner.closest('#jf-dl-modal').classList.add('hide'); });

        inner.querySelectorAll('.jf-lang-tab').forEach(function (tb) {
            tb.addEventListener('click', function () {
                state.language = tb.getAttribute('data-lang');
                renderModalContent(inner.closest('#jf-dl-modal'), page, itemId);
            });
        });

        inner.querySelectorAll('.jf-dl-option-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var row = btn.closest('[data-uri]');
                if (!row) return;
                var uri  = row.getAttribute('data-uri');
                var size = parseFloat(row.getAttribute('data-size')) || 0;
                startDownload(page, itemId, uri, size);
                inner.closest('#jf-dl-modal').classList.add('hide');
            });
        });
    }

    // ── Start a download ──────────────────────────────────────────────
    function startDownload(page, itemId, magnetUri, sizeGb) {
        jfFetch('/System/Configuration/Downloaders/Start/' + itemId, {
            method: 'POST',
            headers: Object.assign({ 'Content-Type': 'application/json' }, authHeaders()),
            body: JSON.stringify({ Uri: magnetUri, SizeGb: sizeGb })
        }).then(function (res) {
            if (!res.ok) throw new Error('start failed');
            state.isDownloading = true;
            state.isDownloadable = false;
            state.status = 'Connecting to cloud...';
            state.progress = 0;
            syncButtons(page);
            startPolling(page, itemId);
        }).catch(function (err) {
            console.error('[JellyFetch] Failed to start download:', err);
        });
    }

    // ── Polling ───────────────────────────────────────────────────────
    function startPolling(page, itemId) {
        stopAll();
        state.pollTimer = setInterval(function () {
            // If user navigated away from the page, stop silently
            if (!page.isConnected || !document.body.contains(page)) {
                stopAll();
                return;
            }
            pollStatus(page, itemId);
        }, 3000);
    }

    function pollStatus(page, itemId) {
        jfFetch('/System/Configuration/Downloaders/Status/' + itemId + '?t=' + Date.now())
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (info) {
                if (!info) return;

                if (info.Completed) {
                    stopAll();
                    state.isDownloading = false;
                    state.isDownloadable = false;
                    page.querySelector('#jf-dl-modal')?.classList.add('hide');
                    syncButtons(page);

                    var resolvedId = info.NewItemId || itemId;

                    // Trigger metadata refresh so images load before navigation
                    jfFetch('/Items/' + resolvedId + '/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=FullRefresh&replaceAllImages=false&replaceAllMetadata=false', { method: 'POST' }).catch(function () {});

                    if (window.Dashboard) window.Dashboard.alert({ title: 'Download Complete', message: 'Download finished! Loading movie page...' });

                    // Navigate after 5s — store timer so we can cancel if user navigates away
                    state.navTimer = setTimeout(function () {
                        state.navTimer = null;
                        if (window.Emby && window.Emby.Page) {
                            window.Emby.Page.showItem(resolvedId);
                        } else {
                            window.location.hash = '#!/details?id=' + resolvedId;
                        }
                    }, 5000);
                    return;
                }

                if (info.Status === 'Failed' || info.Status === 'Stopped') {
                    stopAll();
                    state.isDownloading = false;
                    state.isDownloadable = true;
                    state.status = info.Status;
                    syncButtons(page);
                    return;
                }

                state.status   = info.Status   || state.status;
                state.progress = info.Progress  || 0;
                state.isPaused = !!info.IsPaused;

                syncButtons(page);

                // Update modal elements if open
                var bar = document.getElementById('jf-modal-bar');
                if (bar) bar.style.width = state.progress + '%';
                var sl  = document.getElementById('jf-modal-status');
                if (sl)  sl.textContent = state.status;
                var pl  = document.getElementById('jf-modal-pct');
                if (pl)  pl.textContent = state.progress + '% completed';
            })
            .catch(function () {});
    }

    // ── Page initialization ───────────────────────────────────────────
    function initPage() {
        if (!isDetailsPage()) return;

        var itemId = getCurrentItemId();
        if (!itemId) return;

        // If same item and already set up, do nothing
        if (itemId === state.itemId && (state.isDownloadable || state.isDownloading)) return;

        // New item — reset
        stopAll();
        var prevId = state.itemId;
        state.itemId = itemId;
        state.isDownloadable = false;
        state.isDownloading  = false;
        state.isPaused = false;
        state.status   = '';
        state.progress = 0;
        state.options  = [];

        // Wait for the details page DOM to appear
        waitFor(function () { return getButtonsArea(getActivePage()); }, 6000).then(function (area) {
            if (!area || state.itemId !== itemId) return; // navigated away
            var page = getActivePage();

            // Remove any old injected UI from a different page visit
            removeInjectedUI(page);

            // 1. Check if actively downloading
            jfFetch('/System/Configuration/Downloaders/Status/' + itemId + '?t=' + Date.now())
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (st) {
                    if (state.itemId !== itemId) return;
                    if (st && !st.Completed && st.Status !== 'Idle' && st.Status !== 'Stopped' && st.Status !== 'Failed') {
                        state.isDownloading = true;
                        state.status = st.Status;
                        state.progress = st.Progress || 0;
                        injectButton(page, itemId);
                        startPolling(page, itemId);
                        return;
                    }
                    // 2. Check if downloadable
                    return jfFetch('/System/Configuration/Downloaders/Options/' + itemId)
                        .then(function (r) { return r.ok ? r.json() : null; })
                        .then(function (optData) {
                            if (state.itemId !== itemId) return;
                            var opts = Array.isArray(optData) ? optData : (optData && optData.Options ? optData.Options : []);
                            if (opts && opts.length > 0) {
                                state.options = opts;
                                state.isDownloadable = true;
                                state.description = (optData && optData.Description) || '';
                                injectButton(page, itemId);
                            }
                        }).catch(function () {});
                }).catch(function () {});
        });
    }

    // ── Page change detection ─────────────────────────────────────────
    // Stop everything when navigating away from details page
    function onHashChange() {
        if (!isDetailsPage()) {
            stopAll(); // cancel poll + nav timer if user navigated away
            return;
        }
        initPage();
    }

    window.addEventListener('hashchange', onHashChange);
    window.addEventListener('popstate',   onHashChange);

    // Catch SPA navigation that doesn't trigger hashchange (React router)
    // by observing DOM mutations on the page container
    var navObserver = new MutationObserver(function () {
        if (isDetailsPage() && getCurrentItemId() !== state.itemId) {
            onHashChange();
        }
    });

    // Start observing once the body is available
    function startObserver() {
        var target = document.querySelector('.mainAnimatedPages') || document.body;
        navObserver.observe(target, { childList: true, subtree: false });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', startObserver);
    } else {
        startObserver();
    }

    // Initial page check (for direct deep-links)
    setTimeout(initPage, 800);

}());
