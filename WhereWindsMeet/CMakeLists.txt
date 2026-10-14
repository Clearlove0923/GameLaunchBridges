cmake_minimum_required(VERSION 3.24)
project(WhereWindsMeetLaunchBridge VERSION 1.4.0 LANGUAGES CXX RC)

function(configure_bridge_target target)
    target_compile_features(${target} PRIVATE cxx_std_20)
    target_compile_definitions(${target} PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_options(${target} PRIVATE /W4 /permissive- /EHsc /utf-8)
    target_link_options(${target} PRIVATE /MANIFEST:NO)
    target_link_libraries(${target} PRIVATE dbghelp shell32)
    set_property(TARGET ${target} PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endfunction()

add_executable(WhereWindsMeetLaunchBridge WIN32 src/main.cpp src/app.rc)
configure_bridge_target(WhereWindsMeetLaunchBridge)

add_executable(WhereWindsMeetLaunchBridgeTestHost WIN32 src/main.cpp src/app.rc)
target_compile_definitions(WhereWindsMeetLaunchBridgeTestHost PRIVATE WWM_TEST_BUILD)
configure_bridge_target(WhereWindsMeetLaunchBridgeTestHost)

install(TARGETS WhereWindsMeetLaunchBridge RUNTIME DESTINATION .)
