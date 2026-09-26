// Isolated x64 experiment, not a production dependency. The harness only targets its own windows.
// The DLL stays loaded until the disposable target exits; detach removes all callbacks/state.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commctrl.h>
#include <new>

static HINSTANCE instance;
static constexpr wchar_t property[] = L"TrenchHQ.ContainmentProbe.State.v1";
static constexpr UINT_PTR subclassId = 0x4e585350;
static UINT AttachMessage(bool embed = false) {
    static UINT normal = RegisterWindowMessage(L"TrenchHQ.ContainmentProbe.Attach.v1");
    static UINT embedded = RegisterWindowMessage(L"TrenchHQ.ContainmentProbe.Embed.v1");
    return embed ? embedded : normal;
}
static UINT DetachMessage() { static UINT id = RegisterWindowMessage(L"TrenchHQ.ContainmentProbe.Detach.v1"); return id; }
static UINT RawHitMessage() { static UINT id = RegisterWindowMessage(L"TrenchHQ.ContainmentProbe.RawHit.v1"); return id; }
struct State {
    HANDLE owner; DWORD ownerPid; HHOOK cbt; UINT_PTR timer;
    HWND container; HWND originalParent; LONG_PTR originalStyle; RECT originalRect;
};
static LRESULT CALLBACK LockedProc(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);

static LRESULT CALLBACK ContainerProc(HWND hwnd, UINT msg, WPARAM wparam, LPARAM lparam, UINT_PTR id, DWORD_PTR data) {
    auto child = reinterpret_cast<HWND>(data);
    if (msg == WM_NCDESTROY) RemoveWindowSubclass(hwnd, ContainerProc, id);
    if (msg == WM_SETFOCUS && IsWindow(child)) { SetFocus(child); return 0; }
    if (msg == WM_MOUSEACTIVATE && IsWindow(child)) { SetFocus(child); return MA_ACTIVATE; }
    return DefSubclassProc(hwnd, msg, wparam, lparam);
}

static void RestoreEmbedding(HWND hwnd, State* state) {
    if (!state->container) return;
    if (IsWindow(hwnd)) {
        SetParent(hwnd, state->originalParent);
        // Never destroy the container if detachment failed: that would destroy the target too.
        if (GetParent(hwnd) == state->container) return;
        SetWindowLongPtr(hwnd, GWL_STYLE, state->originalStyle);
        auto r = state->originalRect;
        SetWindowPos(hwnd, nullptr, r.left, r.top, r.right - r.left, r.bottom - r.top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOSENDCHANGING);
    }
    DestroyWindow(state->container);
    state->container = nullptr;
}

static bool EmbedOnTargetThread(HWND hwnd, State* state) {
    state->originalParent = GetParent(hwnd);
    state->originalStyle = GetWindowLongPtr(hwnd, GWL_STYLE);
    // Use the target's own coordinate/DPI context for both windows and restore on this stack.
    auto previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd));
    GetWindowRect(hwnd, &state->originalRect);
    auto r = state->originalRect;
    state->container = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, L"STATIC", L"TrenchHQ disposable target-owned container",
        WS_POPUP | WS_CLIPCHILDREN, r.left, r.top, r.right - r.left, r.bottom - r.top, nullptr, nullptr, instance, nullptr);
    bool success = false;
    if (state->container) {
        success = SetWindowSubclass(state->container, ContainerProc, subclassId + 1, reinterpret_cast<DWORD_PTR>(hwnd)) != FALSE;
        if (success) {
            SetWindowLongPtr(hwnd, GWL_STYLE, (state->originalStyle & ~static_cast<LONG_PTR>(0x80CF0000)) | WS_CHILD);
            SetParent(hwnd, state->container);
            success = GetParent(hwnd) == state->container;
        }
        if (success) {
            ShowWindow(state->container, SW_SHOWNOACTIVATE);
            success = SetWindowPos(hwnd, nullptr, 0, 0, r.right - r.left, r.bottom - r.top,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOSENDCHANGING) != FALSE;
        }
        if (!success) RestoreEmbedding(hwnd, state);
    }
    SetThreadDpiAwarenessContext(previous);
    return success;
}

static void Release(HWND hwnd, State* state) {
    RemoveWindowSubclass(hwnd, LockedProc, subclassId);
    RemoveProp(hwnd, property);
    if (state->timer) KillTimer(hwnd, state->timer);
    if (state->cbt) UnhookWindowsHookEx(state->cbt);
    RestoreEmbedding(hwnd, state);
    CloseHandle(state->owner);
    delete state;
}

static LRESULT CALLBACK CbtProc(int code, WPARAM wparam, LPARAM lparam) {
    if (code == HCBT_MOVESIZE || code == HCBT_MINMAX) {
        HWND hwnd = reinterpret_cast<HWND>(wparam);
        if (GetWindowThreadProcessId(hwnd, nullptr) == GetCurrentThreadId()) {
            auto state = reinterpret_cast<State*>(GetProp(hwnd, property));
            if (state && WaitForSingleObject(state->owner, 0) == WAIT_TIMEOUT) return 1;
        }
    }
    return CallNextHookEx(nullptr, code, wparam, lparam);
}

static LRESULT CALLBACK LockedProc(HWND hwnd, UINT msg, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR data) {
    auto state = reinterpret_cast<State*>(data);
    if (msg == WM_NCDESTROY || WaitForSingleObject(state->owner, 0) != WAIT_TIMEOUT
        || (msg == DetachMessage() && wparam == state->ownerPid)) {
        bool detached = msg == DetachMessage();
        Release(hwnd, state);
        return detached ? 1 : DefSubclassProc(hwnd, msg, wparam, lparam);
    }
    if (msg == WM_TIMER && wparam == state->timer) return 0;
    if (msg == RawHitMessage()) return DefSubclassProc(hwnd, WM_NCHITTEST, 0, lparam);
    if (msg == WM_NCHITTEST) {
        LRESULT hit = DefSubclassProc(hwnd, msg, wparam, lparam);
        // Keep these points nonclient. Reclassifying Chrome's caption as client can leave its
        // custom input routing in a drag/selection state even though native movement was blocked.
        return hit == HTCAPTION || (hit >= HTLEFT && hit <= HTBOTTOMRIGHT) ? HTBORDER : hit;
    }
    if (msg == WM_SYSCOMMAND) {
        WPARAM command = wparam & 0xfff0;
        if (command == SC_MOVE || command == SC_SIZE || command == SC_MINIMIZE
            || command == SC_MAXIMIZE || command == SC_RESTORE) return 0;
    }
    if (msg == WM_WINDOWPOSCHANGING) {
        // The probe deliberately tests the documented SWP_NOSENDCHANGING bypass separately.
        auto position = reinterpret_cast<WINDOWPOS*>(lparam);
        position->flags |= SWP_NOMOVE | SWP_NOSIZE;
        return 0;
    }
    return DefSubclassProc(hwnd, msg, wparam, lparam);
}

static LRESULT CALLBACK AttachProc(int code, WPARAM wparam, LPARAM lparam) {
    if (code >= 0) {
        auto message = reinterpret_cast<CWPSTRUCT*>(lparam);
        if ((message->message == AttachMessage() || message->message == AttachMessage(true)) && !GetProp(message->hwnd, property)) {
            DWORD ownerPid = 0;
            GetWindowThreadProcessId(reinterpret_cast<HWND>(message->lParam), &ownerPid);
            if (ownerPid && ownerPid == message->wParam && ownerPid != GetCurrentProcessId()) {
                HANDLE owner = OpenProcess(SYNCHRONIZE, FALSE, ownerPid);
                auto state = owner ? new (std::nothrow) State{ owner, ownerPid, nullptr, 0, nullptr, nullptr, 0, {} } : nullptr;
                if (state) {
                    // A callback must never point into an unloaded module after UnhookWindowsHookEx.
                    HMODULE kept = nullptr;
                    bool ready = GetModuleHandleEx(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                        reinterpret_cast<LPCWSTR>(&LockedProc), &kept)
                        && (message->message != AttachMessage(true) || EmbedOnTargetThread(message->hwnd, state))
                        && SetWindowSubclass(message->hwnd, LockedProc, subclassId, reinterpret_cast<DWORD_PTR>(state));
                    if (ready) {
                        if (!SetProp(message->hwnd, property, state)) Release(message->hwnd, state);
                        else {
                            state->timer = SetTimer(message->hwnd, reinterpret_cast<UINT_PTR>(state), 250, nullptr);
                            state->cbt = SetWindowsHookEx(WH_CBT, CbtProc, instance, GetCurrentThreadId());
                            if (!state->timer || !state->cbt) Release(message->hwnd, state);
                        }
                    } else { RestoreEmbedding(message->hwnd, state); CloseHandle(owner); delete state; }
                } else if (owner) CloseHandle(owner);
            }
        }
    }
    return CallNextHookEx(nullptr, code, wparam, lparam);
}

extern "C" __declspec(dllexport) HHOOK AttachLock(HWND hwnd, HWND ownerWindow, BOOL embed) {
    DWORD targetPid = 0;
    DWORD thread = GetWindowThreadProcessId(hwnd, &targetPid);
    DWORD ownerPid = 0;
    GetWindowThreadProcessId(ownerWindow, &ownerPid);
    if (!thread || targetPid == GetCurrentProcessId() || ownerPid != GetCurrentProcessId() || GetProp(hwnd, property)) return nullptr;
    HHOOK hook = SetWindowsHookEx(WH_CALLWNDPROC, AttachProc, instance, thread);
    if (!hook) return nullptr;
    DWORD_PTR result = 0;
    if (!SendMessageTimeout(hwnd, AttachMessage(embed != FALSE), ownerPid, reinterpret_cast<LPARAM>(ownerWindow), SMTO_ABORTIFHUNG, 2000, &result)
        || !GetProp(hwnd, property)) {
        UnhookWindowsHookEx(hook);
        return nullptr;
    }
    return hook;
}

extern "C" __declspec(dllexport) BOOL DetachLock(HWND hwnd, HHOOK hook) {
    DWORD_PTR result = 0;
    BOOL sent = SendMessageTimeout(hwnd, DetachMessage(), GetCurrentProcessId(), 0, SMTO_ABORTIFHUNG, 2000, &result) != 0;
    if (hook) UnhookWindowsHookEx(hook);
    return sent && !GetProp(hwnd, property);
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) instance = module;
    return TRUE;
}
