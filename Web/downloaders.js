export default function(view, params) {
    const pluginId = 'A1B2C3D4-E5F6-7890-1234-567890ABCDEF';

    function loadPage() {
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {

            view.querySelector('#txtSeedrUsername').value = config.SeedrUsername || '';
            view.querySelector('#txtSeedrPassword').value = config.SeedrPassword || '';
            view.querySelector('#txtTorboxApiKey').value = config.TorboxApiKey || '';
            view.querySelector('#txtDownloadsDirectory').value = config.DownloadsDirectory || '/media/Downloads';
            
            const chkSeedr = view.querySelector('#chkEnableSeedr');
            const chkTorbox = view.querySelector('#chkEnableTorbox');
            chkSeedr.checked = config.EnableSeedr !== false;
            chkTorbox.checked = config.EnableTorbox !== false;
            
            const seedrActive = view.querySelector('#seedrActiveStatus');
            const torboxActive = view.querySelector('#torboxActiveStatus');
            
            const updateStatusDisplays = () => {
                const sUser = view.querySelector('#txtSeedrUsername').value.trim();
                const sPass = view.querySelector('#txtSeedrPassword').value.trim();
                const tKey = view.querySelector('#txtTorboxApiKey').value.trim();

                if (seedrActive) {
                    if (chkSeedr.checked) {
                        seedrActive.style.display = 'block';
                        seedrActive.style.color = '#ff9800';
                        view.querySelector('#seedrActiveIcon').innerText = 'hourglass_empty';
                        view.querySelector('#seedrActiveText').innerText = 'Checking credentials...';
                    } else {
                        seedrActive.style.display = 'none';
                    }
                }
                
                if (torboxActive) {
                    if (chkTorbox.checked) {
                        torboxActive.style.display = 'block';
                        torboxActive.style.color = '#ff9800';
                        view.querySelector('#torboxActiveIcon').innerText = 'hourglass_empty';
                        view.querySelector('#torboxActiveText').innerText = 'Checking credentials...';
                    } else {
                        torboxActive.style.display = 'none';
                    }
                }

                if (!chkSeedr.checked && !chkTorbox.checked) return;

                if (chkSeedr.checked && (!sUser || !sPass)) {
                    seedrActive.style.color = '#ff9800';
                    view.querySelector('#seedrActiveIcon').innerText = 'warning';
                    view.querySelector('#seedrActiveText').innerText = 'Missing Seedr credentials';
                }
                if (chkTorbox.checked && !tKey) {
                    torboxActive.style.color = '#ff9800';
                    view.querySelector('#torboxActiveIcon').innerText = 'warning';
                    view.querySelector('#torboxActiveText').innerText = 'Missing Torbox API Key';
                }

                // If everything that is checked is missing creds, abort network call
                if ((chkSeedr.checked && (!sUser || !sPass)) && (chkTorbox.checked && !tKey) || 
                    (chkSeedr.checked && (!sUser || !sPass) && !chkTorbox.checked) || 
                    (chkTorbox.checked && !tKey && !chkSeedr.checked)) {
                    return;
                }

                fetch('/System/Configuration/Downloaders/TestCredentials', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
                    },
                    body: JSON.stringify({
                        SeedrUsername: sUser,
                        SeedrPassword: sPass,
                        TorboxApiKey: tKey
                    })
                }).then(res => {
                    return res.json().then(data => ({ status: res.status, ok: res.ok, body: data }));
                }).then(res => {
                    if (chkSeedr.checked && sUser && sPass) {
                        if (res.ok || (res.body.Message && !res.body.Message.includes("Seedr login failed"))) {
                            seedrActive.style.color = '#4caf50';
                            view.querySelector('#seedrActiveIcon').innerText = 'check_circle';
                            view.querySelector('#seedrActiveText').innerText = 'Seedr cloud downloader active';
                        } else {
                            seedrActive.style.color = '#f44336';
                            view.querySelector('#seedrActiveIcon').innerText = 'error';
                            view.querySelector('#seedrActiveText').innerText = 'Seedr login failed. Invalid credentials.';
                        }
                    }

                    if (chkTorbox.checked && tKey) {
                        if (res.ok || (res.body.Message && !res.body.Message.includes("Torbox API"))) {
                            torboxActive.style.color = '#4caf50';
                            view.querySelector('#torboxActiveIcon').innerText = 'check_circle';
                            view.querySelector('#torboxActiveText').innerText = 'Torbox cloud downloader active';
                        } else {
                            torboxActive.style.color = '#f44336';
                            view.querySelector('#torboxActiveIcon').innerText = 'error';
                            view.querySelector('#torboxActiveText').innerText = 'Torbox API Key invalid or expired.';
                        }
                    }
                }).catch(() => {
                    if (chkSeedr.checked) {
                        seedrActive.style.color = '#f44336';
                        view.querySelector('#seedrActiveIcon').innerText = 'error';
                        view.querySelector('#seedrActiveText').innerText = 'Network error checking Seedr';
                    }
                    if (chkTorbox.checked) {
                        torboxActive.style.color = '#f44336';
                        view.querySelector('#torboxActiveIcon').innerText = 'error';
                        view.querySelector('#torboxActiveText').innerText = 'Network error checking Torbox';
                    }
                });
            };
            
            updateStatusDisplays();
            chkSeedr.addEventListener('change', updateStatusDisplays);
            chkTorbox.addEventListener('change', updateStatusDisplays);
            
            view.querySelector('#txtSeedrUsername').addEventListener('blur', updateStatusDisplays);
            view.querySelector('#txtSeedrPassword').addEventListener('blur', updateStatusDisplays);
            view.querySelector('#txtTorboxApiKey').addEventListener('blur', updateStatusDisplays);

            fetch('/System/Configuration/Downloaders/Domain', {
                headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
            }).then(r => r.json()).then(d => {
                if (d) {
                    const activeDom = view.querySelector('#lblActiveDomain');
                    if (activeDom) activeDom.textContent = d.Domain || '1tamilmv.meme';
                    const domInput = view.querySelector('#txtCustomDomain');
                    if (domInput) domInput.value = d.Override || '';
                }
            }).catch(e => console.error("Error loading domain:", e));

            fetch('/System/Configuration/Downloaders/Languages', {
                headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
            }).then(r => r.json()).then(langs => {
                const checkboxes = view.querySelectorAll('input[name="chkLanguage"]');
                checkboxes.forEach(cb => {
                    cb.checked = langs.includes(cb.value);
                });
            }).catch(e => console.error("Error loading languages:", e)).finally(() => {
                Dashboard.hideLoadingMsg();
            });
        });

        const btnRunScraper = view.querySelector('#btnRunScraper');
        const btnStopScraper = view.querySelector('#btnStopScraper');
        const btnCleanupScraper = view.querySelector('#btnCleanupScraper');
        const progressContainer = view.querySelector('#scraperProgressContainer');
        const progressBar = view.querySelector('#scraperProgressBar');
        const statusText = view.querySelector('#scraperStatusText');
        const pctText = view.querySelector('#scraperPctText');
        const logContainer = view.querySelector('#operationLogContainer');
        const logBox = view.querySelector('#operationLogBox');
        const logCount = view.querySelector('#operationLogCount');
        let pollInterval = null;

        function updateLogDisplay(logs) {
            if (!logContainer || !logBox) return;
            if (logs && logs.length > 0) {
                logContainer.style.display = 'block';
                logBox.textContent = logs.join('\n');
                logBox.scrollTop = logBox.scrollHeight;
                if (logCount) logCount.textContent = logs.length + ' entries';
            }
        }

        function startPolling() {
            if (pollInterval) clearInterval(pollInterval);
            hideCleanupBanner(); // clear any stale cleanup banner when scraper starts
            progressContainer.style.display = 'block';
            progressBar.style.backgroundColor = '#00a4dc';
            btnRunScraper.style.display = 'none';
            if (btnCleanupScraper) btnCleanupScraper.style.display = 'none';
            if (btnCleanMetadata) btnCleanMetadata.style.display = 'none';
            btnStopScraper.style.display = 'block';
            btnStopScraper.querySelector('span').innerText = 'Stop Scraper';
            if (logBox) logBox.textContent = '';
            
            pollInterval = setInterval(() => {
                fetch('/System/Configuration/Downloaders/Scrape/Status', {
                    headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                }).then(r => r.json()).then(d => {
                    if (d.IsRunning || d.Progress > 0) {
                        progressContainer.style.display = 'block';
                        btnRunScraper.style.display = 'none';
                        if (btnCleanupScraper) btnCleanupScraper.style.display = 'none';
                        if (btnCleanMetadata) btnCleanMetadata.style.display = 'none';
                        btnStopScraper.style.display = d.IsRunning ? 'block' : 'none';
                        
                        let pct = d.Progress || 0;
                        progressBar.style.width = pct + '%';
                        pctText.innerText = Math.round(pct) + '%';
                        statusText.innerText = d.Status || (d.IsRunning ? 'Running...' : 'Idle');
                        updateLogDisplay(d.Logs);
                        
                        if (!d.IsRunning && (pct === 100 || d.Status === 'Idle' || d.Status.startsWith('Error') || d.Status.startsWith('Failed'))) {
                            clearInterval(pollInterval);
                            setTimeout(() => {
                                btnRunScraper.style.display = 'block';
                                if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                                if (btnCleanMetadata) btnCleanMetadata.style.display = 'block';
                                btnStopScraper.style.display = 'none';
                                progressContainer.style.display = 'none';
                            }, 3000);
                        }
                    } else {
                        clearInterval(pollInterval);
                        btnRunScraper.style.display = 'block';
                        if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                        if (btnCleanMetadata) btnCleanMetadata.style.display = 'block';
                        btnStopScraper.style.display = 'none';
                        progressContainer.style.display = 'none';
                    }
                }).catch(() => {
                    clearInterval(pollInterval);
                    btnRunScraper.style.display = 'block';
                    if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                    if (btnCleanMetadata) btnCleanMetadata.style.display = 'block';
                    btnStopScraper.style.display = 'none';
                    progressContainer.style.display = 'none';
                });
            }, 1200);
        }

        let bannerTimeout = null;
        function showCleanupBanner(msg, isError) {
            var banner = view.querySelector('#cleanupResultBanner');
            if (!banner) return;
            banner.innerText = (isError ? '❌ ' : '✓ ') + msg;
            banner.style.display = 'flex';
            banner.style.background = isError ? 'rgba(244,67,54,0.12)' : 'rgba(76,175,80,0.12)';
            banner.style.borderColor  = isError ? 'rgba(244,67,54,0.4)' : 'rgba(76,175,80,0.4)';
            banner.style.color = isError ? '#f44336' : '#4caf50';
            
            if (bannerTimeout) clearTimeout(bannerTimeout);
            bannerTimeout = setTimeout(hideCleanupBanner, 5000); // auto-hide after 5 seconds
        }
        function hideCleanupBanner() {
            var banner = view.querySelector('#cleanupResultBanner');
            if (banner) banner.style.display = 'none';
        }

        function startMetadataPolling() {
            if (pollInterval) clearInterval(pollInterval);
            hideCleanupBanner();
            progressContainer.style.display = 'block';
            progressBar.style.backgroundColor = '#4caf50';
            btnRunScraper.style.display = 'none';
            if (btnCleanupScraper) btnCleanupScraper.style.display = 'none';
            if (btnCleanMetadata) btnCleanMetadata.style.display = 'none';
            btnStopScraper.style.display = 'none';
            if (logBox) logBox.textContent = '';

            pollInterval = setInterval(() => {
                fetch('/System/Configuration/Downloaders/MetadataStatus', {
                    headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                }).then(r => r.json()).then(d => {
                    let pct = d.Progress || 0;
                    let stat = d.Status || 'Idle';
                    
                    if (stat !== 'Idle') {
                        progressContainer.style.display = 'block';
                        btnRunScraper.style.display = 'none';
                        if (btnCleanupScraper) btnCleanupScraper.style.display = 'none';
                        if (btnCleanMetadata) btnCleanMetadata.style.display = 'none';
                        btnStopScraper.style.display = 'none';

                        progressBar.style.width = pct + '%';
                        pctText.innerText = Math.round(pct) + '%';
                        statusText.innerText = stat;
                        updateLogDisplay(d.Logs);

                        var isErr = stat === 'Error' || stat.startsWith('Error') || stat.startsWith('Failed');
                        var isComplete = (stat === 'Completed' || pct === 100 || isErr);

                        if (isComplete) {
                            clearInterval(pollInterval);
                            showCleanupBanner(stat === 'Completed' ? 'Metadata sanitization completed successfully.' : 'Metadata sanitization stopped or failed.', isErr);
                            
                            setTimeout(() => {
                                btnRunScraper.style.display = 'block';
                                if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                                if (btnCleanMetadata) btnCleanMetadata.style.display = 'block';
                                progressContainer.style.display = 'none';
                            }, 5000);
                        }
                    } else {
                        clearInterval(pollInterval);
                        btnRunScraper.style.display = 'block';
                        if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                        if (btnCleanMetadata) btnCleanMetadata.style.display = 'block';
                        progressContainer.style.display = 'none';
                    }
                }).catch(() => {
                    clearInterval(pollInterval);
                    btnRunScraper.style.display = 'block';
                    if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                    if (btnCleanMetadata) btnCleanMetadata.style.display = 'block';
                    progressContainer.style.display = 'none';
                });
            }, 1500);
        }

        function startCleanupPolling() {
            if (pollInterval) clearInterval(pollInterval);
            hideCleanupBanner(); // clear any previous result banner when a new cleanup starts
            progressContainer.style.display = 'block';
            progressBar.style.backgroundColor = '#e05206';
            btnRunScraper.style.display = 'none';
            if (btnCleanupScraper) btnCleanupScraper.style.display = 'none';
            if (btnCleanMetadata) btnCleanMetadata.style.display = 'none';
            btnStopScraper.style.display = 'block';
            btnStopScraper.querySelector('span').innerText = 'Stop Cleanup';
            if (logBox) logBox.textContent = '';

            pollInterval = setInterval(() => {
                fetch('/System/Configuration/Downloaders/CleanupStrm/Status', {
                    headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                }).then(r => r.json()).then(d => {
                    if (d.IsRunning || d.Progress > 0) {
                        progressContainer.style.display = 'block';
                        btnRunScraper.style.display = 'none';
                        if (btnCleanupScraper) btnCleanupScraper.style.display = 'none';
                        if (btnCleanMetadata) btnCleanMetadata.style.display = 'none';
                        btnStopScraper.style.display = d.IsRunning ? 'block' : 'none';

                        let pct = d.Progress || 0;
                        progressBar.style.width = pct + '%';
                        pctText.innerText = Math.round(pct) + '%';
                        statusText.innerText = d.Status || (d.IsRunning ? 'Cleaning up...' : 'Idle');
                        updateLogDisplay(d.Logs);

                        var isErr = d.Status && (d.Status.startsWith('Error') || d.Status.startsWith('Failed'));
                        var isComplete = !d.IsRunning && (pct === 100 || d.Status === 'Idle' || isErr);

                        if (isComplete) {
                            clearInterval(pollInterval);

                            // Item #2a — auto-refresh history table so new state is reflected immediately
                            loadHistory();

                            // Item #2b — show persistent result banner with the backend's status message
                            if (!isErr && pct === 100) {
                                showCleanupBanner('✓ ' + (d.Status || 'Cleanup complete.'), false);
                            } else if (isErr) {
                                showCleanupBanner('✗ ' + (d.Status || 'Cleanup encountered an error.'), true);
                            }

                            setTimeout(() => {
                                btnRunScraper.style.display = 'block';
                                if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                                if (btnCleanMetadata) btnCleanMetadata.style.display = 'block';
                                btnStopScraper.style.display = 'none';
                                progressContainer.style.display = 'none';
                            }, 3000);
                        }
                    } else {
                        clearInterval(pollInterval);
                        btnRunScraper.style.display = 'block';
                        if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                        if (btnCleanMetadata) btnCleanMetadata.style.display = 'block';
                        btnStopScraper.style.display = 'none';
                        progressContainer.style.display = 'none';
                    }
                }).catch(() => {
                    clearInterval(pollInterval);
                    btnRunScraper.style.display = 'block';
                    if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                    if (btnCleanMetadata) btnCleanMetadata.style.display = 'block';
                    btnStopScraper.style.display = 'none';
                    progressContainer.style.display = 'none';
                });
            }, 1000);
        }

        function loadHistory() {
            const tbody = view.querySelector('#historyTableBody');
            if (tbody) {
                tbody.innerHTML = '<tr><td colspan="4" style="padding: 10px; text-align: center; color: #888;">Loading history...</td></tr>';
                fetch('/System/Configuration/Downloaders/History', {
                    headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                }).then(r => r.json()).then(data => {
                    tbody.innerHTML = '';
                    if (!data || data.length === 0) {
                        tbody.innerHTML = '<tr><td colspan="4" style="padding: 10px; text-align: center; color: #888;">No downloads found.</td></tr>';
                        return;
                    }
                    data.forEach(item => {
                        let row = document.createElement('tr');
                        row.style.borderBottom = '1px solid #333';
                        let dt = new Date(item.Timestamp).toLocaleString();
                        
                        let color = '#ccc';
                        if (item.Status === 'Completed') color = '#4caf50';
                        else if (item.Status === 'Failed') color = '#f44336';
                        else if (item.Status === 'Stopped') color = '#ff9800';
                        else if (item.IsActive) color = '#00a4dc';

                        let statusStr = `<span style="color:${color}; font-weight:bold;">${item.Status}</span> <span style="font-size:0.8em; color:#888;">(${item.Provider})</span>`;
                        if (item.ErrorMessage) {
                            statusStr += `<br><span style="font-size:0.85em; color:#888;">${item.ErrorMessage}</span>`;
                        }

                        let actionsStr = '';
                        if (item.IsActive) {
                            const isPaused = item.Status === 'Paused' || item.Status.includes('Paused');
                            actionsStr = `
                                <div style="margin-top: 5px; display: flex; gap: 10px;">
                                    <button class="btnHistoryAction raised" data-action="${isPaused ? 'Resume' : 'Pause'}" data-id="${item.ItemId}" style="font-size: 0.8em; padding: 2px 6px; border-radius: 3px; cursor: pointer;">
                                        ${isPaused ? 'Resume' : 'Pause'}
                                    </button>
                                    <button class="btnHistoryAction raised" data-action="Stop" data-id="${item.ItemId}" style="font-size: 0.8em; padding: 2px 6px; border-radius: 3px; cursor: pointer; color: #f44336;">
                                        Cancel
                                    </button>
                                </div>
                            `;
                        }

                        row.innerHTML = `
                            <td style="padding: 10px; vertical-align:top;">${dt}</td>
                            <td style="padding: 10px; vertical-align:top;">${item.MovieName}</td>
                            <td style="padding: 10px; vertical-align:top;">${(item.SizeGb && item.SizeGb > 0) ? item.SizeGb.toFixed(2) + ' GB' : '—'}</td>
                            <td style="padding: 10px; vertical-align:top;">${statusStr}${actionsStr}</td>
                        `;
                        tbody.appendChild(row);
                    });
                    
                    // Attach event listeners to buttons
                    tbody.querySelectorAll('.btnHistoryAction').forEach(btn => {
                        btn.addEventListener('click', function(e) {
                            e.preventDefault();
                            const action = this.getAttribute('data-action');
                            const id = this.getAttribute('data-id');
                            fetch('/System/Configuration/Downloaders/' + action + '/' + id, {
                                method: 'POST',
                                headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                            }).then(() => loadHistory());
                        });
                    });
                }).catch(e => {
                    tbody.innerHTML = '<tr><td colspan="4" style="padding: 10px; text-align: center; color: #f44336;">Failed to load history.</td></tr>';
                });
            }
        }

        const btnRefreshHistory = view.querySelector('#btnRefreshHistory');
        if (btnRefreshHistory) {
            btnRefreshHistory.addEventListener('click', loadHistory);
        }

        const btnClearHistory = view.querySelector('#btnClearHistory');
        if (btnClearHistory) {
            btnClearHistory.addEventListener('click', function(e) {
                e.preventDefault();
                var msg = 'Are you sure you want to clear your download history? Active downloads will not be removed.';
                var executeClear = function() {
                    fetch('/System/Configuration/Downloaders/Activity', {
                        method: 'DELETE',
                        headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                    }).then(r => {
                        if (r.ok) {
                            loadHistory();
                        } else {
                            alert('Failed to clear history.');
                        }
                    }).catch(err => alert('Error clearing history: ' + err));
                };
                if (window.Dashboard && typeof window.Dashboard.confirm === 'function') {
                    window.Dashboard.confirm(msg, 'Clear History', executeClear);
                } else if (window.confirm(msg)) {
                    executeClear();
                }
            });
        }

        const btnResetPlugin = view.querySelector('#btnResetPlugin');
        if (btnResetPlugin) {
            btnResetPlugin.addEventListener('click', function(e) {
                e.preventDefault();
                var msg = 'WARNING: This will completely reset the JellyFetch plugin settings and wipe all saved credentials. You will need to re-configure the plugin. Are you absolutely sure?';
                var executeReset = function() {
                    fetch('/System/Configuration/Downloaders/ResetPlugin', {
                        method: 'POST',
                        headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                    }).then(r => {
                        if (r.ok) {
                            alert('Plugin configuration has been reset. The page will now reload.');
                            window.location.reload();
                        } else {
                            alert('Failed to reset plugin.');
                        }
                    }).catch(err => alert('Error resetting plugin: ' + err));
                };
                if (window.Dashboard && typeof window.Dashboard.confirm === 'function') {
                    window.Dashboard.confirm(msg, 'Factory Reset JellyFetch', executeReset);
                } else if (window.confirm(msg)) {
                    executeReset();
                }
            });
        }

        function checkInitialStatus() {
            fetch('/System/Configuration/Downloaders/MetadataStatus', {
                headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
            }).then(r => r.json()).then(m => {
                var isMetaErr = m.Status === 'Error' || (m.Status && m.Status.startsWith('Error')) || (m.Status && m.Status.startsWith('Failed'));
                if (m && m.Status && m.Status !== 'Idle' && m.Status !== 'Completed' && !isMetaErr) {
                    startMetadataPolling();
                } else {
                    fetch('/System/Configuration/Downloaders/Scrape/Status', {
                        headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                    }).then(r => r.json()).then(d => {
                        if (d && d.IsRunning) {
                            startPolling();
                        } else {
                            fetch('/System/Configuration/Downloaders/CleanupStrm/Status', {
                                headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                            }).then(r => r.json()).then(c => {
                                if (c && c.IsRunning) {
                                    startCleanupPolling();
                                } else {
                                    progressContainer.style.display = 'none';
                                    btnRunScraper.style.display = 'block';
                                    if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                                    if (btnCleanMetadata) btnCleanMetadata.style.display = 'block';
                                    btnStopScraper.style.display = 'none';
                                }
                            }).catch(() => {
                                progressContainer.style.display = 'none';
                            });
                        }
                    }).catch(() => {
                        progressContainer.style.display = 'none';
                    });
                }
            }).catch(() => {
                progressContainer.style.display = 'none';
            });
        }

        checkInitialStatus();
        loadHistory();

        if (btnCleanupScraper) {
            btnCleanupScraper.addEventListener('click', function (e) {
                e.preventDefault();
                var msg = 'Cleanup will remove duplicate .strm files for movies already downloaded in your library, movies in unchecked languages, or orphaned dummy files. Real downloaded video files (.mp4, .mkv) will NOT be touched. Are you sure you want to proceed?';
                
                var executeCleanup = function() {
                    var checkboxes = view.querySelectorAll('input[name="chkLanguage"]:checked');
                    var selectedLangs = Array.from(checkboxes).map(function(cb) { return cb.value; });
                    
                    fetch('/System/Configuration/Downloaders/Languages', {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
                        },
                        body: JSON.stringify(selectedLangs)
                    }).then(function() {
                        return fetch('/System/Configuration/Downloaders/CleanupStrm', {
                            method: 'POST',
                            headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                        });
                    }).then(r => {
                        if (r.ok) {
                            startCleanupPolling();
                        } else {
                            r.json().then(j => alert(j.Message || 'Failed to start cleanup.'));
                        }
                    }).catch(err => {
                        alert('Error starting cleanup: ' + err);
                    });
                };

                if (window.Dashboard && typeof window.Dashboard.confirm === 'function') {
                    window.Dashboard.confirm(msg, 'Smart Cleanup .strm Files', executeCleanup);
                } else if (window.confirm(msg)) {
                    executeCleanup();
                }
            });
        }

        const btnPurgeAllScraped = view.querySelector('#btnPurgeAllScraped');
        if (btnPurgeAllScraped) {
            btnPurgeAllScraped.addEventListener('click', function (e) {
                e.preventDefault();
                var msg = 'WARNING: This will completely WIPE ALL .strm dummy folders that were generated by the scraper. Any actual video files (.mkv, .mp4) you have downloaded will be safe and untouched. Are you absolutely sure you want to purge all scraped movies?';
                
                var executePurge = function() {
                    Dashboard.showLoadingMsg();
                    fetch('/System/Configuration/Downloaders/PurgeAllStrm', {
                        method: 'DELETE',
                        headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                    }).then(r => r.json().then(j => ({ ok: r.ok, json: j }))).then(res => {
                        Dashboard.hideLoadingMsg();
                        if (res.ok) {
                            showCleanupBanner(res.json.Message || 'Purge completed.', false);
                        } else {
                            showCleanupBanner(res.json.Message || 'Failed to purge.', true);
                        }
                    }).catch(err => {
                        Dashboard.hideLoadingMsg();
                        showCleanupBanner('Error purging: ' + err, true);
                    });
                };

                if (window.Dashboard && typeof window.Dashboard.confirm === 'function') {
                    window.Dashboard.confirm(msg, 'Purge All Scraped Movies', executePurge);
                } else if (window.confirm(msg)) {
                    executePurge();
                }
            });
        }

        const btnCleanMetadata = view.querySelector('#btnCleanMetadata');
        if (btnCleanMetadata) {
            btnCleanMetadata.addEventListener('click', function (e) {
                e.preventDefault();
                var msg = 'This will scan your downloads directory in the background and use ffmpeg to instantly strip "1TamilMV" tags and other spam metadata from all existing .mkv and .mp4 files. A library rescan will trigger automatically when finished. Are you sure you want to proceed?';
                
                var executeCleanMetadata = function() {
                    fetch('/System/Configuration/Downloaders/CleanMetadata', {
                        method: 'POST',
                        headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                    }).then(r => {
                        if (r.ok) {
                            startMetadataPolling();
                        } else {
                            r.json().then(j => showCleanupBanner(j.Message || 'Failed to start metadata cleanup.', true));
                        }
                    }).catch(err => {
                        showCleanupBanner('Error starting metadata cleanup: ' + err, true);
                    });
                };

                if (window.Dashboard && typeof window.Dashboard.confirm === 'function') {
                    window.Dashboard.confirm(msg, 'Sanitize Library Metadata', executeCleanMetadata);
                } else if (window.confirm(msg)) {
                    executeCleanMetadata();
                }
            });
        }

        if (btnRunScraper) {
            btnRunScraper.addEventListener('click', function(e) {
                e.preventDefault();
                var checkboxes = view.querySelectorAll('input[name="chkLanguage"]:checked');
                var selectedLangs = Array.from(checkboxes).map(function(cb) { return cb.value; });

                fetch('/System/Configuration/Downloaders/Languages', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
                    },
                    body: JSON.stringify(selectedLangs)
                }).then(function() {
                    return fetch('/System/Configuration/Downloaders/Scrape', {
                        method: 'POST',
                        headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                    });
                }).then(r => {
                    if (r.ok) startPolling();
                });
            });
        }
        
        if (btnStopScraper) {
            btnStopScraper.addEventListener('click', function(e) {
                e.preventDefault();
                btnStopScraper.disabled = true;
                btnStopScraper.querySelector('span').innerText = 'Stopping...';
                Promise.all([
                    fetch('/System/Configuration/Downloaders/Scrape', {
                        method: 'DELETE',
                        headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                    }),
                    fetch('/System/Configuration/Downloaders/CleanupStrm', {
                        method: 'DELETE',
                        headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
                    })
                ]).finally(() => {
                    btnStopScraper.disabled = false;
                    btnStopScraper.querySelector('span').innerText = 'Stop';
                });
            });
        }
    }

    view.querySelector('.downloadersConfigurationForm').addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.SeedrUsername = view.querySelector('#txtSeedrUsername').value;
            config.SeedrPassword = view.querySelector('#txtSeedrPassword').value;
            config.TorboxApiKey = view.querySelector('#txtTorboxApiKey').value;
            config.DownloadsDirectory = view.querySelector('#txtDownloadsDirectory').value;
            
            config.EnableSeedr = view.querySelector('#chkEnableSeedr').checked;
            config.EnableTorbox = view.querySelector('#chkEnableTorbox').checked;

            fetch('/System/Configuration/Downloaders/TestCredentials', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
                },
                body: JSON.stringify({
                    SeedrUsername: config.SeedrUsername,
                    SeedrPassword: config.SeedrPassword,
                    TorboxApiKey: config.TorboxApiKey
                })
            }).then(res => {
                if (!res.ok) {
                    return res.json().then(err => {
                        throw new Error(err.Message || "Invalid credentials.");
                    });
                }
                return res.json();
            }).then(() => {
                ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                    const checkboxes = view.querySelectorAll('input[name="chkLanguage"]:checked');
                    const selectedLangs = Array.from(checkboxes).map(cb => cb.value);
                    
                    const customDomain = (view.querySelector('#txtCustomDomain').value || '').trim();
                    fetch('/System/Configuration/Downloaders/Domain', {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
                        },
                        body: JSON.stringify({ Domain: customDomain })
                    }).then(r => r.json()).then(d => {
                        if (d && d.Domain) {
                            const activeDom = view.querySelector('#lblActiveDomain');
                            if (activeDom) activeDom.textContent = d.Domain;
                        }
                    }).catch(e => console.error("Error saving domain:", e));

                    fetch('/System/Configuration/Downloaders/Languages', {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
                        },
                        body: JSON.stringify(selectedLangs)
                    }).then(() => {
                        Dashboard.processPluginConfigurationUpdateResult();
                    }).catch(e => {
                        console.error("Error saving languages:", e);
                        Dashboard.processPluginConfigurationUpdateResult();
                    });
                });
            }).catch(err => {
                Dashboard.hideLoadingMsg();
                Dashboard.alert({ message: err.message, title: "Configuration Error" });
            });
        });
    });

    // Tab switching logic
    const tabBtns = view.querySelectorAll('.plugin-tab-btn');
    const tabContents = view.querySelectorAll('.tabContent');
    tabBtns.forEach(btn => {
        btn.addEventListener('click', function() {
            tabBtns.forEach(b => {
                b.style.color = '#888';
                b.style.borderBottom = '2px solid transparent';
            });
            this.style.color = '#00a4dc';
            this.style.borderBottom = '2px solid #00a4dc';
            
            const targetId = this.getAttribute('data-tab');
            tabContents.forEach(c => {
                if (c.id === targetId) {
                    c.style.display = 'block';
                    c.classList.add('is-active');
                } else {
                    c.style.display = 'none';
                    c.classList.remove('is-active');
                }
            });
        });
    });

    // Manual Download logic
    const formManual = view.querySelector('#formManualDownload');
    if (formManual) {
        formManual.addEventListener('submit', function(e) {
            e.preventDefault();
            const uri = view.querySelector('#txtMagnetUri').value.trim();
            const provider = view.querySelector('#selManualProvider').value;
            const statusDiv = view.querySelector('#manualDownloadStatus');
            
            if (!uri) return;

            statusDiv.style.display = 'block';
            statusDiv.style.color = '#ff9800';
            statusDiv.innerText = 'Starting download...';
            
            fetch('/System/Configuration/Downloaders/ManualDownload', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
                },
                body: JSON.stringify({ MagnetUri: uri, Provider: provider })
            }).then(async r => {
                if (!r.ok) {
                    let errStr = await r.text();
                    throw new Error(errStr || "HTTP Error " + r.status);
                }
                return r.json();
            }).then(res => {
                if (res.Success) {
                    statusDiv.style.color = '#4caf50';
                    statusDiv.innerText = 'Download started successfully via ' + res.Provider + '. Switching to Activity tab...';
                    const btnRefresh = view.querySelector('#btnRefreshHistory');
                    if (btnRefresh) btnRefresh.click();
                    view.querySelector('#txtMagnetUri').value = '';
                    
                    const activityTabBtn = view.querySelector('.plugin-tab-btn[data-tab="tabActivity"]');
                    if (activityTabBtn) {
                        setTimeout(() => {
                            activityTabBtn.click();
                            statusDiv.style.display = 'none';
                        }, 1500);
                    }
                } else {
                    statusDiv.style.color = '#f44336';
                    statusDiv.innerText = 'Failed: ' + (res.ErrorMessage || 'Unknown error');
                }
            }).catch(err => {
                statusDiv.style.color = '#f44336';
                statusDiv.innerText = 'Error: ' + err.message;
            });
        });
    }

    view.addEventListener('viewshow', function (e) {
        loadPage();
    });
}
