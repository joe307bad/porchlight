// The browser side of the WebGL build: WebSockets (polled from C#, so no
// callbacks cross into the runtime), the page's URL, storage, downloads and
// sign-out. Platform.cs is the C# half; other platforms use .NET there.

mergeInto(LibraryManager.library, {
  $PL: {
    sockets: {},
    next: 1,
    str: function (s) {
      if (s === null || s === undefined) return 0;
      var n = lengthBytesUTF8(s) + 1;
      var p = _malloc(n);
      stringToUTF8(s, p, n);
      return p;
    },
  },

  PL_WsOpen__deps: ['$PL'],
  PL_WsOpen: function (urlPtr) {
    var url = UTF8ToString(urlPtr);
    var id = PL.next++;
    var s = { queue: [], state: 0, code: 0, reason: '' };
    PL.sockets[id] = s;
    try {
      var ws = new WebSocket(url);
      ws.binaryType = 'arraybuffer';
      s.ws = ws;
      ws.onopen = function () { s.state = 1; };
      ws.onmessage = function (e) {
        if (e.data instanceof ArrayBuffer) s.queue.push(new Uint8Array(e.data));
      };
      ws.onclose = function (e) {
        s.state = 3;
        s.code = e.code;
        s.reason = e.reason || '';
      };
    } catch (err) {
      s.state = 3;
      s.code = 1006;
      s.reason = String(err);
    }
    return id;
  },

  // 0 connecting, 1 open, 3 closed (after its messages have been read).
  PL_WsState__deps: ['$PL'],
  PL_WsState: function (id) {
    var s = PL.sockets[id];
    if (!s) return 3;
    if (s.state === 3 && s.queue.length) return 1;
    return s.state;
  },

  PL_WsCloseCode__deps: ['$PL'],
  PL_WsCloseCode: function (id) {
    var s = PL.sockets[id];
    return s ? s.code : 1006;
  },

  // The next message's length, or -1 when there's none.
  PL_WsPeek__deps: ['$PL'],
  PL_WsPeek: function (id) {
    var s = PL.sockets[id];
    return s && s.queue.length ? s.queue[0].length : -1;
  },

  PL_WsRead__deps: ['$PL'],
  PL_WsRead: function (id, ptr) {
    var s = PL.sockets[id];
    if (!s || !s.queue.length) return 0;
    var m = s.queue.shift();
    HEAPU8.set(m, ptr);
    return m.length;
  },

  PL_WsSend__deps: ['$PL'],
  PL_WsSend: function (id, ptr, length) {
    var s = PL.sockets[id];
    if (!s || !s.ws || s.ws.readyState !== 1) return;
    s.ws.send(HEAPU8.slice(ptr, ptr + length));
  },

  PL_WsClose__deps: ['$PL'],
  PL_WsClose: function (id, code) {
    var s = PL.sockets[id];
    if (!s) return;
    try {
      if (s.ws) s.ws.close(code);
    } catch (err) {}
  },

  PL_WsFree__deps: ['$PL'],
  PL_WsFree: function (id) {
    delete PL.sockets[id];
  },

  PL_Location__deps: ['$PL'],
  PL_Location: function () {
    return PL.str(location.href);
  },

  PL_SetHash: function (hashPtr) {
    // replaceState doesn't fire hashchange, so this never loops back into a regenerate.
    history.replaceState(null, '', '#' + UTF8ToString(hashPtr));
  },

  PL_ReplaceUrl: function (urlPtr) {
    history.replaceState(null, '', UTF8ToString(urlPtr));
  },

  PL_Navigate: function (urlPtr) {
    location.assign(UTF8ToString(urlPtr));
  },

  PL_PostForm: function (urlPtr) {
    var form = document.createElement('form');
    form.method = 'POST';
    form.action = UTF8ToString(urlPtr);
    document.body.append(form);
    form.submit();
  },

  PL_StorageGet__deps: ['$PL'],
  PL_StorageGet: function (session, keyPtr) {
    try {
      var store = session ? sessionStorage : localStorage;
      return PL.str(store.getItem(UTF8ToString(keyPtr)));
    } catch (err) {
      return 0;
    }
  },

  PL_StorageSet: function (session, keyPtr, valuePtr) {
    try {
      var store = session ? sessionStorage : localStorage;
      var key = UTF8ToString(keyPtr);
      if (valuePtr) store.setItem(key, UTF8ToString(valuePtr));
      else store.removeItem(key);
    } catch (err) {}
  },

  PL_Download: function (ptr, length, namePtr, typePtr) {
    var blob = new Blob([HEAPU8.slice(ptr, ptr + length)], { type: UTF8ToString(typePtr) });
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url;
    a.download = UTF8ToString(namePtr);
    a.click();
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
  },

  PL_Dpr: function () {
    return window.devicePixelRatio || 1;
  },

  PL_Hidden: function () {
    return document.visibilityState === 'hidden' ? 1 : 0;
  },

  PL_Confirm: function (textPtr) {
    return window.confirm(UTF8ToString(textPtr)) ? 1 : 0;
  },

  PL_Free: function (ptr) {
    _free(ptr);
  },

  PL_SetTitle: function (textPtr) {
    document.title = UTF8ToString(textPtr);
  },
});
