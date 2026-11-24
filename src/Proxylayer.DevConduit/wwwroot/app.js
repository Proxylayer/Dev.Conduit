(function () {
    'use strict';

    var REDACTED = '***redacted***';

    var dom = {
        signedInAs: document.getElementById('signedInAs'),
        historySearch: document.getElementById('historySearch'),
        clearHistoryButton: document.getElementById('clearHistoryButton'),
        historyList: document.getElementById('historyList'),
        historyEmpty: document.getElementById('historyEmpty'),
        composerForm: document.getElementById('composerForm'),
        methodSelect: document.getElementById('methodSelect'),
        customMethod: document.getElementById('customMethod'),
        urlInput: document.getElementById('urlInput'),
        sendButton: document.getElementById('sendButton'),
        headerRows: document.getElementById('headerRows'),
        headerCount: document.getElementById('headerCount'),
        headerSetSelect: document.getElementById('headerSetSelect'),
        saveHeaderSetButton: document.getElementById('saveHeaderSetButton'),
        manageHeaderSetsButton: document.getElementById('manageHeaderSetsButton'),
        headerSetManager: document.getElementById('headerSetManager'),
        contentTypeInput: document.getElementById('contentTypeInput'),
        formatJsonButton: document.getElementById('formatJsonButton'),
        bodyInput: document.getElementById('bodyInput'),
        timeoutInput: document.getElementById('timeoutInput'),
        followRedirects: document.getElementById('followRedirects'),
        allowInvalidCerts: document.getElementById('allowInvalidCerts'),
        responseStatus: document.getElementById('responseStatus'),
        responseMeta: document.getElementById('responseMeta'),
        responseError: document.getElementById('responseError'),
        responseBody: document.getElementById('responseBody'),
        responseHeaders: document.getElementById('responseHeaders'),
        responseHeaderCount: document.getElementById('responseHeaderCount'),
        prettyToggle: document.getElementById('prettyToggle'),
        bodyNotice: document.getElementById('bodyNotice'),
        copyBodyButton: document.getElementById('copyBodyButton'),
        toast: document.getElementById('toast'),

        railConsoleTab: document.getElementById('railConsoleTab'),
        railRoutedTab: document.getElementById('railRoutedTab'),
        routedTotalBadge: document.getElementById('routedTotalBadge'),
        consoleRail: document.getElementById('consoleRail'),
        routedRail: document.getElementById('routedRail'),
        routedSearch: document.getElementById('routedSearch'),
        routedList: document.getElementById('routedList'),
        routedEmpty: document.getElementById('routedEmpty'),
        routedError: document.getElementById('routedError'),
        routedPager: document.getElementById('routedPager'),
        routedPrevButton: document.getElementById('routedPrevButton'),
        routedNextButton: document.getElementById('routedNextButton'),
        routedRange: document.getElementById('routedRange'),
        clearRoutedButton: document.getElementById('clearRoutedButton'),

        routedBar: document.getElementById('routedBar'),
        routedBarMeta: document.getElementById('routedBarMeta'),
        copyToComposerButton: document.getElementById('copyToComposerButton'),
        routedRequestBodyTab: document.getElementById('routedRequestBodyTab'),
        routedRequestHeadersTab: document.getElementById('routedRequestHeadersTab'),
        routedRequestBody: document.getElementById('routedRequestBody'),
        routedRequestBodyNotice: document.getElementById('routedRequestBodyNotice'),
        routedRequestHeaders: document.getElementById('routedRequestHeaders'),
        routedRequestHeaderCount: document.getElementById('routedRequestHeaderCount'),

        storageRows: document.getElementById('storageRows'),
        storageTotal: document.getElementById('storageTotal'),
        storageRoot: document.getElementById('storageRoot'),
        storageError: document.getElementById('storageError'),
        storageRefreshButton: document.getElementById('storageRefreshButton'),

        confirmBackdrop: document.getElementById('confirmBackdrop'),
        confirmMessage: document.getElementById('confirmMessage'),
        confirmDetail: document.getElementById('confirmDetail'),
        confirmCancel: document.getElementById('confirmCancel'),
        confirmAccept: document.getElementById('confirmAccept')
    };

    var ROUTED_PAGE_SIZE = 50;

    var state = {
        headerSets: [],
        selectedHistoryId: null,
        lastResponse: null,
        sending: false,
        rail: 'console',
        selectedRoutedId: null,
        routedEntry: null,
        routedSkip: 0,
        routedTotal: 0,
        routedItems: [],
        confirmAction: null
    };

    // ------------------------------------------------------------------ api

    function apiFetch(path, options) {
        options = options || {};
        var init = {
            method: options.method || 'GET',
            credentials: 'same-origin',
            headers: { 'X-DevConduit-Client': '1' }
        };

        if (options.body !== undefined) {
            init.body = JSON.stringify(options.body);
            init.headers['Content-Type'] = 'application/json';
        }

        return fetch(path, init).then(function (response) {
            if (response.status === 401 || response.status === 403) {
                window.location.reload();
                throw new Error('The session has expired.');
            }

            if (response.redirected && response.url.indexOf('/api/') === -1) {
                window.location.reload();
                throw new Error('The session has expired.');
            }

            if (response.status === 204) {
                return null;
            }

            return response.text().then(function (text) {
                var payload = null;
                if (text) {
                    try {
                        payload = JSON.parse(text);
                    } catch (error) {
                        payload = null;
                    }
                }

                if (!response.ok) {
                    var message = (payload && (payload.error || payload.detail || payload.title)) ||
                        (response.status + ' ' + response.statusText);
                    var failure = new Error(message);
                    failure.status = response.status;
                    throw failure;
                }

                return payload;
            });
        });
    }

    // -------------------------------------------------------------- helpers

    function setText(node, value) {
        node.textContent = value === null || value === undefined ? '' : String(value);
    }

    function formatBytes(bytes) {
        if (bytes === null || bytes === undefined) {
            return '0 B';
        }
        if (bytes < 1024) {
            return bytes + ' B';
        }
        if (bytes < 1024 * 1024) {
            return (bytes / 1024).toFixed(1) + ' KB';
        }
        if (bytes < 1024 * 1024 * 1024) {
            return (bytes / (1024 * 1024)).toFixed(2) + ' MB';
        }
        return (bytes / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
    }

    function describeFailure(error) {
        var status = error && error.status;
        if (status === 503) {
            return 'The server answered 503. Dev.Conduit is running without a usable Entra configuration, ' +
                'so the stored data is unavailable until sign-in is configured.';
        }
        if (status === 401 || status === 403) {
            return 'The server refused the request (' + status + '). Your session may no longer be authorised.';
        }
        return error && error.message ? error.message : String(error);
    }

    function relativeTime(value) {
        var then = new Date(value).getTime();
        if (isNaN(then)) {
            return '';
        }
        var seconds = Math.max(0, Math.round((Date.now() - then) / 1000));
        if (seconds < 60) {
            return seconds + 's ago';
        }
        if (seconds < 3600) {
            return Math.round(seconds / 60) + 'm ago';
        }
        if (seconds < 86400) {
            return Math.round(seconds / 3600) + 'h ago';
        }
        return Math.round(seconds / 86400) + 'd ago';
    }

    function statusClass(statusCode, hasError) {
        if (hasError || !statusCode) {
            return 'chip-error';
        }
        if (statusCode < 200) {
            return 'chip-1xx';
        }
        if (statusCode < 300) {
            return 'chip-2xx';
        }
        if (statusCode < 400) {
            return 'chip-3xx';
        }
        if (statusCode < 500) {
            return 'chip-4xx';
        }
        return 'chip-5xx';
    }

    var toastTimer = null;

    function toast(message, isError) {
        setText(dom.toast, message);
        dom.toast.className = 'toast' + (isError ? ' error' : '');
        if (toastTimer) {
            window.clearTimeout(toastTimer);
        }
        toastTimer = window.setTimeout(function () {
            dom.toast.className = 'toast hidden';
        }, 4000);
    }

    function reportError(error) {
        toast(error && error.message ? error.message : String(error), true);
    }

    // ----------------------------------------------------------------- tabs

    function wireTabs() {
        var groups = document.querySelectorAll('.tabs:not(.rail-tabs)');
        Array.prototype.forEach.call(groups, function (group) {
            group.addEventListener('click', function (event) {
                var tab = event.target.closest ? event.target.closest('.tab') : null;
                if (!tab || !group.contains(tab)) {
                    return;
                }

                var pane = group.closest('.pane');
                Array.prototype.forEach.call(group.querySelectorAll('.tab'), function (other) {
                    other.classList.toggle('active', other === tab);
                });
                Array.prototype.forEach.call(pane.querySelectorAll('.tab-panel'), function (panel) {
                    panel.classList.toggle('active', panel.getAttribute('data-panel') === tab.getAttribute('data-tab'));
                });
            });
        });
    }

    function activateResponseTab(name) {
        var pane = document.querySelector('.response-pane');
        Array.prototype.forEach.call(pane.querySelectorAll('.tab'), function (tab) {
            tab.classList.toggle('active', tab.getAttribute('data-tab') === name);
        });
        Array.prototype.forEach.call(pane.querySelectorAll('.tab-panel'), function (panel) {
            panel.classList.toggle('active', panel.getAttribute('data-panel') === name);
        });
    }

    function activeResponseTabName() {
        var active = document.querySelector('.response-pane .tab.active');
        return active ? active.getAttribute('data-tab') : 'responseBody';
    }

    // ------------------------------------------------------------- confirm

    function confirmDestructive(message, detail, onAccept) {
        state.confirmAction = onAccept;
        setText(dom.confirmMessage, message);
        setText(dom.confirmDetail, detail);
        dom.confirmBackdrop.classList.remove('hidden');
        dom.confirmCancel.focus();
    }

    function closeConfirm() {
        state.confirmAction = null;
        dom.confirmBackdrop.classList.add('hidden');
    }

    function confirmIsOpen() {
        return !dom.confirmBackdrop.classList.contains('hidden');
    }

    // -------------------------------------------------------- header editor

    function createHeaderRow(header) {
        header = header || {};
        var row = document.createElement('div');
        row.className = 'header-row';

        var enabled = document.createElement('input');
        enabled.type = 'checkbox';
        enabled.checked = header.enabled !== false;
        enabled.title = 'Send this header';

        var name = document.createElement('input');
        name.type = 'text';
        name.className = 'name';
        name.placeholder = 'Header';
        name.spellcheck = false;
        name.value = header.name || '';

        var value = document.createElement('input');
        value.className = 'value';
        value.placeholder = 'Value';
        value.spellcheck = false;
        value.value = header.value || '';
        value.type = header.isSecret ? 'password' : 'text';

        var secret = document.createElement('button');
        secret.type = 'button';
        secret.className = 'icon-button' + (header.isSecret ? ' on' : '');
        secret.textContent = 'sec';
        secret.title = 'Treat this value as a secret';

        var reveal = document.createElement('button');
        reveal.type = 'button';
        reveal.className = 'icon-button';
        reveal.textContent = 'show';
        reveal.title = 'Reveal the value';

        var remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'icon-button';
        remove.textContent = '✕';
        remove.title = 'Remove this header';

        row.dataset.secret = header.isSecret ? '1' : '0';

        secret.addEventListener('click', function () {
            var isSecret = row.dataset.secret !== '1';
            row.dataset.secret = isSecret ? '1' : '0';
            secret.classList.toggle('on', isSecret);
            value.type = isSecret ? 'password' : 'text';
        });

        reveal.addEventListener('click', function () {
            value.type = value.type === 'password' ? 'text' : 'password';
        });

        remove.addEventListener('click', function () {
            row.remove();
            ensureTrailingRow();
            updateHeaderCount();
        });

        function onInput() {
            ensureTrailingRow();
            updateHeaderCount();
        }

        name.addEventListener('input', onInput);
        value.addEventListener('input', onInput);

        row.appendChild(enabled);
        row.appendChild(name);
        row.appendChild(value);
        row.appendChild(secret);
        row.appendChild(reveal);
        row.appendChild(remove);
        return row;
    }

    function readHeaderRow(row) {
        return {
            enabled: row.children[0].checked,
            name: row.children[1].value.trim(),
            value: row.children[2].value,
            isSecret: row.dataset.secret === '1'
        };
    }

    function collectHeaders() {
        var headers = [];
        Array.prototype.forEach.call(dom.headerRows.children, function (row) {
            var header = readHeaderRow(row);
            if (header.name) {
                headers.push(header);
            }
        });
        return headers;
    }

    function ensureTrailingRow() {
        var rows = dom.headerRows.children;
        if (rows.length === 0) {
            dom.headerRows.appendChild(createHeaderRow(null));
            return;
        }

        var last = readHeaderRow(rows[rows.length - 1]);
        if (last.name || last.value) {
            dom.headerRows.appendChild(createHeaderRow(null));
        }
    }

    function updateHeaderCount() {
        var count = collectHeaders().length;
        setText(dom.headerCount, count ? '(' + count + ')' : '');
    }

    function setHeaders(headers) {
        dom.headerRows.replaceChildren();
        (headers || []).forEach(function (header) {
            dom.headerRows.appendChild(createHeaderRow(header));
        });
        ensureTrailingRow();
        updateHeaderCount();
    }

    function mergeHeaders(headers) {
        var existing = collectHeaders();
        (headers || []).forEach(function (header) {
            var match = null;
            for (var i = 0; i < existing.length; i++) {
                if (existing[i].name.toLowerCase() === (header.name || '').toLowerCase()) {
                    match = existing[i];
                    break;
                }
            }
            if (match) {
                match.value = header.value;
                match.isSecret = header.isSecret;
                match.enabled = header.enabled !== false;
            } else {
                existing.push({
                    name: header.name,
                    value: header.value,
                    enabled: header.enabled !== false,
                    isSecret: !!header.isSecret
                });
            }
        });
        setHeaders(existing);
    }

    // ----------------------------------------------------------- header sets

    function loadHeaderSets(applyDefaults) {
        return apiFetch('/api/headers').then(function (sets) {
            state.headerSets = sets || [];
            renderHeaderSetSelect();
            renderHeaderSetManager();

            if (applyDefaults) {
                state.headerSets.forEach(function (set) {
                    if (set.applyByDefault) {
                        mergeHeaders(set.headers);
                    }
                });
            }
        });
    }

    function renderHeaderSetSelect() {
        dom.headerSetSelect.replaceChildren();
        var placeholder = document.createElement('option');
        placeholder.value = '';
        placeholder.textContent = 'Apply saved set…';
        dom.headerSetSelect.appendChild(placeholder);

        state.headerSets.forEach(function (set) {
            var option = document.createElement('option');
            option.value = set.id;
            option.textContent = set.name + (set.applyByDefault ? ' (default)' : '');
            dom.headerSetSelect.appendChild(option);
        });
    }

    function renderHeaderSetManager() {
        dom.headerSetManager.replaceChildren();

        if (state.headerSets.length === 0) {
            var empty = document.createElement('div');
            empty.className = 'hint';
            empty.textContent = 'No saved header sets yet.';
            dom.headerSetManager.appendChild(empty);
            return;
        }

        state.headerSets.forEach(function (set) {
            var row = document.createElement('div');
            row.className = 'header-set-row';

            var name = document.createElement('span');
            name.className = 'name';
            name.textContent = set.name + ' · ' + (set.headers ? set.headers.length : 0) + ' header(s)';

            var defaultLabel = document.createElement('label');
            defaultLabel.className = 'inline-toggle';
            var defaultToggle = document.createElement('input');
            defaultToggle.type = 'checkbox';
            defaultToggle.checked = !!set.applyByDefault;
            defaultToggle.addEventListener('change', function () {
                apiFetch('/api/headers/' + encodeURIComponent(set.id), {
                    method: 'PUT',
                    body: {
                        name: set.name,
                        applyByDefault: defaultToggle.checked,
                        headers: set.headers || []
                    }
                }).then(function () {
                    return loadHeaderSets(false);
                }).catch(reportError);
            });
            defaultLabel.appendChild(defaultToggle);
            defaultLabel.appendChild(document.createTextNode(' apply by default'));

            var applyButton = document.createElement('button');
            applyButton.type = 'button';
            applyButton.className = 'ghost';
            applyButton.textContent = 'Apply';
            applyButton.addEventListener('click', function () {
                mergeHeaders(set.headers);
                toast('Applied ' + set.name + '.');
            });

            var deleteButton = document.createElement('button');
            deleteButton.type = 'button';
            deleteButton.className = 'ghost danger';
            deleteButton.textContent = 'Delete';
            deleteButton.addEventListener('click', function () {
                if (!window.confirm('Delete the header set "' + set.name + '"?')) {
                    return;
                }
                apiFetch('/api/headers/' + encodeURIComponent(set.id), { method: 'DELETE' })
                    .then(function () {
                        return loadHeaderSets(false);
                    })
                    .catch(reportError);
            });

            row.appendChild(name);
            row.appendChild(defaultLabel);
            row.appendChild(applyButton);
            row.appendChild(deleteButton);
            dom.headerSetManager.appendChild(row);
        });
    }

    function saveCurrentHeadersAsSet() {
        var headers = collectHeaders();
        if (headers.length === 0) {
            toast('There are no headers to save.', true);
            return;
        }

        var name = window.prompt('Name for this header set');
        if (!name || !name.trim()) {
            return;
        }

        apiFetch('/api/headers', {
            method: 'POST',
            body: { name: name.trim(), applyByDefault: false, headers: headers }
        }).then(function () {
            return loadHeaderSets(false);
        }).then(function () {
            toast('Saved header set "' + name.trim() + '".');
        }).catch(reportError);
    }

    // -------------------------------------------------------------- history

    function loadHistory() {
        var query = dom.historySearch.value.trim();
        var url = '/api/history?skip=0&take=100' + (query ? '&q=' + encodeURIComponent(query) : '');
        return apiFetch(url).then(function (page) {
            renderHistory(page && page.items ? page.items : []);
        });
    }

    function createSummaryItem(item, options) {
        var li = document.createElement('li');
        li.className = 'history-item' + (item.id === options.selectedId ? ' selected' : '');
        li.tabIndex = 0;

        var verb = (item.method || '').toUpperCase();
        var method = document.createElement('span');
        method.className = 'method-badge' + (verb === 'DELETE' ? ' destructive' : '');
        method.textContent = item.method || '';

        var url = document.createElement('span');
        url.className = 'history-url';
        url.textContent = item.url || '';
        url.title = item.url || '';

        var remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'history-delete';
        remove.textContent = '✕';
        remove.title = 'Delete this entry';
        remove.addEventListener('click', function (event) {
            event.stopPropagation();
            options.onDelete(item);
        });

        var meta = document.createElement('span');
        meta.className = 'history-meta';

        var chip = document.createElement('span');
        chip.className = 'chip ' + statusClass(item.statusCode, !!item.error);
        chip.textContent = item.error ? 'ERR' : String(item.statusCode || '-');

        var timing = document.createElement('span');
        timing.className = 'history-timing';
        timing.textContent = relativeTime(item.timestampUtc) + ' · ' + (item.durationMs || 0) + ' ms · ' +
            formatBytes(item.responseBytes);

        meta.appendChild(chip);
        meta.appendChild(timing);

        li.appendChild(method);
        li.appendChild(url);
        li.appendChild(remove);
        li.appendChild(meta);

        li.addEventListener('click', function () {
            options.onOpen(item);
        });
        li.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                options.onOpen(item);
            }
        });

        return li;
    }

    function renderHistory(items) {
        dom.historyList.replaceChildren();
        dom.historyEmpty.classList.toggle('hidden', items.length > 0);

        items.forEach(function (item) {
            dom.historyList.appendChild(createSummaryItem(item, {
                selectedId: state.selectedHistoryId,
                onOpen: function (summary) {
                    openHistoryEntry(summary.id);
                },
                onDelete: function (summary) {
                    apiFetch('/api/history/' + encodeURIComponent(summary.id), { method: 'DELETE' })
                        .then(loadHistory)
                        .catch(reportError);
                }
            }));
        });
    }

    function openHistoryEntry(id) {
        apiFetch('/api/history/' + encodeURIComponent(id)).then(function (entry) {
            if (!entry) {
                return;
            }

            state.selectedHistoryId = entry.id;
            state.selectedRoutedId = null;
            showRoutedDetail(false);
            renderRoutedList();
            loadRequestIntoComposer(entry.request);
            renderResult({
                response: entry.response,
                error: entry.error,
                errorKind: entry.errorKind,
                durationMs: entry.response ? entry.response.durationMs : 0,
                historyId: entry.id
            });

            var hadSecret = (entry.request.headers || []).some(function (header) {
                return header.value === REDACTED;
            });
            if (hadSecret) {
                toast('Secret header values were not stored in history; re-enter them before sending.');
            }

            return loadHistory();
        }).catch(reportError);
    }

    function loadRequestIntoComposer(request) {
        if (!request) {
            return;
        }

        setMethod(request.method || 'GET');
        dom.urlInput.value = request.url || '';
        dom.bodyInput.value = request.body || '';
        dom.contentTypeInput.value = request.contentType || '';
        dom.timeoutInput.value = request.timeoutSeconds || 100;
        dom.followRedirects.checked = !!request.followRedirects;
        dom.allowInvalidCerts.checked = !!request.allowInvalidCertificates;
        setHeaders(request.headers || []);
    }

    function setMethod(method) {
        var known = false;
        Array.prototype.forEach.call(dom.methodSelect.options, function (option) {
            if (option.value === method) {
                known = true;
            }
        });

        if (known) {
            dom.methodSelect.value = method;
            dom.customMethod.classList.add('hidden');
            dom.customMethod.value = '';
        } else {
            dom.methodSelect.value = '__custom';
            dom.customMethod.classList.remove('hidden');
            dom.customMethod.value = method;
        }
    }

    function currentMethod() {
        if (dom.methodSelect.value === '__custom') {
            return (dom.customMethod.value || '').trim() || 'GET';
        }
        return dom.methodSelect.value;
    }

    // -------------------------------------------------------- routed rail

    function selectRail(rail) {
        state.rail = rail;

        var isConsole = rail === 'console';
        dom.railConsoleTab.classList.toggle('active', isConsole);
        dom.railRoutedTab.classList.toggle('active', !isConsole);
        dom.railConsoleTab.setAttribute('aria-selected', isConsole ? 'true' : 'false');
        dom.railRoutedTab.setAttribute('aria-selected', isConsole ? 'false' : 'true');

        dom.consoleRail.classList.toggle('hidden', !isConsole);
        dom.routedRail.classList.toggle('hidden', isConsole);
        dom.historySearch.classList.toggle('hidden', !isConsole);
        dom.routedSearch.classList.toggle('hidden', isConsole);
        dom.clearHistoryButton.classList.toggle('hidden', !isConsole);
        dom.clearRoutedButton.classList.toggle('hidden', isConsole);

        if (!isConsole) {
            loadRouted().catch(reportError);
        }
    }

    function loadRouted() {
        var query = dom.routedSearch.value.trim();
        var url = '/api/routed?skip=' + state.routedSkip + '&take=' + ROUTED_PAGE_SIZE +
            (query ? '&q=' + encodeURIComponent(query) : '');

        return apiFetch(url).then(function (page) {
            dom.routedError.classList.add('hidden');
            setText(dom.routedError, '');
            state.routedItems = (page && page.items) || [];
            state.routedTotal = (page && page.total) || 0;
            renderRoutedList();
        }).catch(function (error) {
            state.routedItems = [];
            state.routedTotal = 0;
            renderRoutedList();
            dom.routedEmpty.classList.add('hidden');
            dom.routedError.classList.remove('hidden');
            setText(dom.routedError, 'Routed traffic could not be loaded. ' + describeFailure(error));
        });
    }

    function renderRoutedList() {
        dom.routedList.replaceChildren();

        state.routedItems.forEach(function (item) {
            dom.routedList.appendChild(createSummaryItem(item, {
                selectedId: state.selectedRoutedId,
                onOpen: function (summary) {
                    openRoutedEntry(summary.id);
                },
                onDelete: function (summary) {
                    deleteRoutedEntry(summary);
                }
            }));
        });

        var hasItems = state.routedItems.length > 0;
        var searching = dom.routedSearch.value.trim().length > 0;
        var errorShown = !dom.routedError.classList.contains('hidden');

        dom.routedEmpty.classList.toggle('hidden', hasItems || errorShown);

        // On the console rail the badge is owned by the storage readout, which knows the
        // count without paging the list.
        if (state.rail === 'routed') {
            setText(dom.routedTotalBadge, state.routedTotal ? '(' + state.routedTotal + ')' : '');
        }

        if (!hasItems && searching) {
            setText(dom.routedEmpty, 'No routed traffic matches that search.');
        } else if (!hasItems) {
            renderRoutedEmptyDefault();
        }

        renderRoutedPager();
    }

    function renderRoutedEmptyDefault() {
        dom.routedEmpty.replaceChildren();

        var title = document.createElement('p');
        title.className = 'empty-title';
        title.textContent = 'No routed traffic captured.';

        var detail = document.createElement('p');
        detail.appendChild(document.createTextNode('Traffic appears here once an application forwards calls through '));
        var path = document.createElement('span');
        path.className = 'mono';
        path.textContent = '/route';
        detail.appendChild(path);
        detail.appendChild(document.createTextNode('. Capture is off by default in local mode, so this list stays ' +
            'empty until it is switched on.'));

        dom.routedEmpty.appendChild(title);
        dom.routedEmpty.appendChild(detail);
    }

    function renderRoutedPager() {
        var total = state.routedTotal;
        var shown = state.routedItems.length;
        var hasPages = total > ROUTED_PAGE_SIZE;

        dom.routedPager.classList.toggle('hidden', !hasPages);
        if (!hasPages) {
            return;
        }

        var first = shown ? state.routedSkip + 1 : 0;
        var last = state.routedSkip + shown;
        setText(dom.routedRange, first + '–' + last + ' of ' + total);
        dom.routedPrevButton.disabled = state.routedSkip <= 0;
        dom.routedNextButton.disabled = last >= total;
    }

    function pageRouted(delta) {
        var next = state.routedSkip + (delta * ROUTED_PAGE_SIZE);
        if (next < 0) {
            next = 0;
        }
        if (next >= state.routedTotal) {
            return;
        }
        state.routedSkip = next;
        loadRouted().catch(reportError);
    }

    function deleteRoutedEntry(summary) {
        apiFetch('/api/routed/' + encodeURIComponent(summary.id), { method: 'DELETE' })
            .then(function () {
                if (state.selectedRoutedId === summary.id) {
                    state.selectedRoutedId = null;
                    state.routedEntry = null;
                    showRoutedDetail(false);
                }
                return loadRouted();
            })
            .then(loadStorage)
            .catch(reportError);
    }

    function openRoutedEntry(id) {
        apiFetch('/api/routed/' + encodeURIComponent(id)).then(function (entry) {
            if (!entry) {
                return;
            }

            state.selectedRoutedId = entry.id;
            state.routedEntry = entry;
            state.selectedHistoryId = null;

            renderResult({
                response: routedResponseOf(entry),
                error: entry.error,
                errorKind: entry.errorKind,
                durationMs: entry.durationMs
            });

            renderRoutedRequest(entry);
            showRoutedDetail(true);
            renderRoutedList();
            return loadHistory();
        }).catch(function (error) {
            toast('That routed entry could not be opened. ' + describeFailure(error), true);
        });
    }

    function routedResponseOf(entry) {
        if (entry.statusCode === null || entry.statusCode === undefined) {
            return null;
        }

        return {
            statusCode: entry.statusCode,
            reasonPhrase: entry.reasonPhrase,
            httpVersion: null,
            headers: entry.responseHeaders || [],
            durationMs: entry.durationMs,
            bodyBytes: entry.responseBytes,
            body: entry.responseBody,
            isBase64: entry.responseBodyIsBase64,
            bodyTruncated: entry.responseBodyTruncated,
            contentType: entry.responseContentType
        };
    }

    function renderRoutedRequest(entry) {
        var headers = entry.requestHeaders || [];
        setText(dom.routedRequestHeaderCount, headers.length ? '(' + headers.length + ')' : '');
        dom.routedRequestHeaders.replaceChildren();

        headers.forEach(function (header) {
            var tr = document.createElement('tr');
            var name = document.createElement('td');
            name.textContent = header.name;
            var value = document.createElement('td');
            value.textContent = header.value;
            tr.appendChild(name);
            tr.appendChild(value);
            dom.routedRequestHeaders.appendChild(tr);
        });

        var notice = [];
        if (entry.requestBodyIsBase64) {
            setText(dom.routedRequestBody, '');
            notice.push('Binary payload (' + formatBytes(entry.requestBytes) + ') is stored base64 and is not rendered.');
        } else {
            setText(dom.routedRequestBody, entry.requestBody || '');
            if (!entry.requestBody) {
                notice.push('No request body was captured.');
            }
        }

        if (entry.requestBodyTruncated) {
            notice.push('The stored copy was truncated at the configured body cap.');
        }

        setText(dom.routedRequestBodyNotice, notice.join(' '));

        var meta = [];
        meta.push(entry.method + ' ' + entry.inboundPath);
        meta.push('→ ' + entry.targetOrigin);
        if (entry.alias) {
            meta.push('alias ' + entry.alias);
        }
        meta.push(formatBytes(entry.requestBytes) + ' up');
        meta.push(relativeTime(entry.timestampUtc));
        setText(dom.routedBarMeta, meta.join(' · '));
    }

    function showRoutedDetail(visible) {
        dom.routedBar.classList.toggle('hidden', !visible);
        dom.routedRequestBodyTab.classList.toggle('hidden', !visible);
        dom.routedRequestHeadersTab.classList.toggle('hidden', !visible);

        if (!visible) {
            state.routedEntry = null;
            setText(dom.routedRequestBody, '');
            setText(dom.routedRequestBodyNotice, '');
            setText(dom.routedRequestHeaderCount, '');
            dom.routedRequestHeaders.replaceChildren();

            var active = activeResponseTabName();
            if (active === 'routedRequestBody' || active === 'routedRequestHeaders') {
                activateResponseTab('responseBody');
            }
        }
    }

    function copyRoutedToComposer() {
        var entry = state.routedEntry;
        if (!entry) {
            toast('Open a routed entry first.', true);
            return;
        }

        var headers = (entry.requestHeaders || []).map(function (header) {
            var redacted = header.value === REDACTED;
            return {
                name: header.name,
                value: redacted ? '' : header.value,
                enabled: true,
                isSecret: redacted || !!header.isSecret
            };
        });

        loadRequestIntoComposer({
            method: entry.method,
            url: entry.url,
            headers: headers,
            body: entry.requestBodyIsBase64 ? '' : (entry.requestBody || ''),
            contentType: entry.requestContentType || '',
            timeoutSeconds: 100,
            followRedirects: false,
            allowInvalidCertificates: false
        });

        var warnings = [];
        if (headers.some(function (header) { return header.isSecret && !header.value; })) {
            warnings.push('secret header values were stored redacted');
        }
        if (entry.requestBodyIsBase64) {
            warnings.push('the base64 body was not copied');
        }
        if (entry.requestBodyTruncated) {
            warnings.push('the captured body was truncated');
        }

        toast(warnings.length
            ? 'Copied to the composer, but ' + warnings.join(' and ') + '.'
            : 'Copied to the composer.');
    }

    // ------------------------------------------------------------- storage

    function loadStorage() {
        return apiFetch('/api/storage').then(function (usage) {
            dom.storageError.classList.add('hidden');
            setText(dom.storageError, '');
            renderStorage(usage);
        }).catch(function (error) {
            dom.storageRows.replaceChildren();
            setText(dom.storageTotal, '');
            setText(dom.storageRoot, '');
            dom.storageError.classList.remove('hidden');
            setText(dom.storageError, 'Storage usage is unavailable. ' + describeFailure(error));
        });
    }

    var STORE_LABELS = {
        history: 'console history',
        routed: 'routed traffic',
        headerSets: 'header sets'
    };

    function renderStorage(usage) {
        dom.storageRows.replaceChildren();
        setText(dom.storageTotal, formatBytes(usage ? usage.totalBytes : 0) + ' total');
        setText(dom.storageRoot, usage && usage.dataRoot ? usage.dataRoot : '');

        var stores = (usage && usage.stores) || [];
        stores.forEach(function (store) {
            var row = document.createElement('div');
            row.className = 'storage-row';

            var name = document.createElement('span');
            name.className = 'store-name';
            name.textContent = STORE_LABELS[store.store] || store.store;
            name.title = store.path || '';

            var entries = document.createElement('span');
            entries.className = 'store-entries';
            entries.textContent = store.maxEntries
                ? store.entries + ' / ' + store.maxEntries
                : String(store.entries);
            if (store.maxEntries && store.entries >= store.maxEntries) {
                entries.classList.add('at-cap');
                entries.title = 'At the retention cap; the oldest entries are being pruned.';
            }

            var bytes = document.createElement('span');
            bytes.className = 'store-bytes';
            bytes.textContent = formatBytes(store.bytes);

            var action = document.createElement('span');
            if (store.store === 'history' || store.store === 'routed') {
                var purge = document.createElement('button');
                purge.type = 'button';
                purge.className = 'ghost danger';
                purge.textContent = 'purge';
                purge.disabled = store.entries === 0;
                purge.title = 'Delete every entry in this store';
                purge.addEventListener('click', function () {
                    if (store.store === 'history') {
                        confirmClearHistory();
                    } else {
                        confirmPurgeRouted();
                    }
                });
                action.appendChild(purge);
            }

            row.appendChild(name);
            row.appendChild(entries);
            row.appendChild(bytes);
            row.appendChild(action);
            dom.storageRows.appendChild(row);
        });

        // The rail badge is the only hint routed traffic exists while the console rail is open.
        if (state.rail === 'console') {
            var routed = stores.filter(function (store) {
                return store.store === 'routed';
            })[0];
            setText(dom.routedTotalBadge, routed && routed.entries ? '(' + routed.entries + ')' : '');
        }
    }

    // ------------------------------------------------------------- purges

    function confirmClearHistory() {
        confirmDestructive(
            'Delete every console history entry for your account?',
            'This removes the stored requests and responses from disk. It cannot be undone.',
            function () {
                apiFetch('/api/history', { method: 'DELETE' })
                    .then(function () {
                        state.selectedHistoryId = null;
                        toast('Console history purged.');
                        return loadHistory();
                    })
                    .then(loadStorage)
                    .catch(function (error) {
                        toast('The purge failed. ' + describeFailure(error), true);
                    });
            });
    }

    function confirmPurgeRouted() {
        confirmDestructive(
            'Delete every captured routed entry for your account?',
            'This removes the full request and response capture from disk. It cannot be undone.',
            function () {
                apiFetch('/api/routed', { method: 'DELETE' })
                    .then(function () {
                        state.selectedRoutedId = null;
                        state.routedSkip = 0;
                        showRoutedDetail(false);
                        toast('Routed traffic purged.');
                        return loadRouted();
                    })
                    .then(loadStorage)
                    .catch(function (error) {
                        toast('The purge failed. ' + describeFailure(error), true);
                    });
            });
    }

    // ------------------------------------------------------------- response

    function renderResult(result) {
        state.lastResponse = result.response || null;

        var response = result.response;
        var hasError = !!result.error;

        dom.responseStatus.className = 'chip ' + statusClass(response ? response.statusCode : 0, hasError);
        if (response) {
            setText(dom.responseStatus, response.statusCode + (response.reasonPhrase ? ' ' + response.reasonPhrase : ''));
            setText(dom.responseMeta, (response.durationMs || 0) + ' ms · ' + formatBytes(response.bodyBytes) +
                (response.httpVersion ? ' · HTTP/' + response.httpVersion : '') +
                (response.contentType ? ' · ' + response.contentType : ''));
        } else {
            setText(dom.responseStatus, result.errorKind ? result.errorKind.toUpperCase() : 'ERROR');
            setText(dom.responseMeta, (result.durationMs || 0) + ' ms');
        }

        if (hasError) {
            dom.responseError.classList.remove('hidden');
            setText(dom.responseError, (result.errorKind ? result.errorKind + ': ' : '') + result.error);
        } else {
            dom.responseError.classList.add('hidden');
            setText(dom.responseError, '');
        }

        renderResponseBody();
        renderResponseHeaders(response);
    }

    function renderResponseBody() {
        var response = state.lastResponse;
        setText(dom.bodyNotice, '');

        if (!response) {
            setText(dom.responseBody, '');
            return;
        }

        if (response.isBase64) {
            setText(dom.responseBody, '');
            setText(dom.bodyNotice, 'Binary payload (' + formatBytes(response.bodyBytes) +
                ') is stored base64 and is not rendered.' +
                (response.bodyTruncated ? ' The stored copy was truncated at the configured body cap.' : ''));
            return;
        }

        var text = response.body || '';
        if (dom.prettyToggle.checked && text) {
            try {
                text = JSON.stringify(JSON.parse(text), null, 2);
            } catch (error) {
                // Not JSON: show it as received.
            }
        }

        if (response.bodyTruncated) {
            setText(dom.bodyNotice, 'Stored copy was truncated at the configured body cap.');
        }

        setText(dom.responseBody, text);
    }

    function renderResponseHeaders(response) {
        dom.responseHeaders.replaceChildren();
        var headers = (response && response.headers) || [];
        setText(dom.responseHeaderCount, headers.length ? '(' + headers.length + ')' : '');

        headers.forEach(function (header) {
            var tr = document.createElement('tr');
            var name = document.createElement('td');
            name.textContent = header.name;
            var value = document.createElement('td');
            value.textContent = header.value;
            tr.appendChild(name);
            tr.appendChild(value);
            dom.responseHeaders.appendChild(tr);
        });
    }

    // ----------------------------------------------------------------- send

    function send() {
        if (state.sending) {
            return;
        }

        var url = dom.urlInput.value.trim();
        if (!url) {
            toast('Enter a URL first.', true);
            dom.urlInput.focus();
            return;
        }

        var request = {
            method: currentMethod(),
            url: url,
            headers: collectHeaders(),
            body: dom.bodyInput.value.length ? dom.bodyInput.value : null,
            contentType: dom.contentTypeInput.value.trim() || null,
            timeoutSeconds: parseInt(dom.timeoutInput.value, 10) || 100,
            followRedirects: dom.followRedirects.checked,
            allowInvalidCertificates: dom.allowInvalidCerts.checked
        };

        state.selectedRoutedId = null;
        showRoutedDetail(false);
        renderRoutedList();

        state.sending = true;
        dom.sendButton.disabled = true;
        setText(dom.sendButton, 'Sending…');
        dom.responseStatus.className = 'chip chip-pending';
        setText(dom.responseStatus, 'Sending');
        setText(dom.responseMeta, '');
        dom.responseError.classList.add('hidden');

        apiFetch('/api/proxy/execute', { method: 'POST', body: request })
            .then(function (result) {
                state.selectedHistoryId = result.historyId;
                renderResult(result);
                return loadHistory().then(loadStorage);
            })
            .catch(function (error) {
                dom.responseStatus.className = 'chip chip-error';
                setText(dom.responseStatus, 'ERROR');
                dom.responseError.classList.remove('hidden');
                setText(dom.responseError, error.message);
            })
            .then(function () {
                state.sending = false;
                dom.sendButton.disabled = false;
                setText(dom.sendButton, 'Send');
            });
    }

    // ----------------------------------------------------------------- init

    function wireEvents() {
        dom.composerForm.addEventListener('submit', function (event) {
            event.preventDefault();
            send();
        });

        document.addEventListener('keydown', function (event) {
            if (event.key === 'Escape' && confirmIsOpen()) {
                event.preventDefault();
                closeConfirm();
                return;
            }

            if ((event.ctrlKey || event.metaKey) && event.key === 'Enter' && !confirmIsOpen()) {
                event.preventDefault();
                send();
            }
        });

        dom.methodSelect.addEventListener('change', function () {
            var custom = dom.methodSelect.value === '__custom';
            dom.customMethod.classList.toggle('hidden', !custom);
            if (custom) {
                dom.customMethod.focus();
            }
        });

        var searchTimer = null;
        dom.historySearch.addEventListener('input', function () {
            if (searchTimer) {
                window.clearTimeout(searchTimer);
            }
            searchTimer = window.setTimeout(function () {
                loadHistory().catch(reportError);
            }, 200);
        });

        dom.clearHistoryButton.addEventListener('click', confirmClearHistory);

        dom.railConsoleTab.addEventListener('click', function () {
            selectRail('console');
        });

        dom.railRoutedTab.addEventListener('click', function () {
            selectRail('routed');
        });

        var routedSearchTimer = null;
        dom.routedSearch.addEventListener('input', function () {
            if (routedSearchTimer) {
                window.clearTimeout(routedSearchTimer);
            }
            routedSearchTimer = window.setTimeout(function () {
                state.routedSkip = 0;
                loadRouted().catch(reportError);
            }, 200);
        });

        dom.routedPrevButton.addEventListener('click', function () {
            pageRouted(-1);
        });

        dom.routedNextButton.addEventListener('click', function () {
            pageRouted(1);
        });

        dom.clearRoutedButton.addEventListener('click', confirmPurgeRouted);

        dom.copyToComposerButton.addEventListener('click', copyRoutedToComposer);

        dom.storageRefreshButton.addEventListener('click', function () {
            loadStorage().catch(reportError);
        });

        dom.confirmCancel.addEventListener('click', closeConfirm);

        dom.confirmAccept.addEventListener('click', function () {
            var action = state.confirmAction;
            closeConfirm();
            if (action) {
                action();
            }
        });

        dom.confirmBackdrop.addEventListener('click', function (event) {
            if (event.target === dom.confirmBackdrop) {
                closeConfirm();
            }
        });

        dom.headerSetSelect.addEventListener('change', function () {
            var id = dom.headerSetSelect.value;
            dom.headerSetSelect.value = '';
            if (!id) {
                return;
            }
            var set = state.headerSets.filter(function (candidate) {
                return candidate.id === id;
            })[0];
            if (set) {
                mergeHeaders(set.headers);
                toast('Applied ' + set.name + '.');
            }
        });

        dom.saveHeaderSetButton.addEventListener('click', saveCurrentHeadersAsSet);

        dom.manageHeaderSetsButton.addEventListener('click', function () {
            dom.headerSetManager.classList.toggle('hidden');
        });

        dom.formatJsonButton.addEventListener('click', function () {
            var text = dom.bodyInput.value;
            if (!text.trim()) {
                return;
            }
            try {
                dom.bodyInput.value = JSON.stringify(JSON.parse(text), null, 2);
                if (!dom.contentTypeInput.value.trim()) {
                    dom.contentTypeInput.value = 'application/json';
                }
            } catch (error) {
                toast('The body is not valid JSON.', true);
            }
        });

        dom.prettyToggle.addEventListener('change', renderResponseBody);

        dom.copyBodyButton.addEventListener('click', function () {
            var text = dom.responseBody.textContent || '';
            if (!text) {
                toast('There is nothing to copy.', true);
                return;
            }
            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text).then(function () {
                    toast('Response body copied.');
                }).catch(function () {
                    toast('The browser refused clipboard access.', true);
                });
            } else {
                toast('Clipboard access is unavailable.', true);
            }
        });
    }

    function init() {
        wireTabs();
        wireEvents();
        setHeaders([]);

        apiFetch('/api/me').then(function (me) {
            setText(dom.signedInAs, me.displayName + ' · ' + me.userName);
        }).catch(reportError);

        loadHeaderSets(true).catch(reportError);
        loadHistory().catch(reportError);
        loadStorage().catch(reportError);
        renderRoutedEmptyDefault();
        dom.urlInput.focus();
    }

    init();
}());
