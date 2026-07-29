/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the Server this WebClient build talks to. */
  readonly VITE_SERVER_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
