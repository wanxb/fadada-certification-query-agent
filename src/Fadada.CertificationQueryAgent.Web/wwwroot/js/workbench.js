// Owns authenticated API/SSE transport; all returned text is passed to Blazor without HTML injection.
let receiver;
let csrfToken;
let activeTurn;

export async function initialize(dotNetReceiver) {
    receiver = dotNetReceiver;
    try {
        csrfToken = await readCsrfToken();
        await refreshConversations();
    } catch (error) {
        await reportError(error);
    }
}

export async function refreshConversations(status = "active") {
    try {
        const scope = status === "archived" ? "archived" : "active";
        const response = await apiFetch(`/api/v1/conversations?status=${scope}`);
        await receiver.invokeMethodAsync("ReceiveConversationsAsync", await response.text());
        return true;
    } catch (error) {
        await reportError(error);
        return false;
    }
}

export async function loadConversation(id) {
    try {
        const response = await apiFetch(`/api/v1/conversations/${encodeURIComponent(id)}`);
        await receiver.invokeMethodAsync("ReceiveConversationAsync", await response.text());
    } catch (error) {
        await reportError(error);
    }
}

export async function createConversation(title) {
    try {
        const response = await apiFetch("/api/v1/conversations", {
            method: "POST",
            headers: jsonHeaders(),
            body: JSON.stringify({ title })
        });
        await receiver.invokeMethodAsync("ReceiveCreatedConversationAsync", await response.text());
    } catch (error) {
        await reportError(error);
    }
}

export async function archiveConversation(id) {
    return changeConversationStatus(id, "archive");
}

export async function restoreConversation(id) {
    return changeConversationStatus(id, "restore");
}

async function changeConversationStatus(id, action) {
    try {
        await apiFetch(`/api/v1/conversations/${encodeURIComponent(id)}/${action}`, {
            method: "POST",
            headers: mutationHeaders()
        });
        return true;
    } catch (error) {
        await reportError(error);
        return false;
    }
}

export async function runTurn(id, message) {
    activeTurn?.abort();
    activeTurn = new AbortController();

    try {
        const response = await apiFetch(`/api/v1/conversations/${encodeURIComponent(id)}/turns`, {
            method: "POST",
            headers: jsonHeaders(),
            body: JSON.stringify({ message }),
            signal: activeTurn.signal
        });
        await consumeEventStream(response.body);
    } catch (error) {
        if (error?.name !== "AbortError") {
            await reportError(error);
        }
    } finally {
        activeTurn = undefined;
    }
}

export function cancelTurn() {
    activeTurn?.abort();
    activeTurn = undefined;
}

export async function logout() {
    try {
        await apiFetch("/auth/logout", {
            method: "POST",
            headers: mutationHeaders()
        });
    } finally {
        window.location.assign("/login");
    }
}

export function scrollTo(element) {
    element?.scrollIntoView({ behavior: "smooth", block: "end" });
}

export function dispose() {
    activeTurn?.abort();
    activeTurn = undefined;
    receiver = undefined;
}

async function readCsrfToken() {
    const response = await fetch("/login", {
        credentials: "same-origin",
        headers: { "Accept": "text/html" }
    });
    if (!response.ok) {
        throw new ApiError(response.status, "AUTH_SESSION_EXPIRED");
    }

    const documentText = await response.text();
    const parsed = new DOMParser().parseFromString(documentText, "text/html");
    const token = parsed.querySelector("input[name='__RequestVerificationToken']")?.value;
    if (!token) {
        throw new ApiError(400, "AUTH_ANTIFORGERY_INVALID");
    }
    return token;
}

async function apiFetch(path, options = {}) {
    const response = await fetch(path, {
        credentials: "same-origin",
        cache: "no-store",
        ...options
    });

    if (response.status === 401) {
        window.location.assign("/login");
        throw new ApiError(response.status, "AUTH_SESSION_EXPIRED");
    }
    if (!response.ok) {
        const contentType = response.headers.get("content-type") ?? "";
        const body = contentType.includes("application/json") ? await response.json() : {};
        const code = response.status === 429 ? "RATE_LIMITED" : body.errorCode;
        throw new ApiError(response.status, code ?? "SERVICE_REQUEST_FAILED");
    }
    return response;
}

function mutationHeaders() {
    return {
        "Accept": "application/json",
        "X-CSRF-TOKEN": csrfToken
    };
}

function jsonHeaders() {
    return {
        ...mutationHeaders(),
        "Content-Type": "application/json"
    };
}

async function consumeEventStream(stream) {
    if (!stream) {
        throw new ApiError(502, "AGENT_STREAM_MISSING");
    }

    const reader = stream.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    while (true) {
        const result = await reader.read();
        buffer += decoder.decode(result.value ?? new Uint8Array(), { stream: !result.done });
        const frames = buffer.split("\n\n");
        buffer = frames.pop() ?? "";

        for (const frame of frames) {
            await dispatchFrame(frame);
        }
        if (result.done) {
            if (buffer.trim()) {
                await dispatchFrame(buffer);
            }
            break;
        }
    }
}

async function dispatchFrame(frame) {
    let eventName;
    let data;
    for (const line of frame.split("\n")) {
        if (line.startsWith("event: ")) {
            eventName = line.slice(7).trim();
        } else if (line.startsWith("data: ")) {
            data = line.slice(6);
        }
    }

    const allowed = new Set([
        "turn.started",
        "agent.text.delta",
        "tool.started",
        "tool.completed",
        "turn.completed",
        "turn.failed"
    ]);
    if (eventName && data && allowed.has(eventName)) {
        JSON.parse(data);
        await receiver.invokeMethodAsync("ReceiveStreamEventAsync", eventName, data);
    }
}

async function reportError(error) {
    if (receiver) {
        await receiver.invokeMethodAsync("ReceiveClientErrorAsync", error?.code ?? "SERVICE_REQUEST_FAILED");
    }
}

class ApiError extends Error {
    constructor(status, code) {
        super(code);
        this.status = status;
        this.code = code;
    }
}
