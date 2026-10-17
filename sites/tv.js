// CouchTV's touches for websites on the TV. CouchTV loads this folder into the browser it opens for a web tile
// (--load-extension, Sites.cs writes it out), so it works with the remote: the arrows move a highlight from video to
// video and OK opens the highlighted one, like a TV app. youtube.js and netflix.js describe their site to this file.
//
// Settings come from settings.json next to this file, which CouchTV rewrites whenever one changes on the TV or the
// phone. It's read again every couple of seconds, so a change shows without reopening the site.
'use strict';

var CouchTV = (function () {
  // Once a page: the extension and CouchTV's DevTools route (PageScripts.cs) could both bring these.
  if (document.documentElement.hasAttribute('data-couchtv')) return null;
  document.documentElement.setAttribute('data-couchtv', typeof chrome !== 'undefined' && chrome.runtime && chrome.runtime.id ? 'extension' : 'devtools');

  var settings = { keys: true, youtubeRow: 0, netflixTidy: true };
  var settingsText = '';
  var listeners = [];
  var site = null;           // the site's description (see youtube.js)
  var current = null;        // the highlighted element
  var asleep = false;        // the mouse moved: the highlight is hidden until an arrow wakes it where it was
  var ring = null, tracking = false;
  var remembered = [];       // [address, element]: the highlight on pages left, for coming back to them
  var address = location.href;

  // ------------------------------------------------------------------ settings

  function readSettings() {
    // Put in through DevTools, CouchTV writes changes here (PageScripts.cs).
    var given = document.documentElement.getAttribute('data-couchtv-settings');
    if (given) {
      try { return apply(JSON.parse(given)); } catch (e) { /* half-written: next time */ }
    }
    if (window.__couchtvSettings) return apply(window.__couchtvSettings);   // DevTools' starting settings; tests
    var url;
    try { url = chrome.runtime.getURL('settings.json'); } catch (e) { return; }   // the extension's gone: keep what we have
    fetch(url, { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(apply)
      .catch(function () {});
  }

  function apply(s) {
    var text = JSON.stringify(s);
    if (text === settingsText) return;
    settingsText = text;
    settings = Object.assign({}, settings, s);
    if (!settings.keys) clear();
    listeners.forEach(function (fn) { try { fn(settings); } catch (e) { console.warn('CouchTV', e); } });
  }

  function onSettings(fn) {
    listeners.push(fn);
    if (settingsText) fn(settings);
  }

  // ------------------------------------------------------------------ what can be highlighted

  /** The elements matching a selector under root, outermost only (a video's card, not the card's title inside it). */
  function all(root, selector) {
    var found = Array.prototype.slice.call((root || document).querySelectorAll(selector));
    var set = new Set(found);
    return found.filter(function (el) {
      for (var p = el.parentElement; p; p = p.parentElement) if (set.has(p)) return false;
      return true;
    });
  }

  /**
   * Shown on the page (not hidden, not a sliver), and near enough to the screen to be worth moving to. Loose: also
   * one past the screen's side, on a site whose rows otherwise only count what's wholly on screen.
   */
  function shown(el, loose) {
    if (!el || !el.isConnected) return false;
    var r = el.getBoundingClientRect();
    if (r.width < 16 || r.height < 12) return false;
    if (r.bottom < -innerHeight * 3 || r.top > innerHeight * 4) return false;
    if (!loose && site && site.wholeAcross && (r.left < -2 || r.right > innerWidth + 2)) return false;
    return el.checkVisibility ? el.checkVisibility({ opacityProperty: true, visibilityProperty: true }) : true;
  }

  function candidates(loose) {
    return (site.cards() || []).filter(function (el) { return shown(el, loose); });
  }

  /**
   * The nearest in a direction, like a TV's remote: across, only along the same row; up and down, the nearest row
   * first, then the one most nearly in line.
   */
  function best(from, list, dir) {
    var a = from.getBoundingClientRect();
    var across = dir === 'left' || dir === 'right';
    var ahead = [];
    list.forEach(function (el) {
      if (el === from || el.contains(from) || from.contains(el)) return;
      var b = el.getBoundingClientRect();
      var gap, offset;
      // Ahead means starting past the middle of this one (a neighbour a little taller isn't "below" it).
      if (across) {
        if (dir === 'right' ? b.left < a.left + a.width / 2 : b.right > a.right - a.width / 2) return;
        var overlap = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
        if (overlap < Math.min(a.height, b.height) * 0.3) return;
        gap = dir === 'right' ? b.left - a.right : a.left - b.right;
        offset = Math.abs((b.top + b.bottom) / 2 - (a.top + a.bottom) / 2);
      } else {
        if (dir === 'down' ? b.top < a.top + a.height / 2 : b.bottom > a.bottom - a.height / 2) return;
        gap = dir === 'down' ? b.top - a.bottom : a.top - b.bottom;
        offset = Math.abs((b.left + b.right) / 2 - (a.left + a.right) / 2);
      }
      ahead.push({ el: el, gap: Math.max(gap, 0), offset: offset, size: across ? b.width : b.height });
    });
    if (!ahead.length) return null;
    var nearest = Math.min.apply(null, ahead.map(function (c) { return c.gap; }));
    var pick = null;
    ahead.forEach(function (c) {
      if (c.gap > nearest + Math.max(24, c.size * 0.5)) return;   // a row further on
      if (!pick || c.offset < pick.offset || (c.offset === pick.offset && c.gap < pick.gap)) pick = c;
    });
    return pick.el;
  }

  /** Where to start when nothing's highlighted yet: the one there before on this page, else the site's first. */
  function start(list) {
    for (var i = remembered.length - 1; i >= 0; i--) {
      if (remembered[i][0] === location.href && list.indexOf(remembered[i][1]) >= 0) return remembered[i][1];
    }
    if (site.start) {
      var chosen = site.start(list);
      if (chosen) return chosen;
    }
    return first(list);
  }

  /** The first on screen, in reading order (top row first, then from the left). */
  function first(list) {
    var top = site.top ? site.top() : 0;
    var onScreen = list.filter(function (el) {
      var r = el.getBoundingClientRect();
      return r.top >= top - 4 && r.bottom <= innerHeight + 4;
    });
    var pool = onScreen.length ? onScreen : list;
    pool.sort(function (x, y) {
      var a = x.getBoundingClientRect(), b = y.getBoundingClientRect();
      return Math.abs(a.top - b.top) > 12 ? a.top - b.top : a.left - b.left;
    });
    return pool[0] || null;
  }

  // ------------------------------------------------------------------ the highlight

  function makeRing() {
    ring = document.createElement('div');
    ring.setAttribute('aria-hidden', 'true');
    ring.style.cssText = 'position:fixed;left:0;top:0;width:0;height:0;margin:0;padding:0;pointer-events:none;' +
      'z-index:2147483647;box-sizing:border-box;opacity:0;' +
      'box-shadow:0 0 0 4px #fff,0 0 0 9px rgba(62,166,255,.6),0 12px 40px rgba(0,0,0,.6);' +
      'transition:transform .13s ease-out,width .13s ease-out,height .13s ease-out,opacity .15s ease-out;';
    document.documentElement.appendChild(ring);
  }

  function show(el) {
    if (!el) return;
    current = el;
    asleep = false;
    remembered = remembered.filter(function (r) { return r[0] !== location.href && r[1].isConnected; }).slice(-20);
    remembered.push([location.href, el]);
    reveal(el);
    if (!ring || !ring.isConnected) makeRing();
    ring.style.borderRadius = (site.radius || 10) + 'px';
    place();
    ring.style.opacity = '1';
    if (!tracking) { tracking = true; requestAnimationFrame(track); }
  }

  /** Nothing highlighted (the site has the keys back). */
  function clear() {
    current = null;
    asleep = false;
    if (ring) ring.style.opacity = '0';
  }

  function sleep() {
    if (!current || asleep) return;
    asleep = true;
    if (ring) ring.style.opacity = '0';
  }

  /** The nearest box around it that scrolls that way (a dialog, a row), short of the page itself. */
  function scroller(el, across) {
    for (var p = el.parentElement; p && p !== document.body && p !== document.documentElement; p = p.parentElement) {
      var style = getComputedStyle(p);
      if (across ? p.scrollWidth > p.clientWidth + 1 && /auto|scroll/.test(style.overflowX)
                 : p.scrollHeight > p.clientHeight + 1 && /auto|scroll/.test(style.overflowY)) return p;
    }
    return null;
  }

  /**
   * Brings it into view, rows to the middle of the screen as a TV's do. Worked out here, not with scrollIntoView,
   * which sites upset (YouTube gives its videos a scroll margin of hundreds of pixels).
   */
  function reveal(el) {
    var r = el.getBoundingClientRect();
    var row = scroller(el, true);
    if (row) {
      var box = row.getBoundingClientRect();
      if (r.left < box.left) row.scrollBy({ left: r.left - box.left - 16, behavior: 'smooth' });
      else if (r.right > box.right) row.scrollBy({ left: r.right - box.right + 16, behavior: 'smooth' });
    }
    var panel = scroller(el, false);
    var top = panel ? panel.getBoundingClientRect().top : (site.top ? site.top() : 0);
    var bottom = panel ? panel.getBoundingClientRect().bottom : innerHeight;
    if (r.top >= top + 8 && r.bottom <= bottom - 8) return;
    var area = bottom - top;
    var by = r.height > area - 16 ? r.top - top - 8 : (r.top + r.height / 2) - (top + area / 2);
    (panel || window).scrollBy({ top: by, behavior: 'smooth' });
  }

  function place() {
    var r = current.getBoundingClientRect();
    var pad = site.pad == null ? 6 : site.pad;
    ring.style.transform = 'translate(' + Math.round(r.left - pad) + 'px,' + Math.round(r.top - pad) + 'px)';
    ring.style.width = Math.round(r.width + pad * 2) + 'px';
    ring.style.height = Math.round(r.height + pad * 2) + 'px';
  }

  /** The ring follows its element as the page scrolls and moves; it goes if the element does. */
  function track() {
    if (!current || asleep) { tracking = false; return; }
    if (!shown(current, true) || document.fullscreenElement) {
      clear();
      tracking = false;
      return;
    }
    place();
    requestAnimationFrame(track);
  }

  // ------------------------------------------------------------------ the remote's keys

  var DIRECTIONS = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right' };

  function typing(el) {
    if (!el) return false;
    if (el.isContentEditable) return true;
    if (el.tagName === 'TEXTAREA' || el.tagName === 'SELECT') return true;
    if (el.tagName !== 'INPUT') return false;
    return ['text', 'search', 'email', 'password', 'number', 'tel', 'url'].indexOf((el.type || 'text').toLowerCase()) >= 0;
  }

  function stop(e) {
    e.preventDefault();
    e.stopImmediatePropagation();
  }

  function onKey(e) {
    if (!site || !settings.keys || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey || e.isComposing) return;
    if (document.fullscreenElement) return;   // a video filling the screen: the arrows seek and change the volume
    var dir = DIRECTIONS[e.key];
    if (!dir && e.key !== 'Enter') return;
    var key = dir || 'enter';
    var focused = document.activeElement;
    if (typing(focused)) {
      // Down leaves a search box (once its suggestions are closed), for the results under it.
      if (key !== 'down' || !site.leaveBox || !site.leaveBox(focused)) return;
      focused.blur();
    }
    // Something opened over it (a title's details) leaves the highlight behind: it starts again in what's open.
    var list = candidates();
    var held = current && list.indexOf(current) >= 0;
    var live = held && !asleep;
    if (site.passes && site.passes(key, live ? current : null)) return;   // the site's own use of the key
    if (key === 'enter') {
      if (live) {
        stop(e);
        site.open(current);
      } else if (site.enter && site.enter(e)) {
        stop(e);
      }
      return;
    }
    if (!list.length) return;   // nothing to move between: the key does what it always did (scrolls)
    stop(e);
    if (!live) {
      // The first press shows where the highlight is, without moving it.
      show(held ? current : start(list));
      return;
    }
    var next = best(current, list, dir);
    if (next) {
      show(next);
      if (site.across && (dir === 'left' || dir === 'right')) site.across(next, dir);
    } else if (site.edge) {
      site.edge(current, dir);
    }
  }

  // ------------------------------------------------------------------ the site

  function use(description) {
    site = description;
    window.addEventListener('keydown', onKey, true);
    // A mouse moving (the phone's trackpad) hides the highlight; the next arrow brings it back where it was.
    window.addEventListener('mousemove', function (e) { if (e.movementX || e.movementY) sleep(); }, true);
    window.addEventListener('mousedown', sleep, true);
    // A new page (these sites change pages without reloading): nothing highlighted until an arrow's pressed.
    setInterval(function () {
      if (location.href === address) return;
      address = location.href;
      clear();
      if (site.page) site.page();
    }, 300);
    setInterval(function () { if (!document.hidden) readSettings(); }, 2000);
    readSettings();
  }

  /**
   * A line about what's going on, kept in the page where Windows' accessibility (and so CouchTV's INSPECT) reads it:
   * how the layout was recognised, for working out a site's changes on the TV. Not shown.
   */
  var note = null;
  function status(text) {
    if (!note || !note.isConnected) {
      note = document.createElement('div');
      note.setAttribute('role', 'note');
      note.style.cssText = 'position:fixed;left:-9999px;top:0;width:1px;height:1px;overflow:hidden;';
      document.documentElement.appendChild(note);
    }
    var said = 'CouchTV: ' + text;
    if (note.getAttribute('aria-label') !== said) note.setAttribute('aria-label', said);
  }

  /**
   * "Hands off for a moment", as CouchTV's own banner says it, over the page while something's done in it for you
   * (picking a profile), so no key lands in the middle of it.
   */
  var banner = null, bannerTimer = 0;
  function busy(text, ms) {
    if (!banner || !banner.isConnected) {
      banner = document.createElement('div');
      banner.setAttribute('aria-hidden', 'true');
      banner.style.cssText = 'position:fixed;left:50%;top:26px;transform:translateX(-50%);z-index:2147483647;pointer-events:none;' +
        'display:flex;align-items:center;gap:18px;padding:14px 32px 16px 24px;border-radius:30px;' +
        'background:rgba(22,27,36,.95);border:2px solid rgba(62,166,255,.8);box-shadow:0 8px 30px rgba(0,0,0,.55);' +
        'font:600 24px "Segoe UI",sans-serif;color:#F4F6FA;transition:opacity .25s;';
      banner.innerHTML = '<div style="width:34px;height:34px;border-radius:50%;border:4px solid #3EA6FF;border-right-color:transparent;' +
        'box-sizing:border-box;animation:couchtv-turn 1.1s linear infinite"></div><div><div class="couchtv-busy-text"></div>' +
        '<div style="font-weight:400;font-size:18px;color:#A3ACBC;margin-top:2px">Hands off the remote for a moment</div></div>' +
        '<style>@keyframes couchtv-turn{to{transform:rotate(360deg)}}</style>';
      document.documentElement.appendChild(banner);
    }
    banner.querySelector('.couchtv-busy-text').textContent = text;
    banner.style.opacity = '1';
    clearTimeout(bannerTimer);
    bannerTimer = setTimeout(function () { banner.style.opacity = '0'; }, ms || 2500);
  }

  /** Clicks it the way a person would, so the site's own handlers (which change pages without reloading) run. */
  function click(el) {
    if (!el) return false;
    el.click();
    return true;
  }

  return {
    site: use, settings: function () { return settings; }, onSettings: onSettings,
    all: all, shown: shown, show: show, clear: clear, current: function () { return current; },
    candidates: candidates, first: first, click: click, nearest: best, status: status, busy: busy,
  };
})();
