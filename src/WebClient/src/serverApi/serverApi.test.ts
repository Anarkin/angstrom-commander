import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import { claimPairingCode, login, logout, register } from "./auth";
import { downloadFile, listDirectory, listMachines } from "./files";
import { ApiError, loadStoredAccessToken } from "./http";

function respondWith(body: unknown, init: ResponseInit = {}): Response {
  return new Response(JSON.stringify(body), {
    status: init.status ?? 200,
    headers: { "Content-Type": "application/json", ...init.headers },
  });
}

function mockFetch(response: Response): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(() => Promise.resolve(response));
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function lastRequest(fetchMock: ReturnType<typeof vi.fn>): {
  url: URL;
  init: RequestInit;
} {
  const [url, init] = fetchMock.mock.calls.at(-1) as [URL, RequestInit];
  return { url, init };
}

beforeEach(() => {
  localStorage.clear();
  loadStoredAccessToken();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("authentication", () => {
  test("login stores the token so it survives a page reload", async () => {
    mockFetch(respondWith({ accessToken: "token-123" }));

    await login("someone@example.com", "Sup3rSecret!");

    expect(localStorage.getItem("angstrom.accessToken")).toBe("token-123");
    expect(loadStoredAccessToken()).toBe("token-123");
  });

  test("authenticated calls send the bearer token", async () => {
    mockFetch(respondWith({ accessToken: "token-abc" }));
    await login("someone@example.com", "Sup3rSecret!");

    const fetchMock = mockFetch(respondWith([]));
    await listMachines();

    const headers = new Headers(lastRequest(fetchMock).init.headers);
    expect(headers.get("Authorization")).toBe("Bearer token-abc");
  });

  test("login and register are sent anonymously", async () => {
    mockFetch(respondWith({ accessToken: "token-abc" }));
    await login("someone@example.com", "Sup3rSecret!");

    const fetchMock = mockFetch(respondWith({ userId: "0199..." }));
    await register("other@example.com", "Sup3rSecret!");

    const headers = new Headers(lastRequest(fetchMock).init.headers);
    expect(headers.get("Authorization")).toBeNull();
  });

  test("logout clears the stored token", async () => {
    mockFetch(respondWith({ accessToken: "token-abc" }));
    await login("someone@example.com", "Sup3rSecret!");

    logout();

    expect(localStorage.getItem("angstrom.accessToken")).toBeNull();
  });
});

describe("error handling", () => {
  test("surfaces the ProblemDetails detail message", async () => {
    mockFetch(
      respondWith(
        {
          title: "Bad Request",
          status: 400,
          detail: "Path is outside the allowed roots.",
        },
        {
          status: 400,
          headers: { "Content-Type": "application/problem+json" },
        },
      ),
    );

    const failure = await listDirectory("11111111-1111-1111-1111-111111111111", "/etc").catch(
      (error: unknown) => error,
    );

    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).message).toBe("Path is outside the allowed roots.");
    expect((failure as ApiError).status).toBe(400);
  });

  test("joins validation problem messages", async () => {
    mockFetch(
      respondWith(
        {
          title: "One or more validation errors occurred.",
          errors: { registration: ["Passwords must have at least one digit."] },
        },
        { status: 400 },
      ),
    );

    const failure = await register("bad@example.com", "weak").catch((error: unknown) => error);

    expect((failure as ApiError).message).toBe("Passwords must have at least one digit.");
  });

  test("flags unauthorized responses so callers can log out", async () => {
    mockFetch(new Response(null, { status: 401 }));

    const failure = await listMachines().catch((error: unknown) => error);

    expect((failure as ApiError).isUnauthorized).toBe(true);
  });
});

describe("file operations", () => {
  test("listDirectory encodes the path as a query parameter", async () => {
    const fetchMock = mockFetch(respondWith([]));

    await listDirectory("22222222-2222-2222-2222-222222222222", "/data/my files");

    const { url } = lastRequest(fetchMock);
    expect(url.pathname).toBe("/api/daemons/22222222-2222-2222-2222-222222222222/list");
    expect(url.searchParams.get("path")).toBe("/data/my files");
  });

  test("claimPairingCode posts the code and display name", async () => {
    const fetchMock = mockFetch(
      respondWith({
        registrationId: "33333333-3333-3333-3333-333333333333",
        displayName: "Home PC",
      }),
    );

    const claimed = await claimPairingCode("ABCD2345", "Home PC");

    expect(claimed.displayName).toBe("Home PC");
    const { init } = lastRequest(fetchMock);
    expect(JSON.parse(init.body as string)).toEqual({
      code: "ABCD2345",
      displayName: "Home PC",
    });
  });

  test("downloadFile takes the file name from Content-Disposition", async () => {
    mockFetch(
      new Response("file bytes", {
        status: 200,
        headers: {
          "Content-Disposition": "attachment; filename=notes.txt; filename*=UTF-8''notes.txt",
        },
      }),
    );

    const downloaded = await downloadFile("44444444-4444-4444-4444-444444444444", "/data/notes.txt");

    expect(downloaded.fileName).toBe("notes.txt");
    expect(await downloaded.blob.text()).toBe("file bytes");
  });

  test("downloadFile falls back to the path's base name", async () => {
    mockFetch(new Response("bytes", { status: 200 }));

    const downloaded = await downloadFile("44444444-4444-4444-4444-444444444444", "/data/sub/report.pdf");

    expect(downloaded.fileName).toBe("report.pdf");
  });
});
