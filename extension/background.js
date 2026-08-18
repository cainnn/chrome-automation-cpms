const DEFAULT_WS_URL = 'ws://127.0.0.1:9333';
const RECONNECT_DELAY_MS = 3000;

let ws = null;
let reconnectTimer = null;
let keepAliveTimer = null;

async function ensureOffscreenDocument() {
  try {
    if (chrome.offscreen?.hasDocument) {
      const has = await chrome.offscreen.hasDocument();
      if (has) return;
    }
    await chrome.offscreen.createDocument({
      url: 'offscreen.html',
      reasons: ['WORKERS'],
      justification: 'Keep automation bridge alive during long CPMS export waits',
    });
  } catch (err) {
    console.warn('[AutomationBridge] offscreen keepalive unavailable:', err);
  }
}

async function getWsUrl() {
  const { wsUrl } = await chrome.storage.local.get('wsUrl');
  return wsUrl || DEFAULT_WS_URL;
}

function connect() {
  if (ws?.readyState === WebSocket.OPEN || ws?.readyState === WebSocket.CONNECTING) {
    return;
  }

  getWsUrl().then((url) => {
    ws = new WebSocket(url);

    ws.onopen = () => {
      console.log('[AutomationBridge] Connected to bridge server');
      ensureOffscreenDocument().catch(() => {});
      ws.send(JSON.stringify({ role: 'extension', type: 'register' }));
      chrome.storage.local.set({ connected: true, lastError: null });
      clearInterval(keepAliveTimer);
      keepAliveTimer = setInterval(() => {
        if (ws?.readyState === WebSocket.OPEN) {
          ws.send(JSON.stringify({ type: 'ping' }));
        } else {
          connect();
        }
      }, 15000);
    };

    ws.onmessage = (event) => {
      handleMessage(event.data).catch((err) => {
        console.error('[AutomationBridge] Command error:', err);
      });
    };

    ws.onclose = () => {
      console.log('[AutomationBridge] Disconnected, reconnecting...');
      clearInterval(keepAliveTimer);
      chrome.storage.local.set({ connected: false });
      scheduleReconnect();
    };

    ws.onerror = () => {
      chrome.storage.local.set({ connected: false, lastError: 'WebSocket connection failed' });
    };
  });
}

function scheduleReconnect() {
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(connect, RECONNECT_DELAY_MS);
}

async function handleMessage(raw) {
  let msg;
  try {
    msg = JSON.parse(raw);
  } catch {
    return;
  }

  if (msg.role === 'extension' || !msg.id || !msg.action) {
    return;
  }

  try {
    const data = await executeAction(msg.action, msg.params || {});
    sendResponse(msg.id, true, data);
  } catch (err) {
    sendResponse(msg.id, false, null, err.message || String(err));
  }
}

function sendResponse(id, success, data, error = null) {
  if (ws?.readyState !== WebSocket.OPEN) return;
  ws.send(JSON.stringify({ id, success, data, error }));
}

async function executeAction(action, params) {
  if (
    action.startsWith('cpms') ||
    action.includes('Download') ||
    action === 'waitForDownload'
  ) {
    await ensureOffscreenDocument();
  }
  switch (action) {
    case 'getCookiesForUrl':
      return getCookiesForUrl(params);
    case 'getExtensionVersion':
      return { version: chrome.runtime.getManifest().version };
    case 'reloadExtension':
      chrome.runtime.reload();
      return { ok: true, version: chrome.runtime.getManifest().version };
    case 'getTabs':
      return getTabs();
    case 'createTab':
      return createTab(params);
    case 'closeTab':
      return closeTab(params);
    case 'activateTab':
      return activateTab(params);
    case 'navigate':
      return navigate(params);
    case 'click':
    case 'clickByText':
    case 'type':
    case 'query':
    case 'queryAll':
    case 'waitFor':
    case 'waitForText':
    case 'getBodyText':
    case 'cpmsHasExportButton':
    case 'cpmsRefreshDownloadList':
    case 'cpmsClickExport':
    case 'cpmsClickDialogConfirm':
    case 'cpmsGetLatestSerial':
    case 'cpmsExportRowStatus':
    case 'cpmsClickDownload':
    case 'cpmsPageContextBlobDownload':
    case 'cpmsResolveDownloadUrl':
    case 'cpmsCaptureDownloadOnClick':
    case 'cpmsSniffDownloadUrlOnClick':
    case 'cpmsGetDownloadUrl':
    case 'cpmsFirstReadyRow':
    case 'cpmsClickFirstReadyDownload':
    case 'cpmsListButtons':
    case 'cpmsDiagRow':
    case 'cpmsApiProbe':
    case 'scroll':
    case 'getPageInfo':
      return runInTab(action, params);
    case 'evaluate':
      return evaluateInTab(params);
    case 'screenshot':
      return takeScreenshot(params);
    case 'enableAutoAcceptDownloads':
      return enableAutoAcceptDownloads(params);
    case 'disableAutoAcceptDownloads':
      return disableAutoAcceptDownloads(params);
    case 'waitForDownload':
      return waitForDownload(params);
    case 'acceptPendingDownloads':
      return acceptPendingDownloads(params);
    case 'acceptDangerousDownloadsViaPage':
      return acceptDangerousDownloadsViaPage(params);
    case 'startDownload':
      return startDownload(params);
    case 'cpmsDownloadBySerial':
      return cpmsDownloadBySerial(params);
    case 'cpmsApiDownloadFile':
      return cpmsApiDownloadFile(params);
    case 'cpmsProbeDownloadAttempts':
      return cpmsProbeDownloadAttempts(params);
    case 'cpmsOpenDownloadUrl':
      return cpmsOpenDownloadUrl(params);
    case 'cpmsDownloadByClickPlanB':
      return cpmsDownloadByClickPlanB(params);
    case 'downloadWithSessionCookies':
      return downloadWithSessionCookies(
        params.url,
        params.tabId,
        params.filename,
        {
          method: params.method,
          body: params.body,
          headers: params.headers,
        },
      );
    case 'cpmsRescueDangerousDownload':
      return cpmsRescueDangerousDownload(params);
    default:
      throw new Error(`Unknown action: ${action}`);
  }
}

async function resolveTabId(tabId, recreateUrl) {
  if (tabId != null) {
    try {
      await chrome.tabs.get(tabId);
      return tabId;
    } catch {
      if (recreateUrl) {
        console.warn('[AutomationBridge] Tab missing, opening new tab (browser stays open):', tabId);
        const tab = await chrome.tabs.create({ url: recreateUrl, active: true });
        await new Promise((r) => setTimeout(r, 8000));
        return tab.id;
      }
      throw new Error(`No tab with id: ${tabId}`);
    }
  }
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id) throw new Error('No active tab found');
  return tab.id;
}

async function getCookiesForUrl(params = {}) {
  const url = params.url || 'http://cpms.hq.cmcc/';
  const cookies = await chrome.cookies.getAll({ url });
  const cookieHeader = cookies.map((c) => `${c.name}=${c.value}`).join('; ');
  return { cookieHeader, count: cookies.length };
}

async function getTabs() {
  const tabs = await chrome.tabs.query({});
  return tabs.map(({ id, url, title, active, windowId }) => ({ id, url, title, active, windowId }));
}

async function createTab(params) {
  const tab = await chrome.tabs.create({ url: params.url || 'about:blank', active: params.active !== false });
  return { id: tab.id, url: tab.url, title: tab.title };
}

async function closeTab(params) {
  const tabId = await resolveTabId(params.tabId);
  await chrome.tabs.remove(tabId);
  return { closed: tabId };
}

async function activateTab(params) {
  const tabId = await resolveTabId(params.tabId);
  await chrome.tabs.update(tabId, { active: true });
  const tab = await chrome.tabs.get(tabId);
  if (tab.windowId != null) {
    try {
      await chrome.windows.update(tab.windowId, {
        focused: true,
        drawAttention: true,
      });
    } catch (err) {
      console.warn('[AutomationBridge] chrome.windows.update failed:', err.message || err);
    }
  }
  return { id: tab.id, url: tab.url, title: tab.title, windowId: tab.windowId };
}

async function navigate(params) {
  const tabId = await resolveTabId(params.tabId, params.recreateUrl);
  const url = params.url;
  if (!url) throw new Error('url is required');

  await chrome.tabs.update(tabId, { url });

  if (params.waitUntil === 'load') {
    await waitForTabLoad(tabId, params.timeout || 30000);
  }

  // SPA hash 路由：tabs.update 有时只改地址栏，需强制 location 切换并等待渲染
  if (params.waitUntil === 'spa') {
    try {
      await waitForTabLoad(tabId, Math.min(params.timeout || 45000, 45000));
    } catch {
      // 部分 CPMS 页 load 事件不完整，继续尝试
    }

    await chrome.scripting.executeScript({
      target: { tabId, allFrames: false },
      world: 'MAIN',
      func: (targetUrl) => {
        const targetHash = targetUrl.includes('#') ? targetUrl.slice(targetUrl.indexOf('#')) : '';
        const currentHash = window.location.hash || '';
        const sameRoute =
          window.location.href === targetUrl ||
          (targetHash && currentHash === targetHash);
        if (!sameRoute) {
          window.location.assign(targetUrl);
        }
      },
      args: [url],
    });

    await new Promise((r) => setTimeout(r, params.delay || 5000));
  }

  const tab = await chrome.tabs.get(tabId);
  return { id: tab.id, url: tab.url, title: tab.title };
}

function waitForTabLoad(tabId, timeout) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      chrome.tabs.onUpdated.removeListener(listener);
      reject(new Error('Navigation timeout'));
    }, timeout);

    function listener(updatedTabId, info) {
      if (updatedTabId === tabId && info.status === 'complete') {
        clearTimeout(timer);
        chrome.tabs.onUpdated.removeListener(listener);
        resolve();
      }
    }

    chrome.tabs.onUpdated.addListener(listener);
  });
}

async function runInTab(action, params) {
  const tabId = await resolveTabId(params.tabId, params.recreateUrl);
  const allFrames = action === 'getBodyText';
  const results = await chrome.scripting.executeScript({
    target: { tabId, allFrames },
    world: 'MAIN',
    func: contentAction,
    args: [action, params],
  });

  if (action === 'getBodyText') {
    let best = '';
    for (const frame of results) {
      const t = frame.result?.data?.text;
      if (typeof t === 'string' && t.length > best.length) best = t;
    }
    return { text: best };
  }

  let lastError = null;
  for (const frame of results) {
    const r = frame.result;
    if (!r) continue;
    if (r.error) {
      lastError = r.error;
      continue;
    }
    if (r.data !== undefined) return r.data;
  }

  if (lastError) throw new Error(lastError);
  throw new Error(`Action failed in all frames: ${action}`);
}

async function evaluateInTab(params) {
  throw new Error('evaluate is disabled due to CSP. Use dedicated actions instead.');
}

async function contentAction(action, params) {
  function normalizeText(el) {
    return (el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim();
  }

  function walkRoots(callback, root = document) {
    callback(root);
    root.querySelectorAll('*').forEach((el) => {
      if (el.shadowRoot) walkRoots(callback, el.shadowRoot);
    });
  }

  function collectClickables(root) {
    const out = [];
    walkRoots((doc) => {
      doc.querySelectorAll('button, .el-button, a, span, div, [role="button"]').forEach((el) => {
        out.push(el);
      });
    }, root);
    return out;
  }

  function isVisible(el) {
    const rect = el.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return false;
    const style = window.getComputedStyle(el);
    return style.display !== 'none' && style.visibility !== 'hidden' && style.opacity !== '0';
  }

  function isLikelyDownloadUrl(url) {
    if (!url || typeof url !== 'string') return false;
    const lower = url.toLowerCase().replace(/:\//g, '/');
    if (
      /operationlog|operation\/log|\/add(?:\?|$)|getattachmentdownloadinfolist|infolist/i.test(
        lower,
      )
    ) {
      return false;
    }
    if (/\.(zip|xlsx|xls)(\?|$)/i.test(lower)) return true;
    if (
      /downloadattachment|\/download|exportfile|filedownload|getfile|downloadfile|file\/get/i.test(
        lower,
      )
    ) {
      return true;
    }
    return false;
  }

  function extractUrlsFromJson(value, out) {
    if (!value) return;
    if (typeof value === 'string') {
      if (isLikelyDownloadUrl(value)) out.push(value);
      return;
    }
    if (Array.isArray(value)) {
      value.forEach((v) => extractUrlsFromJson(v, out));
      return;
    }
    if (typeof value === 'object') {
      Object.values(value).forEach((v) => extractUrlsFromJson(v, out));
    }
  }

  function pickBestDownloadUrl(urls) {
    if (!Array.isArray(urls)) return null;
    return (
      urls.find((u) => u && /\.(zip|xlsx|xls)(\?|$)/i.test(u)) ||
      urls.find((u) => u && isLikelyDownloadUrl(u)) ||
      null
    );
  }

  function collectObjectArrays(value, out, depth = 0) {
    if (!value || depth > 10) return;
    if (Array.isArray(value)) {
      if (
        value.length > 0 &&
        value.every((x) => x && typeof x === 'object' && !Array.isArray(x))
      ) {
        out.push(value);
      }
      value.forEach((v) => collectObjectArrays(v, out, depth + 1));
      return;
    }
    if (typeof value === 'object') {
      Object.values(value).forEach((v) => collectObjectArrays(v, out, depth + 1));
    }
  }

  function findListRowBySerial(json, serial) {
    const arrays = [];
    collectObjectArrays(json, arrays);
    for (const list of arrays) {
      const row = list.find((item) => {
        try {
          return JSON.stringify(item).includes(serial);
        } catch {
          return false;
        }
      });
      if (row) return row;
    }
    return null;
  }

  function isHttpHeaderByteString(str) {
    for (let i = 0; i < str.length; i++) {
      if (str.charCodeAt(i) > 255) return false;
    }
    return true;
  }

  function collectAuthHeaders() {
    const headers = { 'Content-Type': 'application/json' };
    const storages = [localStorage, sessionStorage];
    for (const storage of storages) {
      for (let i = 0; i < storage.length; i++) {
        const key = storage.key(i);
        if (!key) continue;
        const value = storage.getItem(key);
        if (!value || value.length > 4000) continue;
        const trimmed = value.trim();
        if (/^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(trimmed)) {
          headers.Authorization = `Bearer ${trimmed}`;
          continue;
        }
        if (!isHttpHeaderByteString(value)) continue;
        if (!/token|auth|session|ticket|jwt|cpms|pms|user|令牌|凭证|登录/i.test(key)) continue;
        if (/authorization/i.test(key)) {
          headers.Authorization = value.startsWith('Bearer') ? value : `Bearer ${value}`;
        } else if (/^token$|access_token|accessToken|userToken|cpmsToken|pmsToken/i.test(key)) {
          headers.Authorization = headers.Authorization || (value.startsWith('Bearer') ? value : `Bearer ${value}`);
          headers.token = value;
        }
        // 不使用 localStorage 原始 key 作为 header 名（可能含中文，fetch 会抛 ISO-8859-1 错误）
      }
    }
    return headers;
  }

  function collectPerformanceUrls(urls) {
    try {
      const entries = performance.getEntriesByType('resource') || [];
      for (const entry of entries) {
        const name = entry.name || '';
        if (!name || name.includes('operationLog')) continue;
        if (
          /attachment|download|export|mops\/mops|getAttachment|downloadAttachment/i.test(name)
        ) {
          urls.push(name);
        }
      }
    } catch {
      /* ignore */
    }
  }

  function buildDownloadAttemptsFromRow(origin, row, serial) {
    const attempts = [];
    const seen = new Set();
    const add = (url, method, body, prepend = false) => {
      const key = url + (method || 'GET');
      if (!url || seen.has(key)) return;
      seen.add(key);
      const item = { url, method: method || 'GET', body: body || null };
      if (prepend) attempts.unshift(item);
      else attempts.push(item);
    };

    const fileId = row.fileId;
    const businessCode = row.businessCode || serial;
    const recordId = row.id;
    if (fileId != null && fileId !== '') {
      const fid = String(fileId);
      add(
        origin + '/cpms/file/fileserver/special/formDownloadByFileIds',
        'POST',
        { fileIds: [fid] },
        true,
      );
      const attBody = { fileId: fid, businessCode };
      if (recordId != null && recordId !== '') attBody.id = String(recordId);
      add(
        origin + '/cpms/mops/mops/attachmentDownload/v1/downloadAttachment',
        'POST',
        attBody,
        true,
      );
      add(
        origin + '/cpms/mops/mops/attachmentDownload/v1/downloadAttachment',
        'POST',
        row,
        true,
      );
    }

    const urlFields = [
      'fileUrl',
      'filePath',
      'downloadUrl',
      'attachmentUrl',
      'annexUrl',
      'url',
      'path',
      'fullPath',
      'fileAddress',
    ];
    for (const field of urlFields) {
      const value = row[field];
      if (typeof value !== 'string' || value.length < 4) continue;
      const abs = value.startsWith('http') ? value : origin + (value.startsWith('/') ? value : `/${value}`);
      add(abs, 'GET');
    }

    const idFields = [
      'id',
      'attachmentId',
      'fileId',
      'downloadId',
      'recordId',
      'businessId',
      'attachmentDownloadId',
    ];
        const paths = [
      '/cpms/mops/mops/attachmentDownload/v1/downloadAttachment',
      '/cpms/mops/mops/v1/downloadAttachment',
      '/pms/mops/mops/v1/downloadAttachment',
      '/cpms/mops/mops/v1/download',
      '/cpms/mops/mops/v1/file/download',
    ];
    for (const field of idFields) {
      const val = row[field];
      if (val == null || val === '') continue;
      for (const path of paths) {
        const base = origin + path;
        add(base, 'POST', { [field]: val });
        add(base, 'POST', { id: val });
        add(`${base}?id=${encodeURIComponent(val)}`, 'GET');
        add(`${base}?${field}=${encodeURIComponent(val)}`, 'GET');
      }
    }

    if (fileId != null && fileId !== '') {
      const fid = String(fileId);
      for (const path of paths) {
        const base = origin + path;
        add(base, 'POST', { fileId: fid });
        add(base, 'POST', { businessCode, fileId: fid });
        add(base, 'POST', { businessSerialNumber: businessCode, fileId: fid });
      }
    }

    for (const path of paths) {
      const base = origin + path;
      add(base, 'POST', { businessSerialNumber: serial });
      add(base, 'POST', { serialNumber: serial });
      add(base, 'POST', { businessFlowCode: serial });
      add(base, 'POST', { businessCode: serial });
    }
    return attempts;
  }

  function findExportButton() {
    const toolHints = ['字段说明', '展示列配置', '显示列配置'];
    for (const hint of toolHints) {
      const anchor = [...document.querySelectorAll('button, span, .el-button, label, div')].find(
        (el) => normalizeText(el) === hint && isVisible(el),
      );
      if (!anchor) continue;
      let container = anchor.parentElement;
      for (let depth = 0; depth < 4 && container; depth++) {
        const btns = [...container.querySelectorAll('button, .el-button, [role="button"]')];
        const exportBtn = btns.find((el) => normalizeText(el) === '导出' && isVisible(el));
        if (exportBtn) return exportBtn;
        container = container.parentElement;
      }
    }

    const candidates = collectClickables(document);
    let exportBtn = candidates.find((el) => normalizeText(el) === '导出' && isVisible(el));
    if (!exportBtn) {
      exportBtn = candidates.find((el) => {
        const text = normalizeText(el);
        return text.includes('导出') && text.length <= 8 && isVisible(el);
      });
    }
    if (!exportBtn) {
      const buttons = document.querySelectorAll('.el-button, button, [role="button"]');
      exportBtn = [...buttons].find((el) => normalizeText(el) === '导出' && isVisible(el));
    }
    if (!exportBtn) {
      exportBtn =
        document.querySelector('button[title="导出"], [aria-label="导出"]') ||
        document.querySelector('[class*="export"], [id*="export"]');
      if (exportBtn && !isVisible(exportBtn)) exportBtn = null;
    }
    return exportBtn;
  }

  function clickElement(el) {
    el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, view: window }));
    el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, view: window }));
    el.click();
  }

  function findCpmsDownloadTarget(serial) {
    const rows = [...document.querySelectorAll('tr, .el-table__row, .ant-table-row')];
    const row = rows.find((r) => (r.innerText || '').includes(serial));
    if (!row) return { error: '未找到流水号所在行: ' + serial };
    if (!(row.innerText || '').includes('后台下载成功')) {
      return { error: '后台尚未处理完成' };
    }
    row.scrollIntoView({ block: 'center', behavior: 'instant' });
    const clickables = [...row.querySelectorAll('button, a, span, .el-button, .el-link, [role="button"], i, .el-icon, [class*="download"], [class*="icon"]')];
    let download = clickables.find((el) => normalizeText(el) === '下载');
    if (!download) {
      download = clickables.find((el) => normalizeText(el).includes('下载'));
    }
    if (!download) {
      download = clickables.find((el) => {
        const cls = (el.className && typeof el.className === 'string') ? el.className : '';
        return cls.includes('download') || cls.includes('Download') || cls.includes('el-icon-download');
      });
    }
    if (!download) {
      const cells = row.querySelectorAll('td, .el-table__cell, .cell');
      if (cells.length >= 2) {
        const actionCell = cells[1];
        const actionClickables = actionCell.querySelectorAll('a, button, span, .el-button, .el-link, [role="button"], i, [class*="icon"]');
        if (actionClickables.length > 0) {
          download = actionClickables[0];
        }
      }
    }
    if (!download) return { error: '该行未找到下载按钮' };
    const target = download.closest('button, a, .el-button, .el-link, [role="button"]') || download;
    return { row, target };
  }

  function installNetworkCapture(state) {
    if (state.installed) return;
    state.installed = true;
    state.requests = [];
    state.urls = [];
    state.authHeaders = state.authHeaders || {};

    function mergeHeaders(init) {
      if (!init?.headers) return;
      const hdrs = init.headers;
      if (hdrs instanceof Headers) {
        hdrs.forEach((v, k) => {
          if (isHttpHeaderByteString(v)) state.authHeaders[k] = v;
        });
      } else if (typeof hdrs === 'object') {
        for (const [k, v] of Object.entries(hdrs)) {
          if (typeof v === 'string' && isHttpHeaderByteString(v)) state.authHeaders[k] = v;
        }
      }
    }

    function record(req) {
      state.requests.push({ ...req, at: Date.now() });
      if (req.url) state.urls.push(String(req.url));
    }

    state.origFetch = window.fetch;
    window.fetch = function (...args) {
      const reqUrl = typeof args[0] === 'string' ? args[0] : args[0]?.url;
      const init = args[1] || {};
      const method = (init.method || 'GET').toUpperCase();
      let bodyStr = null;
      if (init.body != null) {
        bodyStr = typeof init.body === 'string' ? init.body.slice(0, 8000) : '[body]';
      }
      mergeHeaders(init);
      if (reqUrl && !/operationLog\/add/i.test(String(reqUrl))) {
        record({ phase: 'request', transport: 'fetch', method, url: String(reqUrl), body: bodyStr });
      }
      return state.origFetch.apply(this, args).then(async (res) => {
        const fullUrl = res?.url || reqUrl || '';
        const ct = res?.headers?.get?.('content-type') || '';
        if (fullUrl && !/operationLog\/add/i.test(String(fullUrl))) {
          let bodyPreview = '';
          if (/json|text|html/i.test(ct)) {
            bodyPreview = (await res.clone().text().catch(() => '')).slice(0, 4000);
          }
          record({
            phase: 'response',
            transport: 'fetch',
            method,
            url: String(fullUrl),
            status: res.status,
            contentType: ct,
            bodyPreview,
            isZip: /zip|octet-stream/i.test(ct),
          });
          if (/zip|octet-stream|spreadsheet|excel|vnd\./i.test(ct)) {
            state.urls.unshift(String(fullUrl));
          }
          try {
            if (/json/i.test(ct) && bodyPreview) {
              extractUrlsFromJson(JSON.parse(bodyPreview), state.urls);
            }
          } catch {
            /* ignore */
          }
        }
        return res;
      });
    };

    const OrigXHR = window.XMLHttpRequest;
    function HookedXHR() {
      const xhr = new OrigXHR();
      const origOpen = xhr.open;
      const origSend = xhr.send;
      const origSetHeader = xhr.setRequestHeader;
      xhr.__headers = {};
      xhr.open = function (method, url, ...rest) {
        xhr.__method = method;
        xhr.__url = url;
        if (url && !/operationLog\/add/i.test(String(url))) {
          record({ phase: 'request', transport: 'xhr', method, url: String(url), body: null });
        }
        return origOpen.call(xhr, method, url, ...rest);
      };
      xhr.setRequestHeader = function (name, value) {
        if (typeof value === 'string' && isHttpHeaderByteString(value)) {
          xhr.__headers[name] = value;
          state.authHeaders[name] = value;
        }
        return origSetHeader.call(xhr, name, value);
      };
      xhr.send = function (body) {
        const bodyStr =
          body == null ? null : typeof body === 'string' ? body.slice(0, 8000) : '[binary]';
        if (xhr.__url && !/operationLog\/add/i.test(String(xhr.__url))) {
          record({
            phase: 'request',
            transport: 'xhr',
            method: xhr.__method || 'GET',
            url: String(xhr.__url),
            body: bodyStr,
            headers: { ...xhr.__headers },
          });
        }
        xhr.addEventListener('load', function () {
          const ct = xhr.getResponseHeader('content-type') || '';
          record({
            phase: 'response',
            transport: 'xhr',
            method: xhr.__method || 'GET',
            url: String(xhr.__url || ''),
            status: xhr.status,
            contentType: ct,
            bodyPreview: (xhr.responseText || '').slice(0, 4000),
            isZip: /zip|octet-stream/i.test(ct),
          });
          if (/zip|octet-stream/i.test(ct)) {
            state.urls.unshift(String(xhr.__url || ''));
          }
        });
        return origSend.call(xhr, body);
      };
      return xhr;
    }
    HookedXHR.prototype = OrigXHR.prototype;
    state.OrigXHR = OrigXHR;
    window.XMLHttpRequest = HookedXHR;

    state.origOpen = window.open;
    window.open = function (url, ...rest) {
      if (typeof url === 'string') {
        record({ phase: 'navigation', transport: 'window.open', method: 'GET', url });
        state.urls.push(url);
      }
      return state.origOpen.call(window, url, ...rest);
    };

    state.origAssign = window.location.assign.bind(window.location);
    window.location.assign = function (url) {
      record({ phase: 'navigation', transport: 'location.assign', method: 'GET', url: String(url) });
      state.urls.push(String(url));
      return state.origAssign(url);
    };

    collectPerformanceUrls(state.urls);
  }

  function restoreNetworkCapture(state) {
    if (!state.installed) return;
    if (state.origFetch) window.fetch = state.origFetch;
    if (state.OrigXHR) window.XMLHttpRequest = state.OrigXHR;
    if (state.origOpen) window.open = state.origOpen;
    if (state.origAssign) window.location.assign = state.origAssign;
    state.installed = false;
  }

  function waitForSelector(selector, timeout = 10000) {
    return new Promise((resolve, reject) => {
      const el = document.querySelector(selector);
      if (el) return resolve(el);

      const observer = new MutationObserver(() => {
        const found = document.querySelector(selector);
        if (found) {
          observer.disconnect();
          clearTimeout(timer);
          resolve(found);
        }
      });

      observer.observe(document.documentElement, { childList: true, subtree: true });

      const timer = setTimeout(() => {
        observer.disconnect();
        reject(new Error(`Timeout waiting for selector: ${selector}`));
      }, timeout);
    });
  }

  function findByText(text, exact = false) {
    const candidates = collectClickables(document);
    for (const el of candidates) {
      if (!isVisible(el)) continue;
      const content = normalizeText(el);
      if (!content) continue;
      if (exact ? content === text : content.includes(text)) {
        return el;
      }
    }
    return null;
  }

  function waitForText(text, timeout = 10000, exact = false) {
    return new Promise((resolve, reject) => {
      const check = () => findByText(text, exact) || document.body.innerText.includes(text);
      if (check()) return resolve(findByText(text, exact) || document.body);

      const observer = new MutationObserver(() => {
        if (check()) {
          observer.disconnect();
          clearTimeout(timer);
          resolve(findByText(text, exact) || document.body);
        }
      });

      observer.observe(document.documentElement, { childList: true, subtree: true, characterData: true });

      const timer = setTimeout(() => {
        observer.disconnect();
        reject(new Error(`Timeout waiting for text: ${text}`));
      }, timeout);
    });
  }

  try {
    switch (action) {
      case 'clickByText': {
        const el = findByText(params.text, params.exact ?? false);
        if (!el) throw new Error(`Element not found with text: ${params.text}`);
        clickElement(el);
        return { data: { clicked: params.text } };
      }
      case 'click': {
        const el = document.querySelector(params.selector);
        if (!el) throw new Error(`Element not found: ${params.selector}`);
        el.click();
        return { data: { clicked: params.selector } };
      }
      case 'type': {
        const el = document.querySelector(params.selector);
        if (!el) throw new Error(`Element not found: ${params.selector}`);
        el.focus();
        el.value = params.text ?? '';
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return { data: { typed: params.text } };
      }
      case 'query': {
        const el = document.querySelector(params.selector);
        if (!el) return { data: null };
        return {
          data: {
            text: el.innerText?.trim() ?? '',
            html: el.innerHTML,
            value: el.value ?? null,
            attributes: Object.fromEntries([...el.attributes].map((a) => [a.name, a.value])),
          },
        };
      }
      case 'queryAll': {
        const els = [...document.querySelectorAll(params.selector)];
        return {
          data: els.map((el) => ({
            text: el.innerText?.trim() ?? '',
            value: el.value ?? null,
          })),
        };
      }
      case 'scroll': {
        if (params.selector) {
          const el = document.querySelector(params.selector);
          if (!el) throw new Error(`Element not found: ${params.selector}`);
          el.scrollIntoView({ behavior: 'smooth', block: 'center' });
        } else {
          window.scrollTo(params.x ?? 0, params.y ?? 0);
        }
        return { data: { scrolled: true } };
      }
      case 'getPageInfo':
        return {
          data: {
            url: location.href,
            title: document.title,
            readyState: document.readyState,
          },
        };
      case 'waitFor': {
        const el = await waitForSelector(params.selector, params.timeout || 10000);
        return { data: { found: true, text: el.innerText?.trim() ?? '' } };
      }
      case 'waitForText': {
        const el = await waitForText(params.text, params.timeout || 10000, params.exact ?? false);
        return { data: { found: true, text: (el.innerText || el.textContent || '').trim() } };
      }
      case 'getBodyText':
        return { data: { text: document.body?.innerText ?? '' } };
      case 'cpmsRefreshDownloadList': {
        const labels = ['查询', '搜索', '刷新'];
        for (const label of labels) {
          const btn = findByText(label, true);
          if (btn && isVisible(btn)) {
            clickElement(btn.closest('button, .el-button, [role="button"]') || btn);
            await new Promise((r) => setTimeout(r, 2500));
            break;
          }
        }
        const text = document.body?.innerText ?? '';
        return {
          data: {
            url: location.href,
            hasTable:
              text.includes('后台下载状态') ||
              text.includes('业务流水号') ||
              text.includes('附件下载'),
          },
        };
      }
      case 'cpmsHasExportButton': {
        const exportBtn = findExportButton();
        return {
          data: {
            found: !!exportBtn,
            text: exportBtn ? normalizeText(exportBtn) : null,
            url: location.href,
          },
        };
      }
      case 'cpmsClickExport': {
        const exportBtn = findExportButton();
        if (!exportBtn) return { error: 'export-button-not-found' };
        const clickTarget =
          exportBtn.closest('button, .el-button, [role="button"], a') || exportBtn;
        clickElement(clickTarget);
        return { data: { ok: true, text: normalizeText(clickTarget) } };
      }
      case 'cpmsClickDialogConfirm': {
        const okLabels = ['确定', '确认', 'OK', '知道了'];
        const matchOk = (el) => {
          const text = (el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim();
          return okLabels.some((label) => text === label || text.endsWith(label));
        };

        const dialogs = [
          ...document.querySelectorAll('.el-dialog, .el-message-box, .el-overlay-message-box, [role="dialog"]'),
        ];
        for (const dlg of dialogs) {
          const style = window.getComputedStyle(dlg);
          if (style.display === 'none' || style.visibility === 'hidden') continue;
          const btns = dlg.querySelectorAll(
            'button, .el-button, .el-message-box__btns button, .el-dialog__footer button, span',
          );
          const okBtn = [...btns].find(matchOk);
          if (okBtn) {
            (okBtn.closest('button, .el-button, [role="button"]') || okBtn).click();
            return { data: { ok: true, source: 'dialog' } };
          }
        }

        const overlays = [...document.querySelectorAll('.el-message-box__wrapper, .v-modal')].filter((el) => {
          const style = window.getComputedStyle(el);
          return style.display !== 'none' && style.visibility !== 'hidden';
        });
        for (const overlay of overlays) {
          const btns = overlay.querySelectorAll('button, .el-button');
          const okBtn = [...btns].find(matchOk);
          if (okBtn) {
            okBtn.click();
            return { data: { ok: true, source: 'overlay' } };
          }
        }

        const globalOk = [...document.querySelectorAll('button, .el-button, span, a')].filter((el) => {
          if (!matchOk(el)) return false;
          const rect = el.getBoundingClientRect();
          return rect.width > 0 && rect.height > 0;
        });
        if (globalOk.length > 0) {
          (globalOk[globalOk.length - 1].closest('button, .el-button, [role="button"]') || globalOk[globalOk.length - 1]).click();
          return { data: { ok: true, source: 'global' } };
        }
        return { error: 'confirm-button-not-found' };
      }
      case 'cpmsGetLatestSerial': {
        const rows = [...document.querySelectorAll('tr, .el-table__row, .ant-table-row')];
        for (const row of rows) {
          const text = row.innerText || '';
          const match = text.match(/\d{13,}/);
          if (match) return { data: { serial: match[0] } };
        }
        return { data: { serial: null } };
      }
      case 'cpmsExportRowStatus': {
        const serial = params.serialNumber || '';
        const rows = [...document.querySelectorAll('tr, .el-table__row, .ant-table-row')];
        const row = rows.find((r) => (r.innerText || '').includes(serial));
        if (!row) return { data: { found: false } };
        const text = row.innerText || '';
        return {
          data: {
            found: true,
            processing: text.includes('正在后台下载'),
            success: text.includes('后台下载成功'),
            rawStatus: text.split('\n').find((t) => t.includes('后台')) || text.slice(0, 120),
          },
        };
      }
      case 'cpmsResolveDownloadUrl': {
        const serial = params.serialNumber || '';
        const urls = [];
        const attempts = [];
        const apiDebug = [];
        const rows = [...document.querySelectorAll('tr, .el-table__row, .ant-table-row')];
        const row = rows.find((r) => (r.innerText || '').includes(serial));
        if (!row) return { error: '未找到流水号所在行: ' + serial };

        const html = row.innerHTML || '';
        const patterns = [
          /['"]([^'"]*downloadAttachment[^'"]*)['"]/gi,
          /['"]([^'"]*(?:download|export|attachment|annex|fileDownload)[^'"]*)['"]/gi,
        ];
        for (const re of patterns) {
          let m;
          while ((m = re.exec(html)) !== null) {
            if (m[1] && isLikelyDownloadUrl(m[1])) urls.push(m[1]);
          }
        }

        const links = [...row.querySelectorAll('a[href]')];
        for (const a of links) {
          if (a.href && isLikelyDownloadUrl(a.href)) urls.push(a.href);
        }

        const origin = window.location.origin;
        collectPerformanceUrls(urls);
        const authHeaders = collectAuthHeaders();
        const apiPaths = [
          '/cpms/mops/mops/attachmentDownload/v1/getAttachmentDownloadInfoList',
          '/cpms/mops/mops/v1/getAttachmentDownloadInfoList',
          '/pms/mops/mops/v1/getAttachmentDownloadInfoList',
          '/pms/cpms/mops/mops/v1/getAttachmentDownloadInfoList',
        ];
        const bodies = [
          { pageNum: 1, pageSize: 50 },
          { pageNum: 1, pageSize: 20 },
          { businessSerialNumber: serial },
          { serialNumber: serial },
          { businessFlowCode: serial },
          { pageNum: 1, pageSize: 20, businessSerialNumber: serial },
        ];

        let matchedApiRow = null;
        for (const path of apiPaths) {
          for (const body of bodies) {
            try {
              const res = await fetch(origin + path, {
                method: 'POST',
                headers: authHeaders,
                credentials: 'include',
                body: JSON.stringify(body),
              });
              const text = await res.text();
              apiDebug.push({
                path,
                status: res.status,
                bodyKeys: Object.keys(body),
                preview: text.slice(0, 400),
              });
              if (!res.ok) continue;
              let json;
              try {
                json = JSON.parse(text);
              } catch {
                continue;
              }
              extractUrlsFromJson(json, urls);
              const found = findListRowBySerial(json, serial);
              if (found) {
                matchedApiRow = found;
                attempts.push(...buildDownloadAttemptsFromRow(origin, found, serial));
              }
            } catch (err) {
              apiDebug.push({ path, bodyKeys: Object.keys(body), error: String(err) });
            }
          }
        }

        if (!matchedApiRow) {
          attempts.push(...buildDownloadAttemptsFromRow(origin, {}, serial));
        }

        for (const att of attempts) {
          if (att.method === 'GET' && isLikelyDownloadUrl(att.url)) urls.push(att.url);
        }

        const best = pickBestDownloadUrl(urls);
        return {
          data: {
            urls: [...new Set(urls)].slice(0, 20),
            best,
            attempts: attempts.slice(0, 30),
            authHeaders: authHeaders,
            rowPreview: matchedApiRow
              ? JSON.stringify(matchedApiRow).slice(0, 600)
              : null,
            apiDebug: apiDebug.slice(0, 8),
          },
        };
      }
      case 'cpmsApiProbe': {
        const serial = params.serialNumber || '';
        const origin = window.location.origin;
        const authHeaders = collectAuthHeaders();
        const apiPaths = [
          '/cpms/mops/mops/v1/getAttachmentDownloadInfoList',
          '/pms/mops/mops/v1/getAttachmentDownloadInfoList',
        ];
        const probes = [];
        for (const path of apiPaths) {
          try {
            const res = await fetch(origin + path, {
              method: 'POST',
              headers: authHeaders,
              credentials: 'include',
              body: JSON.stringify({ pageNum: 1, pageSize: 50 }),
            });
            const text = await res.text();
            let row = null;
            try {
              row = findListRowBySerial(JSON.parse(text), serial);
            } catch {
              /* ignore */
            }
            probes.push({
              path,
              status: res.status,
              preview: text.slice(0, 1200),
              row: row ? JSON.stringify(row).slice(0, 800) : null,
            });
          } catch (err) {
            probes.push({ path, error: String(err) });
          }
        }
        return { data: { serial, probes } };
      }
      case 'cpmsDiagRow': {
        const serial = params.serialNumber || '';
        const rows = [...document.querySelectorAll('tr, .el-table__row, .ant-table-row')];
        const row = rows.find((r) => (r.innerText || '').includes(serial));
        if (!row) return { data: { found: false, rowCount: rows.length } };

        // Collect all clickable elements in the row
        const elements = [];
        for (const el of row.querySelectorAll('a, button, span, .el-button, .el-link, [role="button"], div, img')) {
          const tag = el.tagName;
          const text = (el.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 60);
          const cls = (el.className && typeof el.className === 'string') ? el.className.slice(0, 80) : '';
          const href = el.getAttribute('href') || '';
          const onclick = el.getAttribute('onclick') || el.getAttribute('@click') || '';
          const dataUrl = el.getAttribute('data-url') || el.getAttribute('data-href') || '';
          if (text || href || onclick || dataUrl) {
            elements.push({ tag, text, cls: cls.slice(0, 60), href: href.slice(0, 100), onclick: onclick.slice(0, 60), dataUrl: dataUrl.slice(0, 100) });
          }
        }
        const html = row.innerHTML.slice(0, 2000);
        return { data: { found: true, innerText: row.innerText.slice(0, 500), elementCount: elements.length, elements: elements.slice(0, 20), htmlPreview: html } };
      }
      case 'cpmsCaptureDownloadOnClick': {
        const serial = params.serialNumber || '';
        const found = findCpmsDownloadTarget(serial);
        if (found.error) return { error: found.error };
        const { target, row } = found;

        const state = {
          authHeaders: collectAuthHeaders(),
          requests: [],
          urls: [],
          installed: false,
        };
        installNetworkCapture(state);
        clickElement(target);
        await new Promise((r) => setTimeout(r, 12000));
        collectPerformanceUrls(state.urls);

        const origin = window.location.origin;
        let matchedRow = null;
        const capturedResponses = state.requests
          .filter((r) => r.phase === 'response' && r.bodyPreview)
          .map((r) => ({ url: r.url, status: r.status, body: r.bodyPreview }));

        for (const resp of capturedResponses) {
          try {
            const json = JSON.parse(resp.body);
            const rowHit = findListRowBySerial(json, serial);
            if (rowHit) {
              matchedRow = rowHit;
              break;
            }
          } catch {
            /* ignore */
          }
        }

        if (!matchedRow && row) {
          const html = row.innerHTML || '';
          const fileIdMatch = html.match(/"fileId"\s*:\s*"?(\d+)"?/i);
          const fileNameMatch = html.match(/"fileName"\s*:\s*"([^"]+)"/i);
          const recordIdMatch = html.match(/"id"\s*:\s*"?(\d+)"?/);
          if (fileIdMatch) {
            matchedRow = {
              fileId: fileIdMatch[1],
              businessCode: serial,
              fileName: fileNameMatch?.[1] || null,
            };
            if (recordIdMatch) matchedRow.id = recordIdMatch[1];
          }
        }

        if (!matchedRow && (state.authHeaders.Authorization || state.authHeaders.token)) {
          const apiPaths = [
            '/cpms/mops/mops/attachmentDownload/v1/getAttachmentDownloadInfoList',
            '/cpms/mops/mops/v1/getAttachmentDownloadInfoList',
            '/pms/mops/mops/v1/getAttachmentDownloadInfoList',
          ];
          const bodies = [
            { pageNum: 1, pageSize: 50, businessSerialNumber: serial },
            { pageNum: 1, pageSize: 20, businessSerialNumber: serial },
            { pageNum: 1, pageSize: 50 },
            { businessSerialNumber: serial },
            { serialNumber: serial },
            { businessFlowCode: serial },
          ];
          for (const listPath of apiPaths) {
            for (const body of bodies) {
              try {
                const res = await state.origFetch(origin + listPath, {
                  method: 'POST',
                  headers: {
                    ...state.authHeaders,
                    'Content-Type': 'application/json;charset=utf-8',
                  },
                  credentials: 'include',
                  body: JSON.stringify(body),
                });
                const text = await res.text();
                if (!res.ok) continue;
                const json = JSON.parse(text);
                const rowHit = findListRowBySerial(json, serial);
                if (rowHit) {
                  matchedRow = rowHit;
                  break;
                }
              } catch {
                /* ignore */
              }
            }
            if (matchedRow) break;
          }
        }

        restoreNetworkCapture(state);

        const attempts = buildDownloadAttemptsFromRow(
          origin,
          matchedRow || {},
          serial,
        );

        const replayCandidates = [];
        for (const req of state.requests) {
          if (req.phase !== 'request') continue;
          const url = req.url || '';
          if (!/download|fileserver|attachment|export|annex/i.test(url)) continue;
          if (/getAttachmentDownloadInfoList|operationLog|refreshToken/i.test(url)) continue;
          let body = null;
          if (req.body && req.body !== '[body]' && req.body !== '[binary]') {
            try {
              body = JSON.parse(req.body);
            } catch {
              body = req.body;
            }
          }
          replayCandidates.push({
            url,
            method: req.method || 'GET',
            body,
            headers: req.headers || null,
            transport: req.transport,
          });
        }

        const url = pickBestDownloadUrl(state.urls);
        return {
          data: {
            ok: true,
            clicked: true,
            url,
            capturedUrls: [...new Set(state.urls)].slice(0, 30),
            authHeaders: state.authHeaders,
            requests: state.requests.slice(0, 50),
            replayCandidates: replayCandidates.slice(0, 20),
            attempts: attempts.slice(0, 30),
            rowPreview: matchedRow ? JSON.stringify(matchedRow).slice(0, 800) : null,
          },
        };
      }
      case 'cpmsSniffDownloadUrlOnClick': {
        const serial = params.serialNumber || '';
        const rows = [...document.querySelectorAll('tr, .el-table__row, .ant-table-row')];
        const row = rows.find((r) => (r.innerText || '').includes(serial));
        if (!row) return { error: '未找到流水号所在行: ' + serial };
        if (!(row.innerText || '').includes('后台下载成功')) {
          return { error: '后台尚未处理完成' };
        }
        row.scrollIntoView({ block: 'center', behavior: 'instant' });
        const clickables = [...row.querySelectorAll('button, a, span, .el-button, .el-link, [role="button"], i, .el-icon, [class*="download"], [class*="icon"]')];
        let download = clickables.find((el) => normalizeText(el) === '下载');
        if (!download) {
          download = clickables.find((el) => normalizeText(el).includes('下载'));
        }
        if (!download) {
          download = clickables.find((el) => {
            const cls = (el.className && typeof el.className === 'string') ? el.className : '';
            return cls.includes('download') || cls.includes('Download') || cls.includes('el-icon-download');
          });
        }
        if (!download) {
          const cells = row.querySelectorAll('td, .el-table__cell, .cell');
          if (cells.length >= 2) {
            const actionCell = cells[1];
            const actionClickables = actionCell.querySelectorAll('a, button, span, .el-button, .el-link, [role="button"], i, [class*="icon"]');
            if (actionClickables.length > 0) {
              download = actionClickables[0];
            }
          }
        }
        if (!download) return { error: '该行未找到下载按钮' };
        const target = download.closest('button, a, .el-button, .el-link, [role="button"]') || download;

        const capturedUrls = [];
        const capturedHeaders = {};
        const capturedResponses = [];

        function mergeHeaders(init) {
          if (!init?.headers) return;
          const hdrs = init.headers;
          if (hdrs instanceof Headers) {
            hdrs.forEach((v, k) => {
              if (isHttpHeaderByteString(v)) capturedHeaders[k] = v;
            });
          } else if (typeof hdrs === 'object') {
            for (const [k, v] of Object.entries(hdrs)) {
              if (typeof v === 'string' && isHttpHeaderByteString(v)) capturedHeaders[k] = v;
            }
          }
        }

        collectPerformanceUrls(capturedUrls);
        const origFetch = window.fetch;
        window.fetch = function (...args) {
          const reqUrl = typeof args[0] === 'string' ? args[0] : args[0]?.url;
          const init = args[1] || {};
          mergeHeaders(init);
          if (reqUrl && !/operationLog\/add/i.test(reqUrl)) capturedUrls.push(String(reqUrl));
          return origFetch.apply(this, args).then(async (res) => {
            const ct = res?.headers?.get?.('content-type') || '';
            const fullUrl = res?.url || reqUrl || '';
            if (fullUrl) capturedUrls.push(String(fullUrl));
            if (/zip|octet-stream|spreadsheet|excel|vnd\./i.test(ct)) {
              capturedUrls.unshift(fullUrl);
            }
            if (/attachmentDownload|downloadAttachment|refreshToken|fileserver|formDownload/i.test(fullUrl)) {
              const text = await res.clone().text().catch(() => '');
              capturedResponses.push({
                url: String(fullUrl),
                status: res.status,
                body: text.slice(0, 8000),
              });
              try {
                extractUrlsFromJson(JSON.parse(text), capturedUrls);
              } catch {
                /* ignore */
              }
            }
            return res;
          });
        };
        const OrigXHR = window.XMLHttpRequest;
        function HookedXHR() {
          const xhr = new OrigXHR();
          const origOpen = xhr.open;
          const origSetHeader = xhr.setRequestHeader;
          xhr.open = function (method, url, ...rest) {
            xhr.__reqUrl = url;
            if (url && !/operationLog\/add/i.test(String(url))) capturedUrls.push(String(url));
            return origOpen.call(xhr, method, url, ...rest);
          };
          xhr.setRequestHeader = function (name, value) {
            if (typeof value === 'string' && isHttpHeaderByteString(value)) {
              capturedHeaders[name] = value;
            }
            return origSetHeader.call(xhr, name, value);
          };
          return xhr;
        }
        HookedXHR.prototype = OrigXHR.prototype;
        window.XMLHttpRequest = HookedXHR;
        const origOpen = window.open;
        window.open = function (url, ...rest) {
          if (typeof url === 'string') capturedUrls.push(url);
          return origOpen.call(window, url, ...rest);
        };

        clickElement(target);
        await new Promise((r) => setTimeout(r, 12000));
        collectPerformanceUrls(capturedUrls);
        window.open = origOpen;
        window.fetch = origFetch;
        window.XMLHttpRequest = OrigXHR;

        const origin = window.location.origin;
        let matchedRow = null;

        async function tryFetchListWithAuth() {
          if (!capturedHeaders.Authorization && !capturedHeaders.token) return;
          const listPath = '/cpms/mops/mops/attachmentDownload/v1/getAttachmentDownloadInfoList';
          const bodies = [
            { pageNum: 1, pageSize: 50, businessSerialNumber: serial },
            { pageNum: 1, pageSize: 50 },
            { businessSerialNumber: serial },
          ];
          for (const body of bodies) {
            try {
              const res = await origFetch(origin + listPath, {
                method: 'POST',
                headers: {
                  ...capturedHeaders,
                  'Content-Type': 'application/json;charset=utf-8',
                },
                credentials: 'include',
                body: JSON.stringify(body),
              });
              const text = await res.text();
              capturedResponses.push({
                url: origin + listPath,
                status: res.status,
                body: text.slice(0, 8000),
              });
              if (!res.ok) continue;
              const json = JSON.parse(text);
              const found = findListRowBySerial(json, serial);
              if (found) return found;
            } catch (err) {
              capturedResponses.push({
                url: origin + listPath,
                status: 0,
                body: String(err),
              });
            }
          }
          return null;
        }

        for (const resp of capturedResponses) {
          try {
            const json = JSON.parse(resp.body);
            const found = findListRowBySerial(json, serial);
            if (found) {
              matchedRow = found;
              break;
            }
          } catch {
            /* ignore */
          }
        }

        if (!matchedRow) {
          matchedRow = await tryFetchListWithAuth();
        }

        const sniffAttempts = matchedRow
          ? buildDownloadAttemptsFromRow(origin, matchedRow, serial)
          : [];

        const url = pickBestDownloadUrl(capturedUrls);
        return {
          data: {
            url,
            captured: [...new Set(capturedUrls)].slice(0, 20),
            authHeaders: capturedHeaders,
            responses: capturedResponses.slice(0, 8),
            attempts: sniffAttempts.slice(0, 30),
            rowPreview: matchedRow ? JSON.stringify(matchedRow).slice(0, 600) : null,
            method: url ? 'network-sniff' : sniffAttempts.length ? 'sniff-row' : 'sniff-no-url',
          },
        };
      }
      case 'cpmsGetDownloadUrl': {
        const serial = params.serialNumber || '';
        const rows = [...document.querySelectorAll('tr, .el-table__row, .ant-table-row')];
        const row = rows.find((r) => (r.innerText || '').includes(serial));
        if (!row) return { error: '未找到流水号所在行: ' + serial };
        const links = [...row.querySelectorAll('a[href]')];
        const direct = links.find((a) => a.href && !a.href.startsWith('javascript:') && isLikelyDownloadUrl(a.href));
        if (direct) return { data: { url: direct.href } };

        const html = row.innerHTML || '';
        const apiMatch = html.match(/['"]([^'"]*(?:download|export|attachment|annex|fileDownload)[^'"]*)['"]/i);
        if (apiMatch?.[1]) {
          return { data: { url: apiMatch[1] } };
        }

        const clickables = [...row.querySelectorAll('a, button, .el-link, .el-button, span')];
        const downloadEl = clickables.find((el) => {
          const text = normalizeText(el);
          return text === '下载' || text.includes('下载');
        });
        if (downloadEl) {
          for (const attr of ['data-url', 'data-href', 'data-path', 'href']) {
            const value =
              downloadEl.getAttribute?.(attr) ||
              downloadEl.closest('a, .el-link, .el-button')?.getAttribute?.(attr);
            if (value && !value.startsWith('javascript:') && isLikelyDownloadUrl(value)) {
              return { data: { url: value } };
            }
          }
        }

        return { data: { url: null } };
      }
      case 'cpmsPageContextBlobDownload': {
        const serial = params.serialNumber || '';
        const origin = window.location.origin;
        const headers = collectAuthHeaders();
        headers.Accept = 'application/json, text/plain, */*';
        headers['Content-Type'] = 'application/json;charset=utf-8';

        async function fetchListRow() {
          const listPath = '/cpms/mops/mops/attachmentDownload/v1/getAttachmentDownloadInfoList';
          const bodies = [
            { pageNum: 1, pageSize: 50, businessSerialNumber: serial },
            { pageNum: 1, pageSize: 50 },
          ];
          for (const body of bodies) {
            const res = await fetch(origin + listPath, {
              method: 'POST',
              headers,
              credentials: 'include',
              body: JSON.stringify(body),
            });
            if (!res.ok) continue;
            const json = await res.json();
            const found = findListRowBySerial(json, serial);
            if (found) return found;
          }
          return null;
        }

        function saveBlob(blob, fileName) {
          const blobUrl = URL.createObjectURL(blob);
          const a = document.createElement('a');
          a.href = blobUrl;
          a.download = fileName || serial + '.zip';
          a.style.display = 'none';
          document.body.appendChild(a);
          a.click();
          a.remove();
          setTimeout(() => URL.revokeObjectURL(blobUrl), 120000);
        }

        const matched = await fetchListRow();
        if (!matched) return { error: '列表 API 未找到流水号行' };

        const fileId = matched.fileId != null ? String(matched.fileId) : '';
        const recordId = matched.id != null ? String(matched.id) : '';
        const businessCode = matched.businessCode || serial;
        const fileName = matched.fileName || `${serial}.zip`;
        const attempts = [];
        if (fileId) {
          const formBodies = [
            { fileIds: [fileId] },
            { fileIds: fileId },
            { fileId },
            { fileId, fileName },
            { ids: [fileId] },
            { fileIds: [fileId], microserviceName: matched.microserviceName },
          ];
          for (const body of formBodies) {
            attempts.push({
              url: origin + '/cpms/file/fileserver/special/formDownloadByFileIds',
              body,
            });
          }
          attempts.push({
            url: origin + '/cpms/mops/mops/attachmentDownload/v1/downloadAttachment',
            body: matched,
          });
          attempts.push({
            url: origin + '/cpms/mops/mops/attachmentDownload/v1/downloadAttachment',
            body: { id: recordId, fileId, businessCode },
          });
          attempts.push({
            url: origin + '/cpms/mops/mops/attachmentDownload/v1/downloadAttachment',
            body: { id: recordId },
          });
        }

        const errors = [];
        for (const att of attempts) {
          try {
            const res = await fetch(att.url, {
              method: 'POST',
              headers,
              credentials: 'include',
              body: JSON.stringify(att.body),
            });
            const ct = res.headers.get('content-type') || '';
            if (!res.ok) {
              const errText = await res.text().catch(() => '');
              errors.push(`HTTP ${res.status} ${att.url}: ${errText.slice(0, 120)}`);
              continue;
            }
            if (/json/i.test(ct)) {
              const text = await res.text();
              errors.push(`JSON ${att.url}: ${text.slice(0, 120)}`);
              try {
                const json = JSON.parse(text);
                const urls = [];
                extractUrlsFromJson(json, urls);
                const nestedUrl = pickBestDownloadUrl(urls);
                if (nestedUrl) {
                  const abs = nestedUrl.startsWith('http')
                    ? nestedUrl
                    : origin + (nestedUrl.startsWith('/') ? nestedUrl : `/${nestedUrl}`);
                  const res2 = await fetch(abs, { method: 'GET', headers, credentials: 'include' });
                  if (res2.ok) {
                    const blob2 = await res2.blob();
                    if (blob2.size > 1000) {
                      saveBlob(blob2, fileName);
                      return {
                        data: {
                          ok: true,
                          method: 'page-nested-blob',
                          url: abs,
                          size: blob2.size,
                          filename: fileName,
                        },
                      };
                    }
                  }
                }
              } catch {
                /* ignore */
              }
              continue;
            }
            const blob = await res.blob();
            if (blob.size > 1000) {
              saveBlob(blob, fileName);
              return {
                data: {
                  ok: true,
                  method: 'page-blob',
                  url: att.url,
                  size: blob.size,
                  filename: fileName,
                },
              };
            }
            errors.push(`small body ${blob.size} ${att.url}`);
          } catch (err) {
            errors.push(`${att.url}: ${err.message || err}`);
          }
        }

        if (fileId) {
          const getUrls = [
            `${origin}/cpms/file/fileserver/special/formDownloadByFileIds?fileIds=${encodeURIComponent(fileId)}`,
            `${origin}/cpms/file/fileserver/special/formDownloadByFileIds?fileId=${encodeURIComponent(fileId)}`,
            `${origin}/cpms/file/fileserver/download?fileId=${encodeURIComponent(fileId)}`,
          ];
          for (const getUrl of getUrls) {
            const a = document.createElement('a');
            a.href = getUrl;
            a.target = '_blank';
            a.rel = 'noopener';
            a.style.display = 'none';
            document.body.appendChild(a);
            a.click();
            a.remove();
          }
          return {
            data: {
              ok: true,
              method: 'page-get-navigate',
              triggered: getUrls,
              fileId,
              filename: fileName,
            },
          };
        }

        return { data: { ok: false, errors: errors.slice(0, 8), fileId, recordId } };
      }
      case 'cpmsClickDownload': {
        const serial = params.serialNumber || '';
        const rows = [...document.querySelectorAll('tr, .el-table__row, .ant-table-row')];
        const row = rows.find((r) => (r.innerText || '').includes(serial));
        if (!row) return { error: '未找到流水号所在行: ' + serial };
        const text = row.innerText || '';
        if (!text.includes('后台下载成功')) {
          return { error: '后台尚未处理完成' };
        }
        row.scrollIntoView({ block: 'center', behavior: 'instant' });
        const clickables = [...row.querySelectorAll('button, a, span, .el-button, .el-link, [role="button"], i, .el-icon, [class*="download"], [class*="icon"]')];
        let download = clickables.find((el) => normalizeText(el) === '下载');
        if (!download) {
          download = clickables.find((el) => normalizeText(el).includes('下载'));
        }
        // Also look for download icon buttons (no text, just icon)
        if (!download) {
          download = clickables.find((el) => {
            const cls = (el.className && typeof el.className === 'string') ? el.className : '';
            return cls.includes('download') || cls.includes('Download') || cls.includes('el-icon-download');
          });
        }
        // Also look for any element in the first cell that might be an action button
        if (!download) {
          const cells = row.querySelectorAll('td, .el-table__cell, .cell');
          if (cells.length >= 2) {
            // "操作" is typically the 2nd cell (index 1)
            const actionCell = cells[1];
            const actionClickables = actionCell.querySelectorAll('a, button, span, .el-button, .el-link, [role="button"], i, [class*="icon"]');
            if (actionClickables.length > 0) {
              download = actionClickables[0];
            }
          }
        }
        if (!download) return { error: '该行未找到下载按钮' };
        const target = download.closest('button, a, .el-button, .el-link, [role="button"]') || download;

        const capturedUrls = [];
        const origFetch = window.fetch;
        window.fetch = function (...args) {
          const reqUrl = typeof args[0] === 'string' ? args[0] : args[0]?.url;
          if (reqUrl && isLikelyDownloadUrl(reqUrl)) capturedUrls.push(reqUrl);
          return origFetch.apply(this, args).then((res) => {
            const ct = res?.headers?.get?.('content-type') || '';
            if (res?.url && (/zip|octet-stream|spreadsheet|excel|vnd\./i.test(ct) || isLikelyDownloadUrl(res.url))) {
              capturedUrls.unshift(res.url);
            }
            return res;
          });
        };

        const OrigXHR = window.XMLHttpRequest;
        function HookedXHR() {
          const xhr = new OrigXHR();
          const origOpen = xhr.open;
          xhr.open = function (method, url, ...rest) {
            if (url && isLikelyDownloadUrl(String(url))) capturedUrls.push(String(url));
            return origOpen.call(xhr, method, url, ...rest);
          };
          return xhr;
        }
        HookedXHR.prototype = OrigXHR.prototype;
        window.XMLHttpRequest = HookedXHR;

        clickElement(target);
        await new Promise((r) => setTimeout(r, 6000));
        window.fetch = origFetch;
        window.XMLHttpRequest = OrigXHR;

        const downloadUrl = pickBestDownloadUrl(capturedUrls);
        if (downloadUrl) {
          return { data: { ok: true, url: downloadUrl, method: 'network', captured: capturedUrls.slice(0, 8) } };
        }
        if (target.href && !target.href.startsWith('javascript:') && isLikelyDownloadUrl(target.href)) {
          return { data: { ok: true, url: target.href, method: 'click' } };
        }
        return { data: { ok: true, method: 'click-only', captured: capturedUrls.slice(0, 8) } };
      }
      case 'cpmsFirstReadyRow': {
        const rows = [...document.querySelectorAll('tr, .el-table__row, .ant-table-row')];
        for (const row of rows) {
          const text = row.innerText || '';
          if (!text || text.includes('序号') && text.includes('业务流水号')) continue;
          if (text.includes('正在后台下载') || text.includes('后台下载成功')) {
            return {
              data: {
                found: true,
                processing: text.includes('正在后台下载'),
                success: text.includes('后台下载成功'),
                rawStatus: text.split('\n').find((t) => t.includes('后台')) || text.slice(0, 120),
              },
            };
          }
        }
        return { data: { found: false } };
      }
      case 'cpmsListButtons': {
        const buttons = collectClickables(document);
        const texts = buttons
          .map((el) => normalizeText(el))
          .filter((t) => t.length > 0 && t.length < 30);
        return { data: { buttons: [...new Set(texts)].slice(0, 40) } };
      }
      case 'cpmsClickFirstReadyDownload': {
        const rows = [...document.querySelectorAll('tr, .el-table__row, .ant-table-row')];
        for (const row of rows) {
          const text = row.innerText || '';
          if (!text.includes('后台下载成功')) continue;
          const clickables = [...row.querySelectorAll('button, a, span, .el-button, .el-link')];
          const download = clickables.find(
            (el) => normalizeText(el) === '下载',
          );
          if (download) {
            download.click();
            return { data: { ok: true } };
          }
        }
        return { error: 'no-ready-download-row' };
      }
      default:
        throw new Error(`Unknown content action: ${action}`);
    }
  } catch (err) {
    return { error: err.message || String(err) };
  }
}

async function takeScreenshot(params) {
  const tabId = await resolveTabId(params.tabId);
  await chrome.tabs.update(tabId, { active: true });
  const dataUrl = await chrome.tabs.captureVisibleTab(null, { format: params.format || 'png' });
  return { dataUrl };
}

let autoAcceptSessionUntil = 0;
let autoAcceptSessionStarted = 0;
const extensionDownloadIds = new Set();
const pendingDownloadWaiters = [];

function isAutoAcceptSessionActive() {
  return Date.now() < autoAcceptSessionUntil;
}

function looksLikeCpmsDownload(item) {
  const name = item.filename || item.url || '';
  return (
    name.includes('项目明细') ||
    name.includes('加工完成') ||
    /\.zip(\?|$)/i.test(name) ||
    /cpms|pms/i.test(name)
  );
}

function shouldAutoAcceptDownload(item) {
  if (!isAutoAcceptSessionActive()) return false;
  if (extensionDownloadIds.has(item.id)) return true;
  if (!item.startTime || item.startTime < autoAcceptSessionStarted - 2000) return false;
  return looksLikeCpmsDownload(item);
}

function setupDownloadListeners() {
  if (setupDownloadListeners.initialized) return;
  setupDownloadListeners.initialized = true;

  // acceptDanger 在 MV3 service worker 中不可用，且语义是“再弹确认框”而非自动保留。
  // 下载统一走 blob 旁路（blob: URL），不在此监听危险下载。

  chrome.downloads.onChanged.addListener(async (delta) => {
    if (!delta.id) return;

    if (delta.state?.current === 'complete' || delta.state?.current === 'interrupted') {
      const [item] = await chrome.downloads.search({ id: delta.id });
      if (!item) return;

      for (const waiter of [...pendingDownloadWaiters]) {
        if (waiter.matches(item)) {
          pendingDownloadWaiters.splice(pendingDownloadWaiters.indexOf(waiter), 1);
          clearTimeout(waiter.timer);
          if (item.state === 'complete') {
            waiter.resolve({
              id: item.id,
              filename: item.filename,
              url: item.url,
              totalBytes: item.totalBytes,
              mime: item.mime,
            });
          } else {
            waiter.reject(new Error(`Download interrupted: ${item.error || 'unknown'}`));
          }
          break;
        }
      }
    }
  });
}

function enableAutoAcceptDownloads(params = {}) {
  setupDownloadListeners();
  const durationMs = params.durationMs || 20 * 60 * 1000;
  autoAcceptSessionStarted = Date.now();
  autoAcceptSessionUntil = autoAcceptSessionStarted + durationMs;
  return { enabled: true, until: autoAcceptSessionUntil };
}

function disableAutoAcceptDownloads() {
  autoAcceptSessionUntil = 0;
  autoAcceptSessionStarted = 0;
  extensionDownloadIds.clear();
  return { enabled: false };
}

async function acceptPendingDownloads(params = {}) {
  return acceptDangerousDownloadsViaPage(params);
}

/** 在可见页面上下文中调用 acceptDanger（MV3 service worker 中无效） */
async function acceptDangerousDownloadsViaPage(params = {}) {
  const sinceMs = params.sinceMs || Date.now() - 300000;
  const serialHint = params.serialNumber || '';
  const items = await chrome.downloads.search({
    orderBy: ['-startTime'],
    limit: 30,
  });

  const accepted = [];
  for (const item of items) {
    const fn = item.filename || '';
    const startedAt = new Date(item.startTime).getTime();
    if (startedAt < sinceMs - 10000) continue;
    if (item.state === 'complete') continue;

    const matches =
      !serialHint ||
      fn.includes(serialHint) ||
      fn.includes('项目明细') ||
      fn.includes('.zip');
    const needsAccept =
      item.danger === 'file' ||
      item.danger === 'url' ||
      item.danger === 'content' ||
      fn.includes('.crdownload') ||
      item.state === 'interrupted';

    if (!matches || !needsAccept) continue;

    try {
      const tab = await chrome.tabs.create({
        url: chrome.runtime.getURL(`accept-download.html?id=${item.id}`),
        active: false,
      });
      await new Promise((r) => setTimeout(r, 2500));
      try {
        await chrome.tabs.remove(tab.id);
      } catch {
        /* ignore */
      }
      accepted.push(item.id);
      console.log('[AutomationBridge] acceptDanger via page:', item.id, fn);
    } catch (err) {
      console.warn('[AutomationBridge] acceptDanger page failed:', err.message || err);
    }
  }

  return { accepted, count: accepted.length };
}

function trackExtensionDownload(id) {
  if (id != null) extensionDownloadIds.add(id);
}

function resolveAbsoluteUrl(url, baseUrl) {
  if (!url) return null;
  let fixed = url;
  if (/^https?:\/\//i.test(url)) {
    fixed = url.replace(/^(https?:\/\/[^/:]+):(\/)/i, '$1$2');
  }
  if (fixed.startsWith('http://') || fixed.startsWith('https://')) return fixed;
  try {
    return new URL(fixed, baseUrl).href;
  } catch {
    return fixed;
  }
}

function isLikelyDownloadUrl(url) {
  if (!url || typeof url !== 'string') return false;
  const lower = url.toLowerCase().replace(/:\//g, '/');
  if (
    /operationlog|operation\/log|\/add(?:\?|$)|getattachmentdownloadinfolist|infolist/i.test(
      lower,
    )
  ) {
    return false;
  }
  if (/\.(zip|xlsx|xls)(\?|$)/i.test(lower)) return true;
  if (
    /downloadattachment|\/download|exportfile|filedownload|getfile|downloadfile|file\/get|fileserver|formdownload/i.test(
      lower,
    )
  ) {
    return true;
  }
  return false;
}

function toAbsoluteDownloadUrl(url, baseUrl) {
  if (!url) return null;
  if (/^https?:\/\//i.test(url)) return url;
  const origin = new URL(baseUrl).origin;
  return origin + (url.startsWith('/') ? url : `/${url}`);
}

function collectDownloadUrlsFromJson(value, out) {
  if (!value) return;
  if (typeof value === 'string') {
    if (
      isLikelyDownloadUrl(value) ||
      /^https?:\/\//i.test(value) ||
      (/^\//.test(value) && /fileserver|formDownload|\/file\/|download/i.test(value)) ||
      /fileserver|formDownload|\/file\//i.test(value)
    ) {
      out.push(value);
    }
    return;
  }
  if (Array.isArray(value)) {
    value.forEach((v) => collectDownloadUrlsFromJson(v, out));
    return;
  }
  if (typeof value === 'object') {
    Object.values(value).forEach((v) => collectDownloadUrlsFromJson(v, out));
  }
}

function extractNestedDownloadUrl(json) {
  const urls = [];
  collectDownloadUrlsFromJson(json, urls);
  return (
    urls.find((u) => /\.(zip|xlsx|xls)(\?|$)/i.test(u)) ||
    urls.find((u) => /fileserver|formDownload|downloadAttachment/i.test(u)) ||
    urls[0] ||
    null
  );
}

function pickBestDownloadUrl(urls) {
  if (!Array.isArray(urls)) return null;
  return (
    urls.find((u) => u && /\.(zip|xlsx|xls)(\?|$)/i.test(u)) ||
    urls.find((u) => u && isLikelyDownloadUrl(u)) ||
    null
  );
}

function suggestFilename(url, response, serialNumber) {
  const disposition = response?.headers?.get?.('content-disposition') || '';
  const match = disposition.match(/filename\*?=(?:UTF-8''|")?([^";]+)/i);
  if (match) {
    try {
      return decodeURIComponent(match[1].trim());
    } catch {
      return match[1].trim();
    }
  }
  try {
    const path = new URL(url).pathname;
    const base = path.split('/').pop();
    if (base && base.includes('.')) return base;
  } catch {
    /* ignore */
  }
  return serialNumber
    ? `cpms-export-${serialNumber}.zip`
    : `cpms-export-${Date.now()}.zip`;
}

/** 用标签页 Cookie 拉取文件，再通过扩展 downloads API 保存，绕过页面「不安全下载」拦截 */
async function downloadWithSessionCookies(url, tabId, filename, options = {}, depth = 0) {
  if (!url) throw new Error('url is required');
  if (depth > 4) throw new Error('Download redirect depth exceeded');
  setupDownloadListeners();

  const tab = await chrome.tabs.get(tabId);
  const absoluteUrl = resolveAbsoluteUrl(url, tab.url || url);
  const cookieUrl = new URL(absoluteUrl).origin + '/';
  const cookies = await chrome.cookies.getAll({ url: cookieUrl });
  const cookieHeader = cookies.map((c) => `${c.name}=${c.value}`).join('; ');

  const method = (options.method || 'GET').toUpperCase();
  const headers = { ...(options.headers || {}) };
  if (cookieHeader) headers.Cookie = cookieHeader;
  let body;
  if (options.body != null) {
    if (!headers['Content-Type']) headers['Content-Type'] = 'application/json';
    body = typeof options.body === 'string' ? options.body : JSON.stringify(options.body);
  }

  const res = await fetch(absoluteUrl, {
    method,
    headers,
    credentials: 'include',
    body: method === 'GET' || method === 'HEAD' ? undefined : body,
  });
  if (!res.ok) {
    const errText = await res.text().catch(() => '');
    throw new Error(
      `Download fetch failed: ${res.status} ${res.statusText}${errText ? ` ${errText.slice(0, 120)}` : ''}`,
    );
  }

  const contentType = res.headers.get('content-type') || '';
  if (/json/i.test(contentType)) {
    const text = await res.text().catch(() => '');
    let nestedUrl = null;
    let json = null;
    try {
      json = JSON.parse(text);
      nestedUrl = extractNestedDownloadUrl(json);
    } catch {
      /* ignore */
    }
    if (nestedUrl) {
      nestedUrl = toAbsoluteDownloadUrl(nestedUrl, absoluteUrl);
      console.log('[AutomationBridge] following nested download URL:', nestedUrl.slice(0, 120));
      return downloadWithSessionCookies(
        nestedUrl,
        tabId,
        filename,
        { headers: options.headers, method: 'GET' },
        depth + 1,
      );
    }
    if (json?.code && json.code !== '000000') {
      throw new Error(`API error ${json.code}: ${json.message || text.slice(0, 120)}`);
    }
    throw new Error(`Download returned JSON without file URL: ${text.slice(0, 200)}`);
  }
  if (/text\/html/i.test(contentType)) {
    const errText = await res.text().catch(() => '');
    throw new Error(`Download returned HTML: ${errText.slice(0, 120)}`);
  }

  const buf = await res.arrayBuffer();
  if (!buf.byteLength) {
    throw new Error('Download returned empty body');
  }

  const mime = contentType || 'application/zip';
  const blob = new Blob([buf], { type: mime });
  const blobUrl = URL.createObjectURL(blob);
  const saveAs = filename || suggestFilename(absoluteUrl, res);

  try {
    const id = await chrome.downloads.download({
      url: blobUrl,
      filename: saveAs,
      saveAs: false,
      conflictAction: 'uniquify',
    });
    trackExtensionDownload(id);
    return {
      ok: true,
      id,
      filename: saveAs,
      size: buf.byteLength,
      method: method === 'GET' ? 'blob-bypass' : 'blob-bypass-post',
      sourceUrl: absoluteUrl,
    };
  } finally {
    setTimeout(() => URL.revokeObjectURL(blobUrl), 120000);
  }
}

async function startDownload(params = {}) {
  if (!params.url) throw new Error('url is required');
  const tabId = await resolveTabId(params.tabId);
  // 只走 blob 旁路。chrome.downloads.download({ url }) 会重新触发 Chrome 危险提示，
  // 而 acceptDanger 在 service worker 里无法生效，所以失败时直接抛错让上层兜底。
  return await downloadWithSessionCookies(params.url, tabId, params.filename);
}

async function tryBlobBypassForUrls(urls, tabId, tabUrl, serialNumber, authHeaders = {}) {
  const seen = new Set();
  for (const raw of urls) {
    if (!raw || seen.has(raw)) continue;
    seen.add(raw);
    const abs = resolveAbsoluteUrl(raw, tabUrl || '');
    if (!isLikelyDownloadUrl(abs)) continue;
    try {
      return await downloadWithSessionCookies(
        abs,
        tabId,
        suggestFilename(abs, null, serialNumber),
        { headers: authHeaders },
      );
    } catch (err) {
      console.warn('[AutomationBridge] blob-bypass failed:', abs, err.message || err);
    }
  }
  return null;
}

async function cpmsApiDownloadFile(params) {
  const tabId = await resolveTabId(params.tabId, params.recreateUrl);
  await chrome.tabs.update(tabId, { active: true });
  const tab = await chrome.tabs.get(tabId);
  const tabUrl = tab.url || '';
  const serialNumber = params.serialNumber || '';

  let resolved = null;
  try {
    resolved = await runInTab('cpmsResolveDownloadUrl', { ...params, tabId });
  } catch (err) {
    console.warn('[AutomationBridge] cpmsResolveDownloadUrl failed:', err.message || err);
  }

  const candidateUrls = [];
  if (resolved?.best) candidateUrls.push(resolved.best);
  if (Array.isArray(resolved?.urls)) candidateUrls.push(...resolved.urls);

  const urlSaved = await tryBlobBypassForUrls(candidateUrls, tabId, tabUrl, serialNumber);
  if (urlSaved) return { ...urlSaved, method: 'api-bypass-url' };

  const authHeaders = resolved?.authHeaders || {};
  const attempts = Array.isArray(resolved?.attempts) ? resolved.attempts : [];
  for (const att of attempts) {
    if (!att?.url) continue;
    try {
      const saved = await downloadWithSessionCookies(
        att.url,
        tabId,
        suggestFilename(att.url, null, serialNumber),
        { method: att.method || 'GET', body: att.body, headers: authHeaders },
      );
      return saved;
    } catch (err) {
      console.warn(
        '[AutomationBridge] api attempt failed:',
        att.method,
        att.url,
        err.message || err,
      );
    }
  }

  return {
    ok: false,
    method: 'failed',
    error: 'api-download-failed',
    attemptCount: attempts.length,
    rowPreview: resolved?.rowPreview || null,
  };
}

function scoreReplayAttempt(att) {
  const url = att?.url || '';
  if (att?.method === 'POST' && url.includes('formDownloadByFileIds')) return 100;
  if (att?.method === 'POST' && url.includes('downloadAttachment')) return 95;
  if (att?.method === 'POST' && /fileserver|attachment/i.test(url)) return 85;
  if (/\.zip(\?|$)/i.test(url)) return 80;
  if (isLikelyDownloadUrl(url)) return 70;
  return 10;
}

async function cpmsDownloadByClickPlanB(params = {}) {
  const tabId = await resolveTabId(params.tabId, params.recreateUrl);
  await chrome.tabs.update(tabId, { active: true });
  const tab = await chrome.tabs.get(tabId);
  const tabUrl = tab.url || '';
  const serialNumber = params.serialNumber || '';
  const sinceMs = params.sinceMs || Date.now();
  const attemptErrors = [];

  console.log('[AutomationBridge] PlanB: capture download on click for', serialNumber);
  const capture = await runInTab('cpmsCaptureDownloadOnClick', { ...params, tabId });
  if (!capture?.clicked) {
    throw new Error(capture?.error || 'cpmsCaptureDownloadOnClick failed');
  }

  const auth = capture.authHeaders || {};
  const replayList = [];
  const seenReplay = new Set();

  function addReplay(att) {
    if (!att?.url || seenReplay.has(att.url + (att.method || 'GET'))) return;
    seenReplay.add(att.url + (att.method || 'GET'));
    replayList.push(att);
  }

  if (Array.isArray(capture.replayCandidates)) {
    for (const c of capture.replayCandidates) addReplay(c);
  }
  if (Array.isArray(capture.attempts)) {
    for (const att of capture.attempts) addReplay({ ...att, headers: auth });
  }
  if (capture.url) {
    addReplay({ url: capture.url, method: 'GET', headers: auth });
  }

  if (replayList.length === 0) {
    try {
      const resolved = await runInTab('cpmsResolveDownloadUrl', { ...params, tabId });
      if (resolved?.authHeaders) Object.assign(auth, resolved.authHeaders);
      if (Array.isArray(resolved?.attempts)) {
        for (const att of resolved.attempts) addReplay({ ...att, headers: auth });
      }
      if (resolved?.best) addReplay({ url: resolved.best, method: 'GET', headers: auth });
      if (!capture.rowPreview && resolved?.rowPreview) capture.rowPreview = resolved.rowPreview;
    } catch (err) {
      console.warn('[AutomationBridge] PlanB resolve fallback failed:', err.message || err);
    }
  }

  replayList.sort((a, b) => scoreReplayAttempt(b) - scoreReplayAttempt(a));
  console.log('[AutomationBridge] PlanB replay candidates:', replayList.length);

  for (const att of replayList) {
    if (!att?.url) continue;
    const headers = { ...auth, ...(att.headers || {}) };
    try {
      const saved = await downloadWithSessionCookies(
        att.url,
        tabId,
        suggestFilename(att.url, null, serialNumber),
        { method: att.method || 'GET', body: att.body, headers },
      );
      if (saved?.ok) {
        return {
          ...saved,
          method: 'planB-replay',
          replayUrl: att.url,
          captureRequestCount: capture.requests?.length || 0,
        };
      }
    } catch (err) {
      const msg = `${att.method || 'GET'} ${att.url}: ${err.message || err}`;
      attemptErrors.push(msg);
      console.warn('[AutomationBridge] PlanB replay failed:', msg);
    }
  }

  const sniffed = await tryBlobBypassForUrls(
    capture.capturedUrls || [],
    tabId,
    tabUrl,
    serialNumber,
    auth,
  );
  if (sniffed) {
    return { ...sniffed, method: 'planB-blob-url' };
  }

  console.log('[AutomationBridge] PlanB: waiting for native Chrome download...');
  await new Promise((r) => setTimeout(r, 3000));

  for (let round = 0; round < 8; round++) {
    try {
      const accepted = await acceptDangerousDownloadsViaPage({
        sinceMs,
        serialNumber,
        tabId,
      });
      if (accepted.count > 0) {
        console.log('[AutomationBridge] PlanB: acceptDanger count=', accepted.count);
      }
    } catch (err) {
      console.warn('[AutomationBridge] PlanB acceptDanger:', err.message || err);
    }

    const items = await chrome.downloads.search({
      orderBy: ['-startTime'],
      limit: 15,
    });
    for (const item of items) {
      const fn = item.filename || '';
      const startedAt = new Date(item.startTime).getTime();
      if (startedAt < sinceMs - 10000) continue;
      const matches =
        fn.includes(serialNumber) ||
        fn.includes('项目明细') ||
        (fn.includes('.zip') && startedAt >= sinceMs - 5000);
      if (!matches) continue;
      if (item.state === 'complete' && fn && !fn.endsWith('.crdownload')) {
        return {
          ok: true,
          method: 'planB-chrome-download',
          filename: item.filename,
          id: item.id,
          url: item.url,
        };
      }
    }

    try {
      const dl = await waitForDownload({
        filenameContains: serialNumber || '项目明细',
        sinceMs,
        timeout: 15000,
      });
      return { ok: true, method: 'planB-wait-download', ...dl };
    } catch {
      /* retry */
    }

    await new Promise((r) => setTimeout(r, 5000));
  }

  try {
    const rescue = await cpmsRescueDangerousDownload({ ...params, tabId, sinceMs });
    if (rescue?.ok) {
      return { ...rescue, method: 'planB-rescue' };
    }
  } catch (err) {
    attemptErrors.push(`rescue: ${err.message || err}`);
  }

  return {
    ok: false,
    method: 'planB-failed',
    attemptErrors: attemptErrors.slice(0, 10),
    captureRequestCount: capture.requests?.length || 0,
    capturedUrlCount: capture.capturedUrls?.length || 0,
    hasAuth: Boolean(auth.Authorization),
    rowPreview: capture.rowPreview || null,
    replayCount: replayList.length,
  };
}

async function cpmsOpenDownloadUrl(params) {
  const fileId = params.fileId != null ? String(params.fileId) : '';
  if (!fileId) throw new Error('fileId is required');
  const origin = 'http://cpms.hq.cmcc';
  const urls = [
    `${origin}/cpms/file/fileserver/special/formDownloadByFileIds?fileIds=${encodeURIComponent(fileId)}`,
    `${origin}/cpms/file/fileserver/special/formDownloadByFileIds?fileId=${encodeURIComponent(fileId)}`,
    `${origin}/cpms/file/fileserver/download?fileId=${encodeURIComponent(fileId)}`,
  ];
  const tabId = await resolveTabId(params.tabId, params.recreateUrl);
  await chrome.tabs.update(tabId, { active: true });
  const opened = [];
  for (const url of urls) {
    try {
      await chrome.tabs.create({ url, active: false });
      opened.push(url);
    } catch (err) {
      console.warn('[AutomationBridge] open download url failed:', url, err.message || err);
    }
  }
  return { ok: opened.length > 0, urls: opened, fileId };
}

async function cpmsProbeDownloadAttempts(params) {
  const tabId = await resolveTabId(params.tabId, params.recreateUrl);
  const sniff = await runInTab('cpmsSniffDownloadUrlOnClick', { ...params, tabId });
  const auth = sniff?.authHeaders || {};
  let rowObj = null;
  if (sniff?.rowPreview) {
    try {
      rowObj = JSON.parse(sniff.rowPreview);
    } catch {
      /* truncated preview */
    }
  }
  const attempts = (Array.isArray(sniff?.attempts) ? sniff.attempts : []);
  if (rowObj) {
    attempts.unshift({
      url: 'http://cpms.hq.cmcc/cpms/mops/mops/attachmentDownload/v1/downloadAttachment',
      method: 'POST',
      body: rowObj,
    });
    if (rowObj.fileId) {
      attempts.unshift({
        url: 'http://cpms.hq.cmcc/cpms/file/fileserver/special/formDownloadByFileIds',
        method: 'POST',
        body: rowObj,
      });
    }
  }
  const sortedAttempts = attempts
    .sort((a, b) => {
      const score = (att) => {
        const url = att?.url || '';
        if (att?.method === 'POST' && url.includes('formDownloadByFileIds')) return 100;
        if (att?.method === 'POST' && url.includes('attachmentDownload/v1/downloadAttachment')) return 95;
        return 10;
      };
      return score(b) - score(a);
    })
    .slice(0, 6);
  const tab = await chrome.tabs.get(tabId);
  const cookieUrl = new URL(tab.url || 'http://cpms.hq.cmcc/').origin + '/';
  const cookies = await chrome.cookies.getAll({ url: cookieUrl });
  const cookieHeader = cookies.map((c) => `${c.name}=${c.value}`).join('; ');
  const probes = [];
  for (const att of sortedAttempts) {
    if (!att?.url) continue;
    const headers = { ...auth };
    if (cookieHeader) headers.Cookie = cookieHeader;
    if (att.body != null && !headers['Content-Type']) {
      headers['Content-Type'] = 'application/json';
    }
    try {
      const res = await fetch(att.url, {
        method: att.method || 'GET',
        headers,
        credentials: 'include',
        body:
          att.method === 'GET' || att.method === 'HEAD' || att.body == null
            ? undefined
            : JSON.stringify(att.body),
      });
      const ct = res.headers.get('content-type') || '';
      const buf = await res.arrayBuffer();
      const head = new Uint8Array(buf.slice(0, 4));
      const isZip = head[0] === 0x50 && head[1] === 0x4b;
      let preview = '';
      if (/json|text|html/i.test(ct) || buf.byteLength < 5000) {
        preview = new TextDecoder().decode(buf.slice(0, Math.min(buf.byteLength, 400)));
      }
      probes.push({
        method: att.method,
        url: att.url,
        status: res.status,
        contentType: ct,
        bytes: buf.byteLength,
        isZip,
        preview,
      });
    } catch (err) {
      probes.push({
        method: att.method,
        url: att.url,
        error: err.message || String(err),
      });
    }
  }
  return { probes, hasAuth: Boolean(auth.Authorization), attemptCount: attempts.length };
}

async function trySniffAuthenticatedDownload(params, tabId, tabUrl) {
  const attemptErrors = [];
  let sniffUrls = [];
  let sniffAuth = {};
  const sniff = await runInTab('cpmsSniffDownloadUrlOnClick', { ...params, tabId });
  if (sniff?.url) sniffUrls.push(sniff.url);
  if (Array.isArray(sniff?.captured)) sniffUrls.push(...sniff.captured);
  sniffAuth = sniff?.authHeaders || {};
  const sniffAttempts = (Array.isArray(sniff?.attempts) ? sniff.attempts : []).sort((a, b) => {
    const score = (att) => {
      const url = att?.url || '';
      if (att?.method === 'POST' && url.includes('formDownloadByFileIds')) return 100;
      if (att?.method === 'POST' && url.includes('attachmentDownload/v1/downloadAttachment')) return 95;
      if (att?.method === 'POST') return 80;
      return 10;
    };
    return score(b) - score(a);
  });
  for (const att of sniffAttempts) {
    if (!att?.url) continue;
    try {
      const saved = await downloadWithSessionCookies(
        att.url,
        tabId,
        suggestFilename(att.url, null, params.serialNumber),
        { method: att.method || 'GET', body: att.body, headers: sniffAuth },
      );
      return { ...saved, method: 'sniff-api', rowPreview: sniff?.rowPreview || null };
    } catch (err) {
      const msg = `${att.method || 'GET'} ${att.url}: ${err.message || err}`;
      attemptErrors.push(msg);
      console.warn('[AutomationBridge] sniff api attempt failed:', msg);
    }
  }

  const sniffed = await tryBlobBypassForUrls(
    sniffUrls,
    tabId,
    tabUrl,
    params.serialNumber,
    sniffAuth,
  );
  if (sniffed) return { ...sniffed, method: 'blob-bypass-sniff' };

  return {
    ok: false,
    sniffMethod: sniff?.method || null,
    rowPreview: sniff?.rowPreview || null,
    attemptErrors: attemptErrors.slice(0, 8),
    hasAuth: Boolean(sniffAuth.Authorization),
    attemptCount: sniffAttempts.length,
  };
}

async function cpmsDownloadBySerial(params) {
  const tabId = await resolveTabId(params.tabId, params.recreateUrl);
  await chrome.tabs.update(tabId, { active: true });
  const tab = await chrome.tabs.get(tabId);
  const tabUrl = tab.url || '';

  // 方案 0：页面上下文 fetch + blob 保存（与 SPA 同域同鉴权）
  try {
    const pageSaved = await runInTab('cpmsPageContextBlobDownload', { ...params, tabId });
    if (pageSaved?.ok && (pageSaved.filename || pageSaved.size > 1000)) {
      return { ...pageSaved, method: pageSaved.method || 'page-context-blob' };
    }
    if (pageSaved?.errors?.length) {
      console.warn('[AutomationBridge] page-context download errors:', pageSaved.errors);
    }
  } catch (err) {
    console.warn('[AutomationBridge] page-context download failed:', err.message || err);
  }

  // 方案 A：嗅探 Bearer + fileId，带鉴权 POST 下载（diag 已验证可拿到 row）
  let sniffMeta = null;
  try {
    const sniffSaved = await trySniffAuthenticatedDownload(params, tabId, tabUrl);
    if (sniffSaved?.ok || sniffSaved?.filename || sniffSaved?.id) {
      return sniffSaved;
    }
    sniffMeta = sniffSaved;
    if (sniffSaved?.attemptCount > 0) {
      console.warn('[AutomationBridge] sniff download attempts exhausted:', sniffSaved);
    }
  } catch (err) {
    console.warn('[AutomationBridge] sniff-first failed:', err.message || err);
  }

  // 方案 B：API 直连（含 POST downloadAttachment）→ blob 保存
  try {
    const apiSaved = await cpmsApiDownloadFile({ ...params, tabId });
    if (apiSaved?.ok && (apiSaved.filename || apiSaved.id)) {
      return apiSaved;
    }
  } catch (err) {
    console.warn('[AutomationBridge] cpmsApiDownloadFile failed:', err.message || err);
  }

  // 被动解析 URL → fetch+blob
  const candidateUrls = [];
  try {
    const resolved = await runInTab('cpmsResolveDownloadUrl', { ...params, tabId });
    if (resolved?.best) candidateUrls.push(resolved.best);
    if (Array.isArray(resolved?.urls)) candidateUrls.push(...resolved.urls);
  } catch (err) {
    console.warn('[AutomationBridge] cpmsResolveDownloadUrl failed:', err.message || err);
  }

  try {
    const legacy = await runInTab('cpmsGetDownloadUrl', { ...params, tabId });
    if (legacy?.url) candidateUrls.push(legacy.url);
  } catch {
    /* ignore */
  }

  const saved = await tryBlobBypassForUrls(candidateUrls, tabId, tabUrl, params.serialNumber);
  if (saved) return saved;

  // 点击触发 Chrome 下载 → 从 downloads API 救援被拦截的危险文件
  console.warn('[AutomationBridge] sniff failed, click + rescue dangerous download...');
  try {
    await runInTab('cpmsClickDownload', { ...params, tabId });
    await new Promise((r) => setTimeout(r, 4000));
    const rescue = await cpmsRescueDangerousDownload({
      ...params,
      tabId,
      sinceMs: Date.now() - 30000,
    });
    if (rescue?.ok) return { ...rescue, method: 'click-rescue' };
  } catch (err) {
    console.warn('[AutomationBridge] click+rescue failed:', err.message || err);
  }

  return {
    ok: false,
    method: 'failed',
    error: 'no-download-url',
    candidates: candidateUrls.slice(0, 12),
    attemptErrors: sniffMeta?.attemptErrors || [],
    hasAuth: sniffMeta?.hasAuth,
    rowPreview: sniffMeta?.rowPreview || null,
  };
}

/**
 * Rescues a dangerous download blocked by Chrome:
 * 1. Finds the blocked download via chrome.downloads.search()
 * 2. Gets its URL
 * 3. Cancels the blocked download
 * 4. Re-downloads via fetch + blob URL (bypasses dangerous file check)
 */
async function cpmsRescueDangerousDownload(params = {}) {
  const sinceMs = params.sinceMs || (Date.now() - 60000);
  const tabId = await resolveTabId(params.tabId);
  const serialHint = params.serialNumber || '';

  // Search for recent downloads that are interrupted (blocked as dangerous)
  const items = await chrome.downloads.search({
    orderBy: ['-startTime'],
    limit: 20,
  });

  console.log('[AutomationBridge] rescue: found', items.length, 'recent downloads');

  // Find the blocked download matching our file
  let target = null;
  for (const item of items) {
    const fn = item.filename || '';
    const url = item.url || '';
    const startedAt = new Date(item.startTime).getTime();

    // Must be recent
    if (startedAt < sinceMs - 5000) continue;

    // Match by serial number or by "项目明细" in filename
    const matches = fn.includes('项目明细') || fn.includes(serialHint) ||
                    url.includes(serialHint) ||
                    fn.endsWith('.zip') || fn.endsWith('.zip.crdownload');

    if (matches && item.state !== 'complete') {
      target = item;
      break;
    }
  }

  if (!target) {
    // Also try any recent non-complete download with .zip
    for (const item of items) {
      const fn = item.filename || '';
      const startedAt = new Date(item.startTime).getTime();
      if (startedAt < sinceMs - 5000) continue;
      if (fn.includes('.zip') && item.state !== 'complete') {
        target = item;
        break;
      }
    }
  }

  if (!target) {
    const accepted = await acceptDangerousDownloadsViaPage({
      sinceMs,
      serialNumber: serialHint,
    });
    if (accepted.count > 0) {
      await new Promise((r) => setTimeout(r, 5000));
      const completed = await chrome.downloads.search({
        orderBy: ['-startTime'],
        limit: 10,
      });
      const done = completed.find(
        (item) =>
          item.state === 'complete' &&
          ((item.filename || '').includes(serialHint) ||
            (item.filename || '').includes('.zip')),
      );
      if (done?.filename) {
        return {
          ok: true,
          method: 'accept-danger-page',
          filename: done.filename,
          id: done.id,
        };
      }
    }
    return { ok: false, error: 'no-blocked-download-found', count: items.length, accepted };
  }

  console.log('[AutomationBridge] rescue: found blocked download:', target.filename, 'state:', target.state, 'danger:', target.danger, 'url:', target.url?.slice(0, 100));

  const downloadUrl = target.url;
  if (!downloadUrl || downloadUrl.startsWith('blob:')) {
    return { ok: false, error: 'invalid-url', url: downloadUrl };
  }

  // Cancel the blocked download
  try {
    await chrome.downloads.cancel(target.id);
    // Also remove it from history to avoid duplicates
    await chrome.downloads.removeFile(target.id);
  } catch (e) {
    console.warn('[AutomationBridge] rescue: cancel failed (ok):', e.message);
  }

  // Wait a moment
  await new Promise(r => setTimeout(r, 500));

  // Re-download via blob bypass
  try {
    const result = await downloadWithSessionCookies(downloadUrl, tabId, target.filename?.replace('.crdownload', ''));
    return { ok: true, ...result, originalUrl: downloadUrl };
  } catch (err) {
    return { ok: false, error: err.message, url: downloadUrl };
  }
}

function waitForDownload(params = {}) {
  setupDownloadListeners();
  const filenameContains = params.filenameContains || '';
  const timeout = params.timeout || 120000;
  const sinceMs = params.sinceMs || Date.now();

  return new Promise((resolve, reject) => {
    const waiter = {
      matches(item) {
        if (item.startTime && item.startTime < sinceMs - 5000) return false;
        if (filenameContains && !(item.filename || '').includes(filenameContains)) return false;
        return true;
      },
      resolve,
      reject,
      timer: setTimeout(() => {
        const idx = pendingDownloadWaiters.indexOf(waiter);
        if (idx >= 0) pendingDownloadWaiters.splice(idx, 1);
        reject(new Error(`Download timeout (${timeout}ms)`));
      }, timeout),
    };

    pendingDownloadWaiters.push(waiter);

    chrome.downloads.search({ startedAfter: new Date(sinceMs - 5000).toISOString(), orderBy: ['-startTime'] })
      .then((items) => {
        for (const item of items) {
          if (waiter.matches(item)) {
            if (item.state === 'complete') {
              clearTimeout(waiter.timer);
              pendingDownloadWaiters.splice(pendingDownloadWaiters.indexOf(waiter), 1);
              resolve({
                id: item.id,
                filename: item.filename,
                url: item.url,
                totalBytes: item.totalBytes,
                mime: item.mime,
              });
              return;
            }
          }
        }
      })
      .catch(() => {});
  });
}

setupDownloadListeners.initialized = false;

chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
connect();

chrome.alarms.create('keepAlive', { periodInMinutes: 0.4 });
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === 'keepAlive') {
    ensureOffscreenDocument().catch(() => {});
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      connect();
    }
  }
});

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg.type === 'offscreenPing') {
    sendResponse({ ok: true });
    return;
  }
  if (msg.type === 'reconnect') {
    ws?.close();
    connect();
    sendResponse({ ok: true });
  }
  if (msg.type === 'getStatus') {
    getWsUrl().then((wsUrl) => {
      const actuallyConnected = ws?.readyState === WebSocket.OPEN;
      chrome.storage.local.set({ connected: actuallyConnected });
      chrome.storage.local.get(['lastError'], (stored) => {
        sendResponse({
          connected: actuallyConnected,
          wsUrl,
          lastError: stored.lastError ?? null,
          wsState: ws?.readyState ?? WebSocket.CLOSED,
        });
      });
    });
    return true;
  }
});
