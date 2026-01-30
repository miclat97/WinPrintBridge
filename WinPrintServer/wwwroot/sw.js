const CACHE_NAME = 'winprint-v1';
const ASSETS = [
  '/',
  '/index.html',
  '/manifest.json',
  '/PDF_file_icon.svg'
];

self.addEventListener('install', (e) => {
  e.waitUntil(
    caches.open(CACHE_NAME).then((cache) => cache.addAll(ASSETS))
  );
});

self.addEventListener('fetch', (e) => {
  // Network first for API, Cache first for statics?
  // Since it's a local controller, network is fast.
  // But for "offline" PWA feel (even if useless offline), cache.

  if (e.request.url.includes('/api/')) {
      e.respondWith(fetch(e.request));
      return;
  }

  e.respondWith(
    caches.match(e.request).then((response) => {
      return response || fetch(e.request);
    })
  );
});
