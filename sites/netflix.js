// Netflix on the TV (with tv.js). Netflix changes its layout (and tries new ones on some accounts), so this goes by
// what things are on screen as much as by their names:
//  - "Who's watching?" picks the profile named in couchtv.ini (NetflixProfile, CouchTV unless set) by itself, once
//    each time Netflix is opened; choosing another profile from Netflix's menu afterwards still works.
//  - The arrows move between titles (a row pages or scrolls on at its end), through a title's details (Play, the
//    episodes, more like it), the profiles and the tabs along the top; OK opens one. The player keeps every key.
//  - With "Tidy Netflix" on, the big poster or trailer at the top of its pages (the billboard) goes, and so do the games
//    rows, so the rows start at the top. The billboard is found by name in the old layout, and as the big picture at
//    the top of the page in the new one.
'use strict';

(function () {
  // Titles and profiles, by name (the old layout's; the new one's are covered by "anything you can press" below).
  var TITLES = '.title-card-container, .titleCard--container, .titleCardList--container, .slider-item .title-card, ' +
               '.profile-link, [data-uia*="title-card"], [data-uia*="profile-link"], [data-uia*="select-profile"]';
  var ANYTHING = 'a[href], button, [role="button"], [role="link"], [role="tab"], [tabindex="0"]';
  var KNOWN_BILLBOARD = '.billboard-row, .billboard-presentation-tracking, [data-uia="billboard"], [data-uia*="billboard"], ' +
                        '[class*="billboard"]';
  var GAMES = /(^|\s)(mobile|netflix|party|tv|top 10 mobile) games\b|^games\b/i;
  var HEADERS = 'h2, h3, .rowHeader, [class*="rowHeader"], [class*="row-header"]';

  var tidy = false, picked = false;

  function playing() { return location.pathname.indexOf('/watch') === 0; }

  function big(el) {
    var r = el.getBoundingClientRect();
    return r.width >= 28 && r.height >= 24;
  }

  /** A title's details (or another dialog), open over the rows: the highlight stays inside it. */
  function dialog() {
    var open = document.querySelectorAll('.previewModal--wrapper.detail-modal, [role="dialog"], [aria-modal="true"]');
    for (var i = open.length - 1; i >= 0; i--) {
      var r = open[i].getBoundingClientRect();
      if (r.width > 300 && r.height > 200 && CouchTV.shown(open[i])) return open[i];
    }
    return null;
  }

  function hidden(el) { return !!el.closest('.couchtv-gone, [data-couchtv-gone]'); }

  // ------------------------------------------------------------------ the arrows

  CouchTV.site({
    radius: 8,
    pad: 4,
    wholeAcross: true,   // a row's half-shown titles at its ends are reached by paging (or scrolling) it on
    top: function () {
      var head = document.querySelector('.pinning-header-container, .main-header, [data-uia="header"], header, nav');
      var r = head && head.getBoundingClientRect();
      return r && r.height < innerHeight / 3 && r.top <= 4 ? Math.max(0, r.bottom) : 0;
    },

    cards: function () {
      var inside = dialog();
      if (inside) return CouchTV.all(inside, ANYTHING).filter(big);
      return CouchTV.all(document, TITLES + ', ' + ANYTHING).filter(function (el) {
        return big(el) && !el.matches('.handle, .handle *') && !hidden(el);
      });
    },

    passes: function () { return playing(); },

    // At a row's end: the old layout pages the row with its arrow; the new one has the next titles past the screen's
    // edge, scrolled to (or slid on with the row's own button).
    edge: function (from, dir) {
      if (dir !== 'left' && dir !== 'right') return;
      var row = from.closest('.lolomoRow, .rowContainer, .slider');
      var handle = row && row.querySelector(dir === 'right' ? '.handleNext' : '.handlePrev');
      if (handle) {
        CouchTV.click(handle);
        setTimeout(function () {
          var now = CouchTV.candidates().filter(function (el) { return row.contains(el); });
          now.sort(function (a, b) { return a.getBoundingClientRect().left - b.getBoundingClientRect().left; });
          if (now.length) CouchTV.show(dir === 'right' ? now[0] : now[now.length - 1]);
        }, 850);
        return;
      }
      var beyond = CouchTV.nearest(from, CouchTV.candidates(true), dir);
      if (!beyond) return;
      CouchTV.show(beyond);
      setTimeout(function () {
        var r = beyond.getBoundingClientRect();
        if (r.left >= 0 && r.right <= innerWidth) return;   // its row scrolled to it
        var box = beyond.parentElement;
        for (var i = 0; i < 6 && box && !box.querySelector('button, [role="button"]'); i++) box = box.parentElement;
        var buttons = box ? box.querySelectorAll('button, [role="button"]') : [];
        var want = dir === 'right' ? /next|right|more|forward/i : /prev|left|back/i;
        for (var j = 0; j < buttons.length; j++) {
          var b = buttons[j];
          if (want.test((b.getAttribute('aria-label') || '') + ' ' + b.className)) { CouchTV.click(b); return; }
        }
      }, 450);
    },

    open: function (el) {
      var link = el.matches('a, button, [role="button"], [role="link"], [role="tab"]') ? el
        : el.querySelector('a[href], button, [role="button"], [role="link"]');
      CouchTV.click(link || el);
    },
  });

  // ------------------------------------------------------------------ Who's watching?

  function asked() {
    var gate = document.querySelector('.profile-gate-container, .list-profiles, [data-uia="profile-choices-list"], [data-uia*="profile-gate"]');
    if (gate && CouchTV.shown(gate)) return true;
    // The new layout's question is plain text in a box with a made-up class: found by its words.
    var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (var n = walker.nextNode(); n; n = walker.nextNode()) {
      if (/^\s*who.s watching\??\s*$/i.test(n.nodeValue) && CouchTV.shown(n.parentElement)) return true;
    }
    return false;
  }

  /** The profile with this name on screen: the thing to press for it. */
  function profile(name) {
    var want = name.trim().toLowerCase();
    var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (var n = walker.nextNode(); n; n = walker.nextNode()) {
      if (n.nodeValue.trim().toLowerCase() !== want || !CouchTV.shown(n.parentElement)) continue;
      return n.parentElement.closest('a, button, [role="link"], [role="button"], [tabindex]') || n.parentElement;
    }
    return null;
  }

  try { picked = sessionStorage.getItem('couchtv-profile') === '1'; } catch (e) { /* no storage: pick each time */ }

  var lookUntil = Date.now() + 60000;   // it asks as Netflix opens; later, it's you choosing to switch

  function pickProfile() {
    var s = CouchTV.settings();
    if (picked || Date.now() > lookUntil || !s.netflixPick || !s.netflixProfile || !asked()) return;
    var choice = profile(s.netflixProfile);
    if (!choice) {
      CouchTV.status('netflix: asked who\'s watching, no profile named ' + s.netflixProfile);
      return;
    }
    picked = true;
    try { sessionStorage.setItem('couchtv-profile', '1'); } catch (e) { /* fine */ }
    CouchTV.status('netflix: picked the profile ' + s.netflixProfile);
    CouchTV.click(choice);
  }

  // ------------------------------------------------------------------ tidy

  var style = document.createElement('style');
  var RULES =
    KNOWN_BILLBOARD + ', .couchtv-gone, [data-couchtv-gone] { display: none !important; }' +
    // The old layout's rows sat over the billboard's bottom edge; without it they start under the header instead.
    '.lolomo { margin-top: 0 !important; padding-top: 96px !important; }';

  /** The big picture at the top of the page (the new layout's billboard): its box, or null. */
  function bigPicture() {
    if (dialog() || asked() || /^\/(search|title|watch)/.test(location.pathname)) return null;
    var best = null, area = 0;
    var media = document.querySelectorAll('img, video, picture, [style*="background-image"]');
    for (var i = 0; i < media.length && i < 500; i++) {
      var r = media[i].getBoundingClientRect();
      if (r.width < innerWidth * 0.55 || r.height < innerHeight * 0.3 || r.top + scrollY > innerHeight * 0.6) continue;
      if (r.width * r.height > area) { best = media[i]; area = r.width * r.height; }
    }
    if (!best) return null;
    // Out to the box around it (its Play and More Info buttons too), short of anything holding a row of titles.
    var box = best, b = best.getBoundingClientRect();
    for (var p = best.parentElement; p && p !== document.body && p !== document.documentElement; p = p.parentElement) {
      var pr = p.getBoundingClientRect();
      if (pr.height > Math.max(b.height * 1.6, innerHeight) || pr.width > innerWidth + 40) break;
      if (p.querySelectorAll('a[href]').length > 6) break;
      box = p;
    }
    return box;
  }

  function tidyUp() {
    pickProfile();
    if (!style.isConnected) document.documentElement.appendChild(style);
    var rules = tidy ? RULES : '';
    if (style.textContent !== rules) style.textContent = rules;
    if (!tidy) {
      CouchTV.status('netflix: tidy off');
      return;
    }
    var gone = document.querySelector('[data-couchtv-gone]');
    if (!gone) {
      var picture = playing() ? null : bigPicture();
      if (picture) picture.setAttribute('data-couchtv-gone', 'billboard');
    }
    // The hidden billboard would still play its trailer (and be heard).
    document.querySelectorAll('[data-couchtv-gone] video, .billboard-row video, .billboard video, [data-uia*="billboard"] video').forEach(function (v) {
      if (!v.paused || !v.muted) { v.muted = true; v.pause(); }
    });
    var headers = document.querySelectorAll(HEADERS);
    for (var i = 0; i < headers.length; i++) {
      var head = headers[i];
      if (!GAMES.test(head.textContent.trim())) continue;
      // The row: out from its heading to the box that holds no other row's heading.
      var row = head;
      while (row.parentElement && row.parentElement !== document.body && row.parentElement.querySelectorAll(HEADERS).length <= 1) row = row.parentElement;
      row.classList.add('couchtv-gone');
    }
    document.querySelectorAll('.lolomoRow[data-list-context*="game"]').forEach(function (row) { row.classList.add('couchtv-gone'); });
    var old = !!document.querySelector('.lolomo, .billboard-row');
    gone = document.querySelector('[data-couchtv-gone]');
    CouchTV.status('netflix: ' + (old ? 'old' : 'new') + ' layout, tidy on, billboard ' +
      (gone ? 'hidden (' + gone.tagName.toLowerCase() + '.' + String(gone.className).trim().split(/\s+/).join('.') + ')' : old ? 'hidden by name' : 'not found'));
  }

  CouchTV.onSettings(function (s) {
    tidy = !!s.netflixTidy;
    if (!tidy) {
      document.querySelectorAll('.couchtv-gone').forEach(function (el) { el.classList.remove('couchtv-gone'); });
      document.querySelectorAll('[data-couchtv-gone]').forEach(function (el) { el.removeAttribute('data-couchtv-gone'); });
    }
    tidyUp();
  });
  // A new page: its own billboard to find.
  var page = location.pathname;
  setInterval(function () {
    if (location.pathname !== page) {
      page = location.pathname;
      document.querySelectorAll('[data-couchtv-gone]').forEach(function (el) { el.removeAttribute('data-couchtv-gone'); });
    }
    tidyUp();
  }, 500);
})();
