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
                if (seedrActive) seedrActive.style.display = 'none';
                if (torboxActive) torboxActive.style.display = 'none';

                if (!chkSeedr.checked && !chkTorbox.checked) return;

                fetch('/System/Configuration/Downloaders/TestCredentials', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
                    },
                    body: JSON.stringify({
                        SeedrUsername: view.querySelector('#txtSeedrUsername').value,
                        SeedrPassword: view.querySelector('#txtSeedrPassword').value,
                        TorboxApiKey: view.querySelector('#txtTorboxApiKey').value
                    })
                }).then(res => {
                    if (res.ok) {
                        if (seedrActive && chkSeedr.checked) seedrActive.style.display = 'block';
                        if (torboxActive && chkTorbox.checked) torboxActive.style.display = 'block';
                    }
                }).catch(() => {});
            };
            
            updateStatusDisplays();
            chkSeedr.addEventListener('change', updateStatusDisplays);
            chkTorbox.addEventListener('change', updateStatusDisplays);

            fetch('/System/Configuration/Downloaders/Domain', {
                headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
            }).then(r => r.json()).then(d => {
                if (d && d.Domain) {
                    const domInput = view.querySelector('#txtCustomDomain');
                    if (domInput) domInput.value = d.Domain;
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
            progressContainer.style.display = 'block';
            progressBar.style.backgroundColor = '#00a4dc';
            btnRunScraper.style.display = 'none';
            if (btnCleanupScraper) btnCleanupScraper.style.display = 'none';
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
                                btnStopScraper.style.display = 'none';
                                progressContainer.style.display = 'none';
                            }, 3000);
                        }
                    } else {
                        clearInterval(pollInterval);
                        btnRunScraper.style.display = 'block';
                        if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                        btnStopScraper.style.display = 'none';
                        progressContainer.style.display = 'none';
                    }
                }).catch(() => {
                    clearInterval(pollInterval);
                    btnRunScraper.style.display = 'block';
                    if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                    btnStopScraper.style.display = 'none';
                    progressContainer.style.display = 'none';
                });
            }, 1200);
        }

        function startCleanupPolling() {
            if (pollInterval) clearInterval(pollInterval);
            progressContainer.style.display = 'block';
            progressBar.style.backgroundColor = '#e05206';
            btnRunScraper.style.display = 'none';
            if (btnCleanupScraper) btnCleanupScraper.style.display = 'none';
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
                        btnStopScraper.style.display = d.IsRunning ? 'block' : 'none';
                        
                        let pct = d.Progress || 0;
                        progressBar.style.width = pct + '%';
                        pctText.innerText = Math.round(pct) + '%';
                        statusText.innerText = d.Status || (d.IsRunning ? 'Cleaning up...' : 'Idle');
                        updateLogDisplay(d.Logs);
                        
                        if (!d.IsRunning && (pct === 100 || d.Status === 'Idle' || d.Status.startsWith('Error') || d.Status.startsWith('Failed'))) {
                            clearInterval(pollInterval);
                            setTimeout(() => {
                                btnRunScraper.style.display = 'block';
                                if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                                btnStopScraper.style.display = 'none';
                                progressContainer.style.display = 'none';
                            }, 3000);
                        }
                    } else {
                        clearInterval(pollInterval);
                        btnRunScraper.style.display = 'block';
                        if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
                        btnStopScraper.style.display = 'none';
                        progressContainer.style.display = 'none';
                    }
                }).catch(() => {
                    clearInterval(pollInterval);
                    btnRunScraper.style.display = 'block';
                    if (btnCleanupScraper) btnCleanupScraper.style.display = 'block';
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
                            <td style="padding: 10px; vertical-align:top;">${item.SizeGb.toFixed(2)} GB</td>
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

        function checkInitialStatus() {
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

        checkInitialStatus();
        loadHistory();

        if (btnCleanupScraper) {
            btnCleanupScraper.addEventListener('click', function (e) {
                e.preventDefault();
                var msg = 'Cleanup will remove duplicate .strm files for movies already downloaded in your library, movies in unchecked languages, or orphaned dummy files. Real downloaded video files (.mp4, .mkv) will NOT be touched. Are you sure you want to proceed?';
                
                var executeCleanup = function() {
                    fetch('/System/Configuration/Downloaders/CleanupStrm', {
                        method: 'POST',
                        headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
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
                    window.Dashboard.confirm(msg, 'Cleanup .strm Files', executeCleanup);
                } else if (window.confirm(msg)) {
                    executeCleanup();
                }
            });
        }

        if (btnRunScraper) {
            btnRunScraper.addEventListener('click', function(e) {
                e.preventDefault();
                fetch('/System/Configuration/Downloaders/Scrape', {
                    method: 'POST',
                    headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
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
                    if (customDomain) {
                        fetch('/System/Configuration/Downloaders/Domain', {
                            method: 'POST',
                            headers: {
                                'Content-Type': 'application/json',
                                'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
                            },
                            body: JSON.stringify({ Domain: customDomain })
                        }).catch(e => console.error("Error saving domain:", e));
                    }

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

    view.addEventListener('viewshow', function (e) {
        loadPage();
    });
}
