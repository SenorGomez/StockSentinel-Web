self.addEventListener('push', event => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch (_) {}
  const title = data.title || 'StockSentinel';
  event.waitUntil(self.registration.showNotification(title, {
    body: data.body || 'Stock update',
    icon: '/icon.svg',
    badge: '/icon.svg',
    tag: 'stocksentinel-stock-' + title,
    data: { url: data.url || '/' }
  }));
});
self.addEventListener('notificationclick', event => {
  event.notification.close();
  const url = event.notification.data?.url || '/';
  event.waitUntil(clients.openWindow(url));
});
