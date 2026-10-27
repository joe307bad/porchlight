// "Who's playing?": before exploring online, pick one of the account's
// characters or make a new one. One sign-in, several characters, each played
// in its own tab or device (a parent and child sharing an account). Resolves
// with the character; the choice is remembered for this tab only.

import { createCharacter, deleteCharacter, listCharacters, type Character } from '../net/characters';

const KEY = 'porchlight.character';

/** This tab's character from earlier (a reload), if it still exists. */
export async function rememberedCharacter(): Promise<Character | null> {
  let id: string | null = null;
  try {
    id = sessionStorage.getItem(KEY);
  } catch {
    return null;
  }
  if (!id) return null;
  try {
    return (await listCharacters()).find((c) => c.id === id) ?? null;
  } catch {
    return null;
  }
}

export function forgetCharacter(): void {
  try {
    sessionStorage.removeItem(KEY);
  } catch {
    // storage blocked: nothing to forget
  }
}

function remember(c: Character): void {
  try {
    sessionStorage.setItem(KEY, c.id);
  } catch {
    // storage blocked: they'll be asked again after a reload
  }
}

function el<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> = {}, ...children: (Node | string)[]) {
  const node = document.createElement(tag);
  Object.assign(node, props);
  node.append(...children);
  return node;
}

/** Show the picker; resolves once a character is chosen, or null if dismissed (play alone). */
export function pickCharacter(randomLook: () => string): Promise<Character | null> {
  return new Promise((resolve) => {
    const list = el('ul', { className: 'picker-list' });
    const error = el('p', { className: 'picker-error', hidden: true });
    error.setAttribute('role', 'alert');
    const name = el('input', { type: 'text', maxLength: 20, placeholder: 'New character’s name', autocomplete: 'off' });
    const create = el('button', { type: 'submit', className: 'picker-create', textContent: 'Create' });
    const form = el('form', { className: 'picker-new' }, name, create);
    const alone = el('button', { type: 'button', className: 'sb-link', textContent: 'Play alone (offline)' });
    const card = el(
      'div',
      { className: 'picker-card' },
      el('h2', { textContent: 'Who’s playing?' }),
      el('p', { className: 'picker-hint', textContent: 'Everyone on this sign-in can have their own character, and play at the same time on another tab or device.' }),
      list,
      form,
      error,
      alone,
    );
    const overlay = el('div', { id: 'picker' }, card);
    document.body.append(overlay);

    const showError = (message: string | null) => {
      error.textContent = message ?? '';
      error.hidden = !message;
    };
    const done = (c: Character | null) => {
      overlay.remove();
      if (c) remember(c);
      resolve(c);
    };

    const render = (characters: Character[]) => {
      list.replaceChildren(
        ...characters.map((c) => {
          const play = el('button', { type: 'button', className: 'picker-play', textContent: c.name });
          play.addEventListener('click', () => done(c));
          const remove = el('button', { type: 'button', className: 'picker-delete', textContent: '×', title: `Delete ${c.name}` });
          remove.setAttribute('aria-label', `Delete ${c.name}`);
          remove.addEventListener('click', async () => {
            if (!confirm(`Delete ${c.name}? Where they were and how they look will be gone.`)) return;
            try {
              await deleteCharacter(c.id);
              render(characters.filter((x) => x !== c));
            } catch (err) {
              showError((err as Error).message);
            }
          });
          return el('li', {}, play, remove);
        }),
      );
      if (!characters.length) list.append(el('li', { className: 'picker-empty', textContent: 'No characters yet. Make one below.' }));
    };

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      if (!name.value.trim()) return name.focus();
      create.disabled = true;
      showError(null);
      try {
        done(await createCharacter(name.value, randomLook()));
      } catch (err) {
        showError((err as Error).message);
        create.disabled = false;
      }
    });
    alone.addEventListener('click', () => done(null));

    list.append(el('li', { className: 'picker-empty', textContent: 'Loading…' }));
    listCharacters()
      .then((characters) => {
        render(characters);
        (list.querySelector<HTMLButtonElement>('.picker-play') ?? name).focus();
      })
      .catch((err: Error) => {
        list.replaceChildren();
        showError(err.message);
        name.focus();
      });
  });
}
