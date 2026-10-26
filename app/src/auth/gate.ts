// The sign-in screen in front of the app. Resolves with who's signed in (or
// null when sign-in is off); until someone is, the app doesn't start.

import { authEnabled, fetchMe, signIn, type User } from './session';

export async function requireSignIn(): Promise<User | null> {
  if (!authEnabled()) return null;

  // A failed sign-in comes back as ?auth_error=…; show it once, then tidy the URL.
  const params = new URLSearchParams(location.search);
  let error = params.get('auth_error');
  if (error) {
    params.delete('auth_error');
    const query = params.toString();
    history.replaceState(null, '', location.pathname + (query ? `?${query}` : '') + location.hash);
  }

  let provider: string | null = null;
  try {
    const me = await fetchMe();
    if (me.user) return me.user;
    provider = me.provider;
  } catch (err) {
    error = (err as Error).message;
  }

  showGate(provider, error);
  return new Promise<never>(() => {}); // the button navigates away
}

function showGate(provider: string | null, error: string | null): void {
  const gate = document.createElement('div');
  gate.id = 'signin';
  gate.innerHTML = `
    <div class="signin-card">
      <h1>Porchlight</h1>
      <p>Grow a town from a seed, then walk its streets.</p>
      <button type="button" class="signin-button"></button>
      <p class="signin-error" role="alert" hidden></p>
    </div>`;
  const button = gate.querySelector<HTMLButtonElement>('.signin-button')!;
  button.textContent = provider === 'google' ? 'Sign in with Google' : 'Sign in';
  button.addEventListener('click', () => {
    button.disabled = true;
    signIn();
  });
  const errorEl = gate.querySelector<HTMLParagraphElement>('.signin-error')!;
  if (error) {
    errorEl.textContent = error;
    errorEl.hidden = false;
  }
  document.body.classList.add('signed-out');
  document.body.append(gate);
  button.focus();
}
