/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** `true` to require sign-in through the auth service. See src/auth/session.ts. */
  readonly VITE_AUTH_ENABLED?: string;
  /** `false` to keep explore mode offline even when signed in. See src/net/multiplayer.ts. */
  readonly VITE_MULTIPLAYER?: string;
}
