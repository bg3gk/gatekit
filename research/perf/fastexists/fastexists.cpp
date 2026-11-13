// fastexists: answers the game's "is there a loose file?" questions from memory.
//
// Before Baldur's Gate 3 opens a file that lives in a .pak it asks Windows whether a loose file
// of the same name exists under Data (GetFileAttributesW): tens of thousands of questions per
// start, nearly all answered "no". This DLL, loaded into the game, replaces the game's import
// of GetFileAttributesW with a function that knows every file and folder under Data (one walk
// of the tree on a thread of its own at load, kept current by directory change notifications) and
// answers "no such file" itself. A path that does exist, or lies outside Data, or belongs to
// Script Extender's hot-reloaded files, goes to Windows as before - so the answers are the
// same, only quicker.
//
// Nothing on disk is changed; the only write is the game's import table entry in memory.
// Counters go to fastexists.log beside the DLL (rewritten every few seconds).
// FASTEXISTS=off in the environment loads the DLL but leaves the game alone (for A/B runs).
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <atomic>
#include <string>
#include <unordered_set>
#include <vector>
#include <cstdio>

namespace {

using AttributesFn = DWORD(WINAPI*)(LPCWSTR);
AttributesFn g_original = nullptr;

SRWLOCK g_lock = SRWLOCK_INIT;
std::unordered_set<std::wstring> g_known;       // every existing path under Data, lower case
std::wstring g_root;                            // "c:\...\data\" lower case
std::atomic<int> g_state{0};                    // 0 not built, 1 building, 2 ready, 3 off
std::atomic<unsigned long long> g_calls{0}, g_inside{0}, g_answered{0}, g_events{0}, g_overflows{0};
std::atomic<unsigned long long> g_ticksPassed{0};
std::vector<std::pair<std::wstring, std::wstring>> g_watch; // (folder to watch, the name it has under Data)
wchar_t g_logPath[MAX_PATH];
wchar_t g_examples[8][300];                     // the first questions that were not under Data, for the log
std::atomic<int> g_exampleCount{0};

void example(LPCWSTR path) {
    int index = g_exampleCount.load();
    if (index < 8 && g_exampleCount.compare_exchange_strong(index, index + 1)) wcsncpy(g_examples[index], path, 299);
}
ULONGLONG g_buildMs = 0;
size_t g_filesSeen = 0;

void lower(std::wstring& text) {
    for (auto& c : text) {
        if (c == L'/') c = L'\\';
        else if (c >= L'A' && c <= L'Z') c = wchar_t(c + 32);
        else if (c > 127) c = (wchar_t)(ULONG_PTR)CharLowerW((LPWSTR)(ULONG_PTR)c);
    }
}

void walk(const std::wstring& folder, const std::wstring& as) {
    // `folder` is where to look, `as` is the lower-case name the game uses for it (they differ
    // below a junction only in spelling; kept separate so that watches can use real handles).
    WIN32_FIND_DATAW item;
    HANDLE find = FindFirstFileExW((folder + L"*").c_str(), FindExInfoBasic, &item, FindExSearchNameMatch, nullptr,
                                   FIND_FIRST_EX_LARGE_FETCH);
    if (find == INVALID_HANDLE_VALUE) return;
    do {
        if (item.cFileName[0] == L'.' && (!item.cFileName[1] || (item.cFileName[1] == L'.' && !item.cFileName[2]))) continue;
        std::wstring name = item.cFileName;
        lower(name);
        g_known.insert(as + name);
        ++g_filesSeen;
        if (item.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            if (item.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)
                g_watch.emplace_back(folder + item.cFileName + L"\\", as + name + L"\\"); // changes below a junction are not reported to its parent
            walk(folder + item.cFileName + L"\\", as + name + L"\\");
        }
    } while (FindNextFileW(find, &item));
    FindClose(find);
}

struct Watch { std::wstring folder, as; };

DWORD WINAPI watcher(LPVOID parameter) {
    Watch* watch = (Watch*)parameter;
    HANDLE directory = CreateFileW(watch->folder.c_str(), FILE_LIST_DIRECTORY, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                   nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (directory == INVALID_HANDLE_VALUE) { g_state = 3; return 0; }   // cannot keep the picture current: stand aside
    std::vector<BYTE> buffer(256 * 1024);
    for (;;) {
        DWORD got = 0;
        if (!ReadDirectoryChangesW(directory, buffer.data(), (DWORD)buffer.size(), TRUE,
                                   FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_DIR_NAME, &got, nullptr, nullptr)) {
            g_state = 3;
            return 0;
        }
        if (got == 0) { ++g_overflows; g_state = 3; return 0; }          // events were lost: stop answering
        for (BYTE* at = buffer.data();;) {
            auto* event = (FILE_NOTIFY_INFORMATION*)at;
            if (event->Action == FILE_ACTION_ADDED || event->Action == FILE_ACTION_RENAMED_NEW_NAME) {
                std::wstring name(event->FileName, event->FileNameLength / sizeof(wchar_t));
                lower(name);
                AcquireSRWLockExclusive(&g_lock);
                // the path and every folder above it (a new folder may arrive in one event with its contents later)
                std::wstring full = watch->as + name;
                for (size_t cut = full.size(); cut > g_root.size(); cut = full.rfind(L'\\', cut - 1)) {
                    g_known.insert(full.substr(0, cut));
                    if (cut == 0) break;
                }
                ReleaseSRWLockExclusive(&g_lock);
                ++g_events;
            }
            if (!event->NextEntryOffset) break;
            at += event->NextEntryOffset;
        }
    }
}

DWORD WINAPI reporter(LPVOID) {
    LARGE_INTEGER frequency;
    QueryPerformanceFrequency(&frequency);
    for (;;) {
        Sleep(3000);
        FILE* out = _wfopen(g_logPath, L"w");
        if (!out) continue;
        fprintf(out, "state %d (2 = answering, 3 = stood aside)\nroot %ls\nknown paths %zu (walk took %llu ms, %zu watches)\n"
                     "questions %llu\n under Data %llu\n answered here %llu\n passed to Windows under Data: %.3f s in total\n"
                     "change events %llu, overflows %llu\n",
                g_state.load(), g_root.c_str(), g_known.size(), g_buildMs, g_watch.size() + 1, g_calls.load(), g_inside.load(),
                g_answered.load(), double(g_ticksPassed.load()) / double(frequency.QuadPart), g_events.load(), g_overflows.load());
        for (int i = 0; i < g_exampleCount.load() && i < 8; ++i) fprintf(out, "not under Data: %ls\n", g_examples[i]);
        fclose(out);
    }
}

void build() {
    ULONGLONG started = GetTickCount64();
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring root = exe;                                    // the exe is in <game>/bin; Data is beside bin
    root.erase(root.rfind(L'\\'));
    root.erase(root.rfind(L'\\') + 1);
    root += L"Data\\";
    std::wstring as = root;
    lower(as);
    g_root = as;
    // Watches first, then the walk: a file created in between is reported and added.
    HANDLE probe = CreateFileW(root.c_str(), FILE_LIST_DIRECTORY, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                               OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (probe == INVALID_HANDLE_VALUE) { g_state = 3; return; }
    CloseHandle(probe);
    CreateThread(nullptr, 0, watcher, new Watch{root, as}, 0, nullptr);
    AcquireSRWLockExclusive(&g_lock);
    walk(root, as);
    ReleaseSRWLockExclusive(&g_lock);
    for (auto& entry : g_watch) CreateThread(nullptr, 0, watcher, new Watch{entry.first, entry.second}, 0, nullptr);
    g_buildMs = GetTickCount64() - started;
    int building = 1;
    g_state.compare_exchange_strong(building, 2);
}

DWORD WINAPI hooked(LPCWSTR path) {
    ++g_calls;
    if (g_state.load(std::memory_order_relaxed) != 2 || !path) return g_original(path);   // still walking the tree, or off
    // The game writes its paths as \\?\C:\...: literal, already in the plain form. Anything else
    // is brought to that form first.
    wchar_t buffer[1024];
    const wchar_t* full;
    size_t length;
    if (path[0] == L'\\' && path[1] == L'\\' && path[2] == L'?' && path[3] == L'\\') {
        full = path + 4;
        length = wcslen(full);
    } else {
        length = GetFullPathNameW(path, 1024, buffer, nullptr);
        full = buffer;
    }
    if (length == 0 || length >= 1024 || length <= g_root.size()) return g_original(path);
    wchar_t local[1024];
    for (size_t i = 0; i < length; ++i) {
        wchar_t c = full[i];
        if (c == L'/') c = L'\\';
        else if (c >= L'A' && c <= L'Z') c = wchar_t(c + 32);
        else if (c > 127) return g_original(path);              // rare; let Windows compare such names
        local[i] = c;
    }
    local[length] = 0;
    if (wcsncmp(local, g_root.c_str(), g_root.size()) != 0) { example(path); return g_original(path); }
    ++g_inside;
    if (wcsstr(local, L"\\scriptextender\\")) return g_original(path);   // hot-reloaded files: always ask Windows
    while (length > g_root.size() && local[length - 1] == L'\\') local[--length] = 0;
    AcquireSRWLockShared(&g_lock);
    bool exists = g_known.find(std::wstring(local, length)) != g_known.end();
    ReleaseSRWLockShared(&g_lock);
    if (!exists) {
        ++g_answered;
        SetLastError(ERROR_FILE_NOT_FOUND);
        return INVALID_FILE_ATTRIBUTES;
    }
    LARGE_INTEGER a, b;
    QueryPerformanceCounter(&a);
    DWORD result = g_original(path);
    QueryPerformanceCounter(&b);
    g_ticksPassed += (unsigned long long)(b.QuadPart - a.QuadPart);
    return result;
}

// Timing-only replacement (FASTEXISTS=measure): every question goes to Windows, and is timed.
DWORD WINAPI measured(LPCWSTR path) {
    ++g_calls;
    LARGE_INTEGER a, b;
    QueryPerformanceCounter(&a);
    DWORD result = g_original(path);
    QueryPerformanceCounter(&b);
    g_ticksPassed += (unsigned long long)(b.QuadPart - a.QuadPart);
    if (result == INVALID_FILE_ATTRIBUTES) ++g_answered; else ++g_inside;
    return result;
}

bool patchImports(AttributesFn replacement) {
    BYTE* base = (BYTE*)GetModuleHandleW(nullptr);
    auto* dos = (IMAGE_DOS_HEADER*)base;
    auto* nt = (IMAGE_NT_HEADERS*)(base + dos->e_lfanew);
    auto& directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!directory.VirtualAddress) return false;
    FARPROC wanted[2] = { GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "GetFileAttributesW"),
                          GetProcAddress(GetModuleHandleW(L"kernelbase.dll"), "GetFileAttributesW") };
    bool done = false;
    for (auto* import = (IMAGE_IMPORT_DESCRIPTOR*)(base + directory.VirtualAddress); import->Name; ++import) {
        for (auto* thunk = (IMAGE_THUNK_DATA*)(base + import->FirstThunk); thunk->u1.Function; ++thunk) {
            if ((FARPROC)thunk->u1.Function == wanted[0] || (FARPROC)thunk->u1.Function == wanted[1]) {
                DWORD old;
                if (VirtualProtect(&thunk->u1.Function, sizeof(void*), PAGE_READWRITE, &old)) {
                    if (!g_original) g_original = (AttributesFn)thunk->u1.Function;
                    thunk->u1.Function = (ULONG_PTR)replacement;
                    VirtualProtect(&thunk->u1.Function, sizeof(void*), old, &old);
                    done = true;
                }
            }
        }
    }
    return done;
}

} // namespace

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        GetModuleFileNameW(module, g_logPath, MAX_PATH);
        wchar_t* dot = wcsrchr(g_logPath, L'.');
        if (dot) wcscpy(dot, L".log");
        wchar_t mode[32] = L"";
        GetEnvironmentVariableW(L"FASTEXISTS", mode, 32);
        bool patched = false;
        if (!wcscmp(mode, L"off")) g_state = 3;
        else if (!wcscmp(mode, L"measure")) { g_state = 3; patched = patchImports(measured); }
        else {
            patched = patchImports(hooked);
            // The walk of Data runs beside the game's own start; until it is done every question goes to Windows.
            if (patched) { g_state = 1; CreateThread(nullptr, 0, [](LPVOID) -> DWORD { build(); return 0; }, nullptr, 0, nullptr); }
        }
        if (!patched && wcscmp(mode, L"off")) g_state = 3;
        CreateThread(nullptr, 0, reporter, nullptr, 0, nullptr);
    }
    return TRUE;
}
