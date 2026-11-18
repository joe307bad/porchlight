// Manage inside the game: the web game shows this page in a frame over itself
// (Plugins/WebGL/Porchlight.jslib, PL_ShowManage). Embedded, it has a way back
// to the game (and Escape), which tells the game's page to close the frame.

/** In the game's frame, rather than a page of its own. */
export const embedded = (() => {
  try {
    return window.parent !== window;
  } catch {
    return true;
  }
})();

/**
 * Back to the desktop app, from the browser: the app serves this page, and brings
 * its window to the front when the page asks (ManageLink.cs, POST /app/front).
 */
export async function backToApp(): Promise<boolean> {
  try {
    return (await fetch('/app/front', { method: 'POST' })).ok;
  } catch {
    return false;
  }
}

/** Back to the game: the game's page closes the frame. */
export function backToGame(): void {
  window.parent.postMessage({ porchlight: 'manage-close' }, location.origin);
}

if (embedded) {
  document.documentElement.classList.add('embedded');
  window.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && !e.defaultPrevented) backToGame();
  });
}
