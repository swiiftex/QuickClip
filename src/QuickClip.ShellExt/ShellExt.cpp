// Windows 11 File Explorer context menu entry ("Edit with QuickClip").
// Explorer loads this through the sparse app package in packaging/AppxManifest.xml; the DLL lives next to
// QuickClip.exe and launches it with each selected file.

#include <windows.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <shobjidl_core.h>
#include <wrl/implements.h>
#include <wrl/module.h>
#include <string>

using namespace Microsoft::WRL;

static HMODULE g_module;

static std::wstring QuickClipExe()
{
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(g_module, path, MAX_PATH);
    PathRemoveFileSpecW(path);
    PathAppendW(path, L"QuickClip.exe");
    return path;
}

class __declspec(uuid("EE08DD55-5F4C-4512-9EF5-EF815DA68098")) EditWithQuickClip final
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>
{
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) override { return SHStrDupW(L"Edit with QuickClip", name); }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        return SHStrDupW((QuickClipExe() + L",0").c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tip) override
    {
        *tip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* guid) override
    {
        *guid = __uuidof(EditWithQuickClip);
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
    {
        *state = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        *commands = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (!items) return S_OK;
        DWORD count = 0;
        items->GetCount(&count);
        const std::wstring exe = QuickClipExe();
        for (DWORD i = 0; i < count && i < 16; i++) // one window per file; cap accidental huge selections
        {
            ComPtr<IShellItem> item;
            PWSTR path = nullptr;
            if (FAILED(items->GetItemAt(i, &item)) || FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)))
                continue;
            const std::wstring args = L"\"" + std::wstring(path) + L"\"";
            ShellExecuteW(nullptr, L"open", exe.c_str(), args.c_str(), nullptr, SW_SHOWNORMAL);
            CoTaskMemFree(path);
        }
        return S_OK;
    }
};

CoCreatableClass(EditWithQuickClip)

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** instance)
{
    return Module<InProc>::GetModule().GetClassObject(clsid, riid, instance);
}

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
