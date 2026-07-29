/** Configurable server URL (build config), so any build can target any environment. */
export const serverUrl: string = import.meta.env.VITE_SERVER_URL ?? "http://localhost:5080";
