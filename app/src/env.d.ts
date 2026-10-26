/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** `true` to require sign-in through the auth service. See src/auth/session.ts. */
  readonly VITE_AUTH_ENABLED?: string;
}
