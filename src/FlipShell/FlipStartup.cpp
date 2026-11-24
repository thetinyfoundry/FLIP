// Avvio automatico del pacchetto Store: StartupTask non passa argomenti, quindi apre Flip.exe in background.
#include <windows.h>
#include <string>

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    std::wstring path(32768, L'\0');
    DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size()) return 1;
    path.resize(length);
    size_t slash = path.find_last_of(L'\\');
    if (slash == std::wstring::npos) return 1;
    std::wstring executable = path.substr(0, slash + 1) + L"Flip.exe";
    std::wstring command = L"\"" + executable + L"\" --background";

    STARTUPINFOW startup = { sizeof(startup) };
    PROCESS_INFORMATION process = {};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process)) return 1;
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 0;
}
