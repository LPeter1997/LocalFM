/* LocalFM for TVs and browsers.
 *
 * Talks to LocalFM on a computer at home: a web socket carries the vibe protocol's JSON messages (hello → welcome,
 * then "lib" requests), and audio and pictures come over plain HTTP with byte ranges. Plays on its own with one
 * <audio> element. Written for old TV engines (Chromium 68 on webOS 5): ES2017 only, so no ?. or ??, no flat().
 * Works with a TV remote (arrows, OK, Back, media keys), a pointer, a mouse or touch.
 */
(function () {
  'use strict';

  var PROTOCOL = 1;
  var ICONS = {
    play: ['M8 5.5v13l10.5-6.5z', true],
    pause: ['M7 5h3.5v14H7zM13.5 5H17v14h-3.5z', true],
    next: ['M5 6l9 6-9 6zM16 6h2.5v12H16z', true],
    prev: ['M19 6l-9 6 9 6zM5.5 6H8v12H5.5z', true],
    shuffle: ['M3 7h3.5c2 0 3 1 4.5 3.5l2 3c1.5 2.5 2.5 3.5 4.5 3.5H21M3 17h3.5c1.2 0 2-.4 2.8-1.3M14.7 8.3c.8-.9 1.6-1.3 2.8-1.3H21M18 4l3 3-3 3M18 14l3 3-3 3'],
    repeat: ['M4 11V9.5A3.5 3.5 0 0 1 7.5 6H20M17 3l3 3-3 3M20 13v1.5a3.5 3.5 0 0 1-3.5 3.5H4M7 21l-3-3 3-3'],
    album: ['M12 3a9 9 0 1 1 0 18 9 9 0 1 1 0-18M12 10a2 2 0 1 1 0 4 2 2 0 1 1 0-4'],
    artist: ['M12 4a4 4 0 1 1 0 8 4 4 0 1 1 0-8M4.5 20c.5-3.5 3.5-5.5 7.5-5.5s7 2 7.5 5.5'],
    tracks: ['M9 17.5V5.5l11-2v12M9 17.5a3 3 0 1 1-6 0 3 3 0 1 1 6 0M20 15.5a3 3 0 1 1-6 0 3 3 0 1 1 6 0'],
    playlist: ['M3.5 6h12M3.5 11h12M3.5 16h7M18 9v8.5M18 17.5a2.2 2.2 0 1 1-4.4 0 2.2 2.2 0 1 1 4.4 0M18 9l3-1'],
    search: ['M10.5 4a6.5 6.5 0 1 1 0 13 6.5 6.5 0 1 1 0-13M15.5 15.5l5 5'],
    wave: ['M3 12h2M7 8v8M11 4v16M15 7v10M19 10v4M21 12h0'],
    note: ['M9 18V6l10-2v12M9 18a3 3 0 1 1-6 0 3 3 0 1 1 6 0M19 16a3 3 0 1 1-6 0 3 3 0 1 1 6 0']
  };

  // ------------------------------------------------------------------ small helpers

  function $(id) { return document.getElementById(id); }

  /** el('div.a.b', {attr}, children...) — text children are escaped by construction. */
  function el(spec, attrs) {
    var parts = spec.split('.');
    var node = document.createElement(parts[0] || 'div');
    if (parts.length > 1) node.className = parts.slice(1).join(' ');
    var kids = Array.prototype.slice.call(arguments, 2);
    if (attrs) {
      Object.keys(attrs).forEach(function (k) {
        var v = attrs[k];
        if (v === undefined || v === null || v === false) return;
        if (k === 'onclick') node.addEventListener('click', v);
        else if (k === 'style') node.setAttribute('style', v);
        else if (k in node && typeof v !== 'string') node[k] = v;
        else node.setAttribute(k, v === true ? '' : v);
      });
    }
    add(node, kids);
    return node;
  }
  function add(node, kids) {
    kids.forEach(function (c) {
      if (c === null || c === undefined || c === false) return;
      if (Array.isArray(c)) add(node, c);
      else node.appendChild(typeof c === 'string' || typeof c === 'number' ? document.createTextNode(String(c)) : c);
    });
  }
  function icon(name) {
    var def = ICONS[name];
    var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('class', 'icon' + (def[1] ? ' filled' : ''));
    var p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    p.setAttribute('d', def[0]);
    svg.appendChild(p);
    return svg;
  }
  function setIcon(holder, name) {
    holder.innerHTML = '';
    holder.appendChild(icon(name));
  }
  function time(ms) {
    if (!ms || ms < 0 || !isFinite(ms)) return '0:00';
    var s = Math.floor(ms / 1000);
    var h = Math.floor(s / 3600);
    var m = Math.floor((s % 3600) / 60);
    var ss = String(s % 60).padStart(2, '0');
    return h > 0 ? h + ':' + String(m).padStart(2, '0') + ':' + ss : m + ':' + ss;
  }
  function year(date) { return date ? String(date).slice(0, 4) : ''; }
  function store(key, value) {
    try {
      if (value === null || value === undefined) localStorage.removeItem(key);
      else localStorage.setItem(key, value);
    } catch (e) { /* private mode */ }
  }
  function load(key) {
    try { return localStorage.getItem(key); } catch (e) { return null; }
  }
  function isTv() { return /Web0S|webOS|SmartTV|NetCast/i.test(navigator.userAgent); }
  function deviceName() {
    if (/Web0S|webOS/i.test(navigator.userAgent)) return 'LG TV';
    if (/SmartTV|Tizen/i.test(navigator.userAgent)) return 'Smart TV';
    if (/Android/i.test(navigator.userAgent)) return 'Android browser';
    if (/iPhone|iPad/i.test(navigator.userAgent)) return 'iPhone browser';
    return 'Web browser';
  }

  // ------------------------------------------------------------------ where LocalFM is

  // Served by LocalFM itself: the host is where the page came from. Packaged (the webOS app, file://): the
  // address the user typed once.
  var packaged = location.protocol === 'file:';
  var base = packaged ? load('lfm.host') : location.protocol + '//' + location.host;

  function tokenKey() { return 'lfm.token:' + base; }
  function token() { return load(tokenKey()); }
  function mediaUrl(path) { return base + path + (path.indexOf('?') < 0 ? '?k=' : '&k=') + encodeURIComponent(token() || ''); }
  function imgUrl(id) { return id ? mediaUrl('/img/' + encodeURIComponent(id)) : null; }

  // ------------------------------------------------------------------ connection

  var conn = {
    ws: null,
    ready: false,
    next: 1,
    pending: {},
    waiting: [],
    retry: 0,
    hostName: '',
    pin: null,

    open: function () {
      var self = this;
      if (!base) { screens.host(); return; }
      var url = base.replace(/^http/, 'ws') + '/ws';
      var ws;
      try { ws = new WebSocket(url); } catch (e) { self.lost(); return; }
      self.ws = ws;
      ws.onopen = function () {
        var secret = self.pin ? 'pin:' + self.pin : token();
        if (!secret) { ws.close(); screens.pair(); return; }
        ws.send(JSON.stringify({ t: 'hello', version: PROTOCOL, role: 'Control', secret: secret, name: deviceName() }));
      };
      ws.onmessage = function (e) {
        var m;
        try { m = JSON.parse(e.data); } catch (x) { return; }
        self.onMessage(m);
      };
      ws.onclose = function () {
        if (self.ws !== ws) return;
        self.ws = null;
        var was = self.ready;
        self.ready = false;
        Object.keys(self.pending).forEach(function (k) { self.pending[k].reject(new Error('disconnected')); });
        self.pending = {};
        if (self.rejected) return;
        if (was || self.retry > 0 || !screens.pairing) self.lost();
      };
    },

    onMessage: function (m) {
      if (m.t === 'welcome') {
        if (this.pin) { store(tokenKey(), m.dataToken); this.pin = null; }
        this.hostName = m.hostName;
        this.ready = true;
        this.retry = 0;
        this.rejected = false;
        $('hostname').textContent = 'On ' + m.hostName;
        screens.close();
        var w = this.waiting;
        this.waiting = [];
        w.forEach(function (f) { f(); });
        app.connected();
      } else if (m.t === 'rejected') {
        this.rejected = true;
        var hadPin = !!this.pin;
        this.pin = null;
        if (!hadPin) store(tokenKey(), null);
        screens.pair(m.reason);
      } else if (m.t === 'libReply') {
        var p = this.pending[m.request];
        if (!p) return;
        delete this.pending[m.request];
        if (m.error) p.reject(new Error(m.error)); else p.resolve(m);
      }
    },

    lost: function () {
      var self = this;
      self.retry++;
      if (self.retry > 2) screens.offline();
      var delay = Math.min(10000, 1000 * self.retry);
      setTimeout(function () { if (!self.ws) self.open(); }, delay);
    },

    /** A library request; resolves with the reply. Waits for the connection if needed. */
    ask: function (op, fields) {
      var self = this;
      return new Promise(function (resolve, reject) {
        var send = function () {
          var id = self.next++;
          var msg = Object.assign({ t: 'lib', request: id, op: op }, fields || {});
          self.pending[id] = { resolve: resolve, reject: reject };
          self.ws.send(JSON.stringify(msg));
        };
        if (self.ready) send(); else self.waiting.push(send);
      });
    },

    pairWith: function (pin) {
      this.pin = pin;
      this.rejected = false;
      if (this.ws) { try { this.ws.close(); } catch (e) { /* closing */ } this.ws = null; }
      this.open();
    }
  };

  // ------------------------------------------------------------------ full-screen screens

  var screens = {
    pairing: false,
    show: function (card, focusSel) {
      var o = $('overlay');
      o.innerHTML = '';
      o.appendChild(card);
      o.classList.remove('hidden');
      var f = card.querySelector(focusSel || '.f');
      if (f) setTimeout(function () { f.focus(); }, 50);
    },
    close: function () {
      this.pairing = false;
      $('overlay').classList.add('hidden');
    },
    connecting: function () {
      this.show(el('div.card', null,
        el('img', { src: 'icon.svg', alt: '' }),
        el('h1', null, 'Connecting…'),
        el('p', null, 'Reaching LocalFM at ' + base),
        el('div.spinner')));
    },
    offline: function () {
      if (this.pairing) return;
      this.show(el('div.card', null,
        el('img', { src: 'icon.svg', alt: '' }),
        el('h1', null, 'Can’t reach LocalFM'),
        el('p', null, 'Make sure LocalFM is running on the computer (' + base.replace(/^https?:\/\//, '') +
          ') and that "Share my library on this network" is on in Sources. Trying again…'),
        el('div.spinner'),
        packaged ? el('p', null, el('button.f.pill', { onclick: function () { screens.host(); } }, 'Change computer')) : null));
    },
    pair: function (error) {
      this.pairing = true;
      var input = el('input.f.field.pin', { type: 'text', inputmode: 'numeric', maxlength: '6', autocomplete: 'off', placeholder: '······' });
      var err = el('div.err', null, error || '');
      var go = function () {
        var pin = input.value.replace(/\D/g, '');
        if (pin.length !== 6) { err.textContent = 'The PIN has six digits.'; return; }
        err.textContent = '';
        conn.pairWith(pin);
      };
      input.addEventListener('keydown', function (e) { if (e.keyCode === 13) { e.preventDefault(); go(); } });
      this.show(el('div.card', null,
        el('img', { src: 'icon.svg', alt: '' }),
        el('h1', null, 'Pair with LocalFM'),
        el('p', null, 'On the computer, open LocalFM → Sources → TV and other devices, choose “Pair a device” and enter the PIN it shows.'),
        input, err,
        el('button.f.pill.primary', { onclick: go }, 'Pair'),
        packaged ? el('button.f.pill', { onclick: function () { screens.host(); } }, 'Change computer') : null), '.field');
    },
    host: function () {
      this.pairing = true;
      var input = el('input.f.field', { type: 'text', autocomplete: 'off', placeholder: '192.168.1.20:47812', value: base ? base.replace(/^https?:\/\//, '') : '' });
      var err = el('div.err');
      var go = function () {
        var v = input.value.trim().replace(/\/+$/, '');
        if (!v) return;
        if (!/^https?:\/\//.test(v)) v = 'http://' + v;
        if (!/:\d+$/.test(v.replace(/^https?:\/\//, '').replace(/\[.*\]/, ''))) v += ':47812';
        err.textContent = 'Checking…';
        fetch(v + '/api/info').then(function (r) { return r.json(); }).then(function (info) {
          if (info.app !== 'LocalFM') throw new Error();
          base = v;
          store('lfm.host', v);
          err.textContent = '';
          if (conn.ws) { try { conn.ws.close(); } catch (e) { /* closing */ } conn.ws = null; }
          conn.rejected = false;
          if (token()) { screens.connecting(); conn.open(); } else screens.pair();
        }).catch(function () {
          err.textContent = 'No LocalFM there. Check the address, and that sharing is on in LocalFM → Sources.';
        });
      };
      input.addEventListener('keydown', function (e) { if (e.keyCode === 13) { e.preventDefault(); go(); } });
      this.show(el('div.card', null,
        el('img', { src: 'icon.svg', alt: '' }),
        el('h1', null, 'Which computer?'),
        el('p', null, 'Type the address LocalFM shows in Sources → TV and other devices on the computer with your music.'),
        input, err,
        el('button.f.pill.primary', { onclick: go }, 'Connect')), '.field');
    }
  };

  // ------------------------------------------------------------------ player

  var audio = $('audio');
  var player = {
    queue: [],
    index: -1,
    shuffle: false,
    repeat: false,
    triedWav: false,
    onChange: [],

    get current() { return this.index >= 0 ? this.queue[this.index] : null; },

    /** Plays the list from item i (a shuffled copy when shuffling). */
    playList: function (tracks, i) {
      if (!tracks.length) return;
      var list = tracks.slice();
      var start = i || 0;
      if (this.shuffle) {
        var first = list.splice(start, 1)[0];
        for (var k = list.length - 1; k > 0; k--) {
          var j = Math.floor(Math.random() * (k + 1));
          var t = list[k]; list[k] = list[j]; list[j] = t;
        }
        list.unshift(first);
        start = 0;
      }
      this.queue = list;
      this.load(start, true, 0);
    },

    shuffleAll: function (tracks) {
      this.shuffle = true;
      this.playList(tracks, Math.floor(Math.random() * tracks.length));
    },

    load: function (i, play, positionMs) {
      if (i < 0 || i >= this.queue.length) return;
      this.index = i;
      var t = this.queue[i];
      this.triedWav = !canPlay(t.codec);
      audio.src = mediaUrl('/media/' + encodeURIComponent(t.id) + (this.triedWav ? '.wav' : ''));
      audio.volume = gain(t);
      if (positionMs) {
        var seekTo = positionMs / 1000;
        audio.addEventListener('loadedmetadata', function once() {
          audio.removeEventListener('loadedmetadata', once);
          try { audio.currentTime = seekTo; } catch (e) { /* not seekable yet */ }
        });
      }
      if (play) audio.play().catch(function () { /* autoplay blocked until a key press */ });
      this.changed();
      this.save();
    },

    toggle: function () {
      if (!this.current) return;
      if (audio.paused) audio.play().catch(function () {}); else audio.pause();
    },
    next: function () {
      if (this.index + 1 < this.queue.length) this.load(this.index + 1, true);
      else if (this.repeat && this.queue.length) this.load(0, true);
    },
    prev: function () {
      if (audio.currentTime > 3 || this.index === 0) { audio.currentTime = 0; return; }
      this.load(this.index - 1, true);
    },
    seekBy: function (seconds) {
      if (!this.current || !isFinite(audio.duration)) return;
      audio.currentTime = Math.max(0, Math.min(audio.duration - 0.5, audio.currentTime + seconds));
    },
    changed: function () { this.onChange.forEach(function (f) { f(); }); },

    save: function () {
      if (!this.current) return;
      var q = this.queue.slice(Math.max(0, this.index - 50), this.index + 200);
      store('lfm.queue:' + base, JSON.stringify({
        queue: q, index: this.index - Math.max(0, this.index - 50), pos: Math.round(audio.currentTime * 1000) || 0,
        shuffle: this.shuffle, repeat: this.repeat
      }));
    },
    restore: function () {
      var raw = load('lfm.queue:' + base);
      if (!raw || this.current) return;
      try {
        var s = JSON.parse(raw);
        this.queue = s.queue || [];
        this.shuffle = !!s.shuffle;
        this.repeat = !!s.repeat;
        if (this.queue.length) this.load(Math.min(s.index || 0, this.queue.length - 1), false, s.pos);
      } catch (e) { /* old state */ }
    }
  };

  var types = {
    mp3: 'audio/mpeg',
    aac: 'audio/mp4; codecs="mp4a.40.2"',
    flac: 'audio/flac',
    vorbis: 'audio/ogg; codecs="vorbis"',
    opus: 'audio/ogg; codecs="opus"',
    pcm_s16le: 'audio/wav; codecs="1"',
    pcm_u8: 'audio/wav; codecs="1"'
  };
  /** Whether this engine plays the codec itself; otherwise LocalFM decodes it to WAV for us. */
  function canPlay(codec) {
    var type = types[codec];
    if (!type) return false;
    try { return audio.canPlayType(type) !== ''; } catch (e) { return false; }
  }
  /** ReplayGain as far as a volume can take it: quieter only, never louder than full scale. */
  function gain(t) {
    if (t.trackGainDb === undefined || t.trackGainDb === null) return 1;
    var g = Math.pow(10, t.trackGainDb / 20);
    if (t.trackPeak) g = Math.min(g, 1 / t.trackPeak);
    return Math.max(0.05, Math.min(1, g));
  }

  audio.addEventListener('ended', function () { player.next(); });
  audio.addEventListener('error', function () {
    // The engine couldn't play the original after all: ask LocalFM for it decoded.
    var t = player.current;
    if (!t || player.triedWav) return;
    player.triedWav = true;
    var at = audio.currentTime;
    audio.src = mediaUrl('/media/' + encodeURIComponent(t.id) + '.wav');
    if (at) audio.addEventListener('loadedmetadata', function once() { audio.removeEventListener('loadedmetadata', once); audio.currentTime = at; });
    audio.play().catch(function () {});
  });
  audio.addEventListener('play', function () { player.changed(); });
  audio.addEventListener('pause', function () { player.changed(); player.save(); });
  setInterval(function () { if (!audio.paused) player.save(); }, 10000);

  // ------------------------------------------------------------------ views

  var main = $('main');
  var history = [];
  var view = null;

  function tile(cover, name, sub, onclick, round) {
    var c = el('div.cover' + (round ? '.round-img' : ''));
    lazyCover(c, cover, round ? 'artist' : 'album');
    return el('button.f.tile', { onclick: onclick }, c, el('div.name.ellipsis', null, name), sub ? el('div.sub.ellipsis', null, sub) : null);
  }

  var observer = 'IntersectionObserver' in window ? new IntersectionObserver(function (entries) {
    entries.forEach(function (e) {
      if (!e.isIntersecting) return;
      var c = e.target;
      observer.unobserve(c);
      c.style.backgroundImage = 'url("' + c.getAttribute('data-src') + '")';
    });
  }, { rootMargin: '600px' }) : null;

  function lazyCover(c, id, fallback) {
    var url = imgUrl(id);
    if (!url) {
      c.appendChild(el('div.ph', null, icon(fallback)));
      return;
    }
    if (observer) { c.setAttribute('data-src', url); observer.observe(c); }
    else c.style.backgroundImage = 'url("' + url + '")';
  }

  /** Track rows, rendered a page at a time so thousands of songs stay quick on a TV. */
  function trackRows(tracks, opts) {
    opts = opts || {};
    var box = el('div.rows');
    var shown = 0;
    var lastDisc = null;
    var more = function () {
      var end = Math.min(tracks.length, shown + 150);
      for (var i = shown; i < end; i++) box.appendChild(row(tracks[i], i));
      shown = end;
    };
    var row = function (t, i) {
      var frag = document.createDocumentFragment();
      if (opts.discs && t.disc !== lastDisc) {
        if (lastDisc !== null || opts.multiDisc) frag.appendChild(el('div.disc', null, 'Disc ' + t.disc));
        lastDisc = t.disc;
      }
      var cover = null;
      if (opts.covers) { cover = el('div.cover.small'); lazyCover(cover, t.cover, 'album'); }
      var r = el('button.f.row', {
        'data-id': t.id,
        onclick: function () { player.playList(tracks, i); }
      },
        opts.covers ? cover : el('span.n', null, opts.numbers ? String(t.position) : String(i + 1)),
        el('div.main', null, el('div.title.ellipsis', null, t.title),
          el('div.sub.ellipsis', null, opts.album ? [t.artist || '', t.album ? ' · ' + t.album : ''] : (t.artist || ''))),
        el('span.dur', null, time(t.durationMs)));
      r.addEventListener('focus', function () { if (shown < tracks.length && i > shown - 25) more(); });
      frag.appendChild(r);
      return frag;
    };
    if (opts.discs) opts.multiDisc = tracks.some(function (t) { return t.disc > 1; });
    more();
    markPlaying(box);
    return box;
  }

  function markPlaying(scope) {
    var cur = player.current;
    var rows = (scope || main).querySelectorAll('.row');
    for (var i = 0; i < rows.length; i++) rows[i].classList.toggle('playing', !!cur && rows[i].getAttribute('data-id') === cur.id);
  }

  function loading() { return el('div.spinner'); }
  function failed(e) { return el('div.empty', null, 'Could not load this: ' + (e && e.message ? e.message : 'connection lost') + '.'); }

  var views = {
    albums: function () {
      var box = el('div', null, el('h1', null, 'Albums'), loading());
      conn.ask('albums').then(function (r) {
        box.lastChild.remove();
        if (!r.albums.length) { box.appendChild(el('div.empty', null, 'No albums yet. Add music folders in LocalFM on the computer.')); return; }
        box.appendChild(el('div.grid', null, r.albums.map(function (a) {
          return tile(a.cover, a.title, [a.artist, year(a.date)].filter(Boolean).join(' · '), function () { go('album', { id: a.id }); });
        })));
        restoreFocus();
      }).catch(function (e) { box.lastChild.replaceWith(failed(e)); });
      return box;
    },

    album: function (p) {
      var box = el('div', null, loading());
      conn.ask('album', { id: p.id }).then(function (r) {
        var a = r.album;
        var c = el('div.cover');
        lazyCover(c, a.cover, 'album');
        box.innerHTML = '';
        box.appendChild(el('div.hero', null, c, el('div.meta', null,
          el('div.kind', null, 'Album'),
          el('h1', null, a.title),
          el('div.line', null, [a.artist, year(a.date), a.edition, r.tracks.length + (r.tracks.length === 1 ? ' song' : ' songs')].filter(Boolean).join(' · ')),
          el('div', null,
            el('button.f.pill.primary', { onclick: function () { player.shuffle = false; player.playList(r.tracks, 0); } }, icon('play'), 'Play'),
            el('button.f.pill', { onclick: function () { player.shuffleAll(r.tracks); } }, icon('shuffle'), 'Shuffle')))));
        box.appendChild(trackRows(r.tracks, { numbers: true, discs: true }));
        restoreFocus();
      }).catch(function (e) { box.innerHTML = ''; box.appendChild(failed(e)); });
      return box;
    },

    artists: function () {
      var box = el('div', null, el('h1', null, 'Artists'), loading());
      conn.ask('artists').then(function (r) {
        box.lastChild.remove();
        box.appendChild(el('div.grid', null, r.artists.map(function (a) {
          return tile(a.photo, a.name, a.albumCount ? a.albumCount + (a.albumCount === 1 ? ' album' : ' albums') : a.trackCount + ' songs',
            function () { go('artist', { id: a.id }); }, true);
        })));
        restoreFocus();
      }).catch(function (e) { box.lastChild.replaceWith(failed(e)); });
      return box;
    },

    artist: function (p) {
      var box = el('div', null, loading());
      conn.ask('artist', { id: p.id }).then(function (r) {
        var a = r.artist;
        var c = el('div.cover.round-img');
        lazyCover(c, a.photo, 'artist');
        var facts = [a.type, a.country, a.begin ? (a.begin.slice(0, 4) + (a.end ? '–' + a.end.slice(0, 4) : '')) : null].concat(a.genres.slice(0, 3));
        box.innerHTML = '';
        box.appendChild(el('div.hero', null, c, el('div.meta', null,
          el('div.kind', null, 'Artist'),
          el('h1', null, a.name),
          el('div.line', null, facts.filter(Boolean).join(' · ')),
          el('div', null, el('button.f.pill.primary', { onclick: function () { playArtist(r.albums, false); } }, icon('play'), 'Play'),
            el('button.f.pill', { onclick: function () { playArtist(r.albums, true); } }, icon('shuffle'), 'Shuffle')))));
        var text = a.bio || a.description;
        if (text) {
          var bio = el('button.f.bio.clamped', { onclick: function () { bio.classList.toggle('clamped'); } }, text);
          box.appendChild(bio);
        }
        if (a.members.length) box.appendChild(el('div.bio', null, 'Members: ' + a.members.join(', ')));
        box.appendChild(el('h2', null, 'Albums'));
        box.appendChild(el('div.grid', null, r.albums.map(function (al) {
          return tile(al.cover, al.title, year(al.date), function () { go('album', { id: al.id }); });
        })));
        restoreFocus();
      }).catch(function (e) { box.innerHTML = ''; box.appendChild(failed(e)); });
      return box;
    },

    songs: function () {
      var box = el('div', null, el('h1', null, 'Songs'), loading());
      conn.ask('tracks').then(function (r) {
        box.lastChild.remove();
        box.appendChild(el('div', { style: 'margin-bottom:1.5rem' },
          el('button.f.pill.primary', { onclick: function () { player.shuffleAll(r.tracks); } }, icon('shuffle'), 'Shuffle all')));
        box.appendChild(trackRows(r.tracks, { covers: true, album: true }));
        restoreFocus();
      }).catch(function (e) { box.lastChild.replaceWith(failed(e)); });
      return box;
    },

    playlists: function () {
      var box = el('div', null, el('h1', null, 'Playlists'), loading());
      conn.ask('playlists').then(function (r) {
        box.lastChild.remove();
        if (!r.playlists.length) { box.appendChild(el('div.empty', null, 'No playlists. Make them in LocalFM on the computer.')); return; }
        box.appendChild(el('div.rows', null, r.playlists.map(function (p) {
          return el('button.f.row', { onclick: function () { go('playlist', { id: p.id }); } },
            el('span.n', null, icon('playlist')),
            el('div.main', null, el('div.title.ellipsis', null, p.name), p.description ? el('div.sub.ellipsis', null, p.description) : null),
            el('span.dur', null, p.entryCount + (p.entryCount === 1 ? ' song' : ' songs')));
        })));
        restoreFocus();
      }).catch(function (e) { box.lastChild.replaceWith(failed(e)); });
      return box;
    },

    playlist: function (p) {
      var box = el('div', null, loading());
      conn.ask('playlist', { id: p.id }).then(function (r) {
        box.innerHTML = '';
        box.appendChild(el('div.hero', null, el('div.meta', null,
          el('div.kind', null, 'Playlist'),
          el('h1', null, r.playlist.name),
          el('div.line', null, r.tracks.length + (r.tracks.length === 1 ? ' song' : ' songs')),
          el('div', null,
            el('button.f.pill.primary', { onclick: function () { player.shuffle = false; player.playList(r.tracks, 0); } }, icon('play'), 'Play'),
            el('button.f.pill', { onclick: function () { player.shuffleAll(r.tracks); } }, icon('shuffle'), 'Shuffle')))));
        box.appendChild(trackRows(r.tracks, { covers: true, album: true }));
        restoreFocus();
      }).catch(function (e) { box.innerHTML = ''; box.appendChild(failed(e)); });
      return box;
    },

    search: function (p) {
      var input = el('input.f.searchbox', { type: 'search', placeholder: 'Albums, artists, songs', value: p.q || '', autocomplete: 'off' });
      var results = el('div');
      var timer = null;
      var seq = 0;
      var run = function () {
        var q = input.value.trim();
        p.q = q;
        var mine = ++seq;
        if (!q) { results.innerHTML = ''; return; }
        conn.ask('search', { search: q }).then(function (r) {
          if (mine !== seq) return;
          results.innerHTML = '';
          if (!r.albums.length && !r.artists.length && !r.tracks.length) { results.appendChild(el('div.empty', null, 'Nothing found for “' + q + '”.')); return; }
          if (r.tracks.length) results.appendChild(el('div', null, el('h2', null, 'Songs'), trackRows(r.tracks.slice(0, 8), { covers: true, album: true })));
          if (r.artists.length) results.appendChild(el('div', null, el('h2', null, 'Artists'), el('div.grid', null, r.artists.slice(0, 12).map(function (a) {
            return tile(a.photo, a.name, null, function () { go('artist', { id: a.id }); }, true);
          }))));
          if (r.albums.length) results.appendChild(el('div', null, el('h2', null, 'Albums'), el('div.grid', null, r.albums.slice(0, 18).map(function (a) {
            return tile(a.cover, a.title, a.artist, function () { go('album', { id: a.id }); });
          }))));
          restoreFocus();
        }).catch(function () {});
      };
      input.addEventListener('input', function () { clearTimeout(timer); timer = setTimeout(run, 350); });
      input.addEventListener('keydown', function (e) {
        if (e.keyCode === 13) { clearTimeout(timer); run(); input.blur(); focusFirst(results); }
      });
      if (p.q) run();
      setTimeout(function () { if (!p.focus) input.focus(); }, 50);
      return el('div', null, el('h1', null, 'Search'), input, results);
    },

    nowplaying: function () {
      return nowPlaying.build();
    }
  };

  function playArtist(albums, shuffle) {
    Promise.all(albums.map(function (a) { return conn.ask('album', { id: a.id }); })).then(function (rs) {
      var all = [];
      rs.forEach(function (r) { all = all.concat(r.tracks); });
      if (shuffle) player.shuffleAll(all); else { player.shuffle = false; player.playList(all, 0); }
    });
  }

  // ------------------------------------------------------------------ now playing

  var nowPlaying = {
    box: null,
    lyricsFor: null,
    lines: null,
    synced: false,
    lastLine: -1,

    build: function () {
      var t = player.current;
      if (!t) return el('div', null, el('h1', null, 'Now playing'), el('div.empty', null, 'Nothing is playing. Pick an album or a song.'));
      var c = el('div.cover');
      lazyCover(c, t.cover, 'album');
      var bg = el('div.npbg');
      if (t.cover) bg.style.backgroundImage = 'url("' + imgUrl(t.cover) + '")';
      var playBtn = el('button.f.round.big', { id: 'npplay', onclick: function () { player.toggle(); } }, icon(audio.paused ? 'play' : 'pause'));
      var right = el('div.npright', { id: 'npright' });
      this.box = el('div.np', null, bg,
        el('div.npleft', null,
          el('div.npcoverbox', null, c),
          el('div.nptitle.ellipsis', null, t.title),
          el('div.npartist.ellipsis', null, [t.artist, t.album].filter(Boolean).join(' · ')),
          el('div.npprogress', null, el('span.time', { id: 'nppos' }, '0:00'), el('div.track', null, el('div.fill', { id: 'npfill' })), el('span.time', { id: 'npdur' }, time(t.durationMs))),
          el('div.npcontrols', null,
            el('button.f.round' + (player.shuffle ? '.on' : ''), { title: 'Shuffle', onclick: function (e) { player.shuffle = !player.shuffle; e.currentTarget.classList.toggle('on', player.shuffle); } }, icon('shuffle')),
            el('button.f.round', { title: 'Previous', onclick: function () { player.prev(); } }, icon('prev')),
            playBtn,
            el('button.f.round', { title: 'Next', onclick: function () { player.next(); } }, icon('next')),
            el('button.f.round' + (player.repeat ? '.on' : ''), { title: 'Repeat', onclick: function (e) { player.repeat = !player.repeat; e.currentTarget.classList.toggle('on', player.repeat); } }, icon('repeat')))),
        right);
      this.showLyrics(t);
      setTimeout(function () { playBtn.focus(); }, 30);
      return this.box;
    },

    showLyrics: function (t) {
      var self = this;
      var right = this.box.querySelector('#npright');
      self.lines = null;
      self.lastLine = -1;
      self.lyricsFor = t.id;
      conn.ask('lyrics', { id: t.id }).then(function (r) {
        if (self.lyricsFor !== t.id || !document.body.contains(right)) return;
        var ly = r.lyrics;
        if (!ly || !ly.lines.length) { self.upNext(right); return; }
        self.synced = ly.lines.some(function (l) { return l.ms !== null && l.ms !== undefined; });
        var box = el('div.lyrics' + (self.synced ? '' : '.plain'), null, ly.lines.map(function (l) { return el('div.l', null, l.text || ' '); }));
        self.lines = ly.lines;
        right.innerHTML = '';
        right.className = 'npright' + (self.synced ? '' : ' scroll');
        right.appendChild(box);
        self.tick();
      }).catch(function () { self.upNext(right); });
    },

    upNext: function (right) {
      var rest = player.queue.slice(player.index + 1, player.index + 30);
      right.innerHTML = '';
      right.className = 'npright scroll';
      if (!rest.length) return;
      var rows = trackRows(rest, { covers: true, album: true });
      // Rows here jump within the queue instead of replacing it.
      var buttons = rows.querySelectorAll('.row');
      for (var i = 0; i < buttons.length; i++) {
        (function (k, b) {
          var fresh = b.cloneNode(true);
          fresh.addEventListener('click', function () { player.load(player.index + 1 + k, true); });
          b.parentNode.replaceChild(fresh, b);
        })(i, buttons[i]);
      }
      right.appendChild(el('div.upnext', null, el('h2', null, 'Up next'), rows));
    },

    tick: function () {
      if (!this.lines || !this.synced || !this.box) return;
      var ms = audio.currentTime * 1000 + 150;
      var at = -1;
      for (var i = 0; i < this.lines.length; i++) {
        var lm = this.lines[i].ms;
        if (lm !== null && lm !== undefined && lm <= ms) at = i;
      }
      if (at === this.lastLine) return;
      this.lastLine = at;
      var box = this.box.querySelector('.lyrics');
      if (!box) return;
      var nodes = box.children;
      for (var k = 0; k < nodes.length; k++) {
        nodes[k].classList.toggle('now', k === at);
        nodes[k].classList.toggle('past', k < at);
      }
      var target = nodes[Math.max(0, at)];
      var right = box.parentNode;
      var y = target ? target.offsetTop - right.clientHeight * 0.35 : 0;
      box.style.transform = 'translateY(' + (-Math.max(0, y)) + 'px)';
    }
  };

  // ------------------------------------------------------------------ navigation between views

  function go(name, params, replace) {
    if (view && !replace) {
      view.focusIndex = focusIndex();
      view.scroll = main.scrollTop;
      history.push(view);
    }
    view = { name: name, params: params || {} };
    render();
  }
  function back() {
    if (!history.length) {
      if (view && view.name !== 'albums') { go('albums', null, true); return true; }
      return false;
    }
    view = history.pop();
    view.restoring = true;
    render();
    return true;
  }
  function render() {
    main.innerHTML = '';
    main.scrollTop = 0;
    var navs = document.querySelectorAll('.navitem');
    for (var i = 0; i < navs.length; i++) {
      var v = navs[i].getAttribute('data-view');
      var on = v === view.name || (v === 'albums' && view.name === 'album') || (v === 'artists' && view.name === 'artist') ||
        (v === 'playlists' && view.name === 'playlist');
      navs[i].classList.toggle('active', on);
    }
    main.appendChild(views[view.name](view.params));
    if (!view.restoring && view.name !== 'search' && view.name !== 'nowplaying') setTimeout(function () { focusFirst(main); }, 30);
    restoreFocus();
  }
  function focusIndex() {
    var all = main.querySelectorAll('.f');
    for (var i = 0; i < all.length; i++) if (all[i] === document.activeElement) return i;
    return -1;
  }
  /** After going back: the item you were on, once its view has loaded. */
  function restoreFocus() {
    if (!view || !view.restoring) return;
    var all = main.querySelectorAll('.f');
    if (view.focusIndex >= 0 && all.length > view.focusIndex) {
      main.scrollTop = view.scroll || 0;
      all[view.focusIndex].focus();
      view.restoring = false;
    } else if (view.focusIndex < 0) view.restoring = false;
  }
  function focusFirst(scope) {
    var f = scope.querySelector('.f');
    if (f) f.focus();
  }

  // ------------------------------------------------------------------ remote control: spatial focus

  var KEY = { LEFT: 37, UP: 38, RIGHT: 39, DOWN: 40, ENTER: 13, BACK: 461, ESC: 27, BACKSPACE: 8,
    PLAY: 415, PAUSE: 19, PLAYPAUSE: 179, STOP: 413, FF: 417, RW: 412, NEXT: 176, PREV: 177 };

  function focusables() {
    var root = $('overlay').classList.contains('hidden') ? document : $('overlay');
    var all = root.querySelectorAll('.f');
    var out = [];
    for (var i = 0; i < all.length; i++) {
      var r = all[i].getBoundingClientRect();
      if (r.width > 0 && r.height > 0) out.push({ el: all[i], r: r });
    }
    return out;
  }

  /** Moves focus to the nearest element in the arrow's direction, preferring ones in line with the current one. */
  function move(dir, from) {
    var cur = from || document.activeElement;
    var items = focusables();
    if (!cur || !cur.classList || !cur.classList.contains('f')) {
      if (items.length) items[0].el.focus();
      return;
    }
    var a = cur.getBoundingClientRect();
    var ax = a.left + a.width / 2, ay = a.top + a.height / 2;
    var best = null, bestScore = Infinity;
    items.forEach(function (it) {
      if (it.el === cur) return;
      var b = it.r;
      var bx = b.left + b.width / 2, by = b.top + b.height / 2;
      var main, cross;
      if (dir === 'left') { if (b.right > a.left + 1) return; main = a.left - b.right; cross = Math.max(0, Math.abs(ay - by) - (a.height + b.height) / 2); }
      else if (dir === 'right') { if (b.left < a.right - 1) return; main = b.left - a.right; cross = Math.max(0, Math.abs(ay - by) - (a.height + b.height) / 2); }
      else if (dir === 'up') { if (b.bottom > a.top + 1) return; main = a.top - b.bottom; cross = Math.max(0, Math.abs(ax - bx) - (a.width + b.width) / 2); }
      else { if (b.top < a.bottom - 1) return; main = b.top - a.bottom; cross = Math.max(0, Math.abs(ax - bx) - (a.width + b.width) / 2); }
      var score = main + cross * 3 + (dir === 'up' || dir === 'down' ? Math.abs(ax - bx) * 0.1 : Math.abs(ay - by) * 0.1);
      if (score < bestScore) { bestScore = score; best = it.el; }
    });
    if (best) {
      best.focus();
      if (best.scrollIntoViewIfNeeded) best.scrollIntoViewIfNeeded(true);
      else best.scrollIntoView({ block: 'nearest' });
    }
  }

  document.addEventListener('keydown', function (e) {
    var k = e.keyCode;
    var typing = document.activeElement && document.activeElement.tagName === 'INPUT';
    if (k === KEY.PLAY) { e.preventDefault(); if (audio.paused) player.toggle(); return; }
    if (k === KEY.PAUSE) { e.preventDefault(); if (!audio.paused) player.toggle(); return; }
    if (k === KEY.PLAYPAUSE || e.key === 'MediaPlayPause') { e.preventDefault(); player.toggle(); return; }
    if (k === KEY.STOP) { e.preventDefault(); audio.pause(); return; }
    if (k === KEY.FF) { e.preventDefault(); player.seekBy(10); return; }
    if (k === KEY.RW) { e.preventDefault(); player.seekBy(-10); return; }
    if (k === KEY.NEXT || e.key === 'MediaTrackNext') { e.preventDefault(); player.next(); return; }
    if (k === KEY.PREV || e.key === 'MediaTrackPrevious') { e.preventDefault(); player.prev(); return; }
    if (k === KEY.BACK || k === KEY.ESC || (k === KEY.BACKSPACE && !typing)) {
      if (!$('overlay').classList.contains('hidden')) return;
      if (typing) { document.activeElement.blur(); focusFirst(main); e.preventDefault(); return; }
      if (back()) e.preventDefault();
      return;
    }
    if (typing && (k === KEY.LEFT || k === KEY.RIGHT)) return;
    if (k === KEY.LEFT) { e.preventDefault(); move('left'); }
    else if (k === KEY.RIGHT) { e.preventDefault(); move('right'); }
    else if (k === KEY.UP || k === KEY.DOWN) {
      e.preventDefault();
      var from = document.activeElement;
      if (typing) from.blur();
      move(k === KEY.UP ? 'up' : 'down', from);
    }
    else if (k === KEY.ENTER && !typing && document.activeElement && document.activeElement.classList.contains('f')) {
      e.preventDefault();
      document.activeElement.click();
    }
  });
  // The LG pointer (Magic Remote) and mice: focus follows the pointer, so arrows carry on from there.
  document.addEventListener('mouseover', function (e) {
    var t = e.target.closest ? e.target.closest('.f') : null;
    if (t && t !== document.activeElement && t.tagName !== 'INPUT') t.focus({ preventScroll: true });
  });

  // ------------------------------------------------------------------ player bar

  var app = {
    started: false,
    connected: function () {
      if (this.started) return;
      this.started = true;
      player.restore();
      go('albums');
    }
  };

  function updateBar() {
    var t = player.current;
    $('app').classList.toggle('nobar', !t);
    $('bar').classList.toggle('hidden', !t);
    if (!t) return;
    $('bartitle').textContent = t.title;
    $('barartist').textContent = t.artist || '';
    var c = $('barcover');
    c.innerHTML = '';
    c.style.backgroundImage = '';
    lazyCover(c, t.cover, 'album');
    $('bardur').textContent = time(t.durationMs);
    setIcon($('barplay'), audio.paused ? 'play' : 'pause');
    var np = $('npplay');
    if (np) setIcon(np, audio.paused ? 'play' : 'pause');
    markPlaying();
    if (view && view.name === 'nowplaying' && nowPlaying.lyricsFor !== t.id) go('nowplaying', null, true);
    if ('mediaSession' in navigator && window.MediaMetadata) {
      try {
        navigator.mediaSession.metadata = new MediaMetadata({
          title: t.title, artist: t.artist || '', album: t.album || '',
          artwork: t.cover ? [{ src: imgUrl(t.cover), sizes: '512x512' }] : []
        });
      } catch (e) { /* old engine */ }
    }
  }
  player.onChange.push(updateBar);

  function progress() {
    var d = isFinite(audio.duration) && audio.duration > 0 ? audio.duration : (player.current ? player.current.durationMs / 1000 : 0);
    var p = d ? Math.min(1, audio.currentTime / d) : 0;
    $('barfill').style.width = (p * 100) + '%';
    $('barpos').textContent = time(audio.currentTime * 1000);
    var f = $('npfill');
    if (f) { f.style.width = (p * 100) + '%'; $('nppos').textContent = time(audio.currentTime * 1000); }
    nowPlaying.tick();
  }
  audio.addEventListener('timeupdate', progress);
  (function frame() {
    if (!audio.paused && nowPlaying.lines) nowPlaying.tick();
    requestAnimationFrame(frame);
  })();

  $('barplay').addEventListener('click', function () { player.toggle(); });
  $('barnext').addEventListener('click', function () { player.next(); });
  $('barprev').addEventListener('click', function () { player.prev(); });
  $('barnow').addEventListener('click', function () { go('nowplaying'); });

  // ------------------------------------------------------------------ start

  var icons = document.querySelectorAll('[data-icon]');
  for (var i = 0; i < icons.length; i++) icons[i].parentNode.replaceChild(icon(icons[i].getAttribute('data-icon')), icons[i]);
  var navs = document.querySelectorAll('.navitem');
  for (var n = 0; n < navs.length; n++) {
    (function (b) {
      b.addEventListener('click', function () {
        history = [];
        view = null;
        go(b.getAttribute('data-view'));
      });
    })(navs[n]);
  }
  if (isTv()) document.documentElement.classList.add('tv');
  updateBar();
  if (!base) screens.host();
  else if (!token()) screens.pair();
  else { screens.connecting(); conn.open(); }
})();
