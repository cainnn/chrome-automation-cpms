const statusEl = document.getElementById('status');
const wsUrlInput = document.getElementById('wsUrl');
const saveBtn = document.getElementById('saveBtn');
const reconnectBtn = document.getElementById('reconnectBtn');

function updateStatus(connected, lastError) {
  if (connected) {
    statusEl.textContent = '已连接到桥接服务器';
    statusEl.className = 'status connected';
  } else {
    statusEl.textContent = lastError ? `未连接: ${lastError}` : '未连接';
    statusEl.className = 'status disconnected';
  }
}

async function refresh() {
  const stored = await chrome.storage.local.get(['wsUrl']);
  wsUrlInput.value = stored.wsUrl || 'ws://127.0.0.1:9333';
  try {
    const status = await chrome.runtime.sendMessage({ type: 'getStatus' });
    updateStatus(status?.connected, status?.lastError);
  } catch {
    updateStatus(false, '无法获取扩展状态');
  }
}

saveBtn.addEventListener('click', async () => {
  await chrome.storage.local.set({ wsUrl: wsUrlInput.value.trim() });
  await chrome.runtime.sendMessage({ type: 'reconnect' });
  setTimeout(refresh, 500);
});

reconnectBtn.addEventListener('click', async () => {
  await chrome.runtime.sendMessage({ type: 'reconnect' });
  setTimeout(refresh, 500);
});

// 打开弹窗时自动尝试重连
chrome.runtime.sendMessage({ type: 'reconnect' }).finally(() => refresh());
setInterval(refresh, 2000);
