#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <appmodel.h>
#include <wrl.h>
#include <filesystem>
#include <string>
#include <vector>
#include <fstream>

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;
using Microsoft::WRL::ClassicCom;
using Microsoft::WRL::Make;
namespace fs=std::filesystem;
// Must match AppxManifest.xml and ShellCommandTest.cpp.
const CLSID CommandId={0x1948a69b,0xd7a8,0x47ce,{0x97,0x24,0x82,0xd2,0xa6,0x4b,0x53,0x7d}};
static HMODULE module;
struct Context { fs::path directory;fs::path data;std::wstring appId; };
static Context context()
{
    wchar_t buffer[32768];auto n=GetModuleFileNameW(module,buffer,32768);if(!n||n==32768)throw HRESULT_FROM_WIN32(GetLastError());
    fs::path directory=fs::path(buffer).parent_path();auto fullName=directory.filename().wstring();
    UINT32 length=0;auto result=PackageFamilyNameFromFullName(fullName.c_str(),&length,nullptr);
    if(result!=ERROR_INSUFFICIENT_BUFFER)throw HRESULT_FROM_WIN32(result);
    std::wstring family(length,L'\0');result=PackageFamilyNameFromFullName(fullName.c_str(),&length,family.data());
    if(result!=ERROR_SUCCESS)throw HRESULT_FROM_WIN32(result);family.resize(length-1);
    PWSTR local=nullptr;auto hr=SHGetKnownFolderPath(FOLDERID_LocalAppData,0,nullptr,&local);if(FAILED(hr))throw hr;
    fs::path data=fs::path(local)/L"Packages"/family/L"LocalState"/L"Data";CoTaskMemFree(local);
    return {directory,data,family+L"!EasyAppKeeper"};
}
static std::string utf8(const std::wstring& text)
{
    int size=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),(int)text.size(),nullptr,0,nullptr,nullptr);
    if(!size)throw HRESULT_FROM_WIN32(GetLastError());std::string result(size,'\0');
    WideCharToMultiByte(CP_UTF8,0,text.data(),(int)text.size(),result.data(),size,nullptr,nullptr);return result;
}
static std::string jsonString(const std::wstring& value)
{
    std::string out="\"";for(unsigned char c:utf8(value))
    {
        if(c=='"'||c=='\\'){out+='\\';out+=(char)c;}
        else if(c<32){const char* hex="0123456789abcdef";out+="\\u00";out+=hex[c>>4];out+=hex[c&15];}
        else out+=(char)c;
    }
    return out+'"';
}
class Command final:public RuntimeClass<RuntimeClassFlags<ClassicCom>,IExplorerCommand>
{
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*,PWSTR* value) override{return SHStrDupW(L"添加到 EasyAppKeeper",value);}
    IFACEMETHODIMP GetIcon(IShellItemArray*,PWSTR* value) override
    {try{return SHStrDupW((context().directory/L"AppKeeper.exe").wstring().append(L",0").c_str(),value);}catch(...){*value=nullptr;return E_FAIL;}}
    IFACEMETHODIMP GetToolTip(IShellItemArray*,PWSTR* value) override{return SHStrDupW(L"批量创建快捷入口，原文件保留原位",value);}
    IFACEMETHODIMP GetCanonicalName(GUID* value) override{*value=CommandId;return S_OK;}
    IFACEMETHODIMP GetState(IShellItemArray*,BOOL,EXPCMDSTATE* value) override
    {try{*value=fs::exists(context().data/L"shell-disabled")?ECS_HIDDEN:ECS_ENABLED;return S_OK;}catch(HRESULT hr){*value=ECS_HIDDEN;return hr;}catch(...){*value=ECS_HIDDEN;return E_FAIL;}}
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* value) override{*value=ECF_DEFAULT;return S_OK;}
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** value) override{*value=nullptr;return E_NOTIMPL;}
    IFACEMETHODIMP Invoke(IShellItemArray* items,IBindCtx*) override
    {
        if(!items)return E_INVALIDARG;
        try
        {
            auto ctx=context();if(fs::exists(ctx.data/L"shell-disabled"))return E_ACCESSDENIED;
            DWORD count=0;auto hr=items->GetCount(&count);if(FAILED(hr))return hr;if(!count||count>1000)return E_INVALIDARG;
            std::string json="{\"Paths\":[";bool first=true;
            for(DWORD i=0;i<count;++i)
            {
                ComPtr<IShellItem> item;PWSTR path=nullptr;hr=items->GetItemAt(i,&item);if(FAILED(hr))return hr;
                hr=item->GetDisplayName(SIGDN_FILESYSPATH,&path);if(FAILED(hr))return hr;
                std::wstring name(path);CoTaskMemFree(path);if(!first)json+=',';first=false;json+=jsonString(name);
            }
            json+="]}";if(json.size()>1024*1024)return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
            auto queue=ctx.data/L"entry-inbox";fs::create_directories(queue);GUID id;hr=CoCreateGuid(&id);if(FAILED(hr))return hr;
            wchar_t guid[40];StringFromGUID2(id,guid,40);auto file=queue/(std::wstring(guid)+L".json");auto temp=file.wstring()+L".tmp";
            {std::ofstream stream(fs::path(temp),std::ios::binary);stream.write(json.data(),json.size());stream.flush();if(!stream)return E_FAIL;}
            if(!MoveFileExW(temp.c_str(),file.c_str(),MOVEFILE_WRITE_THROUGH))return HRESULT_FROM_WIN32(GetLastError());
            ComPtr<IApplicationActivationManager> activation;hr=CoCreateInstance(CLSID_ApplicationActivationManager,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&activation));if(FAILED(hr))return hr;
            DWORD pid;return activation->ActivateApplication(ctx.appId.c_str(),L"--inbox",AO_NONE,&pid);
        }
        catch(HRESULT hr){return hr;}catch(...){return E_FAIL;}
    }
};
class Factory final:public RuntimeClass<RuntimeClassFlags<ClassicCom>,IClassFactory>
{
public:
    IFACEMETHODIMP CreateInstance(IUnknown* outer,REFIID iid,void** value) override
    {if(outer)return CLASS_E_NOAGGREGATION;auto command=Make<Command>();return command?command.CopyTo(iid,value):E_OUTOFMEMORY;}
    IFACEMETHODIMP LockServer(BOOL) override{return S_OK;}
};
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID id,REFIID iid,void** value)
{*value=nullptr;if(id!=CommandId)return CLASS_E_CLASSNOTAVAILABLE;auto factory=Make<Factory>();return factory?factory.CopyTo(iid,value):E_OUTOFMEMORY;}
extern "C" HRESULT __stdcall DllCanUnloadNow(){return S_FALSE;}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){module=instance;DisableThreadLibraryCalls(instance);}return TRUE;}
