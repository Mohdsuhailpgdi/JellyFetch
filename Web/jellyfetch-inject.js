/**
 * JellyFetch UI Injection Script — v1.3.8.8
 *
 * Listens to Jellyfin's native viewshow / hashchange events, with a scoped
 * observer for context menu action sheets. Zero recursive DOM loops.
 */
(function () {
    'use strict';

    // ── Spin animation & play button suppression styles ──────────────────
    if (!document.getElementById('jf-styles')) {
        var st = document.createElement('style');
        st.id = 'jf-styles';
        st.textContent = [
            '@keyframes jf-spin{0%{transform:rotate(0deg)}100%{transform:rotate(360deg)}}',
            '.jf-spin{animation:jf-spin 1.2s linear infinite!important;display:inline-block!important;}',
            'body.jf-download-mode .btnPlay, body.jf-download-mode .btnReplay, body.jf-download-mode [data-action="play"], body.jf-download-mode [data-action="resume"], .jf-suppress-play .btnPlay, .jf-suppress-play .btnReplay, .jf-suppress-play [data-action="play"], .jf-suppress-play [data-action="resume"], .jf-suppress-play #jf-dl-btn { display: none !important; }',
            '#jf-dl-modal.hide, #jf-dl-modal[hidden] { display: none !important; visibility: hidden !important; pointer-events: none !important; }',
            '#jf-dl-btn:not([data-downloading="true"]) { transition: transform 0.2s ease, background-color 0.2s ease, color 0.2s ease !important; border-radius: 50% !important; background: transparent !important; }',
            '#jf-dl-btn:not([data-downloading="true"]):hover { transform: scale(1.15) !important; background-color: rgba(255, 255, 255, 0.12) !important; color: #00a4dc !important; }',
            '#jf-dl-btn:not([data-downloading="true"]):active { transform: scale(0.95) !important; }',
            '#jf-dl-btn[data-downloading="true"] { transition: transform 0.2s ease, background 0.3s ease, border-color 0.3s ease, box-shadow 0.3s ease !important; }',
            '#jf-dl-btn[data-downloading="true"]:hover { transform: scale(1.04) !important; filter: brightness(1.15) !important; }'
        ].join('\n');
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
        navTimer: null,
        lowPeerWarning: false   // Item #7: low-peer warning flag from backend
    };

    var excludedItemIds = {};
    var strmItemCache = {};

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
        var m = h.match(/[?&]id=([a-f0-9\-]+)/i);
        return m ? m[1] : null;
    }

    function isPlayerActive() {
        return !!(
            document.getElementById('videoOsdPage') ||
            document.querySelector('.videoPlayerContainer') ||
            document.querySelector('div[data-role="page"].videoOsdPage') ||
            (window.location.hash && /#\/?video/i.test(window.location.hash))
        );
    }

    function isDetailsPage() {
        if (isPlayerActive()) return false;
        return /[/#]details\?/.test(window.location.hash || '');
    }

    function stopAll() {
        if (state.pollTimer) { clearInterval(state.pollTimer); state.pollTimer = null; }
        if (state.navTimer)  { clearTimeout(state.navTimer);  state.navTimer  = null; }
    }

    function waitFor(predFn, timeoutMs) {
        return new Promise(function (resolve) {
            var deadline = Date.now() + (timeoutMs || 3000);
            (function check() {
                var el = predFn();
                if (el) { resolve(el); return; }
                if (Date.now() > deadline) { resolve(null); return; }
                setTimeout(check, 50);
            }());
        });
    }

    // ── Button Creation & Styling ─────────────────────────────────────
    function getButtonArea(container) {
        var root = container || document;
        if (root && (root.id === 'videoOsdPage' || (root.classList && root.classList.contains('videoPlayerContainer')))) {
            return null;
        }
        return root.querySelector('.mainDetailButtons')
            || root.querySelector('.itemDetailButtons')
            || root.querySelector('.detailButtons');
    }

    function applyButtonSync(area, itemId) {
        if (!area || isPlayerActive() || (itemId && excludedItemIds[itemId])) return;

        var shouldDownload = state.isDownloadable || state.isDownloading;

        // Hide Play / Replay buttons
        area.querySelectorAll('.btnPlay, .btnReplay, [data-action="play"], [data-action="resume"]').forEach(function (b) {
            b.style.setProperty('display', shouldDownload ? 'none' : '', 'important');
        });

        if (shouldDownload) {
            document.body.classList.add('jf-download-mode');
        } else {
            document.body.classList.remove('jf-download-mode');
            var existing = area.querySelector('#jf-dl-btn');
            if (existing) existing.remove();
            return;
        }

        var btn = area.querySelector('#jf-dl-btn');
        if (!btn) {
            btn = document.createElement('button');
            btn.id = 'jf-dl-btn';
            btn.type = 'button';
            btn.setAttribute('is', 'emby-button');
            btn.className = 'button-flat detailButton';
            btn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                showModal(area.closest('.mainAnimatedPage') || document.body, itemId);
            });

            var playBtn = area.querySelector('.btnPlay, .btnReplay, [data-action="play"]');
            if (playBtn && playBtn.parentNode === area) {
                area.insertBefore(btn, playBtn);
            } else if (area.firstChild) {
                area.insertBefore(btn, area.firstChild);
            } else {
                area.appendChild(btn);
            }
        }

        if (state.isDownloading) {
            var pct      = state.progress || 0;
            var sP       = (pct > 0) ? Math.round(pct) + '%' : '0%';
            var isErr    = /fail|stop/i.test(state.status);
            var isPaused = /pause/i.test(state.status) || state.isPaused;
            var isLowPeer = !!state.lowPeerWarning;
            if (isErr) sP = 'Error';
            else if (isPaused) sP = Math.round(pct) + '%';

            var icon        = isErr ? 'error' : (isPaused ? 'pause' : (isLowPeer ? 'warning_amber' : 'sync'));
            var spinCls     = (!isErr && !isPaused && !isLowPeer) ? ' jf-spin' : '';
            var accentColor = isErr ? '#f44336' : (isPaused ? '#ff9800' : (isLowPeer ? '#ff9800' : '#00a4dc'));
            var bgColor     = isErr ? 'rgba(244,67,54,0.18)' : (isPaused ? 'rgba(255,152,0,0.18)' : (isLowPeer ? 'rgba(255,152,0,0.18)' : 'rgba(0,164,220,0.18)'));
            var borderColor = isErr ? 'rgba(244,67,54,0.45)' : (isPaused ? 'rgba(255,152,0,0.45)' : (isLowPeer ? 'rgba(255,152,0,0.6)' : 'rgba(0,164,220,0.45)'));
            var glowColor   = isErr ? 'none' : (isPaused ? 'none' : (isLowPeer ? '0 0 10px rgba(255,152,0,0.6)' : '0 0 8px rgba(0,164,220,0.5)'));

            btn.setAttribute('data-downloading', 'true');
            btn.title = isLowPeer
                ? 'This file size is currently not active on the internet — click to stop and try another size'
                : ((state.status || 'Downloading') + ' (' + sP + ') — Click to view details');
            btn.style.cssText = 'background:' + bgColor + '!important;border:1px solid ' + borderColor + '!important;' +
                'color:#fff!important;display:inline-flex!important;align-items:center!important;justify-content:center!important;' +
                'width:76px!important;min-width:76px!important;max-width:76px!important;height:38px!important;' +
                'padding:0 8px!important;border-radius:19px!important;cursor:pointer!important;' +
                'flex-direction:row!important;margin:0 0.5em 0 0!important;box-sizing:border-box!important;gap:5px!important;overflow:hidden!important;' +
                'transition:background 0.3s ease,border-color 0.3s ease,box-shadow 0.3s ease!important;' +
                'box-shadow:' + glowColor + '!important;';
            btn.innerHTML = '<span class="material-icons' + spinCls + '" style="font-size:18px;color:' + accentColor + ';line-height:1;" aria-hidden="true">' +
                icon + '</span><span style="font-size:13px;font-weight:600;color:#fff;line-height:1;white-space:nowrap;overflow:hidden;text-overflow:clip;">' +
                escHtml(sP) + '</span>';

        } else {
            btn.removeAttribute('data-downloading');
            btn.title = 'Download';
            btn.className = 'button-flat detailButton emby-button';
            btn.style.cssText = 'cursor:pointer!important;border:none!important;' +
                'padding:.7em .7em!important;margin:0!important;';
            btn.innerHTML = '<div class="detailButton-content" style="display:flex;align-items:center;justify-content:center;">' +
                '<span class="material-icons detailButton-icon" style="font-size:1.6em;" aria-hidden="true">download</span>' +
                '</div>';
        }
    }

    function syncButton(view, itemId) {
        if (isPlayerActive()) return;
        var area = getButtonArea(view) || getButtonArea(document);
        if (area) {
            applyButtonSync(area, itemId);
            return;
        }
        waitFor(function () { return getButtonArea(document); }, 2000).then(function (found) {
            if (found && state.itemId === itemId && !isPlayerActive()) {
                applyButtonSync(found, itemId);
            }
        });
    }

    // ── Main Page Load Handler ────────────────────────────────────────
    function handleView(view, itemId) {
        if (!isDetailsPage()) return;
        var targetId = itemId || getCurrentItemId();
        if (!targetId || excludedItemIds[targetId]) return;

        state.itemId = targetId;
        lastKnownItemId = targetId;

        // Query Status and Options in parallel
        var statusPromise = jfFetch('/System/Configuration/Downloaders/Status/' + targetId + '?t=' + Date.now())
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; });

        var optionsPromise = jfFetch('/System/Configuration/Downloaders/Options/' + targetId)
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; });

        var userPromise = (window.ApiClient && window.ApiClient.getCurrentUser)
            ? window.ApiClient.getCurrentUser()
            : Promise.resolve(null);

        Promise.all([statusPromise, optionsPromise, userPromise]).then(function (results) {
            if (isPlayerActive() || state.itemId !== targetId) return;
            var st = results[0];
            var optData = results[1];
            var user = results[2];

            var isActivelyDownloading = st && !st.Completed && st.Status !== 'Idle' && st.Status !== 'Stopped' && st.Status !== 'Failed';
            var opts = Array.isArray(optData) ? optData : (optData && optData.Options ? optData.Options : []);
            var isStrm = optData && optData.IsStrm;
            state.isStrm = isStrm;
            strmItemCache[targetId] = !!isStrm;
            var isItemDownloadable = (opts && opts.length > 0) || isStrm;

            if (isActivelyDownloading) {
                state.isDownloading = true;
                state.isDownloadable = false;
                state.status = st.Status;
                state.progress = st.Progress || 0;
                syncButton(view, targetId);
                startPolling(view, targetId);
            } else if (isItemDownloadable) {
                state.isDownloadable = true;
                state.isDownloading = false;
                state.options = opts;
                state.description = (optData && optData.Description) || '';
                syncButton(view, targetId);
            } else {
                state.isDownloadable = false;
                state.isDownloading = false;
                syncButton(view, targetId);
            }
        });
    }

    // ── Download modal & cleanup ─────────────────────────────────────
    function hideModal() {
        var m = document.getElementById('jf-dl-modal');
        if (m) {
            m.classList.add('hide');
            m.style.setProperty('display', 'none', 'important');
            m.style.setProperty('pointer-events', 'none', 'important');
            m.innerHTML = '';
        }

        // Clean up any lingering backdrops or empty dialog containers that might block the UI
        document.querySelectorAll('.dialogBackdrop, .actionSheetBackdrop, .actionsheet-backdrop').forEach(function(el) {
            try { el.remove(); } catch(e) {}
        });
        document.querySelectorAll('.dialogContainer').forEach(function(el) {
            var activeDlg = el.querySelector('.dialog:not(.hide), .actionSheet:not(.hide)');
            if (!activeDlg) {
                try { el.remove(); } catch(e) {}
            }
        });

        // Ensure body and html scrolling and pointer events are fully unlocked
        document.body.classList.remove('noScroll');
        document.documentElement.classList.remove('noScroll');
        document.body.style.overflow = '';
        document.documentElement.style.overflow = '';
        document.body.style.pointerEvents = '';
        document.documentElement.style.pointerEvents = '';
    }

    function cleanupAllModalsAndBackdrops() {
        hideModal();
    }

    function ensureModal() {
        var m = document.getElementById('jf-dl-modal');
        if (!m) {
            m = document.createElement('div');
            m.id = 'jf-dl-modal';
            m.className = 'hide';
            m.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;z-index:9999999;' +
                'background:rgba(0,0,0,0.85);display:none;align-items:center;justify-content:center;' +
                'padding:20px;box-sizing:border-box;backdrop-filter:blur(4px);pointer-events:none;';
            m.addEventListener('click', function(e) {
                if (e.target === m) {
                    hideModal();
                }
            });
            document.body.appendChild(m);
        }
        return m;
    }

    function showModal(view, itemId) {
        var modal = ensureModal();
        renderModalContent(modal, view, itemId);
        modal.classList.remove('hide');
        modal.style.setProperty('display', 'flex', 'important');
        modal.style.setProperty('pointer-events', 'auto', 'important');
        modal.style.setProperty('visibility', 'visible', 'important');
        modal.style.setProperty('z-index', '9999999', 'important');
    }

    function getSelectedMultiItemIds() {
        var ids = [];
        var allBoxes = document.querySelectorAll('.chkItemSelect, .itemSelectionPanel input[type="checkbox"]');
        allBoxes.forEach(function (cb) {
            var isChecked = cb.checked || cb.hasAttribute('checked') || cb.classList.contains('checkedInitial');
            if (isChecked) {
                var card = cb.closest('[data-id], [data-itemid]');
                if (!card) {
                    var p = cb.parentElement;
                    while (p && p !== document.body) {
                        if (p.getAttribute('data-id') || p.getAttribute('data-itemid')) {
                            card = p;
                            break;
                        }
                        p = p.parentElement;
                    }
                }
                if (card) {
                    var id = card.getAttribute('data-id') || card.getAttribute('data-itemid');
                    if (id && ids.indexOf(id) === -1) {
                        ids.push(id);
                    }
                }
            }
        });
        return ids;
    }

    function exitMultiSelectMode() {
        var panel = document.querySelector('.selectionCommandsPanel');
        if (!panel) return;

        var closeBtn = panel.querySelector('.btnCloseSelectionPanel') || document.querySelector('.btnCloseSelectionPanel');
        if (closeBtn) {
            try { closeBtn.click(); } catch(e) {}
        }
        if (panel.parentNode) {
            try { panel.parentNode.removeChild(panel); } catch(e) {}
        }
        document.querySelectorAll('.itemSelectionPanel').forEach(function(el) {
            var parent = el.parentNode;
            try { el.remove(); } catch(e) {}
            if (parent) parent.classList.remove('withMultiSelect');
        });
        document.querySelectorAll('.withMultiSelect').forEach(function(el) {
            el.classList.remove('withMultiSelect');
        });
    }

    function showExcludeConfirmModal(targetItemIds) {
        var ids = Array.isArray(targetItemIds) ? targetItemIds : [targetItemIds];
        var count = ids.length;
        var isMultiple = count > 1;

        hideModal();

        var modal = ensureModal();
        modal.classList.remove('hide');
        modal.style.setProperty('display', 'flex', 'important');
        modal.style.setProperty('pointer-events', 'auto', 'important');
        modal.style.setProperty('visibility', 'visible', 'important');
        modal.style.setProperty('z-index', '9999999', 'important');

        var inner = modal.querySelector('.jf-modal-inner') || document.createElement('div');
        inner.className = 'jf-modal-inner';
        modal.innerHTML = '';
        modal.appendChild(inner);

        inner.style.cssText = 'background:#222;padding:24px;border-radius:8px;width:90%;max-width:450px;color:#fff;box-shadow:0 8px 32px rgba(0,0,0,0.8);position:relative;z-index:10000000;';

        var title = isMultiple ? ('Exclude & Remove ' + count + ' Movies') : 'Exclude & Remove Movie';
        var desc = isMultiple
            ? ('Are you sure you want to block these ' + count + ' movies from being scraped again? This will also remove them from your Jellyfin library.')
            : 'Are you sure you want to block this movie from being scraped again? This will also remove the item from your Jellyfin library.';
        var btnText = isMultiple ? ('Exclude & Remove (' + count + ')') : 'Exclude & Remove';

        inner.innerHTML =
            '<div style="display:flex;flex-direction:column;align-items:center;padding:20px;text-align:center;">' +
            '<span class="material-icons" style="font-size:48px;color:#f44336;margin-bottom:15px;">warning</span>' +
            '<h2 style="margin:0 0 10px;font-size:1.4em;font-weight:600;">' + escHtml(title) + '</h2>' +
            '<p style="margin:0 0 25px;color:#bbb;line-height:1.5;max-width:400px;font-size:0.95em;">' + escHtml(desc) + '</p>' +
            '<div style="display:flex;gap:15px;justify-content:center;">' +
            '<button type="button" id="jf-confirm-cancel-ctx" style="background:#444;color:#fff;border:none;padding:10px 20px;border-radius:4px;cursor:pointer;font-weight:bold;transition:background 0.2s;">Cancel</button>' +
            '<button type="button" id="jf-confirm-exclude-ctx" style="background:#f44336;color:#fff;border:none;padding:10px 20px;border-radius:4px;cursor:pointer;font-weight:bold;transition:background 0.2s;">' + escHtml(btnText) + '</button>' +
            '</div></div>';

        var cancelBtn = inner.querySelector('#jf-confirm-cancel-ctx');
        if (cancelBtn) {
            cancelBtn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                hideModal();
            });
        }

        var confBtn = inner.querySelector('#jf-confirm-exclude-ctx');
        if (confBtn) {
            confBtn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                executeExclusion(ids, confBtn);
            });
        }
    }

    function executeExclusion(targetItemIds, confBtn) {
        var ids = Array.isArray(targetItemIds) ? targetItemIds : [targetItemIds];
        confBtn.disabled = true;
        confBtn.textContent = ids.length > 1 ? ('Excluding ' + ids.length + ' movies...') : 'Excluding...';

        // Exit multi-select if active
        exitMultiSelectMode();

        // 1. Mark as excluded immediately so handleView / applyButtonSync cannot unhide play button
        ids.forEach(function (id) {
            excludedItemIds[id] = true;
        });

        // 2. Check if currently on a details page for one of the target items
        var currentId = getCurrentItemId();
        var onCurrentDetails = isDetailsPage() && (!currentId || ids.some(function (i) {
            return (currentId + '').toLowerCase() === (i + '').toLowerCase();
        }));

        if (onCurrentDetails) {
            var area = getButtonArea(document);
            if (area) {
                area.querySelectorAll('.btnPlay, .btnReplay, [data-action="play"], [data-action="resume"]').forEach(function (b) {
                    b.style.setProperty('display', 'none', 'important');
                    try { b.remove(); } catch(e) {}
                });
                var dlBtn = area.querySelector('#jf-dl-btn');
                if (dlBtn) dlBtn.remove();
            }
            document.body.classList.add('jf-suppress-play');
        }

        var promises = ids.map(function (id) {
            return jfFetch('/System/Configuration/Downloaders/Exclude/' + id, { method: 'POST' });
        });

        Promise.all(promises)
            .then(function () {
                hideModal();
                exitMultiSelectMode();

                // 3. Remove item cards from any grid or list currently rendered
                ids.forEach(function (id) {
                    var cards = document.querySelectorAll('[data-id="' + id + '"], [data-itemid="' + id + '"]');
                    cards.forEach(function (card) {
                        var wrapper = card.closest('.cardWrapper, .card, .listItem') || card;
                        wrapper.remove();
                    });
                });

                // 4. Instruct Jellyfin items containers to refresh (matching native delete)
                var containers = document.querySelectorAll('[is="emby-itemscontainer"]');
                containers.forEach(function (c) {
                    if (typeof c.notifyRefreshNeeded === 'function') {
                        c.notifyRefreshNeeded(true);
                    }
                });

                // 5. Navigation: ONLY IF currently on a details page for one of the deleted items
                if (onCurrentDetails) {
                    stopAll();
                    state.itemId = null;
                    state.isDownloadable = false;
                    state.isDownloading = false;
                    document.body.classList.remove('jf-download-mode');
                    document.body.classList.add('jf-suppress-play');

                    // 5a. Native back navigation (returns to previous library list / home)
                    try {
                        window.history.back();
                    } catch (e) {}

                    // 5b. Safety fallback if history.back did not transition view
                    setTimeout(function () {
                        if (isDetailsPage()) {
                            if (window.Emby && window.Emby.Page && typeof window.Emby.Page.goHome === 'function') {
                                window.Emby.Page.goHome();
                            } else if (window.Dashboard && typeof window.Dashboard.navigate === 'function') {
                                window.Dashboard.navigate('home');
                            } else {
                                window.location.hash = '#/home';
                            }
                        }
                        hideModal();
                    }, 150);
                } else {
                    // On Home page or list views: remain on page smoothly
                    document.body.classList.remove('jf-suppress-play');
                }
            })
            .catch(function () {
                ids.forEach(function (id) {
                    delete excludedItemIds[id];
                });
                document.body.classList.remove('jf-suppress-play');
                confBtn.disabled = false;
                confBtn.textContent = 'Failed';
                setTimeout(function () {
                    confBtn.textContent = ids.length > 1 ? ('Exclude & Remove (' + ids.length + ')') : 'Exclude & Remove';
                }, 2000);
            });
    }


    function renderModalContent(modal, view, itemId) {
        var inner = modal.querySelector('.jf-modal-inner') || document.createElement('div');
        inner.className = 'jf-modal-inner';
        inner.style.cssText = 'background:#222;padding:24px;border-radius:8px;width:90%;' +
            'max-width:600px;color:#fff;max-height:80vh;overflow-y:auto;box-shadow:0 4px 20px rgba(0,0,0,0.6);';

        if (state.isDownloading) {
            renderProgressView(inner, modal, view, itemId);
        } else {
            renderOptionsView(inner, modal, view, itemId);
        }

        if (!modal.contains(inner)) modal.appendChild(inner);
    }

    function renderProgressView(inner, modal, view, itemId) {
        var sTxt = state.status || 'Downloading...';
        var pct  = state.progress || 0;
        var isErr = /fail|stop/i.test(sTxt);
        var isPaused = /pause/i.test(sTxt) || state.isPaused;
        var isLowPeer = !!state.lowPeerWarning; // Item #7

        var headerIcon = isErr ? 'error' : (isPaused ? 'pause' : 'sync');
        var headerSpin = (!isErr && !isPaused) ? ' jf-spin' : '';
        var headerClr  = isErr ? '#f44336' : (isPaused ? '#ff9800' : (isLowPeer ? '#ff9800' : '#2196f3'));
        var headerTxt  = isErr ? 'Download Failed' : (isPaused ? 'Download Paused' : (isLowPeer ? 'Download Inactive' : 'Download in Progress'));
        var statusClr  = isLowPeer ? '#ff9800' : '#bbb';
        var barGrad    = isLowPeer
            ? 'linear-gradient(90deg,#ff9800,#ffc107)'
            : 'linear-gradient(90deg,#2196f3,#00bcd4)';
        var lowPeerBadge =
            '<div id="jf-modal-warning" style="display:' + (isLowPeer ? 'flex' : 'none') + ';align-items:flex-start;gap:12px;background:rgba(255,152,0,0.15);border:1px solid rgba(255,152,0,0.5);border-radius:6px;padding:12px 14px;margin-bottom:14px;color:#ffb74d;">' +
            '<span class="material-icons" style="font-size:22px;color:#ff9800;flex-shrink:0;margin-top:2px;">warning_amber</span>' +
            '<div style="font-size:13px;line-height:1.45;color:#ffe0b2;">' +
            '<div style="font-weight:700;color:#ffb74d;margin-bottom:3px;font-size:14px;">This file size is currently not active or available on the internet.</div>' +
            'Please stop this download and try a different size or quality option.</div></div>';

        var pauseOrResumeBtn = isPaused
            ? '<button type="button" id="jf-btn-resume" style="background:#4caf50;color:#fff;padding:8px 18px;border-radius:4px;border:none;cursor:pointer;font-weight:bold;display:flex;align-items:center;gap:6px;">' +
              '<span class="material-icons" style="font-size:18px;">play_arrow</span>Resume</button>'
            : '<button type="button" id="jf-btn-pause" style="background:#ff9800;color:#fff;padding:8px 18px;border-radius:4px;border:none;cursor:pointer;font-weight:bold;display:flex;align-items:center;gap:6px;">' +
              '<span class="material-icons" style="font-size:18px;">pause</span>Pause</button>';

        inner.innerHTML =
            '<div style="display:flex;align-items:flex-start;justify-content:space-between;border-bottom:1px solid #333;padding-bottom:12px;margin-bottom:12px;">' +
            '<h2 id="jf-modal-header" style="margin:0;display:flex;align-items:center;gap:10px;">' +
            '<span class="material-icons' + headerSpin + '" style="color:' + headerClr + ';">' + headerIcon + '</span>' + headerTxt + '</h2>' +
            '<button type="button" id="jf-btn-close" title="Close" style="background:transparent;color:#bbb;border:none;cursor:pointer;padding:4px;display:flex;align-items:center;justify-content:center;border-radius:50%;">' +
            '<span class="material-icons" style="font-size:24px;">close</span></button></div>' +
            '<div style="margin-bottom:16px;">' +
            lowPeerBadge +
            '<div id="jf-modal-status" style="font-size:0.9em;color:' + statusClr + ';margin-bottom:12px;">' + escHtml(sTxt) + '</div>' +
            '<div style="background:#333;border-radius:8px;height:14px;overflow:hidden;margin-bottom:8px;">' +
            '<div id="jf-modal-bar" style="background:' + barGrad + ';height:100%;width:' + pct + '%;transition:width 0.3s ease;"></div>' +
            '</div>' +
            '<div style="display:flex;justify-content:space-between;font-size:0.85em;color:#888;">' +
            '<span id="jf-modal-pct">' + pct + '% completed</span>' +
            '<span>Seedr / Torbox Cloud</span></div></div>' +
            '<div style="display:flex;gap:12px;margin-top:20px;padding-top:16px;border-top:1px solid #333;">' +
            pauseOrResumeBtn +
            '<button type="button" id="jf-btn-stop" style="background:#f44336;color:#fff;padding:8px 18px;border-radius:4px;border:none;cursor:pointer;font-weight:bold;display:flex;align-items:center;gap:6px;">' +
            '<span class="material-icons" style="font-size:18px;">stop</span>Stop</button></div>';

        var closeBtn = inner.querySelector('#jf-btn-close');
        if (closeBtn) closeBtn.addEventListener('click', function () { modal.classList.add('hide'); });

        var pauseBtn = inner.querySelector('#jf-btn-pause');
        if (pauseBtn) {
            pauseBtn.addEventListener('click', function () {
                pauseBtn.disabled = true;
                jfFetch('/System/Configuration/Downloaders/Pause/' + itemId, { method: 'POST' })
                    .then(function () { state.isPaused = true; renderModalContent(modal, view, itemId); })
                    .catch(function () { pauseBtn.disabled = false; });
            });
        }

        var resumeBtn = inner.querySelector('#jf-btn-resume');
        if (resumeBtn) {
            resumeBtn.addEventListener('click', function () {
                resumeBtn.disabled = true;
                jfFetch('/System/Configuration/Downloaders/Resume/' + itemId, { method: 'POST' })
                    .then(function () { state.isPaused = false; renderModalContent(modal, view, itemId); })
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
                        syncButton(view, itemId);
                        renderModalContent(modal, view, itemId);
                    }).catch(function () {
                        stopAll();
                        state.isDownloading = false;
                        state.isDownloadable = true;
                        syncButton(view, itemId);
                    });
            });
        }
    }

    function renderOptionsView(inner, modal, view, itemId) {
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
            '<div style="display:flex;align-items:flex-start;justify-content:space-between;border-bottom:1px solid #333;padding-bottom:10px;margin-bottom:12px;">' +
            '<h2 style="margin:0;">Available Downloads</h2>' +
            '<button type="button" id="jf-btn-close" title="Close" style="background:transparent;color:#bbb;border:none;cursor:pointer;padding:4px;display:flex;align-items:center;justify-content:center;border-radius:50%;">' +
            '<span class="material-icons" style="font-size:24px;">close</span></button></div>' +
            (state.description ? '<p style="margin:0 0 14px;font-size:0.95em;color:#ffb74d;background:rgba(255,152,0,0.12);padding:10px 14px;border-radius:6px;border:1px solid rgba(255,152,0,0.35);line-height:1.4;">' + escHtml(state.description) + '</p>' : '') +
            (opts.length > 0 ? ('<div id="jf-lang-tabs" style="display:flex;gap:10px;margin-bottom:15px;border-bottom:1px solid #333;padding-bottom:10px;overflow-x:auto;">' + tabsHtml + '</div>' +
            '<div id="jf-opt-list" style="display:flex;flex-direction:column;gap:12px;">' + listHtml + '</div>') : '');

        var closeBtn = inner.querySelector('#jf-btn-close');
        if (closeBtn) closeBtn.addEventListener('click', function () { hideModal(); });

        inner.querySelectorAll('.jf-lang-tab').forEach(function (tb) {
            tb.addEventListener('click', function () {
                state.language = tb.getAttribute('data-lang');
                renderModalContent(modal, view, itemId);
            });
        });

        inner.querySelectorAll('.jf-dl-option-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var row = btn.closest('[data-uri]');
                if (!row) return;
                var uri  = row.getAttribute('data-uri');
                var size = parseFloat(row.getAttribute('data-size')) || 0;

                // 1. Immediately switch modal to downloading progress view (DO NOT MINIMIZE!)
                state.isDownloading = true;
                state.isDownloadable = false;
                state.status = 'Starting download...';
                state.progress = 0;

                renderModalContent(modal, view, itemId);
                syncButton(view, itemId);

                startDownload(modal, view, itemId, uri, size);
            });
        });
    }

    // ── Start Download & Polling ──────────────────────────────────────
    function startDownload(modal, view, itemId, magnetUri, sizeGb) {
        jfFetch('/System/Configuration/Downloaders/Download/' + itemId, {
            method: 'POST',
            headers: Object.assign({ 'Content-Type': 'application/json' }, authHeaders()),
            body: JSON.stringify({ MagnetUri: magnetUri, SizeGb: sizeGb })
        }).then(function (res) {
            if (!res.ok) throw new Error('start failed with status ' + res.status);
            state.isDownloading = true;
            state.isDownloadable = false;
            state.status = 'Connecting to cloud...';
            state.progress = 0;
            syncButton(view, itemId);

            if (modal && !modal.classList.contains('hide')) {
                renderModalContent(modal, view, itemId);
            }

            startPolling(view, itemId);
        }).catch(function (err) {
            console.error('[JellyFetch] Failed to start download:', err);
            state.isDownloading = false;
            state.isDownloadable = true;
            syncButton(view, itemId);
            if (modal && !modal.classList.contains('hide')) {
                renderModalContent(modal, view, itemId);
            }
            alert('Failed to start download. Check server logs.');
        });
    }

    function startPolling(view, itemId) {
        stopAll();
        state.pollTimer = setInterval(function () {
            if (getCurrentItemId() !== itemId) {
                stopAll();
                return;
            }
            pollStatus(view, itemId);
        }, 2000);
    }

    function pollStatus(view, itemId) {
        jfFetch('/System/Configuration/Downloaders/Status/' + itemId + '?t=' + Date.now())
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (info) {
                if (!info) return;

                if (info.Completed) {
                    stopAll();
                    state.isDownloading = false;
                    state.isDownloadable = false;
                    document.body.classList.remove('jf-download-mode');

                    var modal = document.getElementById('jf-dl-modal');
                    if (modal) modal.classList.add('hide');

                    syncButton(view, itemId);

                    var resolvedId = info.NewItemId || itemId;

                    jfFetch('/Items/' + resolvedId + '/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=FullRefresh&replaceAllImages=false&replaceAllMetadata=false', { method: 'POST' }).catch(function () {});

                    if (window.Dashboard) window.Dashboard.alert({ title: 'Download Complete', message: 'Download finished! Loading movie page...' });

                    state.navTimer = setTimeout(function () {
                        state.navTimer = null;
                        if (window.Emby && window.Emby.Page) {
                            window.Emby.Page.showItem(resolvedId);
                        } else {
                            window.location.hash = '#/details?id=' + resolvedId;
                        }
                    }, 5000);
                    return;
                }

                if (info.Status === 'Failed' || info.Status === 'Stopped') {
                    stopAll();
                    state.isDownloading = false;
                    state.isDownloadable = true;
                    state.status = info.Status;
                    syncButton(view, itemId);
                    var m = document.getElementById('jf-dl-modal');
                    if (m && !m.classList.contains('hide')) {
                        renderModalContent(m, view, itemId);
                    }
                    return;
                }

                state.status   = info.Status   || state.status;
                state.progress = info.Progress  || 0;
                state.isPaused = !!info.IsPaused;
                state.lowPeerWarning = !!info.LowPeerWarning; // Item #7

                syncButton(view, itemId);

                var bar = document.getElementById('jf-modal-bar');
                if (bar) {
                    bar.style.width = state.progress + '%';
                    bar.style.background = state.lowPeerWarning ? 'linear-gradient(90deg,#ff9800,#ffc107)' : 'linear-gradient(90deg,#2196f3,#00bcd4)';
                }
                var sl  = document.getElementById('jf-modal-status');
                if (sl) {
                    sl.textContent = state.status;
                    sl.style.color = state.lowPeerWarning ? '#ff9800' : '#bbb';
                }
                var pl  = document.getElementById('jf-modal-pct');
                if (pl)  pl.textContent = state.progress + '% completed';
                var wb  = document.getElementById('jf-modal-warning');
                if (wb)  wb.style.display = state.lowPeerWarning ? 'flex' : 'none';
            })
            .catch(function () {});
    }

    // ── Event Listeners (Native Jellyfin Event-Driven) ────────────────
    function onPageChange() {
        cleanupAllModalsAndBackdrops();

        if (isPlayerActive() || !isDetailsPage()) {
            document.body.classList.remove('jf-download-mode');
            document.body.classList.remove('jf-suppress-play');
            stopAll();
            var btn = document.getElementById('jf-dl-btn');
            if (btn) btn.remove();
            state.itemId = null;
            return;
        }

        var itemId = getCurrentItemId();
        if (!itemId) return;

        // Find active details page
        var page = document.querySelector('.mainAnimatedPages > .mainAnimatedPage:not(.hide)')
            || document.getElementById('itemDetailPage')
            || document.body;

        handleView(page, itemId);
    }

    // Jellyfin dispatches 'viewshow' with bubbles: true on the active view
    document.addEventListener('viewshow', function (e) {
        if (isPlayerActive() || !isDetailsPage()) return;
        if (e.target && (e.target.id === 'videoOsdPage' || (e.target.classList && e.target.classList.contains('videoPlayerContainer')))) return;
        var view = e.target;
        var itemId = (e.detail && e.detail.params && e.detail.params.id) || getCurrentItemId();
        handleView(view, itemId);
    });

    // Cleanup on view hide
    document.addEventListener('viewhide', function () {
        cleanupAllModalsAndBackdrops();
        if (isPlayerActive() || !isDetailsPage()) {
            stopAll();
            document.body.classList.remove('jf-download-mode');
        }
    });

    window.addEventListener('hashchange', onPageChange);
    window.addEventListener('popstate', onPageChange);

    // Initial check for hard refresh
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', onPageChange);
    } else {
        setTimeout(onPageChange, 300);
    }

    var lastKnownItemId = null;
    function updateLastKnownId(e) {
        // Find the closest ancestor that has a data-id or data-itemid
        var el = e.target.closest('[data-id], [data-itemid]');
        if (el) {
            lastKnownItemId = el.getAttribute('data-id') || el.getAttribute('data-itemid');
        }
    }
    document.addEventListener('click', updateLastKnownId, true);
    document.addEventListener('contextmenu', updateLastKnownId, true);

    // ── Context Menu Injection ──────────────────────────────────────────────────
    var contextMenuObserver = new MutationObserver(function (mutations) {
        if (isPlayerActive()) return;

        var sheets = document.querySelectorAll('.actionSheet, .actionsheet');
        if (!sheets || sheets.length === 0) return;
        var sheet = null;
        for (var sIdx = sheets.length - 1; sIdx >= 0; sIdx--) {
            if (sheets[sIdx].style.display !== 'none' && !sheets[sIdx].classList.contains('hide')) {
                sheet = sheets[sIdx];
                break;
            }
        }
        if (!sheet) sheet = sheets[sheets.length - 1];

        var scroller = sheet.querySelector('.actionSheetScroller') || sheet;
        if (scroller.querySelector('#jf-ctx-exclude')) return;

        var allBtns = scroller.querySelectorAll('.actionSheetMenuItem');
        if (!allBtns || allBtns.length === 0) return;

        // Check if multi-select mode is currently active
        var isMultiSelect = !!document.querySelector('.selectionCommandsPanel');
        var multiIds = isMultiSelect ? getSelectedMultiItemIds() : [];

        var targetIds = [];
        if (isMultiSelect && multiIds.length > 0) {
            targetIds = multiIds;
        } else {
            var singleId = (isDetailsPage() ? getCurrentItemId() : null) || lastKnownItemId || getCurrentItemId();
            if (singleId) targetIds = [singleId];
        }

        if (targetIds.length === 0) return;

        var cacheKey = targetIds.join(',');
        if (sheet.getAttribute('data-jf-checked') === cacheKey) return;
        sheet.setAttribute('data-jf-checked', cacheKey);

        var probeId = targetIds[0];

        function injectExcludeButton() {
            if (!document.body.contains(sheet)) return;
            if (sheet.getAttribute('data-jf-checked') !== cacheKey) return;
            if (scroller.querySelector('#jf-ctx-exclude')) return;

            var currentBtns = scroller.querySelectorAll('.actionSheetMenuItem');
            var templateBtn = (currentBtns && currentBtns.length > 0) ? currentBtns[currentBtns.length - 1] : allBtns[allBtns.length - 1];
            var btn = templateBtn.cloneNode(true);
            btn.id = 'jf-ctx-exclude';
            btn.removeAttribute('data-id');
            btn.removeAttribute('data-action');
            
            var iconEls = btn.querySelectorAll('.listItemIcon, .actionSheetMenuItemIcon, .md-icon, .material-symbols-outlined, .material-icons, svg');
            if (iconEls.length > 0) {
                var templateIcon = iconEls[0];
                var exactClasses = typeof templateIcon.className === 'object' ? templateIcon.className.baseVal : templateIcon.className;
                
                var newSvg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                newSvg.setAttribute('viewBox', '0 0 24 24');
                newSvg.setAttribute('width', '24');
                newSvg.setAttribute('height', '24');
                newSvg.setAttribute('fill', 'none');
                newSvg.setAttribute('stroke', 'currentColor');
                newSvg.setAttribute('stroke-width', '1.8');
                newSvg.setAttribute('stroke-linecap', 'round');
                newSvg.setAttribute('stroke-linejoin', 'round');
                newSvg.style.color = '#ff5252'; 
                
                if (typeof newSvg.className === 'object') {
                    newSvg.className.baseVal = exactClasses;
                } else {
                    newSvg.setAttribute('class', exactClasses);
                }

                newSvg.innerHTML = '<circle cx="12" cy="12" r="10"></circle><line x1="5.5" y1="5.5" x2="18.5" y2="18.5"></line>';
                
                templateIcon.parentNode.insertBefore(newSvg, templateIcon);
                
                for (var i = 0; i < iconEls.length; i++) {
                    if (iconEls[i].parentNode) {
                        iconEls[i].parentNode.removeChild(iconEls[i]);
                    }
                }
            }
            
            var label = targetIds.length > 1 ? ('Exclude & Remove (' + targetIds.length + ')') : 'Exclude & Remove';
            var textEl = btn.querySelector('.listItemBodyText, .actionSheetMenuItemText');
            if (textEl) {
                textEl.textContent = label;
                textEl.style.color = 'inherit';
            } else if (btn.querySelector('.listItemBody')) {
                var bodyText = btn.querySelector('.listItemBody');
                bodyText.textContent = label;
                bodyText.style.color = 'inherit';
            }
            
            scroller.appendChild(btn);

            btn.setAttribute('data-id', 'jf-exclude');
            btn.addEventListener('click', function(e) {
                var currentTargetIds = targetIds.slice();

                var modalTriggered = false;
                function openConfirm() {
                    if (modalTriggered) return;
                    modalTriggered = true;
                    showExcludeConfirmModal(currentTargetIds);
                }

                // Listen for sheet closing/close event or wait for clean dialogHelper teardown
                sheet.addEventListener('close', openConfirm, { once: true });
                setTimeout(openConfirm, 160);
            });
        }

        var isDetails = isDetailsPage();
        var isKnownStrm = isMultiSelect ||
            (isDetails && (probeId === state.itemId || probeId === getCurrentItemId()) && (state.isDownloadable || state.isDownloading || (state.options && state.options.length > 0))) ||
            strmItemCache[probeId] === true;

        if (isKnownStrm) {
            injectExcludeButton();
            return;
        }

        jfFetch('/System/Configuration/Downloaders/Options/' + probeId)
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (optData) {
                var isStrm = optData && optData.IsStrm;
                if (isStrm) strmItemCache[probeId] = true;
                if (!isStrm && !isMultiSelect) return;
                injectExcludeButton();
            });
    });
    contextMenuObserver.observe(document.body, { childList: true, subtree: true });

}());
