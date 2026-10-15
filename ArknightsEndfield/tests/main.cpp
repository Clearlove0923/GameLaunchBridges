#define ENDFIELD_UNIT_TEST
#include "../src/main.cpp"
#include <iostream>
namespace {
int checks=0;
void Check(bool ok,const char* name){if(!ok)throw std::runtime_error(name);++checks;}
template<class F> void Reject(F f,const char* name){bool failed=false;try{f();}catch(const BridgeError&){failed=true;}Check(failed,name);}
void Touch(const fs::path& p){fs::create_directories(p.parent_path());std::ofstream(p)<<"fixture";}
void TestState(){
    const std::string header="[thread 42] csc PrintConfig item:strProjectName: endfield, strGameUUID: abc,\n";
    const std::string ready="[thread 42] cwdmb OnMainBtnStateInfoChanged info:strUUID:abc, strState:start_game, bQueryLatestGameInfoResult:1, bGameServiceStopped:0, bNeedLogin:0, bNeedPurchase:0, bPreparingStartGame:0\n";
    const std::string web="[thread 42] cwdmb OnJsBridgeConnected\n[thread 42] cwdmb OnRecvWebPageLoaded\n";
    Check(ParseState(header+ready,42,L"endfield")=="web_ui_pending","web gate");
    Check(ParseState(header+ready+web,42,L"endfield")=="start_game","ready");
    Check(ParseState(header+ready,42,L"endfield",false)=="start_game","visible gate");
    Check(ParseState(header+ready+web,43,L"endfield")=="session_missing","thread isolation");
    Check(ParseState(header+ready+web+header,42,L"endfield")=="state_pending","new session invalidates old ready");
    Check(ParseState(header+ready+web+"[thread 42] cwdmb OnMainBtnStateInfoChanged info:strUUID:abc, strState:need_login,\n",42,L"endfield")=="need_login","latest login");
    Check(ParseState(header+ready+web+"[thread 42] cwdmb OnMainBtnStateInfoChanged info:strUUID:other, strState:update_game,\n",42,L"endfield")=="start_game","uuid isolation");
    auto pending=ready;pending.replace(pending.find("bNeedLogin:0"),12,"bNeedLogin:1");
    Check(ParseState(header+pending+web,42,L"endfield")=="not_ready","login flag");
    Check(AuthFailure("PC launch auth failed, errorCode: 10100"),"sdk failure");
    Check(!AuthFailure("errorCode: 10100"),"sdk context");
}
}
int wmain(int argc,wchar_t** argv){
    try{
        if(argc!=2)throw std::runtime_error("fixture path required");
        TestState();
        auto o=ParseArguments({L"--validate",L"--",L"x.exe",L"--install-root",L"secret"});
        Check(o.validate&&!o.root&&o.ignored==4,"steam tail isolation");
        Check(ParseArguments({L"%command%",L"--validate"}).ignored==2,"command placeholder");
        Check(ParseArguments({L"steam://run/1",L"--validate"}).ignored==2,"steam uri");
        Check(ParseArguments({L"-applaunch",L"1"}).ignored==2,"steam applaunch");
        Reject([]{ParseArguments({L"--install-root"});},"missing argument");
        for(const auto& text:{L"",L"simple",L"two words",L"中文 path\\",L"a\"b\\"}){
            const auto command=L"bridge.exe "+QuoteArgument(text);int n=0;
            auto args=CommandLineToArgvW(command.c_str(),&n);Check(args&&n==2&&std::wstring(args[1])==text,"command quoting");LocalFree(args);
        }
        Check(IsoTicks(L"[2026-10-15T12:00:00.000+08:00]")==IsoTicks(L"[2026-10-15T04:00:00.000+00:00]"),"timezone retention");
        Check(!IsoTicks(L"unformatted"),"invalid timestamp");
        Check(MayForceClose(true,false,false,true),"close allowed");
        Check(!MayForceClose(false,false,false,true)&&!MayForceClose(true,true,false,true)&&!MayForceClose(true,false,true,true)&&!MayForceClose(true,false,false,false),"close guards");
        Check(UiVersionMatches(L"1.6.0.1604",L"1.6.")&&!UiVersionMatches(L"1.7.0",L"1.6."),"version gate");
        Check(Yellow(RGB(255,220,20))&&!Yellow(RGB(10,10,10))&&!Yellow(CLR_INVALID),"pixel gate");
        const auto root=ExecutablePath().parent_path()/L"fixtures"/std::to_wstring(GetCurrentProcessId());
        fs::create_directories(root);Configuration c;
        const auto standard=root/L"standard";Touch(standard/L"Arknights Endfield/Endfield.exe");Touch(standard/L"Backup/Endfield.exe");
        Check(PickGame(standard,c)==fs::weakly_canonical(standard/L"Arknights Endfield/Endfield.exe"),"standard discovery");
        Check(FindFiles(standard,c.gameFile,c).size()==1,"excluded backup directory");
        const auto compact=root/L"compact";Touch(compact/L"Endfield.exe");Check(PickGame(compact,c).filename()==L"Endfield.exe","compact discovery");
        const auto changed=root/L"changed";Touch(changed/L"channel/new-version/deeper/Endfield.exe");
        Check(PickGame(changed,c).filename()==L"Endfield.exe","version independent discovery");
        auto shallow=c;shallow.depth=1;Reject([&]{PickGame(changed,shallow);},"search depth bound");
        Touch(changed/L"other/Endfield.exe");Reject([&]{PickGame(changed,c);},"ambiguous candidates");
        c.gameCandidates={L"other/Endfield.exe"};Check(PickGame(changed,c)==fs::weakly_canonical(changed/L"other/Endfield.exe"),"candidate priority");
        c.gameCandidates={L"../escape.exe"};Reject([&]{PickGame(changed,c);},"candidate escape");c=Configuration{};
        Check(Within(compact,root)&&!Within(root.parent_path()/L"sibling",root),"scope boundary");
        c.launcherProduct=L"ArknightsEndfieldLaunchBridge";
        fs::create_directories(standard/L"Official");fs::copy_file(ExecutablePath(),standard/L"Official/Launcher.exe",fs::copy_options::overwrite_existing);
        Check(FindLauncher(standard/L"Arknights Endfield/Endfield.exe",c)==fs::weakly_canonical(standard/L"Official/Launcher.exe"),"launcher resource identity");
        Options explicitRoot;explicitRoot.root=standard;Check(ResolveInstallation(explicitRoot,c).game.filename()==L"Endfield.exe","explicit root");
        explicitRoot.root=root.root_path();Reject([&]{ResolveInstallation(explicitRoot,c);},"drive root forbidden");
        const auto game=root/L"process/Endfield.exe",decoy=root/L"decoy/Endfield.exe";
        fs::create_directories(game.parent_path());fs::create_directories(decoy.parent_path());
        fs::copy_file(argv[1],game,fs::copy_options::overwrite_existing);fs::copy_file(argv[1],decoy,fs::copy_options::overwrite_existing);
        auto wrong=StartProcess(decoy,L"",decoy.parent_path(),true);Check(MatchingProcesses(game).empty(),"same name wrong path");
        auto real=StartProcess(game,L"--fixture-value \"two words\"",game.parent_path(),true);
        Check(MatchingProcesses(game)==std::vector<DWORD>{real.pid},"full path match");
        Logger log;c.debounce=1;c.pollMs=100;const auto start=std::chrono::steady_clock::now();
        WatchGame(game,c,log);Check(real.ExitCode()==L"7","game exit code");
        Check(std::chrono::steady_clock::now()-start>=2400ms,"exit debounce");
        Check(ReadSharedFile(game.parent_path()/L"fixture-observed.txt").find("two words")!=std::string::npos,"arguments delivered");
        Check(ReadSharedFile(game.parent_path()/L"fixture-observed.txt").find(Utf8(game.parent_path().wstring()))!=std::string::npos,"working directory delivered");
        const auto logged=ReadSharedFile(log.path());Check(logged.find("exitCode=7")!=std::string::npos&&logged.find("game.finished")!=std::string::npos,"lifecycle logs");
        Check(log.path().parent_path().parent_path()==ExecutablePath().parent_path(),"log beside executable");
        try{throw BridgeError(3,"stack fixture");}catch(const BridgeError& e){Check(e.stack.find(L"at ")!=std::wstring::npos,"native stack capture");}
        std::cout<<checks<<" native checks passed\n";return 0;
    }catch(const std::exception& ex){std::cerr<<ex.what()<<"\n";return 1;}
}
