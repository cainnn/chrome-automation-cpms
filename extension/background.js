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
    case 'cpmsClickExport':
    case 'cpmsClickDialogConfirm':
    case 'cpmsGetLatestSerial':
    case 'cpmsExportRowStatus':
    case 'cpmsClickDownload':
    case 'cpmsGetDownloadUrl':
    case 'cpmsFirstReadyRow':
    case 'cpmsClickFirstReadyDownload':
    case 'cpmsListButtons':
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
    case 'startDownload':
      return startDownload(params);
    case 'cpmsDownloadBySerial':
      return cpmsDownloadBySerial(params);
    case 'downloadWithSessionCookies':
      return downloadWithSessionCookies(params.url, params.tabId, params.filename);
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
  return { id: tab.id, url: tab.url, title: tab.title };
}

async function navigate(params) {
  const tabId = await resolveTabId(params.tabId, params.recreateUrl);
  const url = params.url;
  if (!url) throw new Error('url is required');

  await chrome.tabs.update(tabId, { url });

  if (params.waitUntil === 'load') {
    await waitForTabLoad(tabId, params.timeout || 30000);
  }

  // SPA hash 路由：给前端一点渲染时间
  if (params.waitUntil === 'spa') {
    await new Promise((r) => setTimeout(r, params.delay || 3000));
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
  const results = await chrome.scripting.executeScript({
    target: { tabId, allFrames: true },
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

  function findExportButton() {
    const candidates = collectClickables(document);
    let exportBtn = candidates.find((el) => normalizeText(el) === '导出' && isVisible(el));
    if (!exportBtn) {
      exportBtn = candidates.find((el) => {
        const text = normalizeText(el);
        return text.includes('导出') && text.length <= 8 && isVisible(el);
      });
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
      case 'cpmsClickExport': {
        const exportBtn = findExportButton();
        if (!exportBtn) return { error: 'export-button-not-found' };
        const clickTarget =
          exportBtn.closest('button, .el-button, [role="button"], a') || exportBtn;
        clickElement(clickTarget);
        return { data: { ok: true, text: normalizeText(clickTarget) } };
      }
      case 'cpmsClickDialogConfirm': {
        const dialogs = [
          ...document.querySelectorAll('.el-dialog, .el-message-box, [role="dialog"]'),
        ];
        for (const dlg of dialogs) {
          const style = window.getComputedStyle(dlg);
          if (style.display === 'none' || style.visibility === 'hidden') continue;
          const btns = dlg.querySelectorAll('button, .el-button, span');
          const okBtn = [...btns].find((b) => (b.innerText || b.textContent || '').trim() === '确定');
          if (okBtn) {
            okBtn.click();
            return { data: { ok: true, source: 'dialog' } };
          }
        }
        const globalOk = [...document.querySelectorAll('button, .el-button, span, a')].filter((el) => {
          const text = (el.innerText || el.textContent || '').trim();
          if (text !== '确定') return false;
          const rect = el.getBoundingClientRect();
          return rect.width > 0 && rect.height > 0;
        });
        if (globalOk.length > 0) {
          globalOk[globalOk.length - 1].click();
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
      case 'cpmsGetDownloadUrl': {
        const serial = params.serialNumber || '';
        const rows = [...document.querySelectorAll('tr, .el-table__row, .ant-table-row')];
        const row = rows.find((r) => (r.innerText || '').includes(serial));
        if (!row) return { error: '未找到流水号所在行: ' + serial };
        const links = [...row.querySelectorAll('a[href]')];
        const direct = links.find((a) => a.href && !a.href.startsWith('javascript:'));
        if (direct) return { data: { url: direct.href } };
        return { data: { url: null } };
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
        const links = [...row.querySelectorAll('a[href]')];
        const direct = links.find((a) => a.href && !a.href.startsWith('javascript:'));
        if (direct) {
          return { data: { ok: true, url: direct.href, method: 'href' } };
        }

        const clickables = [...row.querySelectorAll('button, a, span, .el-button, .el-link, [role="button"]')];
        let download = clickables.find((el) => normalizeText(el) === '下载');
        if (!download) {
          download = clickables.find((el) => normalizeText(el).includes('下载'));
        }
        if (!download) return { error: '该行未找到下载按钮' };
        const target = download.closest('button, a, .el-button, .el-link, [role="button"]') || download;

        const capturedUrls = [];
        const origOpen = window.open;
        window.open = function (url, ...rest) {
          if (typeof url === 'string') capturedUrls.push(url);
          return origOpen.call(window, url, ...rest);
        };

        const origFetch = window.fetch;
        window.fetch = function (...args) {
          const reqUrl = typeof args[0] === 'string' ? args[0] : args[0]?.url;
          if (reqUrl) capturedUrls.push(reqUrl);
          return origFetch.apply(this, args).then((res) => {
            if (res?.url) capturedUrls.push(res.url);
            return res;
          });
        };

        const OrigXHR = window.XMLHttpRequest;
        function HookedXHR() {
          const xhr = new OrigXHR();
          const origOpen = xhr.open;
          xhr.open = function (method, url, ...rest) {
            if (url) capturedUrls.push(String(url));
            return origOpen.call(xhr, method, url, ...rest);
          };
          return xhr;
        }
        HookedXHR.prototype = OrigXHR.prototype;
        window.XMLHttpRequest = HookedXHR;

        clickElement(target);
        await new Promise((r) => setTimeout(r, 3500));
        window.open = origOpen;
        window.fetch = origFetch;
        window.XMLHttpRequest = OrigXHR;

        const downloadUrl =
          capturedUrls.find((u) => u && /\.(zip|xlsx|xls)(\?|$)/i.test(u)) ||
          capturedUrls.find(
            (u) =>
              u &&
              (/\/download/i.test(u) ||
                /\/export/i.test(u) ||
                /attachment/i.test(u) ||
                /fileDownload/i.test(u) ||
                /annex/i.test(u)),
          ) ||
          capturedUrls.find((u) => u && u.startsWith('http'));
        if (downloadUrl) {
          return { data: { ok: true, url: downloadUrl, method: 'network' } };
        }
        if (capturedUrls.length > 0) {
          return { data: { ok: true, url: capturedUrls[capturedUrls.length - 1], method: 'captured' } };
        }
        if (target.href && !target.href.startsWith('javascript:')) {
          return { data: { ok: true, url: target.href, method: 'click' } };
        }
        return { data: { ok: true, method: 'click-only' } };
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

  chrome.downloads.onCreated.addListener((item) => {
    if (!shouldAutoAcceptDownload(item)) return;
    if (item.danger) {
      chrome.downloads.acceptDanger(item.id).catch(() => {});
    }
  });

  chrome.downloads.onChanged.addListener(async (delta) => {
    if (!delta.id) return;

    if (delta.danger && isAutoAcceptSessionActive()) {
      const [item] = await chrome.downloads.search({ id: delta.id });
      if (item && shouldAutoAcceptDownload(item)) {
        try {
          await chrome.downloads.acceptDanger(delta.id);
          console.log('[AutomationBridge] Accepted dangerous download:', delta.id);
        } catch (err) {
          console.warn('[AutomationBridge] acceptDanger failed:', err);
        }
      }
    }

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
  if (!isAutoAcceptSessionActive()) {
    return { accepted: [], count: 0, skipped: 'session-inactive' };
  }
  setupDownloadListeners();
  const accepted = [];
  const items = await chrome.downloads.search({
    orderBy: ['-startTime'],
    limit: 20,
  });

  for (const item of items) {
    if (!item.danger || !shouldAutoAcceptDownload(item)) continue;
    try {
      await chrome.downloads.acceptDanger(item.id);
      accepted.push({ id: item.id, filename: item.filename });
    } catch (err) {
      console.warn('[AutomationBridge] acceptDanger failed:', item.id, err);
    }
  }

  return { accepted, count: accepted.length };
}

function trackExtensionDownload(id) {
  if (id != null) extensionDownloadIds.add(id);
}

function resolveAbsoluteUrl(url, baseUrl) {
  if (!url) return null;
  if (url.startsWith('http://') || url.startsWith('https://')) return url;
  try {
    return new URL(url, baseUrl).href;
  } catch {
    return url;
  }
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
async function downloadWithSessionCookies(url, tabId, filename) {
  if (!url) throw new Error('url is required');
  setupDownloadListeners();
  enableAutoAcceptDownloads({ durationMs: 15 * 60 * 1000 });

  const tab = await chrome.tabs.get(tabId);
  const absoluteUrl = resolveAbsoluteUrl(url, tab.url || url);
  const cookieUrl = new URL(absoluteUrl).origin + '/';
  const cookies = await chrome.cookies.getAll({ url: cookieUrl });
  const cookieHeader = cookies.map((c) => `${c.name}=${c.value}`).join('; ');

  const res = await fetch(absoluteUrl, {
    method: 'GET',
    headers: cookieHeader ? { Cookie: cookieHeader } : {},
    credentials: 'include',
  });
  if (!res.ok) {
    throw new Error(`Download fetch failed: ${res.status} ${res.statusText}`);
  }

  const buf = await res.arrayBuffer();
  const mime = res.headers.get('content-type') || 'application/zip';
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
      id,
      filename: saveAs,
      size: buf.byteLength,
      method: 'blob-bypass',
      sourceUrl: absoluteUrl,
    };
  } finally {
    setTimeout(() => URL.revokeObjectURL(blobUrl), 120000);
  }
}

async function startDownload(params = {}) {
  if (!params.url) throw new Error('url is required');
  const tabId = await resolveTabId(params.tabId);
  try {
    return await downloadWithSessionCookies(params.url, tabId, params.filename);
  } catch (err) {
    console.warn('[AutomationBridge] blob-bypass failed, fallback to direct download:', err);
    setupDownloadListeners();
    enableAutoAcceptDownloads({ durationMs: 15 * 60 * 1000 });
    const id = await chrome.downloads.download({ url: params.url, saveAs: false });
    trackExtensionDownload(id);
    return { id, method: 'direct-url' };
  }
}

async function cpmsDownloadBySerial(params) {
  const tabId = await resolveTabId(params.tabId, params.recreateUrl);
  await chrome.tabs.update(tabId, { active: true });
  enableAutoAcceptDownloads({ durationMs: 15 * 60 * 1000 });

  const clickResult = await runInTab('cpmsClickDownload', { ...params, tabId });
  const downloadUrl = resolveAbsoluteUrl(
    clickResult?.url,
    (await chrome.tabs.get(tabId)).url,
  );

  if (downloadUrl) {
    try {
      return await downloadWithSessionCookies(
        downloadUrl,
        tabId,
        clickResult?.filename || suggestFilename(downloadUrl, null, params.serialNumber),
      );
    } catch (err) {
      console.warn('[AutomationBridge] session download failed:', err);
      try {
        const id = await chrome.downloads.download({ url: downloadUrl, saveAs: false });
        trackExtensionDownload(id);
        await acceptPendingDownloads();
        return { id, sourceUrl: downloadUrl, method: 'direct-fallback', error: err.message };
      } catch (err2) {
        console.warn('[AutomationBridge] direct download failed:', err2);
      }
    }
  }

  await acceptPendingDownloads();
  return { ...(clickResult || {}), method: clickResult?.method || 'click-only' };
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
            if (item.danger && shouldAutoAcceptDownload(item)) {
              chrome.downloads.acceptDanger(item.id).catch(() => {});
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
