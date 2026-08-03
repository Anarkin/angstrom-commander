import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, expect, test, vi } from "vitest";
import App from "./App";
import type { DirectoryEntry, Machine } from "./serverApi/types";

const homeMachine: Machine = {
    registrationId: "11111111-1111-1111-1111-111111111111",
    displayName: "Home PC",
    platform: "Windows",
    createdAt: "2026-07-01T10:00:00Z",
    lastSeenAt: "2026-07-30T10:00:00Z",
    online: true,
};

const laptopMachine: Machine = {
    registrationId: "22222222-2222-2222-2222-222222222222",
    displayName: "Laptop",
    platform: "Linux",
    createdAt: "2026-07-02T10:00:00Z",
    lastSeenAt: "2026-07-30T10:00:00Z",
    online: true,
};

const notes: DirectoryEntry = {
    name: "notes.txt",
    isDirectory: false,
    sizeBytes: 1024,
    modifiedAt: "2026-07-30T09:00:00Z",
};

const projects: DirectoryEntry = {
    name: "projects",
    isDirectory: true,
    sizeBytes: null,
    modifiedAt: "2026-07-30T09:00:00Z",
};

/** Routes each API path to a canned response, and records what was called. */
function stubApi(overrides: Record<string, unknown> = {}) {
    const calls: { method: string; url: string }[] = [];
    const fetchMock = vi.fn((input: URL | string, init?: RequestInit) => {
        const url = input instanceof URL ? input : new URL(input);
        calls.push({ method: init?.method ?? "GET", url: url.pathname + url.search });

        const match = Object.entries(overrides).find(([key]) => (url.pathname + url.search).includes(key));
        const body = match?.[1] ?? (url.pathname === "/api/daemons" ? [homeMachine, laptopMachine] : []);
        return Promise.resolve(
            new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } }),
        );
    });
    vi.stubGlobal("fetch", fetchMock);
    return calls;
}

beforeEach(() => {
    localStorage.clear();
});

afterEach(() => {
    vi.unstubAllGlobals();
    // Restores window.confirm too, which the overwrite tests replace.
    vi.restoreAllMocks();
});

test("asks for sign-in when there is no session", () => {
    stubApi();

    render(<App />);

    expect(screen.getByRole("heading", { name: /sign in/i })).toBeInTheDocument();
});

test("signing in reveals the machines and the two panes", async () => {
    stubApi({ "/api/auth/login": { accessToken: "token-abc" } });
    const user = userEvent.setup();
    render(<App />);

    await user.type(screen.getByLabelText(/email/i), "someone@example.com");
    await user.type(screen.getByLabelText(/password/i), "Sup3rSecret!");
    await user.click(screen.getByRole("button", { name: /^sign in$/i }));

    expect(await screen.findByLabelText("Left: machine")).toBeInTheDocument();
    expect(screen.getByLabelText("Right: machine")).toBeInTheDocument();

    // The machines list lives in Settings, not on the main screen.
    expect(screen.queryByRole("heading", { name: /my machines/i })).toBeNull();
    await user.click(screen.getByRole("button", { name: /settings/i }));
    // Scoped to the machines list: the name also appears in both panes' machine pickers.
    const machineList = await screen.findByRole("list");
    expect(within(machineList).getByText("Home PC")).toBeInTheDocument();
    expect(within(machineList).getByText("Laptop")).toBeInTheDocument();

    // Hiding settings returns to the panes with their state intact.
    await user.click(screen.getByRole("button", { name: /hide settings/i }));
    expect(screen.getByLabelText("Left: machine")).toBeInTheDocument();
});

test("shows a failed sign-in without entering the app", async () => {
    const fetchMock = vi.fn(() =>
        Promise.resolve(
            new Response(JSON.stringify({ title: "Unauthorized", status: 401 }), {
                status: 401,
                headers: { "Content-Type": "application/problem+json" },
            }),
        ),
    );
    vi.stubGlobal("fetch", fetchMock);
    const user = userEvent.setup();
    render(<App />);

    await user.type(screen.getByLabelText(/email/i), "someone@example.com");
    await user.type(screen.getByLabelText(/password/i), "wrong");
    await user.click(screen.getByRole("button", { name: /^sign in$/i }));

    expect(await screen.findByText(/session has expired|request failed|unauthorized/i)).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: /sign in/i })).toBeInTheDocument();
});

test("picking a machine lists its directory", async () => {
    localStorage.setItem("angstrom.accessToken", "token-abc");
    stubApi({ "/list": [notes, projects] });
    const user = userEvent.setup();
    render(<App />);

    await user.selectOptions(await screen.findByLabelText("Left: machine"), homeMachine.registrationId);

    expect(await screen.findByText(/notes\.txt/)).toBeInTheDocument();
    // Directories sort first, and only files offer a download.
    const firstEntryRow = screen.getAllByRole("row")[1]!;
    expect(firstEntryRow).toHaveTextContent("projects");
    expect(within(firstEntryRow).queryByRole("button", { name: /download/i })).toBeNull();
});

test("clicking a directory navigates into it", async () => {
    localStorage.setItem("angstrom.accessToken", "token-abc");
    const calls = stubApi({ "/list": [projects] });
    const user = userEvent.setup();
    render(<App />);

    await user.selectOptions(await screen.findByLabelText("Left: machine"), homeMachine.registrationId);
    await user.click(await screen.findByText(/projects/));

    await waitFor(() => {
        expect(calls.some((call) => call.url.includes("path=%2Fdata%2Fprojects"))).toBe(true);
    });
    expect(screen.getByLabelText("Left: path")).toHaveValue("/data/projects");
});

test("copies the selected file to the other machine without downloading it", async () => {
    localStorage.setItem("angstrom.accessToken", "token-abc");
    const calls = stubApi({ "/list": [notes], "/copy-to/": { bytesTransferred: 1024 } });
    // Both panes list the same name, so this copy replaces a file and has to ask first.
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    const user = userEvent.setup();
    render(<App />);

    await user.selectOptions(await screen.findByLabelText("Left: machine"), homeMachine.registrationId);
    await user.selectOptions(screen.getByLabelText("Right: machine"), laptopMachine.registrationId);
    await user.click((await screen.findAllByText(/notes\.txt/))[0]!);
    await user.click(screen.getByRole("button", { name: /copy →/i }));

    expect(await screen.findByText(/copied notes\.txt/i)).toBeInTheDocument();
    expect(confirm).toHaveBeenCalled();
    const copyCall = calls.find((call) => call.url.includes("/copy-to/"));
    expect(copyCall?.method).toBe("POST");
    expect(copyCall?.url).toContain(`${homeMachine.registrationId}/copy-to/${laptopMachine.registrationId}`);
    expect(copyCall?.url).toContain("sourcePath=%2Fdata%2Fnotes.txt");
    expect(copyCall?.url).toContain("targetPath=%2Fuploads%2Fnotes.txt");
    expect(copyCall?.url).toContain("overwrite=true");
    // No download endpoint involved — the bytes never touch the browser.
    expect(calls.some((call) => call.url.includes("/download"))).toBe(false);
});

test("a copy that would replace a file is not sent when the user declines", async () => {
    localStorage.setItem("angstrom.accessToken", "token-abc");
    const calls = stubApi({ "/list": [notes], "/copy-to/": { bytesTransferred: 1024 } });
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const user = userEvent.setup();
    render(<App />);

    await user.selectOptions(await screen.findByLabelText("Left: machine"), homeMachine.registrationId);
    await user.selectOptions(screen.getByLabelText("Right: machine"), laptopMachine.registrationId);
    await user.click((await screen.findAllByText(/notes\.txt/))[0]!);
    await user.click(screen.getByRole("button", { name: /copy →/i }));

    expect(confirm).toHaveBeenCalled();
    expect(calls.some((call) => call.url.includes("/copy-to/"))).toBe(false);
});

test("unpairing a machine asks first, then deletes and refreshes the list", async () => {
    localStorage.setItem("angstrom.accessToken", "token-abc");
    const calls = stubApi();
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole("button", { name: /settings/i }));
    // The first Unpair button belongs to the first machine in the list.
    await user.click((await screen.findAllByRole("button", { name: /unpair/i }))[0]!);

    expect(await screen.findByText(/unpaired home pc/i)).toBeInTheDocument();
    expect(confirm).toHaveBeenCalled();
    const deleteCall = calls.find((call) => call.method === "DELETE");
    expect(deleteCall?.url).toBe(`/api/daemons/${homeMachine.registrationId}`);
    // The list refreshes so the machine disappears.
    expect(calls.filter((call) => call.method === "GET" && call.url === "/api/daemons").length).toBeGreaterThan(1);
});

test("settings shows the daily transfer allowance with the relay-only note", async () => {
    localStorage.setItem("angstrom.accessToken", "token-abc");
    stubApi({ "/api/transfers/usage": { bytesUsedToday: 2_147_483_648, dailyLimitBytes: 10_737_418_240 } });
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole("button", { name: /settings/i }));

    const bar = await screen.findByRole("progressbar");
    expect(bar).toHaveAttribute("value", "2147483648");
    expect(bar).toHaveAttribute("max", "10737418240");
    expect(screen.getByText(/2\.0 GB of 10\.0 GB used today/i)).toBeInTheDocument();
    // The tooltip carries the caveat that only relayed transfers count.
    expect(screen.getByLabelText(/relayed through the cloud/i)).toHaveAttribute("title");
});

test("no allowance card when the environment has no limit configured", async () => {
    localStorage.setItem("angstrom.accessToken", "token-abc");
    stubApi({ "/api/transfers/usage": { bytesUsedToday: 0, dailyLimitBytes: 0 } });
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole("button", { name: /settings/i }));

    expect(await screen.findByRole("heading", { name: /my machines/i })).toBeInTheDocument();
    expect(screen.queryByRole("progressbar")).toBeNull();
});

test("declining the unpair confirmation sends nothing", async () => {
    localStorage.setItem("angstrom.accessToken", "token-abc");
    const calls = stubApi();
    vi.spyOn(window, "confirm").mockReturnValue(false);
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole("button", { name: /settings/i }));
    await user.click((await screen.findAllByRole("button", { name: /unpair/i }))[0]!);

    expect(calls.some((call) => call.method === "DELETE")).toBe(false);
});

test("copy stays disabled until a file is selected on both machines", async () => {
    localStorage.setItem("angstrom.accessToken", "token-abc");
    stubApi({ "/list": [notes] });
    const user = userEvent.setup();
    render(<App />);

    expect(screen.getByRole("button", { name: /copy →/i })).toBeDisabled();

    await user.selectOptions(await screen.findByLabelText("Left: machine"), homeMachine.registrationId);
    await user.click((await screen.findAllByText(/notes\.txt/))[0]!);

    // Still disabled: the right pane has no machine yet.
    expect(screen.getByRole("button", { name: /copy →/i })).toBeDisabled();

    await user.selectOptions(screen.getByLabelText("Right: machine"), laptopMachine.registrationId);
    expect(screen.getByRole("button", { name: /copy →/i })).toBeEnabled();
});

test("surfaces a sandbox refusal in the pane", async () => {
    localStorage.setItem("angstrom.accessToken", "token-abc");
    const fetchMock = vi.fn((input: URL | string) => {
        const url = input instanceof URL ? input : new URL(input);
        if (url.pathname === "/api/daemons") {
            return Promise.resolve(
                new Response(JSON.stringify([homeMachine]), {
                    status: 200,
                    headers: { "Content-Type": "application/json" },
                }),
            );
        }

        return Promise.resolve(
            new Response(JSON.stringify({ status: 400, detail: "Path is outside the allowed roots." }), {
                status: 400,
                headers: { "Content-Type": "application/problem+json" },
            }),
        );
    });
    vi.stubGlobal("fetch", fetchMock);
    const user = userEvent.setup();
    render(<App />);

    await user.selectOptions(await screen.findByLabelText("Left: machine"), homeMachine.registrationId);

    expect(await screen.findByText(/outside the allowed roots/i)).toBeInTheDocument();
});

test("pairing a machine posts the code and refreshes the list", async () => {
    localStorage.setItem("angstrom.accessToken", "token-abc");
    const calls = stubApi({
        "/api/enrollment/claim": { registrationId: homeMachine.registrationId, displayName: "Home PC" },
    });
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole("button", { name: /settings/i }));
    await user.click(await screen.findByText(/pair a machine/i));
    await user.type(screen.getByPlaceholderText(/pairing code/i), "abcd2345");
    await user.type(screen.getByPlaceholderText(/name this machine/i), "Home PC");
    await user.click(screen.getByRole("button", { name: /^pair$/i }));

    expect(await screen.findByText(/paired home pc/i)).toBeInTheDocument();
    expect(calls.some((call) => call.url === "/api/enrollment/claim" && call.method === "POST")).toBe(true);
});
