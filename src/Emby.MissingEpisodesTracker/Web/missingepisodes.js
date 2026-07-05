define(['loading', 'emby-input', 'emby-button', 'emby-select', 'emby-checkbox'], function (loading) {
    'use strict';

    var pluginId = '1f3feded-fa2b-4497-a7a6-8fe855670455';
    var scanTaskKey = 'MissingEpisodesTrackerScan';

    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function formatDate(value) {
        if (!value) { return '—'; }
        var d = new Date(value);
        if (isNaN(d.getTime()) || d.getFullYear() < 1902) { return '—'; }
        return d.toLocaleDateString();
    }

    function epCode(season, episode) {
        function pad(n) { return (n < 10 ? '0' : '') + n; }
        return 'S' + pad(season) + 'E' + pad(episode);
    }

    function getReport(view) {
        var apiView = view === 'series' ? 'all' : view;
        return ApiClient.getJSON(ApiClient.getUrl('MissingEpisodesTracker/Report', { View: apiView }));
    }

    function post(path, data) {
        return ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('MissingEpisodesTracker/' + path),
            data: JSON.stringify(data || {}),
            contentType: 'application/json'
        });
    }

    function renderSummary(view, report) {
        var el = view.querySelector('#scanSummary');
        var scan = report.LastScan;
        if (!scan) {
            el.innerHTML = 'No scan has run yet. Use <b>Run scan now</b> (or wait for the scheduled task).';
            return;
        }
        el.innerHTML =
            '<b>' + report.TotalMissing + '</b> missing total · last scan ' +
            escapeHtml(new Date(scan.StartedUtc).toLocaleString()) + ' (' + scan.DurationMs + ' ms): ' +
            scan.NewCount + ' new, ' + scan.KnownCount + ' known, ' + scan.ResolvedCount + ' resolved, ' +
            scan.RemovedCount + ' removed, ' + scan.IgnoredCount + ' ignored, ' +
            scan.DroppedByFilter + ' filtered, ' + scan.SkippedEndedCompleteSeries + ' ended-complete series skipped';
    }

    function renderEpisodes(container, episodes, viewName) {
        if (!episodes.length) {
            container.innerHTML = '<p>Nothing here.</p>';
            return;
        }
        var showStatus = viewName === 'resolved' || viewName === 'ignored';
        var html = '<table class="detailTable" style="width:100%;border-collapse:collapse;">';
        html += '<thead><tr>' +
            '<th style="text-align:left;padding:.4em;">Series</th>' +
            '<th style="text-align:left;padding:.4em;">Episode</th>' +
            '<th style="text-align:left;padding:.4em;">Title</th>' +
            '<th style="text-align:left;padding:.4em;">Aired</th>' +
            '<th style="text-align:left;padding:.4em;">First seen</th>' +
            (showStatus ? '<th style="text-align:left;padding:.4em;">Status</th>' : '') +
            '<th style="text-align:left;padding:.4em;">Actions</th>' +
            '</tr></thead><tbody>';

        episodes.forEach(function (e) {
            html += '<tr style="border-top:1px solid rgba(128,128,128,.25);">' +
                '<td style="padding:.4em;">' + escapeHtml(e.SeriesName) + '</td>' +
                '<td style="padding:.4em;white-space:nowrap;">' + epCode(e.Season, e.Episode) + '</td>' +
                '<td style="padding:.4em;">' + escapeHtml(e.Title) + '</td>' +
                '<td style="padding:.4em;white-space:nowrap;">' + formatDate(e.PremiereDateUtc) + '</td>' +
                '<td style="padding:.4em;white-space:nowrap;">' + formatDate(e.FirstSeenUtc) + '</td>' +
                (showStatus ? '<td style="padding:.4em;">' + escapeHtml(e.Status) + '</td>' : '') +
                '<td style="padding:.4em;white-space:nowrap;">';
            if (e.Status === 'Missing') {
                html += '<button is="emby-button" type="button" class="raised btnIgnoreEp" data-key="' + escapeHtml(e.Key) + '"><span>Ignore</span></button> ' +
                    '<button is="emby-button" type="button" class="raised btnIgnoreSeries" data-seriesid="' + e.SeriesId + '"><span>Ignore series</span></button>';
            } else if (e.Status === 'Ignored') {
                html += '<button is="emby-button" type="button" class="raised btnUnignoreEp" data-key="' + escapeHtml(e.Key) + '"><span>Un-ignore</span></button>';
            }
            html += '</td></tr>';
        });
        html += '</tbody></table>';
        container.innerHTML = html;
    }

    function renderSeries(container, seriesList) {
        if (!seriesList.length) {
            container.innerHTML = '<p>No series flags. Series get flagged here when they are ignored or detected as ended with a complete collection.</p>';
            return;
        }
        var html = '<table class="detailTable" style="width:100%;border-collapse:collapse;">';
        html += '<thead><tr>' +
            '<th style="text-align:left;padding:.4em;">Series</th>' +
            '<th style="text-align:left;padding:.4em;">Flag</th>' +
            '<th style="text-align:left;padding:.4em;">Since</th>' +
            '<th style="text-align:left;padding:.4em;">Actions</th>' +
            '</tr></thead><tbody>';
        seriesList.forEach(function (s) {
            var flags = [];
            if (s.EndedComplete) { flags.push('Ended + complete (skipped)'); }
            if (s.Ignored) { flags.push('Ignored'); }
            html += '<tr style="border-top:1px solid rgba(128,128,128,.25);">' +
                '<td style="padding:.4em;">' + escapeHtml(s.SeriesName || ('Series #' + s.SeriesId)) + '</td>' +
                '<td style="padding:.4em;">' + flags.join(', ') + '</td>' +
                '<td style="padding:.4em;white-space:nowrap;">' + formatDate(s.FlaggedUtc) + '</td>' +
                '<td style="padding:.4em;white-space:nowrap;">';
            if (s.EndedComplete) {
                html += '<button is="emby-button" type="button" class="raised btnResetComplete" data-seriesid="' + s.SeriesId + '"><span>Re-check</span></button> ';
            }
            if (s.Ignored) {
                html += '<button is="emby-button" type="button" class="raised btnUnignoreSeries" data-seriesid="' + s.SeriesId + '"><span>Un-ignore</span></button>';
            }
            html += '</td></tr>';
        });
        html += '</tbody></table>';
        container.innerHTML = html;
    }

    function csvCell(v) {
        var s = String(v == null ? '' : v).replace(/"/g, '""');
        if (/^[=+\-@\t]/.test(s)) {
            // Neutralize spreadsheet formula injection from provider-supplied titles.
            s = "'" + s;
        }
        return '"' + s + '"';
    }

    function exportCsv(episodes, seriesList) {
        var content;
        if (episodes.length) {
            var header = 'Series,Season,Episode,Title,AirDate,FirstSeen,Status\n';
            content = header + episodes.map(function (e) {
                return [csvCell(e.SeriesName), e.Season, e.Episode, csvCell(e.Title),
                    csvCell(formatDate(e.PremiereDateUtc)), csvCell(formatDate(e.FirstSeenUtc)), csvCell(e.Status)].join(',');
            }).join('\n');
        } else {
            content = 'Series,EndedComplete,Ignored,Since\n' + (seriesList || []).map(function (s) {
                return [csvCell(s.SeriesName || ('Series #' + s.SeriesId)), s.EndedComplete, s.Ignored,
                    csvCell(formatDate(s.FlaggedUtc))].join(',');
            }).join('\n');
        }
        var blob = new Blob([content], { type: 'text/csv;charset=utf-8;' });
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = 'missing-episodes.csv';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(a.href);
    }

    function runScan() {
        return ApiClient.getJSON(ApiClient.getUrl('ScheduledTasks')).then(function (tasks) {
            var task = (tasks || []).filter(function (t) { return t.Key === scanTaskKey; })[0];
            if (!task) { return Promise.reject(new Error('Scan task not found')); }
            return ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('ScheduledTasks/Running/' + task.Id)
            });
        });
    }

    function loadConfig(view) {
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            view.querySelector('#chkIgnoreNoAirDate').checked = config.IgnoreNoAirDate;
            view.querySelector('#chkIgnoreUnaired').checked = config.IgnoreUnaired;
            view.querySelector('#txtGraceDays').value = config.GraceDays;
            view.querySelector('#chkIgnoreSpecials').checked = config.IgnoreSpecials;
            view.querySelector('#chkEndedCompleteSkip').checked = config.EnableEndedCompleteSkip;
            view.querySelector('#chkNotify').checked = config.NotifyOnNewMissing;
        });
    }

    function saveConfig(view) {
        loading.show();
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.IgnoreNoAirDate = view.querySelector('#chkIgnoreNoAirDate').checked;
            config.IgnoreUnaired = view.querySelector('#chkIgnoreUnaired').checked;
            config.GraceDays = parseInt(view.querySelector('#txtGraceDays').value, 10) || 0;
            config.IgnoreSpecials = view.querySelector('#chkIgnoreSpecials').checked;
            config.EnableEndedCompleteSkip = view.querySelector('#chkEndedCompleteSkip').checked;
            config.NotifyOnNewMissing = view.querySelector('#chkNotify').checked;
            return ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });
    }

    function waitForScanIdle(attempts) {
        if (attempts <= 0) { return Promise.resolve(); }
        return new Promise(function (resolve) { setTimeout(resolve, 2500); }).then(function () {
            return ApiClient.getJSON(ApiClient.getUrl('ScheduledTasks')).then(function (tasks) {
                var task = (tasks || []).filter(function (t) { return t.Key === scanTaskKey; })[0];
                if (!task || task.State === 'Idle') { return; }
                return waitForScanIdle(attempts - 1);
            }, function () { return; });
        });
    }

    return function (view) {
        var currentEpisodes = [];
        var currentSeries = [];

        function refresh() {
            loading.show();
            var viewName = view.querySelector('#selectView').value;
            getReport(viewName).then(function (report) {
                renderSummary(view, report);
                var container = view.querySelector('#reportContainer');
                currentSeries = report.Series || [];
                if (viewName === 'series') {
                    currentEpisodes = [];
                    renderSeries(container, currentSeries);
                } else {
                    currentEpisodes = report.Episodes || [];
                    renderEpisodes(container, currentEpisodes, viewName);
                }
                loading.hide();
            }, function () {
                loading.hide();
                view.querySelector('#reportContainer').innerHTML = '<p>Failed to load report.</p>';
            });
        }

        view.querySelector('#selectView').addEventListener('change', refresh);

        view.querySelector('#btnRunScan').addEventListener('click', function () {
            loading.show();
            runScan().then(function () {
                loading.hide();
                require(['toast'], function (toast) { toast('Scan started.'); });
                waitForScanIdle(48).then(refresh);
            }, function () {
                loading.hide();
                require(['toast'], function (toast) { toast('Could not start the scan task.'); });
            });
        });

        view.querySelector('#btnExportCsv').addEventListener('click', function () {
            exportCsv(currentEpisodes, currentSeries);
        });

        view.querySelector('#btnResetState').addEventListener('click', function () {
            require(['confirm'], function (confirm) {
                confirm('This clears the whole ledger: history, ignores and ended-complete flags. The next scan rebuilds from scratch. Continue?', 'Reset state').then(function () {
                    loading.show();
                    post('ResetState', {}).then(function () { refresh(); }, function () { loading.hide(); });
                });
            });
        });

        view.querySelector('#reportContainer').addEventListener('click', function (ev) {
            var btn = ev.target.closest ? ev.target.closest('button') : null;
            if (!btn) { return; }
            var action = null;
            var data = null;
            if (btn.classList.contains('btnIgnoreEp')) {
                action = 'Ignore'; data = { Key: btn.getAttribute('data-key'), Scope: 'episode' };
            } else if (btn.classList.contains('btnIgnoreSeries')) {
                action = 'Ignore'; data = { SeriesId: parseInt(btn.getAttribute('data-seriesid'), 10), Scope: 'series' };
            } else if (btn.classList.contains('btnUnignoreEp')) {
                action = 'Unignore'; data = { Key: btn.getAttribute('data-key'), Scope: 'episode' };
            } else if (btn.classList.contains('btnUnignoreSeries')) {
                action = 'Unignore'; data = { SeriesId: parseInt(btn.getAttribute('data-seriesid'), 10), Scope: 'series' };
            } else if (btn.classList.contains('btnResetComplete')) {
                action = 'ResetEndedComplete'; data = { SeriesId: parseInt(btn.getAttribute('data-seriesid'), 10) };
            }
            if (action) {
                loading.show();
                post(action, data).then(function () { refresh(); }, function () { loading.hide(); });
            }
        });

        view.querySelector('#missingEpisodesTrackerForm').addEventListener('submit', function (ev) {
            ev.preventDefault();
            saveConfig(view);
            return false;
        });

        view.addEventListener('viewshow', function () {
            loading.show();
            loadConfig(view).then(function () { refresh(); }, function () { loading.hide(); });
        });
    };
});
