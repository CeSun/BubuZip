#include "ExplorerCommand.cpp"
#include <shellapi.h>
#undef NDEBUG
#include <cassert>
#include <filesystem>
#include <fstream>
#include <iostream>

static ComPtr<IShellItemArray> Items(const std::filesystem::path& path)
{
    ComPtr<IShellItem> item;
    assert(SUCCEEDED(SHCreateItemFromParsingName(path.c_str(), nullptr, IID_PPV_ARGS(&item))));
    ComPtr<IShellItemArray> items;
    assert(SUCCEEDED(SHCreateShellItemArrayFromShellItem(item.Get(), IID_PPV_ARGS(&items))));
    return items;
}

int main()
{
    assert(SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)));
    {
        // Exercise the real COM activation and lifetime contract without registering anything.
        assert(DllCanUnloadNow() == S_OK);
        ComPtr<IClassFactory> factory;
        assert(SUCCEEDED(DllGetClassObject(CommandId, IID_PPV_ARGS(&factory))));
        assert(DllCanUnloadNow() == S_FALSE);
        ComPtr<IExplorerCommand> root;
        assert(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&root))));
        EXPCMDFLAGS flags;
        assert(SUCCEEDED(root->GetFlags(&flags)) && flags == ECF_HASSUBCOMMANDS);
        EXPCMDSTATE state;
        assert(SUCCEEDED(root->GetState(nullptr, FALSE, &state)) && state == ECS_HIDDEN);
        ComPtr<IEnumExplorerCommand> enumerator;
        assert(SUCCEEDED(root->EnumSubCommands(&enumerator)));
        assert(enumerator->Next(2, nullptr, nullptr) == E_POINTER);
        assert(enumerator->Skip(5) == S_OK);
        ComPtr<IEnumExplorerCommand> clone;
        assert(SUCCEEDED(enumerator->Clone(&clone)));
        assert(clone->Skip(ULONG_MAX) == S_FALSE);
        assert(enumerator->Reset() == S_OK);
        IExplorerCommand* children[10]{};
        ULONG fetched = 0;
        assert(enumerator->Next(10, children, &fetched) == S_FALSE && fetched == 9);
        auto temporary = std::filesystem::temp_directory_path() / (L"BubuZip shell test " + std::to_wstring(GetCurrentProcessId()));
        std::filesystem::create_directory(temporary);
        const auto zip = temporary / L"中文 & $ archive.ZIP";
        const auto plain = temporary / L"plain.txt";
        const auto folder = temporary / L"folder.zip";
        { std::ofstream stream(zip, std::ios::binary); stream << "PK\x05\x06"; for (int i = 0; i < 18; ++i) stream.put(0); }
        { std::ofstream stream(plain); stream << "test"; }
        std::filesystem::create_directory(folder);
        auto zipItems = Items(zip), plainItems = Items(plain), folderItems = Items(folder);
        for (ULONG i = 0; i < fetched; ++i) {
            assert(SUCCEEDED(children[i]->GetState(zipItems.Get(), FALSE, &state)) && state == ECS_ENABLED);
            assert(SUCCEEDED(children[i]->GetState(plainItems.Get(), FALSE, &state)) && state == static_cast<EXPCMDSTATE>(actions[i].archiveOnly ? ECS_HIDDEN : ECS_ENABLED));
            assert(SUCCEEDED(children[i]->GetState(folderItems.Get(), FALSE, &state)) && state == static_cast<EXPCMDSTATE>(actions[i].archiveOnly ? ECS_HIDDEN : ECS_ENABLED));
            assert(children[i]->Invoke(nullptr, nullptr) == E_INVALIDARG);
            PWSTR title = nullptr;
            assert(SUCCEEDED(children[i]->GetTitle(zipItems.Get(), &title)));
            assert(std::wstring(title) == actions[i].title);
            CoTaskMemFree(title);
            children[i]->Release();
        }
        // Windows command-line parsing must round-trip spaces, metacharacters, quotes and trailing slashes.
        for (const auto& argument : {zip.wstring(), std::wstring(L"C:\\"), std::wstring(L"C:\\a b\\"), std::wstring(L"a\\\"b")}) {
            auto command = L"BubuZip.exe --shell compress -- " + QuoteArgument(argument);
            int count = 0;
            auto argv = CommandLineToArgvW(command.c_str(), &count);
            assert(argv && count == 5 && argument == argv[4]);
            LocalFree(argv);
        }
        zipItems.Reset(); plainItems.Reset(); folderItems.Reset();
        std::filesystem::remove_all(temporary);
    }
    assert(DllCanUnloadNow() == S_OK);
    CoUninitialize();
    std::cout << "Shell COM, selection, enumeration, lifetime and argument tests passed.\n";
}
