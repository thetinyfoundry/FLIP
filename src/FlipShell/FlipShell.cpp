// Voce "Converti con FLIP" del menu contestuale, usata solo dal pacchetto Microsoft Store.
// Windows la carica in un processo separato (dllhost), non dentro Esplora file.
#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <shobjidl.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <wrl/module.h>
#include <appmodel.h>
#include <string>

using namespace Microsoft::WRL;

static HMODULE g_module;

static std::wstring PackageFile(const wchar_t* name)
{
    std::wstring path(32768, L'\0');
    DWORD length = GetModuleFileNameW(g_module, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size()) return {};
    path.resize(length);
    size_t slash = path.find_last_of(L'\\');
    return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash + 1) + name;
}

// L'interruttore in FLIP crea questo file quando l'utente non vuole la voce nel menu.
static bool DisabledByUser()
{
    wchar_t family[PACKAGE_FAMILY_NAME_MAX_LENGTH + 1];
    UINT32 length = ARRAYSIZE(family);
    if (GetCurrentPackageFamilyName(&length, family) != ERROR_SUCCESS) return false;
    PWSTR local = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) return false;
    std::wstring marker = std::wstring(local) + L"\\Packages\\" + family + L"\\LocalState\\FLIP\\context-menu.off";
    CoTaskMemFree(local);
    return GetFileAttributesW(marker.c_str()) != INVALID_FILE_ATTRIBUTES;
}

// Regole di CommandLineToArgvW: un percorso può terminare con una barra rovesciata.
static void AppendArgument(std::wstring& command, const wchar_t* value)
{
    command += L" \"";
    size_t slashes = 0;
    for (const wchar_t* c = value; *c; ++c)
    {
        if (*c == L'\\') { ++slashes; continue; }
        command.append(*c == L'"' ? slashes * 2 + 1 : slashes, L'\\');
        slashes = 0;
        command += *c;
    }
    command.append(slashes * 2, L'\\');
    command += L'"';
}

class __declspec(uuid("7C0B6C53-6C1E-4B5C-9B35-2E9C5D7E4A11")) ConvertCommand final
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>
{
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override
    {
        bool italian = PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_ITALIAN;
        return SHStrDupW(italian ? L"Converti con FLIP" : L"Convert with FLIP", name);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
    {
        std::wstring path = PackageFile(L"Flip.ico");
        return path.empty() ? E_FAIL : SHStrDupW(path.c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetCanonicalName(GUID* name) override { *name = __uuidof(ConvertCommand); return S_OK; }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
    {
        *state = DisabledByUser() ? ECS_HIDDEN : ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (!items) return E_INVALIDARG;
        std::wstring executable = PackageFile(L"Flip.exe");
        if (executable.empty()) return E_FAIL;

        std::wstring command = L"\"" + executable + L"\" --files";
        DWORD count = 0, added = 0;
        HRESULT hr = items->GetCount(&count);
        if (FAILED(hr)) return hr;
        for (DWORD i = 0; i < count && i < 128; ++i)
        {
            ComPtr<IShellItem> item;
            if (FAILED(items->GetItemAt(i, &item))) continue;
            PWSTR path = nullptr;
            if (FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &path))) continue;
            if (command.size() + wcslen(path) < 32000) { AppendArgument(command, path); ++added; }
            CoTaskMemFree(path);
        }
        if (added == 0) return E_INVALIDARG;

        STARTUPINFOW startup = { sizeof(startup) };
        PROCESS_INFORMATION process = {};
        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process))
            return HRESULT_FROM_WIN32(GetLastError());
        AllowSetForegroundWindow(process.dwProcessId);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override { *flags = ECF_DEFAULT; return S_OK; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override { *commands = nullptr; return E_NOTIMPL; }
};

CoCreatableClass(ConvertCommand)

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** object)
{
    return Module<InProc>::GetModule().GetClassObject(clsid, iid, object);
}

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().Terminate() ? S_OK : S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
