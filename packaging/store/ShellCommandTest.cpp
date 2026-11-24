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
int wmain(int argc,wchar_t** argv)
{
    if(argc<3)return 2;CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    if(std::wstring(argv[2])==L"--confirm")return confirm(argc>3?argv[3]:L"");
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
