// Runs only on the explicitly selected, same-architecture application's UI thread.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commctrl.h>
#include <new>

static HINSTANCE instance;
static constexpr wchar_t stateProperty[] = L"TrenchHQ.WindowPin.State.v1";
static constexpr wchar_t leaseProperty[] = L"TrenchHQ.WindowPin.Lease.v1";
static constexpr UINT_PTR subclassId = 0x4e585750;
static constexpr ULONG_PTR boundsCommand = 0x4e585742;
static UINT AttachMessage() { static UINT id = RegisterWindowMessage(L"TrenchHQ.WindowPin.Attach.v1"); return id; }
static UINT DetachMessage() { static UINT id = RegisterWindowMessage(L"TrenchHQ.WindowPin.Detach.v1"); return id; }
struct State {
    HANDLE owner = nullptr;
    HWND ownerWindow = nullptr, container = nullptr;
    DWORD ownerPid = 0;
    HHOOK cbt = nullptr;
    UINT_PTR timer = 0;
    LONG_PTR originalStyle = 0;
    bool topmost = false, adjusting = false, releasing = false, changed = false;
    WINDOWPLACEMENT placement{ sizeof(WINDOWPLACEMENT) };
    unsigned references = 1;
};
static void Drop(State* state) { if (!--state->references) delete state; }
struct Reference {
    State* state;
    explicit Reference(State* value) : state(value) { ++state->references; }
    ~Reference() { Drop(state); }
};
struct DpiScope {
    DPI_AWARENESS_CONTEXT previous;
    explicit DpiScope(DPI_AWARENESS_CONTEXT context = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)
        : previous(SetThreadDpiAwarenessContext(context)) {}
    ~DpiScope() { if (previous) SetThreadDpiAwarenessContext(previous); }
};
static LRESULT CALLBACK PinnedProc(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);

static bool HasOwner(HWND hwnd, State* state) {
    DWORD pid = 0;
    GetWindowThreadProcessId(state->ownerWindow, &pid);
    return pid == state->ownerPid && WaitForSingleObject(state->owner, 0) == WAIT_TIMEOUT
        && GetProp(hwnd, leaseProperty) == state->ownerWindow;
}

static LRESULT CALLBACK ContainerProc(HWND hwnd, UINT msg, WPARAM wparam, LPARAM lparam, UINT_PTR id, DWORD_PTR data) {
    auto child = reinterpret_cast<HWND>(data);
    if (msg == WM_NCDESTROY) RemoveWindowSubclass(hwnd, ContainerProc, id);
    if (msg == WM_SETFOCUS && IsWindow(child)) { SetFocus(child); return 0; }
    if (msg == WM_MOUSEACTIVATE && IsWindow(child)) { SetFocus(child); return MA_ACTIVATE; }
    if (msg == WM_CLOSE) { PostMessage(child, WM_CLOSE, 0, 0); return 0; }
    return DefSubclassProc(hwnd, msg, wparam, lparam);
}

static bool Release(HWND hwnd, State* state, bool destroying = false) {
    if (state->releasing) return false;
    state->releasing = true;
    state->adjusting = true;
    DpiScope dpi;
    if (state->container && !destroying && IsWindow(hwnd)) {
        SetParent(hwnd, nullptr);
        if (GetParent(hwnd) == state->container) {
            // Keep the recovery timer and container alive. Destroying this parent would close the app.
            state->releasing = false;
            state->adjusting = false;
            return false;
        }
    }
    RemoveWindowSubclass(hwnd, PinnedProc, subclassId);
    RemoveProp(hwnd, stateProperty);
    if (GetProp(hwnd, leaseProperty) == state->ownerWindow) RemoveProp(hwnd, leaseProperty);
    if (state->timer) KillTimer(hwnd, state->timer);
    if (state->cbt) UnhookWindowsHookEx(state->cbt);
    if (!destroying && state->changed && IsWindow(hwnd)) {
        constexpr LONG_PTR mask = 0xC0CF0000; // Popup/child and the controls changed during attach.
        SetWindowLongPtr(hwnd, GWL_STYLE, (GetWindowLongPtr(hwnd, GWL_STYLE) & ~mask) | (state->originalStyle & mask));
        SetWindowPos(hwnd, state->topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOSENDCHANGING);
        auto placement = state->placement;
        if (placement.showCmd == SW_SHOWNORMAL) placement.showCmd = SW_SHOWNOACTIVATE;
        const auto previousDpi = GetDpiForWindow(hwnd);
        if (SetWindowPlacement(hwnd, &placement) && previousDpi && GetDpiForWindow(hwnd) != previousDpi) {
            // An app's synchronous WM_DPICHANGED handler can rescale the restore request.
            // Reapply the saved placement once after the destination DPI has taken effect.
            SetWindowPlacement(hwnd, &placement);
        }
    }
    if (state->container) DestroyWindow(state->container);
    CloseHandle(state->owner);
    Drop(state);
    return true;
}

static bool Position(HWND hwnd, State* state, const RECT& bounds) {
    if (bounds.right <= bounds.left || bounds.bottom <= bounds.top) return false;
    state->adjusting = true;
    bool positioned;
    {
        DpiScope dpi;
        positioned = SetWindowPos(state->container ? state->container : hwnd, HWND_TOPMOST,
            bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top,
            SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_NOCOPYBITS | SWP_FRAMECHANGED) != FALSE;
    }
    if (positioned && state->container && !state->releasing) {
        DpiScope dpi(GetWindowDpiAwarenessContext(state->container));
        RECT client{};
        GetClientRect(state->container, &client);
        positioned = SetWindowPos(hwnd, nullptr, 0, 0, client.right, client.bottom,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_FRAMECHANGED) != FALSE;
    }
    state->adjusting = false;
    return positioned && !state->releasing;
}

static LRESULT CALLBACK CbtProc(int code, WPARAM wparam, LPARAM lparam) {
    if (code == HCBT_MOVESIZE || code == HCBT_MINMAX) {
        auto hwnd = reinterpret_cast<HWND>(wparam);
        if (GetWindowThreadProcessId(hwnd, nullptr) == GetCurrentThreadId()) {
            DWORD_PTR data = 0;
            GetWindowSubclass(hwnd, PinnedProc, subclassId, &data);
            auto state = reinterpret_cast<State*>(data);
            if (state && !state->adjusting && HasOwner(hwnd, state)) return 1;
        }
    }
    return CallNextHookEx(nullptr, code, wparam, lparam);
}

static LRESULT CALLBACK PinnedProc(HWND hwnd, UINT msg, WPARAM wparam, LPARAM lparam, UINT_PTR, DWORD_PTR data) {
    auto state = reinterpret_cast<State*>(data);
    Reference reference(state);
    // Release before the app's close path hides/resizes/destroys its windows. A CBT veto
    // during that teardown can otherwise leave a WinForms message loop alive without a window.
    if (!state->releasing && (msg == WM_CLOSE || msg == WM_NCDESTROY || !HasOwner(hwnd, state)
        || (msg == DetachMessage() && reinterpret_cast<HWND>(wparam) == state->ownerWindow))) {
        bool released = Release(hwnd, state, msg == WM_NCDESTROY);
        return msg == DetachMessage() ? released : DefSubclassProc(hwnd, msg, wparam, lparam);
    }
    if (msg == WM_TIMER && wparam == state->timer) return 0;
    if (msg == WM_COPYDATA && reinterpret_cast<HWND>(wparam) == state->ownerWindow && !state->releasing) {
        auto copy = reinterpret_cast<COPYDATASTRUCT*>(lparam);
        if (copy && copy->dwData == boundsCommand && copy->cbData == sizeof(RECT) && copy->lpData)
            return Position(hwnd, state, *static_cast<RECT*>(copy->lpData));
    }
    if (!state->adjusting) {
        if (msg == WM_NCHITTEST) {
            auto hit = DefSubclassProc(hwnd, msg, wparam, lparam);
            // HTCLIENT breaks Chrome input routing after a blocked custom-caption drag.
            return hit == HTCAPTION || (hit >= HTLEFT && hit <= HTBOTTOMRIGHT) ? HTBORDER : hit;
        }
        if (msg == WM_SYSCOMMAND) {
            auto command = wparam & 0xfff0;
            if (command == SC_MOVE || command == SC_SIZE || command == SC_MINIMIZE
                || command == SC_MAXIMIZE || command == SC_RESTORE) return 0;
        }
        if (msg == WM_WINDOWPOSCHANGING) {
            reinterpret_cast<WINDOWPOS*>(lparam)->flags |= SWP_NOMOVE | SWP_NOSIZE;
            return 0;
        }
    }
    return DefSubclassProc(hwnd, msg, wparam, lparam);
}

static LRESULT CALLBACK AttachProc(int code, WPARAM wparam, LPARAM lparam) {
    if (code >= 0) {
        auto message = reinterpret_cast<CWPSTRUCT*>(lparam);
        auto hwnd = message->hwnd;
        auto ownerWindow = reinterpret_cast<HWND>(message->wParam);
        int mode = static_cast<int>(message->lParam);
        if (message->message == AttachMessage() && mode >= 0 && mode <= 1
            && GetProp(hwnd, leaseProperty) == ownerWindow && !GetProp(hwnd, stateProperty)) {
            DWORD pid = 0;
            GetWindowThreadProcessId(ownerWindow, &pid);
            auto state = pid && pid != GetCurrentProcessId() ? new (std::nothrow) State : nullptr;
            if (state) {
                Reference reference(state);
                state->ownerPid = pid;
                state->ownerWindow = ownerWindow;
                state->owner = OpenProcess(SYNCHRONIZE, FALSE, pid);
                state->originalStyle = GetWindowLongPtr(hwnd, GWL_STYLE);
                state->topmost = (GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
                state->adjusting = true;
                HMODULE kept = nullptr;
                DpiScope dpi;
                bool ready = state->owner && GetWindowPlacement(hwnd, &state->placement)
                    // Callbacks remain safe after the temporary injection hook is unhooked.
                    && GetModuleHandleEx(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                        reinterpret_cast<LPCWSTR>(&PinnedProc), &kept)
                    && SetProp(hwnd, stateProperty, state)
                    && SetWindowSubclass(hwnd, PinnedProc, subclassId, reinterpret_cast<DWORD_PTR>(state));
                if (ready) {
                    state->timer = SetTimer(hwnd, reinterpret_cast<UINT_PTR>(state), 250, nullptr);
                    state->cbt = SetWindowsHookEx(WH_CBT, CbtProc, instance, GetCurrentThreadId());
                    ready = state->timer && state->cbt;
                    auto normal = state->placement;
                    normal.flags = 0;
                    normal.showCmd = SW_SHOWNOACTIVATE;
                    if (ready) { state->changed = true; ready = SetWindowPlacement(hwnd, &normal) != FALSE; }
                    if (ready && !state->releasing && mode != 0) {
                        DpiScope targetDpi(GetWindowDpiAwarenessContext(hwnd));
                        RECT rect{};
                        GetWindowRect(hwnd, &rect);
                        // Target-owned, never a child of TrenchHQ: a TrenchHQ crash cannot destroy this HWND.
                        state->container = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, L"STATIC", L"",
                            WS_POPUP | WS_CLIPCHILDREN | SS_BLACKRECT, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top,
                            nullptr, nullptr, instance, nullptr);
                        ready = state->container && SetWindowSubclass(state->container, ContainerProc, subclassId + 1, reinterpret_cast<DWORD_PTR>(hwnd));
                        if (ready) {
                            SetWindowLongPtr(hwnd, GWL_STYLE, (GetWindowLongPtr(hwnd, GWL_STYLE) & ~static_cast<LONG_PTR>(0x80CF0000)) | WS_CHILD);
                            SetParent(hwnd, state->container);
                            ready = GetParent(hwnd) == state->container;
                            if (ready) ShowWindow(state->container, SW_SHOWNOACTIVATE);
                        }
                    } else if (ready && !state->releasing) {
                        // WPF apps such as Visual Studio may retain their own caption/control styles.
                        // The subclass and CBT hook enforce the lock independently of those styles.
                        SetLastError(ERROR_SUCCESS);
                        auto previous = SetWindowLongPtr(hwnd, GWL_STYLE, GetWindowLongPtr(hwnd, GWL_STYLE) & ~static_cast<LONG_PTR>(0xCF0000));
                        ready = previous != 0 || GetLastError() == ERROR_SUCCESS;
                    }
                }
                state->adjusting = false;
                if ((!ready || !HasOwner(hwnd, state)) && !state->releasing) Release(hwnd, state);
            }
        }
    }
    return CallNextHookEx(nullptr, code, wparam, lparam);
}

extern "C" __declspec(dllexport) BOOL AttachWindow(HWND hwnd, HWND ownerWindow, int mode) {
    if (mode < 0 || mode > 1) return FALSE;
    DWORD pid = 0, ownerPid = 0;
    auto thread = GetWindowThreadProcessId(hwnd, &pid);
    GetWindowThreadProcessId(ownerWindow, &ownerPid);
    if (!thread || pid == GetCurrentProcessId() || ownerPid != GetCurrentProcessId()
        || GetProp(hwnd, stateProperty) || GetProp(hwnd, leaseProperty)) return FALSE;
    if (!SetProp(hwnd, leaseProperty, ownerWindow)) return FALSE;
    auto hook = SetWindowsHookEx(WH_CALLWNDPROC, AttachProc, instance, thread);
    DWORD_PTR result = 0;
    bool attached = hook && SendMessageTimeout(hwnd, AttachMessage(), reinterpret_cast<WPARAM>(ownerWindow), mode,
        SMTO_ABORTIFHUNG | SMTO_BLOCK, 2000, &result) && GetProp(hwnd, stateProperty);
    if (!attached) RemoveProp(hwnd, leaseProperty); // Late attach observes cancellation; timer also releases it.
    if (hook) UnhookWindowsHookEx(hook);
    return attached;
}

extern "C" __declspec(dllexport) BOOL PositionWindow(HWND hwnd, HWND ownerWindow, const RECT* bounds) {
    if (!bounds || GetProp(hwnd, leaseProperty) != ownerWindow) return FALSE;
    COPYDATASTRUCT copy{ boundsCommand, sizeof(RECT), const_cast<RECT*>(bounds) };
    DWORD_PTR result = 0;
    return SendMessageTimeout(hwnd, WM_COPYDATA, reinterpret_cast<WPARAM>(ownerWindow), reinterpret_cast<LPARAM>(&copy),
        SMTO_ABORTIFHUNG | SMTO_BLOCK, 2000, &result) && result;
}

extern "C" __declspec(dllexport) BOOL DetachWindow(HWND hwnd, HWND ownerWindow) {
    if (GetProp(hwnd, leaseProperty) == ownerWindow) RemoveProp(hwnd, leaseProperty);
    DWORD_PTR result = 0;
    SendMessageTimeout(hwnd, DetachMessage(), reinterpret_cast<WPARAM>(ownerWindow), 0,
        SMTO_ABORTIFHUNG | SMTO_BLOCK, 2000, &result);
    // If the target is hung, its own timer completes restoration when it pumps messages again.
    return !GetProp(hwnd, stateProperty);
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) instance = module;
    return TRUE;
}
