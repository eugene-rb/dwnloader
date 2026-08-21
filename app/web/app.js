'use strict';

/* Python 側とのやり取り。pywebview が window.pywebview.api を注入する。 */
const api = () => window.pywebview && window.pywebview.api;

const $ = (id) => document.getElementById(id);
const el = {
  watch: $('watch'), watchHint: $('watch-hint'), kinds: $('kinds'),
  playlistAll: $('playlist-all'),
  dests: $('dests'), outputDir: $('output-dir'),
  videoDir: $('video-dir'), audioDir: $('audio-dir'),
  addForm: $('add-form'), urlInput: $('url-input'),
  cards: $('cards'), empty: $('empty'), count: $('queue-count'),
  startAll: $('start-all'), retryAll: $('retry-all'), clearDone: $('clear-done'),
  clearAll: $('clear-all'),
  pin: $('pin'), winMin: $('win-min'), winMax: $('win-max'), winClose: $('win-close'),
  status: $('status'), log: $('log'), logToggle: $('log-toggle'),
  logBadge: $('log-badge'), logClear: $('log-clear'),
  toasts: $('toasts'), dropzone: $('dropzone'),
  settings: $('settings'), settingsForm: $('settings-form'),
  openSettings: $('open-settings'), settingsCancel: $('settings-cancel'),
};

const state = { entries: new Map(), settings: {}, unread: 0, worstLevel: 'warn' };

/* ==========================================================================
   カードの描画
   ========================================================================== */

const ICON = {
  open: 'i-open', folder: 'i-folder', retry: 'i-retry',
  stop: 'i-stop', x: 'i-x', page: 'i-open',
  video: 'i-video', audio: 'i-audio',
};

function icon(name) {
  return `<svg class="icon" aria-hidden="true"><use href="#${name}"/></svg>`;
}

/** 動画↔音声の切り替え。可否は Python 側が決めた canSwitchKind に従う。 */
function kindAction(e) {
  if (!e.canSwitchKind) return null;
  const next = e.kind === 'audio' ? 'video' : 'audio';
  return {
    act: 'kind', data: next,
    // 3つ並ぶので短く。何が起きるかは読み上げ名とツールチップで補う。
    label: next === 'audio' ? '音声' : '動画',
    icon: next === 'audio' ? ICON.audio : ICON.video,
    aria: next === 'audio' ? '音声として取得する' : '動画として取得する',
  };
}

/** 状態ごとに出す操作を決める。進行中に意味のある操作は「中止」だけ。 */
function actionsFor(e) {
  const swap = kindAction(e);
  if (e.active) {
    // 進行中でもキューから外せるようにする。中止してから消す、の2手を強いない。
    // 待機中でまだ走り出していないものは、ここで種別も変えられる。
    return [
      ...(swap ? [swap] : []),
      { act: 'cancel', label: '中止', icon: ICON.stop, cls: 'btn--danger' },
      { act: 'remove', label: '', icon: ICON.x, cls: 'btn--ghost btn--icon',
        aria: '中止してキューから削除' },
    ];
  }
  if (e.status === 'DONE' || e.status === 'SKIPPED') {
    return [
      { act: 'open', label: '開く', icon: ICON.open, cls: 'btn--primary' },
      { act: 'reveal', label: 'フォルダ', icon: ICON.folder },
      { act: 'remove', label: '', icon: ICON.x, cls: 'btn--ghost btn--icon', aria: 'この行を消す' },
    ];
  }
  if (e.status === 'PARTIAL') {
    return [
      { act: 'open', label: '開く', icon: ICON.open, cls: 'btn--primary' },
      { act: 'retry', label: '再試行', icon: ICON.retry },
      { act: 'remove', label: '', icon: ICON.x, cls: 'btn--ghost btn--icon', aria: 'この行を消す' },
    ];
  }
  // 失敗・中止: 復旧の入口をカード上に常に置く
  return [
    { act: 'retry', label: '再試行', icon: ICON.retry, cls: 'btn--primary' },
    // 元ページは失敗の切り分けに要る（消えたのか、年齢制限なのか）。種別の
    // 切り替えはその隣に足す。どちらも復旧の手段なので、片方に譲らせない。
    { act: 'page', label: 'ページ', icon: ICON.page },
    ...(swap ? [swap] : []),
    { act: 'remove', label: '', icon: ICON.x, cls: 'btn--ghost btn--icon', aria: 'この行を消す' },
  ];
}

function esc(text) {
  return String(text ?? '').replace(/[&<>"]/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}

function cardHtml(e) {
  const indeterminate = e.active && !e.total;
  const pct = e.total ? Math.round((e.done / e.total) * 100) : 0;
  const stopped = e.status === 'FAILED' || e.status === 'CANCELED';

  const actions = actionsFor(e).map((a) => `
    <button type="button" class="btn btn--sm ${a.cls || ''}" data-act="${a.act}"
            ${a.data ? `data-arg="${esc(a.data)}"` : ''}
            ${a.aria ? `aria-label="${esc(a.aria)}" title="${esc(a.aria)}"` : ''}>
      ${icon(a.icon)}${a.label ? esc(a.label) : ''}
    </button>`).join('');

  const thumb = e.thumb
    ? `<img src="data:image/jpeg;base64,${e.thumb}" alt="" class="is-loaded">`
    : '';

  const foot = stopped
    ? `<p class="jc__reason" title="${esc(e.message)}">${esc(e.message || '失敗しました')}</p>`
    : `<div class="bar ${indeterminate ? 'bar--indeterminate' : ''}"
             role="progressbar" aria-valuemin="0" aria-valuemax="100"
             ${indeterminate ? '' : `aria-valuenow="${pct}"`}>
         <div class="bar__fill"${indeterminate ? ''
            : ` style="transform:scaleX(${pct / 100})"`}></div>
       </div>`;

  return `
    <div class="jc__thumb">${thumb}
      <span class="jc__site" data-site="${esc(e.site)}">${esc(e.siteLabel)}</span>
    </div>
    <div class="jc__body">
      <h3 class="jc__title" title="${esc(e.title)}">${esc(e.title)}</h3>
      <p class="jc__sub" title="${esc(e.subtitle)}">${esc(e.subtitle)}</p>
      <div class="jc__foot">
        ${foot}
        <span class="jc__detail">${esc(e.detail)}</span>
      </div>
    </div>
    <div class="jc__actions">${actions}</div>`;
}

function renderCard(e) {
  let node = el.cards.querySelector(`[data-id="${e.id}"]`);
  if (!node) {
    node = document.createElement('li');
    node.className = 'jc';
    node.dataset.id = e.id;
    node.tabIndex = 0;                      // キーボードでも辿れるようにする
    node.setAttribute('role', 'listitem');
    el.cards.appendChild(node);
  }
  node.className = `jc jc--${e.status}${e.active ? ' jc--active' : ''}`;
  node.setAttribute('aria-label', `${e.title} — ${e.statusLabel}`);
  node.innerHTML = cardHtml(e);
  return node;
}

function removeCard(id) {
  const node = el.cards.querySelector(`[data-id="${id}"]`);
  if (!node) return;
  node.classList.add('jc--leaving');
  node.addEventListener('transitionend', () => node.remove(), { once: true });
  // transition が走らない環境でも必ず消す
  setTimeout(() => node.remove(), 400);
}

function syncEmpty() {
  el.empty.hidden = state.entries.size > 0;
}

/* ==========================================================================
   全体状態
   ========================================================================== */

function applyState(s) {
  if (!s) return;
  state.settings = s.settings || {};
  fillDests(s.dirs);
  setKindState(state.settings.media_kind || 'video');
  setPlaylistState(!!state.settings.playlist_all);
  el.watch.checked = !!s.watching;
  el.watchHint.textContent = s.watching ? 'URLをコピーすると自動で追加' : '停止中';
  el.status.textContent = s.status || '';
  el.count.textContent = s.entries && s.entries.length ? `${s.entries.length} 件` : '';

  el.startAll.hidden = !s.canStart;
  el.retryAll.hidden = !(s.unfinished >= 2);
  el.retryAll.textContent = `未完了 ${s.unfinished} 件を再試行`;

  if (Array.isArray(s.entries)) {
    const seen = new Set();
    for (const e of s.entries) {
      state.entries.set(e.id, e);
      renderCard(e);
      seen.add(e.id);
    }
    for (const id of [...state.entries.keys()]) {
      if (!seen.has(id)) { state.entries.delete(id); removeCard(id); }
    }
    el.clearDone.hidden = !s.entries.some((e) => !e.active);
    el.clearAll.hidden = s.entries.length === 0;
  }
  if (typeof s.alwaysOnTop === 'boolean') setPinState(s.alwaysOnTop);
  syncEmpty();
}

/** 動画/音声トグルの見た目。押した瞬間に返し、Python の返事で確定させる。 */
function setKindState(kind) {
  state.settings.media_kind = kind;
  for (const button of el.kinds.querySelectorAll('.kind')) {
    button.setAttribute('aria-pressed', String(button.dataset.kind === kind));
  }
}

/** 3つの保存先。Python が「実際に使う場所」を計算して送ってくる。 */
function fillDests(dirs) {
  if (!dirs) return;
  el.outputDir.value = dirs.pdf || '';
  el.videoDir.value = dirs.video || '';
  el.audioDir.value = dirs.audio || '';
}

/** 再生リストを全部取るかどうか。オフなら先頭の1本だけ。 */
function setPlaylistState(on) {
  state.settings.playlist_all = on;
  el.playlistAll.setAttribute('aria-pressed', String(on));
}

function setPinState(on) {
  el.pin.setAttribute('aria-pressed', String(on));
  el.pin.title = on ? '最前面の固定を解除' : '最前面に固定';
  el.pin.setAttribute('aria-label', el.pin.title);
}

/* ==========================================================================
   Python からのイベント
   ========================================================================== */

window.__recv = (batch) => {
  for (const ev of batch) {
    switch (ev.type) {
      case 'state': applyState(ev); break;
      case 'entry':
        state.entries.set(ev.id, ev);
        renderCard(ev);
        syncEmpty();
        break;
      case 'removed':
        state.entries.delete(ev.id);
        removeCard(ev.id);
        syncEmpty();
        break;
      case 'progress': onProgress(ev); break;
      case 'thumb': onThumb(ev); break;
      case 'log': appendLog(ev.level, ev.message); break;
      case 'notify': toast(ev.title, ev.body, ev.kind); break;
      case 'focus': focusCard(ev.id); break;
    }
  }
};

function onProgress(ev) {
  const e = state.entries.get(ev.id);
  if (e) { e.done = ev.done; e.total = ev.total; e.detail = ev.detail; }
  const node = el.cards.querySelector(`[data-id="${ev.id}"]`);
  if (!node) return;
  const bar = node.querySelector('.bar');
  const fill = node.querySelector('.bar__fill');
  const detail = node.querySelector('.jc__detail');
  if (bar && fill && ev.total) {
    bar.classList.remove('bar--indeterminate');
    const pct = Math.round((ev.done / ev.total) * 100);
    // width ではなく transform。再レイアウトを起こさず合成だけで動く。
    fill.style.transform = `scaleX(${pct / 100})`;
    bar.setAttribute('aria-valuenow', String(pct));
  }
  if (detail) detail.textContent = ev.detail;
}

function onThumb(ev) {
  const e = state.entries.get(ev.id);
  if (e) e.thumb = ev.data;
  const holder = el.cards.querySelector(`[data-id="${ev.id}"] .jc__thumb`);
  if (!holder || holder.querySelector('img')) return;
  const img = document.createElement('img');
  img.alt = '';
  img.src = `data:image/jpeg;base64,${ev.data}`;
  // 到着を短くフェードで示す。1作品につき1回きりの変化。
  img.addEventListener('load', () => img.classList.add('is-loaded'), { once: true });
  holder.prepend(img);
}

function focusCard(id) {
  const node = el.cards.querySelector(`[data-id="${id}"]`);
  if (node) node.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}

/* ==========================================================================
   ログ
   ========================================================================== */

function appendLog(level, message) {
  const line = document.createElement('div');
  const time = new Date().toTimeString().slice(0, 8);
  line.innerHTML = `<b>${time}</b> <span class="lv-${esc(level)}">${esc(message)}</span>`;
  el.log.appendChild(line);
  while (el.log.childElementCount > 3000) el.log.firstElementChild.remove();
  el.log.scrollTop = el.log.scrollHeight;

  if (!el.log.classList.contains('is-open') && (level === 'warn' || level === 'error')) {
    state.unread += 1;
    if (level === 'error') state.worstLevel = 'error';
    el.logBadge.hidden = false;
    el.logBadge.textContent = String(state.unread);
    el.logBadge.dataset.level = state.worstLevel;
  }
}

el.logToggle.addEventListener('click', () => {
  const open = el.log.classList.toggle('is-open');
  el.logToggle.setAttribute('aria-expanded', String(open));
  el.logClear.hidden = !open;
  if (open) {
    state.unread = 0; state.worstLevel = 'warn';
    el.logBadge.hidden = true;
    el.log.scrollTop = el.log.scrollHeight;
  }
});
el.logClear.addEventListener('click', () => { el.log.textContent = ''; });

/* ==========================================================================
   トースト
   ========================================================================== */

const TOAST_ICON = { success: 'i-check', warning: 'i-alert', error: 'i-x', info: 'i-info' };
const TOAST_LIMIT = 4;

function toast(title, body, kind = 'info') {
  const node = document.createElement('div');
  node.className = `toast toast--${kind}`;
  node.innerHTML = `
    <span class="toast__badge">${icon(TOAST_ICON[kind] || TOAST_ICON.info)}</span>
    <div>
      <p class="toast__title">${esc(title)}</p>
      ${body ? `<p class="toast__body">${esc(body)}</p>` : ''}
    </div>`;
  el.toasts.appendChild(node);

  while (el.toasts.childElementCount > TOAST_LIMIT) dismiss(el.toasts.firstElementChild);

  let timer = setTimeout(() => dismiss(node), 4200);
  // 読んでいる最中に消えるのは事故なので、ホバー中は止める
  node.addEventListener('pointerenter', () => clearTimeout(timer));
  node.addEventListener('pointerleave', () => { timer = setTimeout(() => dismiss(node), 2000); });
  node.addEventListener('click', () => dismiss(node));
}

function dismiss(node) {
  if (!node || node.classList.contains('is-leaving')) return;
  node.classList.add('is-leaving');
  node.addEventListener('transitionend', () => node.remove(), { once: true });
  setTimeout(() => node.remove(), 500);
}

/* ==========================================================================
   操作
   ========================================================================== */

el.cards.addEventListener('click', (event) => {
  const button = event.target.closest('button[data-act]');
  const card = event.target.closest('.jc');
  if (!card) return;
  const id = card.dataset.id;
  if (button) {
    event.stopPropagation();
    invoke(button.dataset.act, id, button.dataset.arg);
  }
});

el.cards.addEventListener('dblclick', (event) => {
  const card = event.target.closest('.jc');
  if (!card) return;
  const e = state.entries.get(card.dataset.id);
  if (!e) return;
  invoke(['DONE', 'PARTIAL', 'SKIPPED'].includes(e.status) ? 'open' : 'page', e.id);
});

// キーボードだけでも一覧を辿って開けるようにする
el.cards.addEventListener('keydown', (event) => {
  const card = event.target.closest('.jc');
  if (!card) return;
  if (event.key === 'Enter') {
    const e = state.entries.get(card.dataset.id);
    if (e) invoke(['DONE', 'PARTIAL', 'SKIPPED'].includes(e.status) ? 'open' : 'page', e.id);
  } else if (event.key === 'Delete') {
    invoke('remove', card.dataset.id);
  } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault();
    const next = event.key === 'ArrowDown'
      ? card.nextElementSibling : card.previousElementSibling;
    if (next) next.focus();
  }
});

function invoke(act, id, arg) {
  const a = api();
  if (!a) return;
  ({
    open: () => a.open_result(id),
    reveal: () => a.reveal_result(id),
    page: () => a.open_page(id),
    retry: () => a.retry(id),
    cancel: () => a.cancel(id),
    remove: () => a.remove(id),
    kind: () => a.set_entry_kind(id, arg),
  }[act] || (() => {}))();
}

el.addForm.addEventListener('submit', (event) => {
  event.preventDefault();
  const text = el.urlInput.value.trim();
  if (!text) return;
  el.urlInput.value = '';
  api() && api().add_urls(text);
});

el.watch.addEventListener('change', () => api() && api().set_watch(el.watch.checked));

el.kinds.addEventListener('click', async (event) => {
  const button = event.target.closest('.kind');
  if (!button || !api()) return;
  setKindState(button.dataset.kind);
  setKindState(await api().set_media_kind(button.dataset.kind));
});

el.playlistAll.addEventListener('click', async () => {
  const next = el.playlistAll.getAttribute('aria-pressed') !== 'true';
  setPlaylistState(next);                    // 押した瞬間に見た目を返す
  const applied = !!(await api().set_playlist_all(next));
  setPlaylistState(applied);
  toast(applied ? '再生リストは全体を取得します' : '再生リストは先頭の1本だけ取得します',
        '', 'info');
});

// 3つとも同じ形なので、1か所で受けて種別で振り分ける。
// 変更後の表示は Python から届く state で揃うので、ここでは書き換えない。
el.dests.addEventListener('click', (event) => {
  const button = event.target.closest('button[data-act]');
  if (!button || !api()) return;
  const kind = button.dataset.dir === 'pdf' ? '' : button.dataset.dir;
  if (button.dataset.act === 'open') api().open_output_dir(kind);
  else if (kind) api().pick_media_dir(kind);
  else api().pick_output_dir();
});
el.startAll.addEventListener('click', () => api() && api().start_all());
el.retryAll.addEventListener('click', () => api() && api().retry_all());
el.clearDone.addEventListener('click', () => api() && api().clear_finished());
el.clearAll.addEventListener('click', () => api() && api().clear_all());

/* ==========================================================================
   ウィンドウ操作（枠が無いので自前）
   ========================================================================== */

el.pin.addEventListener('click', async () => {
  const next = el.pin.getAttribute('aria-pressed') !== 'true';
  setPinState(next);                       // 押した瞬間に見た目を返す
  const applied = await api().set_on_top(next);
  setPinState(!!applied);
  toast(applied ? '最前面に固定しました' : '最前面の固定を解除しました', '', 'info');
});

// 掴んだ時点で Windows に移動を任せる。以降マウスを追いかける処理は不要。
$('titlebar').addEventListener('mousedown', (event) => {
  if (event.button !== 0) return;
  if (!event.target.closest('[data-drag]')) return;
  if (event.detail > 1) return;              // ダブルクリックは最大化に使う
  api() && api().begin_drag();
});

el.winMin.addEventListener('click', () => api() && api().minimize_window());
el.winClose.addEventListener('click', () => api() && api().close_window());

el.winMax.addEventListener('click', async () => {
  const maximized = await api().toggle_maximize();
  syncMaxIcon(!!maximized);
});

function syncMaxIcon(maximized) {
  el.winMax.querySelector('use')
    .setAttribute('href', maximized ? '#i-restore' : '#i-max');
  el.winMax.title = maximized ? '元のサイズに戻す' : '最大化';
  el.winMax.setAttribute('aria-label', el.winMax.title);
}

// タイトルバーのダブルクリックで最大化（枠付きウィンドウと同じ操作感）
$('titlebar').addEventListener('dblclick', async (event) => {
  if (event.target.closest('button')) return;
  syncMaxIcon(!!(await api().toggle_maximize()));
});

/* ==========================================================================
   ドラッグ&ドロップ
   ========================================================================== */

let dragDepth = 0;
window.addEventListener('dragenter', (e) => {
  e.preventDefault();
  if (++dragDepth === 1) el.dropzone.classList.add('is-active');
});
window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('dragleave', () => {
  if (--dragDepth <= 0) { dragDepth = 0; el.dropzone.classList.remove('is-active'); }
});
window.addEventListener('drop', (e) => {
  e.preventDefault();
  dragDepth = 0;
  el.dropzone.classList.remove('is-active');
  const text = e.dataTransfer.getData('text/uri-list') || e.dataTransfer.getData('text/plain');
  if (text && api()) api().add_urls(text);
});

/* ==========================================================================
   設定ダイアログ
   ========================================================================== */

el.openSettings.addEventListener('click', async () => {
  const values = await api().get_settings();
  fillSettings(values);
  el.settings.showModal();
});

el.settingsCancel.addEventListener('click', () => el.settings.close());

document.querySelectorAll('.tab').forEach((tab) => {
  tab.addEventListener('click', () => {
    document.querySelectorAll('.tab').forEach((t) =>
      t.setAttribute('aria-selected', String(t === tab)));
    document.querySelectorAll('.panel').forEach((p) => {
      p.hidden = p.dataset.panel !== tab.dataset.tab;
    });
  });
});

// 保存先の「参照」。選ぶと即座に保存されるので、欄にも結果を書き戻す。
el.settingsForm.addEventListener('click', async (event) => {
  const button = event.target.closest('button[data-pick]');
  if (!button || !api()) return;
  const kind = button.dataset.pick;
  const path = kind === 'cookies'
    ? await api().pick_media_cookies_file()
    : await api().pick_media_dir(kind);
  if (!path) return;
  const field = el.settingsForm.elements[
    kind === 'cookies' ? 'media_cookies_file' : kind === 'audio' ? 'audio_dir' : 'video_dir'];
  if (field) field.value = path;
});

function fillSettings(values) {
  for (const [key, value] of Object.entries(values)) {
    const field = el.settingsForm.elements[key];
    if (!field) continue;
    if (field.type === 'checkbox') field.checked = !!value;
    else field.value = value;
  }
}

el.settingsForm.addEventListener('submit', () => {
  const values = {};
  for (const field of el.settingsForm.elements) {
    if (!field.name) continue;
    values[field.name] = field.type === 'checkbox' ? field.checked
      : field.type === 'number' ? Number(field.value) : field.value;
  }
  api() && api().save_settings(values).then(applySettingsResult);
});

function applySettingsResult(values) {
  // 保存先の表示は state（実際に使う場所）で更新されるので、ここでは触らない
  state.settings = values || state.settings;
}

/* ==========================================================================
   起動
   ========================================================================== */

function boot() {
  const a = api();
  if (!a) { setTimeout(boot, 60); return; }
  a.ready().then(applyState);
}

window.addEventListener('pywebviewready', boot);
document.addEventListener('DOMContentLoaded', boot);
boot();
