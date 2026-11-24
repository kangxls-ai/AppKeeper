#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <wrl.h>
#include <UIAutomation.h>
#include <vector>
#include <iostream>
using Microsoft::WRL::ComPtr;
const CLSID CommandId={0x1948a69b,0xd7a8,0x47ce,{0x97,0x24,0x82,0xd2,0xa6,0x4b,0x53,0x7d}};
static ComPtr<IUIAutomationCondition> condition(IUIAutomation* ui,PROPERTYID property,const wchar_t* value)
{
    VARIANT text;VariantInit(&text);text.vt=VT_BSTR;text.bstrVal=SysAllocString(value);ComPtr<IUIAutomationCondition> result;
    ui->CreatePropertyCondition(property,text,&result);VariantClear(&text);return result;
}
static int confirm(const wchar_t* tag)
{
    ComPtr<IUIAutomation> ui;if(FAILED(CoCreateInstance(CLSID_CUIAutomation,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&ui))))return 20;
    ComPtr<IUIAutomationElement> root;ui->GetRootElement(&root);auto title=condition(ui.Get(),UIA_NamePropertyId,L"添加到 EasyAppKeeper");
    for(int n=0;n<100;++n)
    {
        ComPtr<IUIAutomationElement> dialog;root->FindFirst(TreeScope_Children,title.Get(),&dialog);
        if(dialog)
        {
            if(tag&&*tag)
            {
                auto id=condition(ui.Get(),UIA_AutomationIdPropertyId,L"ExternalNewTag");ComPtr<IUIAutomationElement> input;dialog->FindFirst(TreeScope_Descendants,id.Get(),&input);
                ComPtr<IUIAutomationValuePattern> value;if(!input||FAILED(input->GetCurrentPatternAs(UIA_ValuePatternId,IID_PPV_ARGS(&value))))return 21;
                auto text=SysAllocString(tag);auto changed=value->SetValue(text);SysFreeString(text);if(FAILED(changed))return 21;
            }
            auto name=condition(ui.Get(),UIA_NamePropertyId,L"添加入口");ComPtr<IUIAutomationElement> button;dialog->FindFirst(TreeScope_Descendants,name.Get(),&button);
            ComPtr<IUIAutomationInvokePattern> invoke;if(!button||FAILED(button->GetCurrentPatternAs(UIA_InvokePatternId,IID_PPV_ARGS(&invoke))))return 22;
            auto hr=invoke->Invoke();return SUCCEEDED(hr)?0:23;
        }
        Sleep(300);
    }
    return 24;
}
static int legacy(int count,wchar_t** paths)
{
    std::vector<PCIDLIST_ABSOLUTE> ids;
    for(int i=0;i<count;++i){PIDLIST_ABSOLUTE item=nullptr;if(FAILED(SHParseDisplayName(paths[i],nullptr,&item,0,nullptr)))return 30;ids.push_back(item);}
    ComPtr<IShellFolder> desktop;ComPtr<IContextMenu> menu;SHGetDesktopFolder(&desktop);
    auto hr=desktop->GetUIObjectOf(nullptr,(UINT)ids.size(),ids.data(),IID_IContextMenu,nullptr,(void**)menu.GetAddressOf());
    for(auto id:ids)CoTaskMemFree((void*)id);if(FAILED(hr))return 31;
    auto popup=CreatePopupMenu();hr=menu->QueryContextMenu(popup,0,1,0x7fff,CMF_NORMAL);if(FAILED(hr))return 32;
    UINT command=0;
    for(int i=0;i<GetMenuItemCount(popup);++i)
    {
        wchar_t label[256];GetMenuStringW(popup,i,label,256,MF_BYPOSITION);
        if(std::wstring(label)==L"添加到 EasyAppKeeper"){command=GetMenuItemID(popup,i);break;}
    }
    if(!command){DestroyMenu(popup);return 33;}
    CMINVOKECOMMANDINFO invoke={sizeof(invoke)};invoke.lpVerb=MAKEINTRESOURCEA(command-1);invoke.nShow=SW_SHOWNORMAL;
    hr=menu->InvokeCommand(&invoke);DestroyMenu(popup);return SUCCEEDED(hr)?0:34;
}
int wmain(int argc,wchar_t** argv)
{
    if(argc<3)return 2;CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    if(std::wstring(argv[2])==L"--confirm")return confirm(argc>3?argv[3]:L"");
    if(std::wstring(argv[2])==L"--legacy")return legacy(argc-3,argv+3);
    auto library=LoadLibraryW(argv[1]);if(!library)return 3;
    auto get=reinterpret_cast<HRESULT(__stdcall*)(REFCLSID,REFIID,void**)>(GetProcAddress(library,"DllGetClassObject"));
    ComPtr<IClassFactory> factory;ComPtr<IExplorerCommand> command;
    if(!get||FAILED(get(CommandId,IID_PPV_ARGS(&factory)))||FAILED(factory->CreateInstance(nullptr,IID_PPV_ARGS(&command))))return 4;
    PWSTR title=nullptr;if(FAILED(command->GetTitle(nullptr,&title))||std::wstring(title)!=L"添加到 EasyAppKeeper")return 5;CoTaskMemFree(title);
    EXPCMDSTATE state;if(FAILED(command->GetState(nullptr,TRUE,&state)))return 6;
    if(std::wstring(argv[2])==L"--hidden")return state==ECS_HIDDEN?0:7;
    if(state!=ECS_ENABLED)return 8;
    PWSTR icon=nullptr;if(FAILED(command->GetIcon(nullptr,&icon)))return 9;CoTaskMemFree(icon);
    if(std::wstring(argv[2])==L"--invoke")
    {
        std::vector<PCIDLIST_ABSOLUTE> ids;
        for(int i=3;i<argc;++i){PIDLIST_ABSOLUTE item=nullptr;if(FAILED(SHParseDisplayName(argv[i],nullptr,&item,0,nullptr)))return 10;ids.push_back(item);}
        ComPtr<IShellItemArray> items;auto hr=SHCreateShellItemArrayFromIDLists((UINT)ids.size(),ids.data(),&items);
        for(auto item:ids)CoTaskMemFree((void*)item);if(FAILED(hr))return 11;
        hr=command->Invoke(items.Get(),nullptr);if(FAILED(hr)){std::cerr<<"Invoke failed: "<<std::hex<<hr<<std::endl;return 12;}
    }
    std::cout<<"PASS: native IExplorerCommand title, icon, state and selection delivery"<<std::endl;return 0;
}
