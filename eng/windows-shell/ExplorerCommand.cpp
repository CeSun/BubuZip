#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <wrl.h>
#include <atomic>
#include <iterator>
#include <string>
#include "ArchiveFormats.h"

using namespace Microsoft::WRL;
// Keep this CLSID in sync with AppxManifest.xml.
inline constexpr GUID CommandId = {0x6c732f10, 0x2f80, 0x4bf1, {0xa2, 0x73, 0x40, 0x63, 0x91, 0x57, 0x37, 0xee}};
static HMODULE moduleHandle;
static std::atomic<long> liveObjects = 0;
static std::atomic<long> serverLocks = 0;
struct Lifetime { Lifetime() { ++liveObjects; } ~Lifetime() { --liveObjects; } };

struct Action { const wchar_t* title; const wchar_t* verb; bool archiveOnly; };
static constexpr Action actions[] = {
    {L"打开压缩包", L"open", true},
    {L"提取文件…", L"extract", true},
    {L"提取到此处", L"extract-here", true},
    {L"提取到同名文件夹", L"extract-folder", true},
    {L"测试压缩包", L"test", true},
    {L"添加到压缩包…", L"compress", false},
    {L"压缩为同名 7z 文件", L"compress-7z", false},
    {L"压缩为同名 ZIP 文件", L"compress-zip", false},
};

static std::wstring QuoteArgument(const std::wstring& value)
{
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t c : value) {
        if (c == L'\\') { ++slashes; continue; }
        result.append(c == L'"' ? slashes * 2 + 1 : slashes, L'\\');
        result += c;
        slashes = 0;
    }
    result.append(slashes * 2, L'\\');
    return result + L'"';
}

static std::wstring ExecutablePath()
{
    wchar_t path[32768];
    DWORD length = GetModuleFileNameW(moduleHandle, path, static_cast<DWORD>(std::size(path)));
    if (!length || length >= std::size(path)) return {};
    std::wstring result(path, length);
    const auto slash = result.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return {};
    return result.substr(0, slash + 1) + L"BubuZip.exe";
}

// Only filesystem items are accepted. Do not read file contents or access the network in GetState.
static HRESULT Selection(IShellItemArray* items, std::wstring& path, bool& archive)
{
    if (!items) return E_INVALIDARG;
    DWORD count = 0;
    HRESULT hr = items->GetCount(&count);
    if (FAILED(hr)) return hr;
    if (count != 1) return E_INVALIDARG;
    ComPtr<IShellItem> item;
    hr = items->GetItemAt(0, &item);
    if (FAILED(hr)) return hr;
    SFGAOF attributes = 0;
    hr = item->GetAttributes(SFGAO_FILESYSTEM | SFGAO_FOLDER, &attributes);
    if (FAILED(hr) || !(attributes & SFGAO_FILESYSTEM)) return E_INVALIDARG;
    PWSTR name = nullptr;
    hr = item->GetDisplayName(SIGDN_FILESYSPATH, &name);
    if (FAILED(hr)) return hr;
    try { path = name; }
    catch (...) { CoTaskMemFree(name); throw; }
    CoTaskMemFree(name);
    archive = false;
    // ZIP shell items can report SFGAO_FOLDER. STREAM distinguishes those from physical folders.
    const wchar_t* extension = PathFindExtensionW(path.c_str());
    for (const auto candidate : ArchiveExtensions)
        if (_wcsicmp(extension, candidate) == 0) { archive = true; break; }
    if ((attributes & SFGAO_FOLDER) && !(attributes & SFGAO_STREAM)) {
        // Ask for STREAM separately: physical folders lack it, archive folders normally have it.
        SFGAOF stream = 0;
        if (SUCCEEDED(item->GetAttributes(SFGAO_STREAM, &stream)) && !(stream & SFGAO_STREAM)) archive = false;
    }
    return S_OK;
}

class Command final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>, private Lifetime
{
    int index_;
public:
    explicit Command(int index = -1) : index_(index) {}
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override
    { return SHStrDupW(index_ < 0 ? L"BubuZip" : actions[index_].title, title); }
    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        *icon = nullptr;
        try {
            auto executable = ExecutablePath();
            return executable.empty() ? E_FAIL : SHStrDupW((L"\"" + executable + L"\",0").c_str(), icon);
        } catch (...) { return E_OUTOFMEMORY; }
    }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetCanonicalName(GUID* name) override
    { *name = CommandId; name->Data1 += index_ + 1; return S_OK; }
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
    {
        *state = ECS_HIDDEN;
        try {
            std::wstring path; bool archive;
            if (SUCCEEDED(Selection(items, path, archive)) && (index_ < 0 || !actions[index_].archiveOnly || archive))
                *state = ECS_ENABLED;
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (index_ < 0) return E_NOTIMPL;
        try {
            std::wstring path; bool archive;
            HRESULT hr = Selection(items, path, archive);
            if (FAILED(hr)) return hr;
            if (actions[index_].archiveOnly && !archive) return E_INVALIDARG;
            auto executable = ExecutablePath();
            if (executable.empty()) return E_FAIL;
            auto command = QuoteArgument(executable) + L" --shell " + actions[index_].verb + L" -- " + QuoteArgument(path);
            if (command.size() >= 32767) return HRESULT_FROM_WIN32(ERROR_FILENAME_EXCED_RANGE);
            STARTUPINFOW startup{}; startup.cb = sizeof(startup);
            PROCESS_INFORMATION process{};
            // Explicit application path and quoted arguments; never dispatch via cmd.exe or PowerShell.
            if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process))
                return HRESULT_FROM_WIN32(GetLastError());
            CloseHandle(process.hThread); CloseHandle(process.hProcess);
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    { *flags = index_ < 0 ? ECF_HASSUBCOMMANDS : ECF_DEFAULT; return S_OK; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** result) override;
};

class CommandEnumerator final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IEnumExplorerCommand>, private Lifetime
{
    ULONG position_;
public:
    explicit CommandEnumerator(ULONG position = 0) : position_(position) {}
    IFACEMETHODIMP Next(ULONG count, IExplorerCommand** commands, ULONG* fetched) override
    {
        if (fetched) *fetched = 0;
        if (!commands || (count != 1 && !fetched)) return E_POINTER;
        ULONG actual = 0;
        while (actual < count && position_ < std::size(actions)) {
            auto command = Make<Command>(static_cast<int>(position_));
            if (!command) {
                for (ULONG i = 0; i < actual; ++i) { commands[i]->Release(); commands[i] = nullptr; }
                position_ -= actual;
                return E_OUTOFMEMORY;
            }
            commands[actual++] = command.Detach(); ++position_;
        }
        if (fetched) *fetched = actual;
        return actual == count ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Skip(ULONG count) override
    {
        const ULONG available = static_cast<ULONG>(std::size(actions)) - position_;
        position_ += count < available ? count : available;
        return count <= available ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Reset() override { position_ = 0; return S_OK; }
    IFACEMETHODIMP Clone(IEnumExplorerCommand** result) override
    {
        *result = nullptr;
        auto clone = Make<CommandEnumerator>(position_);
        return clone ? clone.CopyTo(result) : E_OUTOFMEMORY;
    }
};

HRESULT Command::EnumSubCommands(IEnumExplorerCommand** result)
{
    *result = nullptr;
    if (index_ >= 0) return E_NOTIMPL;
    auto enumerator = Make<CommandEnumerator>();
    return enumerator ? enumerator.CopyTo(result) : E_OUTOFMEMORY;
}

class Factory final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IClassFactory>, private Lifetime
{
public:
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** result) override
    {
        *result = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto command = Make<Command>();
        return command ? command->QueryInterface(iid, result) : E_OUTOFMEMORY;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override { if (lock) ++serverLocks; else --serverLocks; return S_OK; }
};

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** result)
{
    *result = nullptr;
    if (clsid != CommandId) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = Make<Factory>();
    return factory ? factory->QueryInterface(iid, result) : E_OUTOFMEMORY;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return liveObjects == 0 && serverLocks == 0 ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) moduleHandle = instance;
    return TRUE;
}
