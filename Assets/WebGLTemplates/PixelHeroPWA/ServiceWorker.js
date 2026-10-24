// PixelHero copy of Unity's PWA template service worker, changed so a new deploy reaches the player (live.md §9):
// - the cache is per build (the Build/ file names are content hashes), and older caches are deleted on activate;
// - the page (index.html, ?hub=...) is fetched from the network first and only falls back to the cache offline;
// - Build/ files are cache first, since a new build has new names;
// - only same-origin GET requests are cached, never the hub's HTTP API.
#if USE_DATA_CACHING
const cacheName = {{{ JSON.stringify("PixelHero-" + LOADER_FILENAME + "-" + FRAMEWORK_FILENAME + "-" + DATA_FILENAME + "-" + CODE_FILENAME) }}};
const contentToCache = [
    "Build/{{{ LOADER_FILENAME }}}",
    "Build/{{{ FRAMEWORK_FILENAME }}}",
#if USE_THREADS
    "Build/{{{ WORKER_FILENAME }}}",
#endif
    "Build/{{{ DATA_FILENAME }}}",
    "Build/{{{ CODE_FILENAME }}}",
    "TemplateData/style.css"

];
#endif

self.addEventListener('install', function (e) {
    console.log('[Service Worker] Install');

#if USE_DATA_CACHING
    e.waitUntil((async function () {
      const cache = await caches.open(cacheName);
      console.log('[Service Worker] Caching all: app shell and content');
      await cache.addAll(contentToCache);
      await self.skipWaiting();
    })());
#else
    e.waitUntil(self.skipWaiting());
#endif
});

self.addEventListener('activate', function (e) {
    e.waitUntil((async function () {
      for (const name of await caches.keys()) {
#if USE_DATA_CACHING
        if (name === cacheName) continue;
#endif
        console.log(`[Service Worker] Deleting old cache: ${name}`);
        await caches.delete(name);
      }
      await self.clients.claim();
    })());
});

#if USE_DATA_CACHING
self.addEventListener('fetch', function (e) {
    const url = new URL(e.request.url);
    if (e.request.method !== 'GET' || url.origin !== self.location.origin) return;

    const isPage = e.request.mode === 'navigate' || url.pathname.endsWith('/') || url.pathname.endsWith('.html');
    e.respondWith(isPage ? networkFirst(e.request) : cacheFirst(e.request));
});

async function networkFirst(request) {
    const cache = await caches.open(cacheName);
    try {
      const response = await fetch(request);
      if (response.ok) cache.put(request, response.clone());
      return response;
    } catch (error) {
      const cached = await cache.match(request, { ignoreSearch: true });
      if (cached) { return cached; }
      throw error;
    }
}

async function cacheFirst(request) {
    const cache = await caches.open(cacheName);
    let response = await cache.match(request);
    if (response) { return response; }

    response = await fetch(request);
    if (response.ok) {
      console.log(`[Service Worker] Caching new resource: ${request.url}`);
      cache.put(request, response.clone());
    }
    return response;
}
#endif
